using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinSshCopyId.Tools.Servers;
using WinSshCopyId.Tools.Conversion;
using WinSshCopyId.Tools.Ollama;

if (args.Length == 3 && args[0] == "--verify-ollama-signature")
{
    using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    var signer = manifest.RootElement.GetProperty("SignatureObservation");
    if (!await OllamaPublisherVerifier.VerifyAsync(Path.GetFullPath(args[2]), signer.GetProperty("Subject").GetString()!, signer.GetProperty("Thumbprint").GetString()!))
        throw new InvalidDataException("Official payload signature or exact signer pin did not verify.");
    Console.WriteLine("PASS: official Ollama payload native Authenticode trust and exact signer pin verified; executable was not launched.");
    return;
}
if (args.Length == 3 && args[0] == "--verify-confined-worker")
{
    var confined = await ConfinedLocalConverter.CreateForPackageVerificationOnlyAsync(args[1]);
    var receipt = await confined.VerifyAdapterAsync(args[2]);
    if (confined.AvailableAdapters.All(x => x.Id != args[2])) throw new InvalidDataException("Confined receipt did not enable its verified adapter.");
    Console.WriteLine("PASS: actual confined worker adapter " + receipt.AdapterId + "; executable SHA-256 " + receipt.ExecutableSha256 + "; output SHA-256 " + receipt.OutputSha256);
    return;
}
if (File.Exists(Path.Combine(Environment.CurrentDirectory, ".fake-server-fixture")))
{
    Console.WriteLine("fixture-ready");
    while (await Console.In.ReadLineAsync() is { } line) if (line == "stop") return;
    return;
}
var completed = 0;
void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); completed++; }
async Task Reject(Func<Task> action, string detail)
{
    try { await action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException or NotSupportedException)
    { completed++; return; }
    throw new Exception(detail);
}
static int FindDualProtocolPort()
{
    for (var attempt = 0; attempt < 64; attempt++)
    {
        var candidate = 12000 + attempt * 431;
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, candidate);
        try
        {
            using var udp = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, candidate));
            tcp.Start(); return candidate;
        }
        catch (System.Net.Sockets.SocketException) { }
        finally { tcp.Stop(); }
    }
    throw new IOException("No dual-protocol temporary test port was available after bounded discovery.");
}

var root = Path.Combine(Path.GetTempPath(), "WinSshCopyId-tools-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var text = "# preserved\r\nserver-port=25565\r\ncustom-unknown=value\r\n";
    var settings = ServerProperties.Parse(text).SetPort(25566);
    Check(settings.ToString() == text.Replace("25565", "25566"), "Unknown property or newline lost.");
    await Reject(() => Task.FromResult(settings.Set("x", "a\nb")), "Property injection accepted.");
    var source = Path.Combine(root, "instance"); Directory.CreateDirectory(source);
    await File.WriteAllTextAsync(Path.Combine(source, "world.dat"), "original world");
    var instance = new ServerInstance(Guid.NewGuid(), "test-server", new(ServerTargetKind.LocalProcess, source),
        new("java", ServerEdition.Java, "test", "vanilla", ServerCatalogue.JavaManifest, null, null, null, null, null),
        true, false, 25565, 1024, DateTimeOffset.UtcNow);
    var registry = new ServerRegistry(Path.Combine(root, "state", "instances.json"));
    await registry.SaveAsync(instance);
    Check((await registry.ReadAsync()).Single() == instance, "Registry round trip changed instance.");
    var plan = ServerLifecyclePlanner.Create(instance, ServerOperation.Start, "docker", "ssh");
    Check(!plan.CanExecute && plan.Blockers.Any(x => x.Contains("EULA")), "Unapproved launch became executable.");
    var remote = instance with { Target = instance.Target with { Kind = ServerTargetKind.SshProcess, Host = "example.invalid", User = "test" } };
    Check(!ServerLifecyclePlanner.Create(remote, ServerOperation.Start, "docker", "ssh").CanExecute, "Untrusted SSH launch accepted.");
    var stopped = new ServerObservation(instance.Id, ServerObservedState.Stopped, DateTimeOffset.UtcNow, null, "Test observation");
    var backups = new ServerBackupService();
    var backup = Path.Combine(root, "snapshot");
    var receipt = await backups.CreateAsync(instance, stopped, backup);
    Check(receipt.Files.Length == 1 && (await backups.VerifyAsync(backup)).TotalBytes == 14, "Backup receipt invalid.");
    await File.WriteAllTextAsync(Path.Combine(source, "world.dat"), "modified world");
    var rollback = await backups.RestoreAsync(instance, stopped, backup);
    Check(await File.ReadAllTextAsync(Path.Combine(source, "world.dat")) == "original world", "Restore did not restore bytes.");
    Check(await File.ReadAllTextAsync(Path.Combine(rollback, "world.dat")) == "modified world", "Rollback did not preserve prior world.");
    await File.WriteAllTextAsync(Path.Combine(backup, "world.dat"), "corrupt");
    await Reject(() => backups.VerifyAsync(backup), "Corrupt backup accepted.");
    await Reject(() => backups.CreateAsync(instance, stopped with { State = ServerObservedState.Running }, Path.Combine(root, "bad")), "Running server snapshot accepted.");
    var metadata = "{\"downloads\":{\"server\":{\"url\":\"https://piston-data.mojang.com/server.jar\",\"sha1\":\"" + new string('a',40) + "\",\"size\":123}},\"javaVersion\":{\"majorVersion\":21}}";
    using var client = new HttpClient(new FakeHandler(metadata));
    var catalog = new ServerCatalogue(client);
    var version = new ServerVersion("test", "release", new("https://piston-meta.mojang.com/test.json"), Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(metadata))), DateTimeOffset.UtcNow);
    Check((await catalog.ResolveJavaAsync(version))?.JavaMajorVersion == 21, "Source-backed runtime not parsed.");
    await Reject(() => catalog.ResolveJavaAsync(version with { MetadataSha1 = new string('0', 40) }), "Wrong metadata digest accepted.");
    Check(ConversionCatalog.Categories.Count == 8 && ConversionCatalog.Adapters.All(x => !x.Available), "Unproven conversion engine enabled.");
    var converter = new LocalConverter();
    var jsonPath = Path.Combine(root, "input.json");
    await File.WriteAllTextAsync(jsonPath, "{\"value\":42}");
    Check((await converter.PreviewAsync(jsonPath)).DetectedType == "json", "JSON signature was not inspected.");
    await Reject(() => converter.ConvertAsync(jsonPath, Path.Combine(root, "blocked.json"), "json-format"), "Unproven conversion was executed.");
    var outputPath = Path.Combine(root, "formatted.json");
    await converter.VerifyManagedImplementationAsync(jsonPath, outputPath, "json-format");
    using (var output = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(outputPath)))
        Check(output.RootElement.GetProperty("value").GetInt32() == 42, "Managed verification lost JSON values.");
    await Reject(() => converter.VerifyManagedImplementationAsync(jsonPath, outputPath, "json-format"), "Existing destination was overwritten.");
    var queue = new ConversionQueue(Path.Combine(root, "queue"));
    for (var i = 0; i < 205; i++) await queue.EnqueueAsync(jsonPath, Path.Combine(root, "out-" + i), "json-format");
    Check(queue.ReadPage(0, 200).Count() == 200 && queue.ReadPage(200, 200).Count() == 5, "Queue pagination lost records.");
    var ollamaStore = new OllamaStateStore(Path.Combine(root, "ollama"));
    var chat = new OllamaConversation(Guid.NewGuid(), "Local test", "test:latest", DateTimeOffset.UtcNow, [new("user", "private content")]);
    await ollamaStore.SaveConversationAsync(chat);
    Check((await ollamaStore.LoadAsync()).Conversations.Single().Messages.Single().Content == "private content", "Local chat was not persisted.");
    Check(!OllamaStateStore.ExportRedacted(chat).Contains("private content", StringComparison.Ordinal), "Export exposed local model content.");
    Check(OllamaHardwareFit.Unknown.Verdict == "Unknown" && OllamaCatalogSnapshot.Unavailable.Completeness == OllamaCatalogCompleteness.Unavailable, "Missing hardware or catalog evidence became a positive claim.");
    await Reject(() => ollamaStore.DeleteConversationAsync(chat.Id, false), "Unconfirmed local history deletion accepted.");
    var fixtureRoot = Path.Combine(root, "process-fixture"); Directory.CreateDirectory(fixtureRoot);
    await File.WriteAllTextAsync(Path.Combine(fixtureRoot, ".fake-server-fixture"), "Bounded test child only");
    var executablePath = Environment.ProcessPath!;
    var executableHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executablePath)));
    var freePort = FindDualProtocolPort();
    var fixture = instance with { Id = Guid.NewGuid(), EulaAccepted = true, Port = freePort,
        Target = new(ServerTargetKind.LocalProcess, fixtureRoot),
        Distribution = new("fixture", ServerEdition.Bedrock, "test", "test", ServerCatalogue.JavaManifest,
            executablePath, executableHash, null, null, null) };
    await using (var adapter = new ServerProcessAdapter())
    {
        var started = await adapter.StartAsync(fixture, new(executablePath, executableHash));
        Check(started.ProcessId != Environment.ProcessId && adapter.Observe(fixture.Id).State == ServerObservedState.Running, "Owned fixture process was not started.");
        var stoppedFixture = await adapter.StopAsync(fixture.Id, TimeSpan.FromSeconds(10));
        Check(stoppedFixture.Exited && stoppedFixture.ExitCode == 0, "Owned fixture did not stop through stdin.");
        Check(adapter.Observe(fixture.Id).State == ServerObservedState.Unknown, "Stopped fixture remained adopted.");
    }
    completed += await OllamaTests.RunAsync();
    var imageDigest = "test/server@sha256:" + new string('a', 64);
    var dockerInstance = instance with { Id = Guid.NewGuid(), EulaAccepted = true, Port = freePort,
        Target = new(ServerTargetKind.LocalDocker, source), Distribution = instance.Distribution with { ContainerImageDigest = imageDigest } };
    var dockerExecutor = new FakeDocker(dockerInstance.Id, imageDigest);
    var dockerAdapter = new ServerDockerAdapter(dockerExecutor, Path.Combine(root, "docker.exe"));
    var container = await dockerAdapter.StartAsync(dockerInstance, new(imageDigest, "/usr/bin/java", ["-jar", "/data/server.jar"], true));
    Check(container.State.Contains("Started") && (await dockerAdapter.ObserveAsync(dockerInstance)).State == ServerObservedState.Running, "Typed Docker lifecycle failed.");
    Check((await dockerAdapter.StopAsync(dockerInstance)).Exited, "Typed Docker stop failed.");
    Check(dockerExecutor.Commands.All(c => !c.Arguments.Contains("pull")) && dockerExecutor.Commands.Any(c => c.Arguments.Contains("never")), "Docker image was implicitly pulled.");
    var sshAdapter = new ServerSshAdapter(new WrongHostTransport());
    await Reject(() => sshAdapter.ExecuteAsync(remote with { Target = remote.Target with { ApprovedHostKeySha256 = "SHA256:approved" } }, ServerOperation.Discover), "Wrong remote host receipt accepted.");
    var fixturePayload = Encoding.UTF8.GetBytes("fixture executable bytes; never executable");
    var fixtureArchive = Path.Combine(root, "fixture-payload.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(fixtureArchive, System.IO.Compression.ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("ollama.exe");
        using var member = entry.Open(); member.Write(fixturePayload);
    }
    var payloadManifest = new OllamaPayloadManifest("0.0.1", "x64", new("https://github.com/ollama/ollama/releases/download/v0.0.1/fixture.zip"),
        new FileInfo(fixtureArchive).Length, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixtureArchive))),
        [new("ollama.exe", fixturePayload.Length, Convert.ToHexString(SHA256.HashData(fixturePayload)))], "Fixture verifier only; not publisher proof");
    using (var installer = new OllamaInstaller(Path.Combine(root, "fixture-installer")))
    {
        var installed = await installer.InstallAsync(payloadManifest, (_, _, _) => Task.FromResult(true), cachedArchivePath: fixtureArchive);
        Check(await File.ReadAllTextAsync(Path.Combine(installed.Directory, "ollama.exe")) == Encoding.UTF8.GetString(fixturePayload), "Installer transaction changed fixture bytes.");
        var next = await installer.InstallAsync(payloadManifest with { Version = "0.0.2", Source = new("https://github.com/ollama/ollama/releases/download/v0.0.2/fixture.zip") },
            (_, _, _) => Task.FromResult(true), cachedArchivePath: fixtureArchive);
        await installer.RestoreRecordAsync(next);
        var active = await File.ReadAllTextAsync(Path.Combine(root, "fixture-installer", "active-installation.json"));
        Check(active.Contains(installed.Directory.Replace("\\", "\\\\"), StringComparison.Ordinal), "Installer rollback lost prior active version.");
        await Reject(() => installer.InstallAsync(payloadManifest, (_, _, _) => Task.FromResult(false), cachedArchivePath: fixtureArchive), "Installer accepted failed publisher verification.");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "fixture-installer", "active-installation.json")) == active, "Rejected installer changed the prior active record.");
    }
    Console.WriteLine($"PASS: {completed} local tool assertions; only bounded test child launched, no real server or service launched.");
}
finally { Directory.Delete(root, true); }

sealed class FakeHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request });
}
sealed class FakeDocker(Guid instanceId, string image) : IServerCommandExecutor
{
    public List<ServerCommand> Commands { get; } = [];
    private bool running;
    public Task<ServerCommandResult> ExecuteAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        var action = command.Arguments.Length > 1 ? command.Arguments[1] : "";
        if (command.Arguments[0] == "image") return Task.FromResult(new ServerCommandResult(0, System.Text.Json.JsonSerializer.Serialize(new[] { image }), "", false));
        if (action == "start") running = true;
        if (action == "stop") running = false;
        var output = action switch
        {
            "create" => new string('b', 64),
            "inspect" => System.Text.Json.JsonSerializer.Serialize(new[] { new { Id = new string('b',64), Config = new { Image = image, Labels = new Dictionary<string,string> { ["winsshcopyid.instance"] = instanceId.ToString("D") } }, State = new { Running = running, ExitCode = 0 } } }),
            _ => new string('b', 64)
        };
        return Task.FromResult(new ServerCommandResult(0, output, "", false));
    }
}
sealed class WrongHostTransport : IApprovedServerSshTransport
{
    public Task<ServerRemoteReceipt> ExecuteAsync(ApprovedServerHost approvedHost, ServerRemoteRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ServerRemoteReceipt(request.RequestId, request.InstanceId, "wrong-host", null, ServerObservedState.Stopped, true, "Fake mismatch", []));
}
