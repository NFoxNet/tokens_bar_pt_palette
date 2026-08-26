namespace TokensLimitsExtension.Core.Providers;

public enum UsageProviderAuthKind
{
    None,
    ApiKey,
    Cookie,
    OAuth,
    Local,
    ApiKeyOrCookie,
    OAuthOrCookie,
}

public sealed record UsageProviderSettingDescriptor(
    string Key,
    string Label,
    string Description,
    bool IsSecret = false,
    string? EnvironmentVariable = null,
    string? DefaultValue = null);

public sealed record UsageProviderDescriptor
{
    public UsageProviderDescriptor(
        string id,
        string displayName,
        UsageProviderAuthKind authKind = UsageProviderAuthKind.None,
        string? dashboardUrl = null,
        bool defaultEnabled = false,
        IEnumerable<UsageProviderSettingDescriptor>? settings = null,
        string? sourceDescription = null)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("Provider ID cannot be empty.", nameof(id))
            : id;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("Provider display name cannot be empty.", nameof(displayName))
            : displayName;
        AuthKind = authKind;
        DashboardUrl = dashboardUrl;
        DefaultEnabled = defaultEnabled;
        Settings = (settings ?? []).ToArray();
        SourceDescription = sourceDescription;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public UsageProviderAuthKind AuthKind { get; }

    public string? DashboardUrl { get; }

    public bool DefaultEnabled { get; }

    public IReadOnlyList<UsageProviderSettingDescriptor> Settings { get; }

    public string? SourceDescription { get; }
}
