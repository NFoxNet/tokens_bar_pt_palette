namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Ordered manifest of supported provider descriptors.
/// Add a descriptor here when a new provider is introduced; the runtime registry
/// decides which instantiated providers are available in the current process.
/// </summary>
public static class UsageProviderDescriptorRegistry
{
    public static UsageProviderDescriptor Codex { get; } = new("codex", "Codex");

    public static IReadOnlyList<UsageProviderDescriptor> All { get; } =
    [
        Codex,
    ];
}
