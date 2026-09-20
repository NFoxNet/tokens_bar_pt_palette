namespace TokensLimitsExtension.Core.Services;

public sealed class UsageProviderConfigurationChangedEventArgs(IReadOnlySet<string> providerIds) : EventArgs
{
    public IReadOnlySet<string> ProviderIds { get; } = providerIds;
}

/// <summary>Raised only when provider credentials or connection settings change.</summary>
public interface IUsageProviderConfigurationChangeSource
{
    event EventHandler<UsageProviderConfigurationChangedEventArgs>? ProviderConfigurationChanged;
}
