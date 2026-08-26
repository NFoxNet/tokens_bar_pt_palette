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
    private ICommandItem[] _dockBands = [];
    private readonly TokensLimitsSettings _settings;
    private readonly UsageProviderRegistry _providerRegistry;
    private readonly bool _ownsProviderRegistry;
    private TokensLimitsPage[] _pages = [];
    private readonly UsageSnapshotCache[] _snapshotCaches;
    private UsageDockBandItem[] _dockBandItems = [];
    private readonly UsageOverviewPage _overviewPage;
    private readonly IDisposable? _ownedUsageService;
    private readonly HttpClient? _ownedProviderHttpClient;
    private readonly bool _settingsDrivenProviders;
    private readonly object _surfaceGate = new();
    private string[] _enabledProviderIds = [];
    private int _disposed;

    public TokensLimitsExtensionCommandsProvider(
        ICodexUsageProvider? usageService = null,
        UsageProviderRegistry? providerRegistry = null)
    {
        DisplayName = "Tokens Limits";
        Id = "com.tokenslimits.extension";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        _settings = new TokensLimitsSettings();
        Settings = _settings.Settings;
        var ownsUsageService = usageService is null;
        usageService ??= CreateDefaultService();
        _ownedUsageService = ownsUsageService ? usageService as IDisposable : null;

        _settingsDrivenProviders = providerRegistry is null;
        _ownsProviderRegistry = providerRegistry is null;
        _ownedProviderHttpClient = providerRegistry is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
            : null;
        _providerRegistry = providerRegistry ?? UsageProviderRegistryFactory.CreateDefault(
            usageService,
            _settings,
            _ownedProviderHttpClient!,
            LogMessage);
        var providers = _providerRegistry.Providers;
        _snapshotCaches = providers
            .Select(provider => new UsageSnapshotCache(provider, _settings))
            .ToArray();
        _overviewPage = new UsageOverviewPage([], [], LogMessage, _settings);
        _commands =
        [
            new CommandItem(_overviewPage)
            {
                Title = "Провайдеры",
                Subtitle = "Лимиты и расход включённых провайдеров",
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
            dockBandItems = _dockBandItems;
            pages = _pages;
            _dockBandItems = [];
            _pages = [];
            _dockBands = [];
        }

        foreach (var dockBandItem in dockBandItems)
        {
            dockBandItem.Dispose();
        }

        _overviewPage.Dispose();

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

        _ownedUsageService?.Dispose();
        _ownedProviderHttpClient?.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    private static (UsageDockBandItem Item, ICommandItem Band) CreateDockBand(
        UsageSnapshotCache provider,
        Action<string> logger,
        ICommand detailsCommand,
        IUsageRefreshSettings refreshSettings)
    {
        var item = new UsageDockBandItem(provider, logger, detailsCommand, refreshSettings);
        var wrappedBand = new WrappedDockItem(
            [item],
            $"com.tokenslimits.provider.{provider.Descriptor.Id}.band",
            provider.Descriptor.DisplayName)
        {
            Icon = item.Icon,
        };

        return (item, wrappedBand);
    }

    private void SettingsOnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            RebuildEnabledSurfaces();
        }
    }

    private void RebuildEnabledSurfaces()
    {
        var enabledCaches = _snapshotCaches
            .Where(cache => !_settingsDrivenProviders || _settings.IsEnabled(cache.Descriptor.Id))
            .ToArray();
        var enabledIds = enabledCaches.Select(cache => cache.Descriptor.Id).ToArray();
        UsageDockBandItem[] oldDockItems;
        TokensLimitsPage[] oldPages;
        lock (_surfaceGate)
        {
            if (_enabledProviderIds.SequenceEqual(enabledIds, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            oldDockItems = _dockBandItems;
            oldPages = _pages;
            _pages = enabledCaches
                .Select(cache => new TokensLimitsPage(cache, LogMessage, _settings))
                .ToArray();
            var dockBands = enabledCaches
                .Select(cache => CreateDockBand(cache, LogMessage, _overviewPage, _settings))
                .ToArray();
            _dockBandItems = dockBands.Select(pair => pair.Item).ToArray();
            _dockBands = dockBands.Select(pair => pair.Band).ToArray();
            _enabledProviderIds = enabledIds;
            _overviewPage.UpdateProviders(enabledCaches, _pages);
        }

        foreach (var item in oldDockItems)
        {
            item.Dispose();
        }

        foreach (var page in oldPages)
        {
            page.Dispose();
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
