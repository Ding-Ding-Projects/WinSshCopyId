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
            Assert.False(store.Check("host", 22, "changed", _ => throw new Exception("Changed keys must not prompt.")));
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
        Assert.Single(result);
        Assert.Equal(DeploymentState.Cancelled, result[0].State);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void IdentityPathIsQuotedExactly()
    {
        Assert.Contains("-i \"C:\\keys\\a b\"", SshInput.BuildConnectCommand("host", 22, "user", @"C:\keys\a b"));
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
