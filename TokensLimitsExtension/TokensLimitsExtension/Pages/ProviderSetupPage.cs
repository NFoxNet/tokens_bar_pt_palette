using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension;

public sealed partial class ProviderSetupPage : ListPage, IDisposable
{
    private readonly TokensLimitsSettings _settings;
    private readonly UsageRefreshCoordinator _coordinator;
    private readonly object _gate = new();
    private IReadOnlyList<UsageSnapshotCache> _caches;
    private readonly Dictionary<string, TokensLimitsPage> _pagesByProvider = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public ProviderSetupPage(
        TokensLimitsSettings settings,
        IReadOnlyList<UsageSnapshotCache> caches,
        UsageRefreshCoordinator coordinator)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Id = "com.tokenslimits.setup";
        Title = _settings.Localization.GetString("setup.title", "Provider setup");
        Name = _settings.Localization.GetString("setup.name", "Connect providers");
        PlaceholderText = Name;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        ShowDetails = true;
        _settings.Changed += SettingsOnChanged;
        _settings.Localization.LanguageChanged += LocalizationOnLanguageChanged;
        Subscribe(caches);
    }

    public void UpdateProviders(
        IReadOnlyList<UsageSnapshotCache> caches,
        IReadOnlyList<TokensLimitsPage> pages)
    {
        ArgumentNullException.ThrowIfNull(caches);
        ArgumentNullException.ThrowIfNull(pages);
        if (caches.Count != pages.Count)
        {
            throw new ArgumentException("Provider caches and pages must have the same length.", nameof(pages));
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Unsubscribe(_caches);
            _caches = caches;
            _pagesByProvider.Clear();
            for (var index = 0; index < caches.Count; index++)
            {
                _pagesByProvider[caches[index].Descriptor.Id] = pages[index];
            }
            Subscribe(caches);
        }
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        if (Volatile.Read(ref _disposed) != 0) return [];
        var localization = _settings.Localization;
        UsageSnapshotCache[] caches;
        Dictionary<string, TokensLimitsPage> pagesByProvider;
        lock (_gate)
        {
            caches = _caches.ToArray();
            pagesByProvider = new Dictionary<string, TokensLimitsPage>(_pagesByProvider, StringComparer.OrdinalIgnoreCase);
        }
        var cacheById = caches.ToDictionary(cache => cache.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        return UsageProviderDescriptorRegistry.All
            .Select(descriptor => CreateItem(descriptor, cacheById.GetValueOrDefault(descriptor.Id), pagesByProvider, localization))
            .ToArray();
    }

    private ListItem CreateItem(
        UsageProviderDescriptor descriptor,
        UsageSnapshotCache? cache,
        IReadOnlyDictionary<string, TokensLimitsPage> pagesByProvider,
        ILocalizationService localization)
    {
        var enabled = _settings.IsEnabled(descriptor.Id);
        var state = cache?.State;
        var title = descriptor.DisplayName;
        var subtitle = GetStatus(descriptor, enabled, state, localization);
        return new ListItem(new NoOpCommand())
        {
            Title = title,
            Subtitle = subtitle,
            Details = new Details
            {
                Title = title,
                Body = CreateDetails(descriptor, localization),
            },
            MoreCommands = CreateActions(descriptor, cache, pagesByProvider, localization),
        };
    }

    private string GetStatus(
        UsageProviderDescriptor descriptor,
        bool enabled,
        UsageProviderState? state,
        ILocalizationService localization)
    {
        if (!enabled)
        {
            return localization.GetString("setup.status.disabled", "Disabled · open settings to enable");
        }

        if (HasMissingAuthentication(descriptor))
        {
            return localization.GetString("setup.status.authRequired", "Authentication required");
        }

        if (HasMissingConfiguration(descriptor))
        {
            return localization.GetString("setup.status.configurationRequired", "Setup incomplete");
        }

        if (state is { ErrorKind: UsageProviderErrorKind.MissingConfiguration or UsageProviderErrorKind.Authentication })
        {
            return localization.GetString("setup.status.authRequired", "Authentication required");
        }

        if (state is { ErrorKind: UsageProviderErrorKind.Network or UsageProviderErrorKind.Timeout or UsageProviderErrorKind.RateLimited })
        {
            return localization.GetString("setup.status.temporaryError", "Temporary connection error");
        }

        if (state is { ErrorKind: not UsageProviderErrorKind.None })
        {
            return localization.GetString("setup.status.unavailable", "Provider data unavailable");
        }

        if (state?.Snapshot is { } snapshot)
        {
            if (HasQuota(snapshot)) return localization.GetString("setup.status.ready", "Data received");
            if (descriptor.Id.Equals("azureopenai", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.Metrics.Any(metric => string.Equals(metric.SemanticKey, "connectionStatus", StringComparison.OrdinalIgnoreCase)
                    && metric.Value.Equals("validated", StringComparison.OrdinalIgnoreCase)))
                {
                    return FormatLocalized(localization, "setup.status.validatedNoQuota", "{0} · {1}",
                        localization.GetString("status.connection.validated", "Connection validated"),
                        localization.GetString("setup.status.quotaUnavailable", "Quota unavailable"));
                }

                return localization.GetString("setup.status.validationRequired", "Connection validation required");
            }
            if (HasData(snapshot)) return localization.GetString("setup.status.quotaUnavailable", "Connected · quota unavailable");
            return localization.GetString("setup.status.unavailable", "Provider data unavailable");
        }

        return localization.GetString("setup.status.waiting", "Configured · waiting for provider data");
    }

    private bool HasMissingAuthentication(UsageProviderDescriptor descriptor)
    {
        var alternatives = GetAuthenticationOptionKeys(descriptor);
        var required = GetRequiredAuthenticationFieldKeys(descriptor);
        return required.Any(key => string.IsNullOrWhiteSpace(_settings.GetValue(descriptor.Id, key)))
            || (alternatives.Length > 0
                && !alternatives.Any(key => !string.IsNullOrWhiteSpace(_settings.GetValue(descriptor.Id, key))));
    }

    private bool HasMissingConfiguration(UsageProviderDescriptor descriptor)
    {
        var authenticationKeys = GetRequiredAuthenticationFieldKeys(descriptor)
            .Concat(GetAuthenticationOptionKeys(descriptor))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetRequiredFieldKeys(descriptor)
            .Where(key => !authenticationKeys.Contains(key))
            .Any(key => string.IsNullOrWhiteSpace(_settings.GetValue(descriptor.Id, key)));
    }

    private static string CreateDetails(UsageProviderDescriptor descriptor, ILocalizationService localization)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(descriptor.SourceDescription))
        {
            sections.Add(descriptor.SourceDescription);
        }

        sections.Add(FormatLocalized(localization, "setup.authSource", "Authorization: {0}",
            localization.GetString(AuthLocalizationKey(descriptor), AuthFallback(descriptor))));

        var requiredKeys = GetRequiredFieldKeys(descriptor);
        var alternativeKeys = GetAuthenticationOptionKeys(descriptor);
        var fields = GetSetupFields(descriptor).Select(field =>
        {
            var label = localization.GetString($"settings.field.{field.Key}.label", field.Label);
            var description = localization.GetString($"settings.field.{field.Key}.description", field.Description);
            var required = requiredKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase);
            var alternative = alternativeKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase);
            var marker = required
                ? FormatLocalized(localization, "setup.field.required", "{0} (required)", label)
                : alternative
                    ? FormatLocalized(localization, "setup.field.authOption", "{0} (authentication option)", label)
                    : FormatLocalized(localization, "setup.field.optional", "{0} (optional)", label);
            return $"- **{marker}** — {description}";
        }).ToArray();

        if (requiredKeys.Length > 0)
        {
            sections.Add($"**{localization.GetString("setup.requiredFields", "Required fields")}:** {string.Join(", ", requiredKeys.Select(key => GetFieldLabel(descriptor, key, localization)))}");
        }

        if (alternativeKeys.Length > 0)
        {
            sections.Add($"**{localization.GetString("setup.requiredFields", "Required fields")}:** {FormatLocalized(localization, "setup.field.oneOf", "Provide one of: {0}", string.Join(", ", alternativeKeys.Select(key => GetFieldLabel(descriptor, key, localization))))}");
        }

        var requiredSettingKeys = requiredKeys.Concat(alternativeKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optionalFields = fields
            .Where((_, index) => !requiredSettingKeys.Contains(GetSetupFields(descriptor)[index].Key))
            .ToArray();
        if (optionalFields.Length > 0)
        {
            sections.Add($"**{localization.GetString("setup.optionalFields", "Optional fields")}:**{Environment.NewLine}{string.Join(Environment.NewLine, optionalFields)}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private IContextItem[] CreateActions(
        UsageProviderDescriptor descriptor,
        UsageSnapshotCache? cache,
        IReadOnlyDictionary<string, TokensLimitsPage> pagesByProvider,
        ILocalizationService localization)
    {
        var actions = new List<IContextItem>
        {
            new CommandContextItem(_settings.CommandSettings.SettingsPage)
            {
                Title = localization.GetString("setup.action.settings", "Open extension settings"),
                Subtitle = localization.GetString("setup.action.settingsSubtitle", "Configure provider credentials in extension settings."),
            },
        };

        if (cache is not null)
        {
            actions.Add(new CommandContextItem(new AnonymousCommand(() => _ = _coordinator.RefreshProviderAsync(cache, force: true)))
            {
                Title = localization.GetString("setup.action.refresh", "Refresh provider data"),
            });
            if (pagesByProvider.TryGetValue(descriptor.Id, out var page))
            {
                actions.Add(new CommandContextItem(page)
                {
                    Title = localization.GetString("setup.action.details", "Open provider details"),
                });
            }
        }

        if (Uri.TryCreate(descriptor.DashboardUrl, UriKind.Absolute, out var dashboard)
            && dashboard.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(new CommandContextItem(new OpenUrlCommand(dashboard.AbsoluteUri))
            {
                Title = localization.GetString("setup.action.dashboard", "Open provider dashboard"),
            });
        }

        return actions.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settings.Changed -= SettingsOnChanged;
        _settings.Localization.LanguageChanged -= LocalizationOnLanguageChanged;
        lock (_gate)
        {
            Unsubscribe(_caches);
            _pagesByProvider.Clear();
        }
        GC.SuppressFinalize(this);
    }

    private void Subscribe(IEnumerable<UsageSnapshotCache> caches)
    {
        foreach (var cache in caches) cache.StateChanged += CacheOnStateChanged;
    }

    private void Unsubscribe(IEnumerable<UsageSnapshotCache> caches)
    {
        foreach (var cache in caches) cache.StateChanged -= CacheOnStateChanged;
    }

    private void CacheOnStateChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) RaiseItemsChanged();
    }

    private void SettingsOnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0) RaiseItemsChanged();
    }

    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Title = _settings.Localization.GetString("setup.title", "Provider setup");
        Name = _settings.Localization.GetString("setup.name", "Connect providers");
        PlaceholderText = Name;
        RaiseItemsChanged();
    }

    private static bool HasQuota(UsageSnapshot snapshot)
        => snapshot.PrimaryWindow is not null
            || snapshot.SecondaryWindow is not null
            || snapshot.AdditionalRateLimits.Any(limit => limit.PrimaryWindow is not null || limit.SecondaryWindow is not null);

    private static bool HasData(UsageSnapshot snapshot)
        => snapshot.Metrics.Count > 0
            || !string.IsNullOrWhiteSpace(snapshot.Plan)
            || !string.IsNullOrWhiteSpace(snapshot.Source);

    private static string[] GetRequiredFieldKeys(UsageProviderDescriptor descriptor)
    {
        if (descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase)) return ["sessionBundle"];

        var required = descriptor.AuthKind switch
        {
            UsageProviderAuthKind.ApiKey => new[] { "apiKey" },
            UsageProviderAuthKind.Cookie => new[] { "cookieHeader" },
            UsageProviderAuthKind.ApiKeyOrCookie or UsageProviderAuthKind.OAuthOrCookie => [],
            UsageProviderAuthKind.OAuth => descriptor.Settings
                .Where(field => field.IsSecret)
                .Select(field => field.Key)
                .ToArray(),
            _ => [],
        };
        if (descriptor.Id.Equals("azureopenai", StringComparison.OrdinalIgnoreCase))
        {
            required = [.. required, "baseUrl", "deploymentName"];
        }
        else if (descriptor.Id.Equals("zed", StringComparison.OrdinalIgnoreCase))
        {
            required = [.. required, "userId"];
        }
        return required
            .Where(key => descriptor.Settings.Any(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetAuthenticationOptionKeys(UsageProviderDescriptor descriptor)
        => descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? descriptor.Settings.Where(field => field.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
                    || field.Key.Equals("adminApiKey", StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Key).ToArray()
            : descriptor.AuthKind switch
        {
            UsageProviderAuthKind.ApiKeyOrCookie => descriptor.Settings
                .Where(field => field.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
                    || field.Key.Equals("cookieHeader", StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Key)
                .ToArray(),
            UsageProviderAuthKind.OAuthOrCookie => descriptor.Settings
                .Where(field => field.IsSecret)
                .Select(field => field.Key)
                .ToArray(),
            _ => [],
        };

    private static string[] GetRequiredAuthenticationFieldKeys(UsageProviderDescriptor descriptor)
    {
        if (descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)) return [];
        if (descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase)) return ["sessionBundle"];
        return descriptor.AuthKind switch
        {
            UsageProviderAuthKind.ApiKey => ["apiKey"],
            UsageProviderAuthKind.Cookie => ["cookieHeader"],
            UsageProviderAuthKind.OAuth => descriptor.Settings.Where(field => field.IsSecret).Select(field => field.Key).ToArray(),
            _ => [],
        };
    }

    private static UsageProviderSettingDescriptor[] GetSetupFields(UsageProviderDescriptor descriptor)
        => descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase)
            ? descriptor.Settings.Where(field => field.Key.Equals("sessionBundle", StringComparison.OrdinalIgnoreCase)).ToArray()
            : descriptor.Settings.ToArray();

    private static string FormatLocalized(ILocalizationService localization, string key, string fallback, params object?[] arguments)
        => string.Format(localization.Culture, localization.GetString(key, fallback), arguments);

    private static string GetFieldLabel(UsageProviderDescriptor descriptor, string key, ILocalizationService localization)
    {
        var setting = descriptor.Settings.First(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return localization.GetString($"settings.field.{key}.label", setting.Label);
    }

    private static string AuthLocalizationKey(UsageProviderDescriptor descriptor)
    {
        if (descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase)) return "setup.auth.sessionBundle";
        if (descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)) return "setup.auth.apiKeyOrAdminApiKey";
        return descriptor.AuthKind switch
        {
            UsageProviderAuthKind.ApiKey => "setup.auth.apiKey",
            UsageProviderAuthKind.Cookie => "setup.auth.cookie",
            UsageProviderAuthKind.OAuth => "setup.auth.oauth",
            UsageProviderAuthKind.Local => "setup.auth.local",
            UsageProviderAuthKind.ApiKeyOrCookie => "setup.auth.apiKeyOrCookie",
            UsageProviderAuthKind.OAuthOrCookie => "setup.auth.oauthOrCookie",
            _ => "setup.auth.none",
        };
    }

    private static string AuthFallback(UsageProviderDescriptor descriptor) => descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase)
        ? "Windsurf session bundle"
        : descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI API key or Admin API key"
            : AuthFallback(descriptor.AuthKind);

    private static string AuthFallback(UsageProviderAuthKind kind) => kind switch
    {
        UsageProviderAuthKind.ApiKey => "API key",
        UsageProviderAuthKind.Cookie => "Cookie",
        UsageProviderAuthKind.OAuth => "Sign-in / OAuth",
        UsageProviderAuthKind.Local => "Local data",
        UsageProviderAuthKind.ApiKeyOrCookie => "API key or Cookie",
        UsageProviderAuthKind.OAuthOrCookie => "Sign-in or Cookie",
        _ => "No sign-in required",
    };
}
