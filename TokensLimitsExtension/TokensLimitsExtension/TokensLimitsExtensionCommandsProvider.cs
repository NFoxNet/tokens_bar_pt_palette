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
    private readonly List<UsageDockBandItem> _retiredDockBandItems = [];
    private readonly List<TokensLimitsPage> _retiredPages = [];
    private readonly UsageOverviewPage _overviewPage;
    private readonly TokensLimitsDockBandPage _dockBandPage;
    private readonly HttpClient? _ownedProviderHttpClient;
    private readonly bool _settingsDrivenProviders;
    private readonly object _surfaceGate = new();
    private string[] _enabledProviderIds = [];
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
        TokensLimitsSettings? settings)
    {
        DisplayName = "Tokens Limits";
        Id = "com.tokenslimits.extension";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        _settings = settings ?? new TokensLimitsSettings();
        Settings = _settings.Settings;
        _settingsDrivenProviders = providerRegistry is null;
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
        lock (_surfaceGate)
        {
            dockBandItems = [.. _dockBandItems, .. _retiredDockBandItems];
            pages = [.. _pages, .. _dockPages, .. _retiredPages];
            _dockBandItems = [];
            _pages = [];
            _dockPages = [];
            _retiredDockBandItems.Clear();
            _retiredPages.Clear();
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
        // A language or interval edit returns immediately from fresh caches. A provider
        // credential edit clears the affected shared state before this call.
        _refreshCoordinator.RefreshAll();
    }

    private void RebuildEnabledSurfaces()
    {
        var enabledCaches = _snapshotCaches
            .Where(cache => !_settingsDrivenProviders || _settings.IsEnabled(cache.Descriptor.Id))
            .ToArray();
        var enabledIds = enabledCaches.Select(cache => cache.Descriptor.Id).ToArray();
        UsageDockBandItem[] oldDockItems;
        TokensLimitsPage[] oldPages;
        TokensLimitsPage[] oldDockPages;
        lock (_surfaceGate)
        {
            if (_enabledProviderIds.SequenceEqual(enabledIds, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            oldDockItems = _dockBandItems;
            oldPages = _pages;
            oldDockPages = _dockPages;
            _pages = enabledCaches
                .Select(cache => new TokensLimitsPage(cache, LogMessage, _settings, localization: _settings.Localization, coordinator: _refreshCoordinator))
                .ToArray();
            _dockPages = enabledCaches
                .Select(cache => new TokensLimitsPage(cache, LogMessage, _settings, idSuffix: "dock", localization: _settings.Localization, coordinator: _refreshCoordinator))
                .ToArray();
            _dockBandItems = enabledCaches
                .Zip(
                    _dockPages,
                    (cache, page) => CreateDockItem(cache, LogMessage, page, _settings, _settings.Localization, _refreshCoordinator))
                .ToArray();
            _enabledProviderIds = enabledIds;
            _overviewPage.UpdateProviders(enabledCaches, _pages);
            _dockBandPage.UpdateItems(_dockBandItems);
            _retiredDockBandItems.AddRange(oldDockItems);
            _retiredPages.AddRange(oldPages);
            _retiredPages.AddRange(oldDockPages);
        }

        _refreshCoordinator.UpdateProviders(enabledCaches);

        foreach (var item in oldDockItems)
        {
            item.Deactivate();
        }

        foreach (var page in oldPages)
        {
            page.Deactivate();
        }

        foreach (var page in oldDockPages)
        {
            page.Deactivate();
        }

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
