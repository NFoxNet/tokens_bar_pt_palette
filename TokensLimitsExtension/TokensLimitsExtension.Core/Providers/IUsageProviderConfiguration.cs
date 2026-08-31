namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Runtime view of provider settings. The core providers do not depend on the
/// Command Palette settings UI, which keeps them testable and reusable.
/// </summary>
public interface IUsageProviderConfiguration
{
    bool IsEnabled(string providerId);

    string? GetValue(string providerId, string key);
}
