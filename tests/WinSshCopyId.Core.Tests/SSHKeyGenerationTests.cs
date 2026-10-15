using WinSshCopyId.Services;
using Xunit;

public sealed class SSHKeyGenerationTests
{
    [Fact]
    public void EveryDiscoveredGenerationAlgorithmProducesMatchingPair()
    {
        string directory = Path.Combine(Path.GetTempPath(), "key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new KeyManager();
            foreach (string algorithm in manager.GetSupportedAlgorithms())
            {
                KeyEntry entry = manager.GenerateKey(Path.Combine(directory, algorithm), algorithm, "fixture", _ => { });
                Assert.True(File.Exists(entry.PrivateKeyPath));
                Assert.True(KeyManager.IsValidPublicKey(entry.PublicKeyLine, out _));
                KeyManager.VerifyPrivateKeyPermissions(entry.PrivateKeyPath!);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void ExplicitTemporaryRsaPairHasVerifiedPermissionsAndCannotOverwrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new KeyManager();
            Assert.Contains("rsa", manager.GetSupportedAlgorithms());
            string path = Path.Combine(directory, "fixture");
            KeyEntry entry = manager.GenerateKey(path, "rsa", "fixture", _ => { });
            Assert.Equal(path, entry.PrivateKeyPath);
            Assert.True(KeyManager.IsValidPublicKey(entry.PublicKeyLine, out _));
            KeyManager.VerifyPrivateKeyPermissions(path);
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => manager.GenerateKey(path, "rsa", "replacement", _ => { }));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CancellationCreatesNoPairAndUnsupportedAlgorithmIsRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "fixture");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            var manager = new KeyManager();
            Assert.Throws<OperationCanceledException>(() => manager.GenerateKey(path, "rsa", "fixture", _ => { }, cts.Token));
            Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".pub"));
            Assert.Throws<ArgumentException>(() => manager.GenerateKey(path, "unknown", "fixture", _ => { }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
