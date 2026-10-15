using WinSshCopyId.Services;
using Xunit;

public sealed class SSHServiceTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA test";
    [Theory]
    [InlineData("-oProxyCommand=x")]
    [InlineData("example.com\nHost *")]
    [InlineData("example.com extra")]
    public void RejectsInjectedHost(string host) => Assert.Throws<ArgumentException>(() => SshInput.ValidateEndpoint(host, 22));

    [Theory]
    [InlineData("host:0")]
    [InlineData("host:bad")]
    [InlineData("host 22 extra")]
    [InlineData("[::1]bad")]
    public void RejectsMalformedTargets(string host) => Assert.ThrowsAny<Exception>(() => HostTarget.ParseList(host, 22));

    [Fact]
    public void TrustIsExplicitPersistentAndScopedByPort()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HostTrustStore(Path.Combine(root, "trust.json"));
            Assert.False(store.Check("host", 22, "first", null));
            Assert.True(store.Check("host", 22, "first", _ => true));
            Assert.True(new HostTrustStore(Path.Combine(root, "trust.json")).Check("HOST", 22, "first", null));
            var changed = Assert.Throws<HostKeyChangedException>(() => store.Check("host", 22, "changed", _ => throw new Exception("Changed keys must not prompt."), "ssh-ed25519"));
            Assert.Equal("first", changed.ExpectedFingerprint);
            Assert.Equal("changed", changed.ObservedFingerprint);
            Assert.Equal("ssh-ed25519", changed.Algorithm);
            Assert.False(store.Check("host", 2222, "first", null));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsPrivateAndMultilineKeys()
    {
        Assert.Throws<ArgumentException>(() => SshInput.ValidatePublicKey("-----BEGIN OPENSSH PRIVATE KEY-----"));
        Assert.Throws<ArgumentException>(() => SshInput.ValidatePublicKey(Key + "\nanything"));
    }

    [Fact]
    public async Task BatchRemainsSequentialAndPinsVerification()
    {
        var fake = new FakeTransport();
        var service = new SequentialDeploymentService(fake);
        var result = await service.DeployAsync(new[] { Request("one"), Request("two") }, _ => Task.FromResult<string?>("fixture"));
        Assert.Equal(new[] { "install:one", "verify:one:fp", "install:two", "verify:two:fp" }, fake.Calls);
        Assert.All(result, r => Assert.True(r.Verified));
    }

    [Fact]
    public async Task CancellationDoesNotStartNextHost()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeTransport { Cancel = cts };
        var result = await new SequentialDeploymentService(fake).DeployAsync(new[] { Request("one"), Request("two") },
            _ => Task.FromResult<string?>("fixture"), cancellationToken: cts.Token);
        Assert.Equal(2, result.Count);
        Assert.Equal(InstallationState.Installed, result[0].Installation);
        Assert.True(result[0].CancellationRequested);
        Assert.Equal("fp", result[0].Fingerprint);
        Assert.Equal(DeploymentState.NotStarted, result[1].State);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void IdentityPathIsQuotedExactly()
    {
        Assert.Contains("-i 'C:\\keys\\a b'", SshInput.BuildConnectCommand("host", 22, "user", @"C:\keys\a b"));
    }

    [Theory]
    [InlineData(@"C:\keys\$(Get-Date).key")]
    [InlineData(@"C:\keys\$value`x.key")]
    [InlineData(@"C:\keys\owner's.key")]
    public void PowerShellParserTreatsIdentityAsLiteral(string path)
    {
        string command = SshInput.BuildConnectCommand("host", 22, "user$", path);
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(command));
        string expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path));
        string script = "$source=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'));" +
            "$expected=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + expected + "'));" +
            "$tokens=$null;$errors=$null;$ast=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors);" +
            "if($errors.Count){exit 1};$cmd=$ast.EndBlock.Statements[0].PipelineElements[0];" +
            "if($cmd.CommandElements[4] -isnot [System.Management.Automation.Language.StringConstantExpressionAst]){exit 2};" +
            "if($cmd.CommandElements[4].Value -cne $expected){exit 3};exit 0";
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(15000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void PublicKeyWireBodyIsRequiredAndFullyConsumed()
    {
        byte[] valid = Convert.FromBase64String(Key.Split(' ')[1]);
        Assert.Equal(Key, SshInput.ValidatePublicKey(Key));
        foreach (byte[] invalid in new[] { valid[..15], valid[..^1], valid.Concat(new byte[] { 0 }).ToArray(),
            valid[..15].Concat(new byte[] { 0, 0, 0, 31 }).Concat(new byte[31]).ToArray() })
            Assert.Throws<ArgumentException>(() => SshInput.ValidatePublicKey("ssh-ed25519 " + Convert.ToBase64String(invalid)));
        Assert.Throws<ArgumentException>(() => SshInput.ValidatePublicKey("ssh-unknown " + Convert.ToBase64String(Wire(System.Text.Encoding.ASCII.GetBytes("ssh-unknown")))));
    }

    [Fact]
    public void AcceptsRealRsaAndEcdsaPublicFixtures()
    {
        var rsa = OpenSshKey.GenerateRsa(2048, "fixture");
        Assert.Equal(rsa.PublicLine, SshInput.ValidatePublicKey(rsa.PublicLine));
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var parameters = ec.ExportParameters(false);
        byte[] point = new byte[] { 4 }.Concat(parameters.Q.X!).Concat(parameters.Q.Y!).ToArray();
        byte[] blob = Wire(System.Text.Encoding.ASCII.GetBytes("ecdsa-sha2-nistp256"))
            .Concat(Wire(System.Text.Encoding.ASCII.GetBytes("nistp256"))).Concat(Wire(point)).ToArray();
        string line = "ecdsa-sha2-nistp256 " + Convert.ToBase64String(blob);
        Assert.Equal(line, SshInput.ValidatePublicKey(line));
    }

    private static byte[] Wire(byte[] value)
    {
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        return length.Concat(value).ToArray();
    }

    private static DeploymentRequest Request(string host) => new(host, 22, "user", Key, "identity");
    private sealed class FakeTransport : ISshDeploymentTransport
    {
        public List<string> Calls { get; } = new();
        public CancellationTokenSource? Cancel { get; init; }
        public Task<DeployResult> InstallAsync(DeploymentRequest request, string password, CancellationToken cancellationToken)
        {
            Calls.Add("install:" + request.Host);
            Cancel?.Cancel();
            return Task.FromResult(new DeployResult { ServerFingerprint = "fp" });
        }
        public Task<bool> VerifyAsync(DeploymentRequest request, string fingerprint, Func<string?>? passphrase, CancellationToken cancellationToken)
        {
            Calls.Add("verify:" + request.Host + ":" + fingerprint);
            return Task.FromResult(true);
        }
    }
}
