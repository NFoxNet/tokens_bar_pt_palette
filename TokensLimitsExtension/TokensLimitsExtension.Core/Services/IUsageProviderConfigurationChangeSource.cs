namespace TokensLimitsExtension.Core.Services;

/// <summary>Raised only when provider credentials or connection settings change.</summary>
public interface IUsageProviderConfigurationChangeSource
{
    event EventHandler? ProviderConfigurationChanged;
}
