using WinSshCopyId.Services;
using Xunit;

public sealed class SSHOutcomeTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA test";
    private static DeploymentRequest Request(string host) => new(host, 22, "user", Key, "identity");

    [Theory]
    [InlineData("false")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task VerificationFailureNeverErasesInstallation(string behavior)
    {
        using var cts = new CancellationTokenSource();
        var fake = new Transport { VerifyBehavior = behavior, Cancellation = cts };
        var outcomes = await new SequentialDeploymentService(fake).DeployAsync(new[] { Request("one"), Request("two") },
            _ => Task.FromResult<string?>("fixture"), cancellationToken: cts.Token);
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(InstallationState.Installed, outcomes[0].Installation);
        Assert.Equal("fp", outcomes[0].Fingerprint);
        Assert.Equal(DeploymentRetryAction.VerifyOnly, outcomes[0].RetryAction);
        Assert.True(outcomes[0].IsRetryableFailure);
        Assert.False(outcomes[0].Verified);
        if (behavior == "cancel")
        {
            Assert.Equal(VerificationState.Cancelled, outcomes[0].Verification);
            Assert.True(outcomes[0].CancellationRequested);
            Assert.Equal(InstallationState.NotStarted, outcomes[1].Installation);
        }
        else Assert.Equal(VerificationState.Failed, outcomes[0].Verification);
    }

    [Fact]
    public async Task InterruptedInstallIsUncertainAndRemainderNotStarted()
    {
        using var cts = new CancellationTokenSource();
        var outcomes = await new SequentialDeploymentService(new Transport { InstallBehavior = "cancel", Cancellation = cts })
            .DeployAsync(new[] { Request("one"), Request("two") }, _ => Task.FromResult<string?>("fixture"), cancellationToken: cts.Token);
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(InstallationState.Uncertain, outcomes[0].Installation);
        Assert.Equal(DeploymentRetryAction.Reconcile, outcomes[0].RetryAction);
        Assert.False(outcomes[0].IsRetryableFailure);
        Assert.Equal(DeploymentState.NotStarted, outcomes[1].State);
    }

    [Fact]
    public async Task KnownUnstartedFailureAloneAllowsDeployRetry()
    {
        var outcomes = await new SequentialDeploymentService(new Transport { InstallBehavior = "not-started" })
            .DeployAsync(new[] { Request("one") }, _ => Task.FromResult<string?>("fixture"));
        Assert.Equal(InstallationState.Failed, outcomes[0].Installation);
        Assert.Equal(DeploymentRetryAction.Deploy, outcomes[0].RetryAction);
        Assert.True(outcomes[0].IsRetryableFailure);
    }

    [Fact]
    public async Task InitiallyCancelledBatchReturnsEveryTargetWithoutCredentials()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var outcomes = await new SequentialDeploymentService(new Transport()).DeployAsync(new[] { Request("one"), Request("two") },
            _ => throw new Exception("Must not request credentials"), cancellationToken: cts.Token);
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal(DeploymentState.NotStarted, outcome.State));
    }

    private sealed class Transport : ISshDeploymentTransport
    {
        public string? VerifyBehavior { get; init; }
        public string? InstallBehavior { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public Task<DeployResult> InstallAsync(DeploymentRequest request, string password, CancellationToken cancellationToken)
        {
            if (InstallBehavior == "cancel") { Cancellation!.Cancel(); throw new OperationCanceledException(); }
            if (InstallBehavior == "not-started") throw new DeploymentNotStartedException("fixture");
            return Task.FromResult(new DeployResult { ServerFingerprint = "fp" });
        }
        public Task<bool> VerifyAsync(DeploymentRequest request, string fingerprint, Func<string?>? passphrase, CancellationToken cancellationToken)
        {
            if (VerifyBehavior == "cancel") { Cancellation!.Cancel(); throw new OperationCanceledException(); }
            if (VerifyBehavior == "throw") throw new IOException("fixture");
            return Task.FromResult(VerifyBehavior != "false");
        }
    }
}
