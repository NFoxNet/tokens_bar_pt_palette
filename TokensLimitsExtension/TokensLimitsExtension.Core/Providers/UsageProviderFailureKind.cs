namespace TokensLimitsExtension.Core.Providers;

/// <summary>Transport or response category preserved across provider error wrappers.</summary>
public enum UsageProviderFailureKind
{
    Unknown,
    Authentication,
    RateLimited,
    Timeout,
    Network,
    UnsupportedResponse,
}
