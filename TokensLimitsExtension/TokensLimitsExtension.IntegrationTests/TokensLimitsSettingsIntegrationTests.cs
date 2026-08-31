using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class TokensLimitsSettingsIntegrationTests
{
    [Fact]
    public void ResolveStorageMigratesMatchingSettingsAndSecretsPair()
    {
        using var canonical = new TestDirectory();
        using var host = new TestDirectory();
        File.WriteAllText(System.IO.Path.Combine(canonical.Path, "tokensLimits.settings.json"), "stale-settings");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.settings.json"), "current-settings");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.secrets.json"), "current-secrets");

        var storage = TokensLimitsSettings.ResolveStorage(canonical.Path, host.Path);

        Assert.Equal("current-settings", File.ReadAllText(storage.SettingsPath));
        Assert.Equal("current-secrets", File.ReadAllText(storage.SecretsPath));
    }

    [Fact]
    public void ResolveStorageKeepsExistingCanonicalPair()
    {
        using var canonical = new TestDirectory();
        using var host = new TestDirectory();
        File.WriteAllText(System.IO.Path.Combine(canonical.Path, "tokensLimits.settings.json"), "canonical-settings");
        File.WriteAllText(System.IO.Path.Combine(canonical.Path, "tokensLimits.secrets.json"), "canonical-secrets");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.settings.json"), "host-settings");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.secrets.json"), "host-secrets");

        var storage = TokensLimitsSettings.ResolveStorage(canonical.Path, host.Path);

        Assert.Equal("canonical-settings", File.ReadAllText(storage.SettingsPath));
        Assert.Equal("canonical-secrets", File.ReadAllText(storage.SecretsPath));
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"TokensLimitsExtension.SettingsTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
