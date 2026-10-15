using WinSshCopyId.Services;
using Xunit;

public sealed class ProfileServiceTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("server", "")]
    [InlineData("", "user")]
    [InlineData("server", "user")]
    public void IncompleteFormsRoundTrip(string hosts, string username)
    {
        string path = Path.GetTempFileName();
        try
        {
            SettingsStore.Export(path, new AppSettings { Hosts = hosts, Username = username });
            var result = SettingsStore.Import(path);
            Assert.Equal(hosts, result.Hosts); Assert.Equal(username, result.Username);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacyIncompleteProfileLoadsWithoutCredentials()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"App\":\"WinSshCopyId\",\"ProtectedPassword\":\"old\",\"RememberPassword\":true}");
            var result = SettingsStore.Import(path);
            Assert.Equal("", result.Username); Assert.Null(result.ProtectedPassword); Assert.False(result.RememberPassword);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NonemptyInvalidUsernameRemainsRejected()
    {
        string path = Path.GetTempFileName();
        try
        {
            SettingsStore.Export(path, new AppSettings { Username = "-bad" });
            Assert.Throws<ArgumentException>(() => SettingsStore.Import(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void ApplyRetainsReceivingAppearanceAndDropsCredentials()
    {
        var applied = SettingsStore.ApplyProfile(new AppSettings { Theme = "Dark", AutoSave = false },
            new AppSettings { Hosts = "server", Username = "user", Theme = "Light", RememberPassword = true, ProtectedPassword = "legacy" });
        Assert.Equal("Dark", applied.Theme);
        Assert.False(applied.AutoSave);
        Assert.Equal("server", applied.Hosts);
        Assert.Null(applied.ProtectedPassword);
        Assert.False(applied.RememberPassword);
    }

    [Fact]
    public void ImportRejectsFutureSchemaWithoutApplyingIt()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"App\":\"WinSshCopyId\",\"SchemaVersion\":999,\"Username\":\"user\"}");
            Assert.Throws<InvalidDataException>(() => SettingsStore.Import(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void PortableProfileExcludesCredentialsAndAppearance()
    {
        string path = Path.GetTempFileName();
        try
        {
            SettingsStore.Export(path, new AppSettings { Hosts = "host", Username = "user", Theme = "Dark", ProtectedPassword = "private", RememberPassword = true });
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("private", json);
            Assert.DoesNotContain("Password", json);
            Assert.DoesNotContain("Theme", json);
            Assert.DoesNotContain("AutoSave", json);
            var imported = SettingsStore.Import(path);
            Assert.Null(imported.ProtectedPassword);
            Assert.False(imported.RememberPassword);
            Assert.Equal("System", imported.Theme);
        }
        finally { File.Delete(path); }
    }
}
