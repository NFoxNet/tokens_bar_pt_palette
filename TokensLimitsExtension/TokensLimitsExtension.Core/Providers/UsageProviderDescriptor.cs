namespace TokensLimitsExtension.Core.Providers;

public sealed record UsageProviderDescriptor
{
    public UsageProviderDescriptor(string id, string displayName)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("Provider ID cannot be empty.", nameof(id))
            : id;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("Provider display name cannot be empty.", nameof(displayName))
            : displayName;
    }

    public string Id { get; }

    public string DisplayName { get; }
}
