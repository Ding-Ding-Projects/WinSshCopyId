using System.Diagnostics;
using System.Text.Json;
using WinSshCopyId.Services;
using Xunit;
using Xunit.Abstractions;

public sealed class LoopbackSshTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ProductionTransportAgainstDisposableLoopbackServer()
    {
        string source = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(source, "ssh_fixture.py")))
            source = Directory.GetParent(source)?.FullName ?? throw new IOException("Fixture source could not be located.");
        string python = Path.Combine(source, ".fixture-env", "Scripts", "python.exe");
        Assert.True(File.Exists(python), "Create the documented task-local Python environment first.");
        string temporary = Path.Combine(Path.GetTempPath(), "loopback-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string control = Path.Combine(temporary, "control.json");
        string events = Path.Combine(temporary, "events.jsonl");
        void Mode(string mode = "normal", int key = 0) => File.WriteAllText(control, JsonSerializer.Serialize(new { mode, key }));
        int Count(string value)
        {
            if (!File.Exists(events)) return 0;
            using var stream = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Count(line => line.Contains('"' + value + '"', StringComparison.Ordinal));
        }
        var checks = new List<string>();
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(source, "ssh_fixture.py")); start.ArgumentList.Add(temporary);
        Process? process = null;
        int pid = 0;
        int serverPid = 0;
        int port = 0;
        DateTime serverStarted = default;
        DateTime processStart = default;
        bool teardownVerified = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Mode();
            process = Process.Start(start)!;
            pid = process.Id; processStart = process.StartTime.ToUniversalTime();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            await WaitUntil(() => File.Exists(Path.Combine(temporary, "ready.json")), TimeSpan.FromSeconds(15));
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary, "ready.json")));
            port = ready.RootElement.GetProperty("port").GetInt32();
            serverPid = ready.RootElement.GetProperty("processId").GetInt32();
            using (var server = Process.GetProcessById(serverPid)) serverStarted = server.StartTime.ToUniversalTime();
            Assert.InRange(port, 1024, 65535);
            Assert.Equal("5.0.0", ready.RootElement.GetProperty("version").GetString());
            var identity = new KeyManager().GenerateKey(Path.Combine(temporary, "client"), "ed25519", "fixture", _ => { });
            var request = new DeploymentRequest("127.0.0.1", port, "fixture", identity.PublicKeyLine, identity.PrivateKeyPath!);
            string trustPath = Path.Combine(temporary, "trust.json");
            var denied = new SshDeploymentTransport(new SshDeployer(new HostTrustStore(trustPath)));
            await Assert.ThrowsAnyAsync<Exception>(() => denied.InstallAsync(request, "fixture-only-not-a-user-credential", CancellationToken.None));
            Assert.Equal(0, Count("password-auth"));
            Assert.False(File.Exists(trustPath));
            checks.Add("Unknown host rejected before password authentication");

            int approvals = 0;
            var approved = new SshDeploymentTransport(new SshDeployer(new HostTrustStore(trustPath), approval =>
            {
                Assert.Equal(0, Count("password-auth"));
                Assert.False(string.IsNullOrWhiteSpace(approval.Algorithm));
                Assert.Equal(port, approval.Port); approvals++;
                return true;
            }));
            var installed = await approved.InstallAsync(request, "fixture-only-not-a-user-credential", CancellationToken.None);
            Assert.False(installed.KeyWasAlreadyPresent);
            Assert.Equal(1, approvals); Assert.Equal(1, Count("installed"));
            Assert.False(string.IsNullOrWhiteSpace(installed.ServerFingerprint));
            checks.Add("Explicit host approval precedes authentication and installs public key");

            var persisted = new SshDeploymentTransport(new SshDeployer(new HostTrustStore(trustPath), _ => throw new Exception("Persisted trust must not prompt")));
            var duplicate = await persisted.InstallAsync(request, "fixture-only-not-a-user-credential", CancellationToken.None);
            Assert.True(duplicate.KeyWasAlreadyPresent);
            Assert.Equal(installed.ServerFingerprint, duplicate.ServerFingerprint);
            Assert.True(await persisted.VerifyAsync(request, installed.ServerFingerprint!, null, CancellationToken.None));
            Assert.True(Count("publickey-auth") > 0); Assert.Equal(1, Count("verify-command"));
            checks.Add("Persisted host trust, duplicate install and genuine public-key SSH authentication verified");

            var rsaIdentity = new KeyManager().GenerateKey(Path.Combine(temporary, "client-rsa"), "rsa", "fixture", _ => { });
            var rsaRequest = request with { PublicKey = rsaIdentity.PublicKeyLine, PrivateKeyPath = rsaIdentity.PrivateKeyPath! };
            var rsaInstalled = await persisted.InstallAsync(rsaRequest, "fixture-only-not-a-user-credential", CancellationToken.None);
            Assert.False(rsaInstalled.KeyWasAlreadyPresent);
            Assert.True(await persisted.VerifyAsync(rsaRequest, installed.ServerFingerprint!, null, CancellationToken.None));
            checks.Add("Generated RSA client identity authenticates with preferred existing SHA-2 signature algorithms");

            int authentications = Count("password-auth");
            Mode(key: 1);
            var changed = await Assert.ThrowsAsync<HostKeyChangedException>(() => persisted.InstallAsync(request, "fixture-only-not-a-user-credential", CancellationToken.None));
            Assert.Equal(installed.ServerFingerprint, changed.ExpectedFingerprint);
            Assert.NotEqual(changed.ExpectedFingerprint, changed.ObservedFingerprint);
            Assert.Equal(authentications, Count("password-auth"));
            checks.Add("Changed host key hard-stops before authentication with expected and observed identities");
            Mode();

            Mode("verify-fail");
            var verificationFailure = await new SequentialDeploymentService(persisted).DeployAsync(new[] { request },
                _ => Task.FromResult<string?>("fixture-only-not-a-user-credential"));
            Assert.Equal(InstallationState.AlreadyPresent, verificationFailure[0].Installation);
            Assert.Equal(VerificationState.Failed, verificationFailure[0].Verification);
            Assert.Equal(installed.ServerFingerprint, verificationFailure[0].Fingerprint);
            Assert.Equal(DeploymentRetryAction.VerifyOnly, verificationFailure[0].RetryAction);
            checks.Add("Actual verification response failure retains installation identity and verification-only retry");

            Mode("stall");
            int stalled = Count("command-stalled");
            using (var cancellation = new CancellationTokenSource())
            {
                var active = persisted.InstallAsync(request, "fixture-only-not-a-user-credential", cancellation.Token);
                await WaitUntil(() => Count("command-stalled") > stalled, TimeSpan.FromSeconds(10));
                var cancellationWatch = Stopwatch.StartNew();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(cancellationWatch.Elapsed < TimeSpan.FromSeconds(10));
            }
            checks.Add("Cancellation interrupts an actual active SSH command");

            int beforeTimeout = Count("command-stalled");
            var timeoutWatch = Stopwatch.StartNew();
            var timedCommand = persisted.InstallAsync(request, "fixture-only-not-a-user-credential", CancellationToken.None);
            await WaitUntil(() => Count("command-stalled") > beforeTimeout, TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<Renci.SshNet.Common.SshOperationTimeoutException>(() => timedCommand.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.InRange(timeoutWatch.Elapsed.TotalSeconds, 24, 30);
            checks.Add("Production 25-second command timeout observed");

            Mode();
            using (var cancellation = new CancellationTokenSource())
            {
                int before = Count("command-stalled");
                var progress = new InlineProgress(value => { if (value.Completed == 1 && value.Stage == "Connecting") Mode("stall"); });
                var batch = new SequentialDeploymentService(persisted).DeployAsync(new[] { request, request, request },
                    _ => Task.FromResult<string?>("fixture-only-not-a-user-credential"), progress: progress, cancellationToken: cancellation.Token);
                await WaitUntil(() => Count("command-stalled") > before, TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                var results = await batch.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(3, results.Count);
                Assert.True(results[0].Verified);
                Assert.Equal(InstallationState.Uncertain, results[1].Installation);
                Assert.Equal(DeploymentRetryAction.Reconcile, results[1].RetryAction);
                Assert.Equal(DeploymentState.NotStarted, results[2].State);
            }
            checks.Add("Real SSH partial batch retains success, uncertain cancelled mutation and unstarted remainder");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    Assert.Equal(pid, process.Id); Assert.Equal(processStart, process.StartTime.ToUniversalTime());
                    process.Kill(true);
                    Assert.True(process.WaitForExit(10000));
                }
                teardownVerified = process.HasExited;
                if (serverPid != 0)
                {
                    try
                    {
                        using var server = Process.GetProcessById(serverPid);
                        teardownVerified &= server.HasExited || server.StartTime.ToUniversalTime() != serverStarted;
                    }
                    catch (ArgumentException) { }
                }
                if (port != 0)
                    teardownVerified &= !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                        .GetActiveTcpListeners().Any(endpoint => endpoint.Port == port && endpoint.Address.Equals(System.Net.IPAddress.Loopback));
                process.Dispose();
            }
            Directory.CreateDirectory(Path.Combine(source, ".fixture-output"));
            string receipt = JsonSerializer.Serialize(new
            {
                capturedAtUtc = DateTime.UtcNow, engine = "Paramiko 5.0.0", processId = pid, processStartedUtc = processStart,
                serverProcessId = serverPid, serverStartedUtc = serverStarted, endpoint = "127.0.0.1:" + port,
                teardownVerified, durationSeconds = stopwatch.Elapsed.TotalSeconds, checks,
                limitation = "Real SSH negotiation/authentication with narrow command emulation. No POSIX shell or OpenSSH sshd interoperability claim."
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(source, ".fixture-output", "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + ".json"), receipt);
            File.WriteAllText(Path.Combine(source, ".fixture-output", "last-run.json"), receipt);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            output.WriteLine($"Loopback fixture: {checks.Count} acceptance checks; owned teardown: {teardownVerified}; engine: Paramiko 5.0.0; commands emulated.");
        }
        Assert.Equal(9, checks.Count);
        Assert.True(teardownVerified);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("Disposable loopback fixture did not reach its bounded condition.");
            await Task.Delay(25);
        }
    }
    private sealed class InlineProgress(Action<DeploymentProgress> report) : IProgress<DeploymentProgress>
    {
        public void Report(DeploymentProgress value) => report(value);
    }
}
