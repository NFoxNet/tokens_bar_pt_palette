// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using System.Threading;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Providers;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension;

public partial class TokensLimitsExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;
    private readonly TokensLimitsSettings _settings;
    private readonly UsageProviderRegistry _providerRegistry;
    private readonly bool _ownsProviderRegistry;
    private TokensLimitsPage[] _pages = [];
    private TokensLimitsPage[] _dockPages = [];
    private readonly UsageSnapshotCache[] _snapshotCaches;
    private readonly UsageRefreshCoordinator _refreshCoordinator;
    private UsageDockBandItem[] _dockBandItems = [];
    private readonly Dictionary<string, TokensLimitsPage> _pagesByProviderId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TokensLimitsPage> _dockPagesByProviderId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageDockBandItem> _dockBandItemsByProviderId = new(StringComparer.OrdinalIgnoreCase);
    private readonly UsageOverviewPage _overviewPage;
    private readonly TokensLimitsDockBandPage _dockBandPage;
    private readonly HttpClient? _ownedProviderHttpClient;
    private readonly bool _settingsDrivenProviders;
    private readonly object _rebuildGate = new();
    private readonly object _surfaceGate = new();
    private string[] _enabledProviderIds = [];
    private string[] _coordinatorProviderIds = [];
    private bool _rebuildInProgress;
    private bool _rebuildRequested;
    private int _disposed;

    public TokensLimitsExtensionCommandsProvider(
        ICodexUsageProvider? usageService = null,
        UsageProviderRegistry? providerRegistry = null)
        : this(usageService, providerRegistry, null)
    {
    }

    internal TokensLimitsExtensionCommandsProvider(
        ICodexUsageProvider? usageService,
        UsageProviderRegistry? providerRegistry,
        TokensLimitsSettings? settings,
        bool settingsDrivenProviders = false)
    {
        DisplayName = "Tokens Limits";
        Id = "com.tokenslimits.extension";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        _settings = settings ?? new TokensLimitsSettings();
        Settings = _settings.Settings;
        _settingsDrivenProviders = providerRegistry is null || settingsDrivenProviders;
        _ownsProviderRegistry = providerRegistry is null;
        _ownedProviderHttpClient = providerRegistry is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
            : null;
        if (providerRegistry is not null)
        {
            _providerRegistry = providerRegistry;
        }
        else
        {
            var ownsUsageService = usageService is null;
            usageService ??= CreateDefaultService();
            _providerRegistry = UsageProviderRegistryFactory.CreateDefault(
                usageService,
                _settings,
                _ownedProviderHttpClient!,
                ownsUsageService,
                LogMessage);
        }
        var providers = _providerRegistry.Providers;
        _snapshotCaches = providers
            .Select(provider => new UsageSnapshotCache(provider, _settings))
            .ToArray();
        _refreshCoordinator = new UsageRefreshCoordinator(_settings);
        _overviewPage = new UsageOverviewPage([], [], LogMessage, _settings, _settings.Localization, _refreshCoordinator);
        _dockBandPage = new TokensLimitsDockBandPage(_settings.Localization);
        _dockBands =
        [
            new CommandItem(_dockBandPage)
            {
                Title = _settings.Localization.GetString("app.title", "Tokens Limits"),
                Subtitle = _settings.Localization.GetString("dock.show", "Show enabled provider limits in Dock"),
                Icon = _dockBandPage.Icon,
            },
        ];
        _commands =
        [
            new CommandItem(_overviewPage)
            {
                Title = _settings.Localization.GetString("overview.providers", "Enabled providers"),
                Subtitle = _settings.Localization.GetString("overview.providers", "Enabled providers"),
            },
        ];
        _settings.Changed += SettingsOnChanged;
        RebuildEnabledSurfaces();
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[] GetDockBands()
    {
        lock (_surfaceGate)
        {
            return _dockBands.ToArray();
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settings.Changed -= SettingsOnChanged;
        UsageDockBandItem[] dockBandItems;
        TokensLimitsPage[] pages;
        lock (_rebuildGate)
        {
            lock (_surfaceGate)
            {
                dockBandItems = [.. _dockBandItemsByProviderId.Values];
                pages = [.. _pagesByProviderId.Values, .. _dockPagesByProviderId.Values];
                _dockBandItemsByProviderId.Clear();
                _pagesByProviderId.Clear();
                _dockPagesByProviderId.Clear();
                _dockBandItems = [];
                _pages = [];
                _dockPages = [];
            }
        }

        foreach (var dockBandItem in dockBandItems)
        {
            dockBandItem.Dispose();
        }

        _dockBandPage.Dispose();
        _overviewPage.Dispose();

        _refreshCoordinator.Dispose();

        foreach (var page in pages)
        {
            page.Dispose();
        }

        foreach (var cache in _snapshotCaches)
        {
            cache.Dispose();
        }

        _settings.Dispose();
        if (_ownsProviderRegistry)
        {
            _providerRegistry.Dispose();
        }

        _ownedProviderHttpClient?.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    private static UsageDockBandItem CreateDockItem(
        UsageSnapshotCache provider,
        Action<string> logger,
        ICommand detailsCommand,
        IUsageRefreshSettings refreshSettings,
        ILocalizationService localization,
        UsageRefreshCoordinator refreshCoordinator)
    {
        return new UsageDockBandItem(provider, logger, detailsCommand, refreshSettings, localization, refreshCoordinator);
    }

    private void SettingsOnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        RebuildEnabledSurfaces();
    }

    private void RebuildEnabledSurfaces()
    {
        lock (_rebuildGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_rebuildInProgress)
            {
                _rebuildRequested = true;
                var enabledCaches = GetEnabledCaches();
                var enabledIds = enabledCaches.Select(cache => cache.Descriptor.Id).ToArray();
                DeactivateDisabledSurfaces(enabledIds);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                enabledCaches = GetEnabledCaches();
                enabledIds = enabledCaches.Select(cache => cache.Descriptor.Id).ToArray();
                if (!_coordinatorProviderIds.SequenceEqual(enabledIds, StringComparer.OrdinalIgnoreCase))
                {
                    _coordinatorProviderIds = enabledIds;
                    _refreshCoordinator.UpdateProviders(enabledCaches);
                }

                return;
            }

            _rebuildInProgress = true;
            try
            {
                do
                {
                    _rebuildRequested = false;
                    RebuildEnabledSurfacesCore();
                }
                while (_rebuildRequested && Volatile.Read(ref _disposed) == 0);
            }
            finally
            {
                _rebuildInProgress = false;
            }
        }
    }

    private void RebuildEnabledSurfacesCore()
    {
        var enabledCaches = GetEnabledCaches();
        var enabledIds = enabledCaches.Select(cache => cache.Descriptor.Id).ToArray();
        bool hasSameComposition;
        lock (_surfaceGate)
        {
            hasSameComposition = _enabledProviderIds.SequenceEqual(enabledIds, StringComparer.OrdinalIgnoreCase)
                && _pages.All(page => page.IsActive)
                && _dockPages.All(page => page.IsActive)
                && _dockBandItems.All(item => item.IsActive);
        }

        if (hasSameComposition)
        {
            // Settings such as credentials and language do not change membership.
            _refreshCoordinator.RefreshAll();
            return;
        }

        // Make retained references inert before cancellation callbacks or other
        // surfaces can observe a provider that settings have just disabled.
        DeactivateDisabledSurfaces(enabledIds);
        if (IsRebuildSuperseded())
        {
            return;
        }

        // Update membership first so cached surfaces for disabled providers cannot
        // start new requests while the visible arrays are being rebuilt.
        if (!_coordinatorProviderIds.SequenceEqual(enabledIds, StringComparer.OrdinalIgnoreCase))
        {
            _coordinatorProviderIds = enabledIds;
            _refreshCoordinator.UpdateProviders(enabledCaches);
            if (IsRebuildSuperseded())
            {
                return;
            }
        }
        else
        {
            _refreshCoordinator.RefreshAll();
            if (IsRebuildSuperseded())
            {
                return;
            }
        }

        TokensLimitsPage[] pages;
        TokensLimitsPage[] dockPages;
        UsageDockBandItem[] dockBandItems;
        TokensLimitsPage[] inactivePages;
        TokensLimitsPage[] inactiveDockPages;
        UsageDockBandItem[] inactiveDockBandItems;
        lock (_surfaceGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _pages = enabledCaches
                .Select(cache => GetOrCreatePage(cache))
                .ToArray();
            _dockPages = enabledCaches
                .Select(cache => GetOrCreateDockPage(cache))
                .ToArray();
            _dockBandItems = enabledCaches
                .Select(cache => GetOrCreateDockBandItem(cache))
                .ToArray();
            _enabledProviderIds = enabledIds;
            pages = _pages;
            dockPages = _dockPages;
            dockBandItems = _dockBandItems;
            var enabledIdSet = enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            inactivePages = _pagesByProviderId
                .Where(pair => !enabledIdSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
            inactiveDockPages = _dockPagesByProviderId
                .Where(pair => !enabledIdSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
            inactiveDockBandItems = _dockBandItemsByProviderId
                .Where(pair => !enabledIdSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
        }

        foreach (var page in inactivePages)
        {
            if (IsRebuildSuperseded())
            {
                return;
            }

            page.SetActive(false);
        }

        foreach (var page in inactiveDockPages)
        {
            if (IsRebuildSuperseded())
            {
                return;
            }

            page.SetActive(false);
        }

        foreach (var item in inactiveDockBandItems)
        {
            if (IsRebuildSuperseded())
            {
                return;
            }

            item.SetActive(false);
        }

        foreach (var page in pages)
        {
            if (IsRebuildSuperseded())
            {
                return;
            }

            page.SetActive(true);
        }

        foreach (var page in dockPages)
        {
            if (IsRebuildSuperseded())
            {
                return;
            }

            page.SetActive(true);
        }

        foreach (var item in dockBandItems)
        {
            if (IsRebuildSuperseded())
            {
                return;
            }

            item.SetActive(true);
        }

        if (IsRebuildSuperseded())
        {
            return;
        }

        _overviewPage.UpdateProviders(enabledCaches, pages);
        if (IsRebuildSuperseded())
        {
            return;
        }

        _dockBandPage.UpdateItems(dockBandItems);
    }

    private bool IsRebuildSuperseded()
        => Volatile.Read(ref _disposed) != 0 || _rebuildRequested;

    private UsageSnapshotCache[] GetEnabledCaches()
        => _snapshotCaches
            .Where(cache => !_settingsDrivenProviders || _settings.IsEnabled(cache.Descriptor.Id))
            .ToArray();

    private void DeactivateDisabledSurfaces(IReadOnlyCollection<string> enabledProviderIds)
    {
        var enabledIdSet = enabledProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TokensLimitsPage[] inactivePages;
        TokensLimitsPage[] inactiveDockPages;
        UsageDockBandItem[] inactiveDockBandItems;
        lock (_surfaceGate)
        {
            inactivePages = _pagesByProviderId
                .Where(pair => !enabledIdSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
            inactiveDockPages = _dockPagesByProviderId
                .Where(pair => !enabledIdSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
            inactiveDockBandItems = _dockBandItemsByProviderId
                .Where(pair => !enabledIdSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
        }

        foreach (var page in inactivePages)
        {
            page.SetActive(false);
        }

        foreach (var page in inactiveDockPages)
        {
            page.SetActive(false);
        }

        foreach (var item in inactiveDockBandItems)
        {
            item.SetActive(false);
        }
    }

    private TokensLimitsPage GetOrCreatePage(UsageSnapshotCache cache)
    {
        var providerId = cache.Descriptor.Id;
        if (!_pagesByProviderId.TryGetValue(providerId, out var page))
        {
            page = new TokensLimitsPage(cache, LogMessage, _settings, localization: _settings.Localization, coordinator: _refreshCoordinator);
            _pagesByProviderId.Add(providerId, page);
        }

        return page;
    }

    private TokensLimitsPage GetOrCreateDockPage(UsageSnapshotCache cache)
    {
        var providerId = cache.Descriptor.Id;
        if (!_dockPagesByProviderId.TryGetValue(providerId, out var page))
        {
            page = new TokensLimitsPage(cache, LogMessage, _settings, idSuffix: "dock", localization: _settings.Localization, coordinator: _refreshCoordinator);
            _dockPagesByProviderId.Add(providerId, page);
        }

        return page;
    }

    private UsageDockBandItem GetOrCreateDockBandItem(UsageSnapshotCache cache)
    {
        var providerId = cache.Descriptor.Id;
        if (!_dockBandItemsByProviderId.TryGetValue(providerId, out var item))
        {
            item = CreateDockItem(
                cache,
                LogMessage,
                _dockPagesByProviderId[providerId],
                _settings,
                _settings.Localization,
                _refreshCoordinator);
            _dockBandItemsByProviderId.Add(providerId, item);
        }

        return item;
    }

    private static CodexUsageService CreateDefaultService()
    {
        var codexHome = (Environment.GetEnvironmentVariable("CODEX_HOME") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        codexHome ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var logger = LogMessage;
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        var auth = new CodexFileAuthTokenProvider(Path.Combine(codexHome, "auth.json"), httpClient);
        return new CodexUsageService(
            auth,
            new CodexUsageClient(httpClient, logger, () => auth.AccountId),
            new CodexLocalSessionFallback(codexHome, logger: logger),
            logger,
            httpClient);
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }

}
