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
    private readonly TokensLimitsPage[] _pages;
    private readonly UsageSnapshotCache[] _snapshotCaches;
    private readonly UsageDockBandItem[] _dockBandItems;
    private readonly UsageOverviewPage _overviewPage;
    private readonly IDisposable? _ownedUsageService;
    private readonly HttpClient? _ownedProviderHttpClient;
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
        _pages = _snapshotCaches
            .Select(provider => new TokensLimitsPage(provider, LogMessage, _settings))
            .ToArray();
        _overviewPage = new UsageOverviewPage(_snapshotCaches, _pages, LogMessage, _settings);
        _commands =
        [
            new CommandItem(_overviewPage)
            {
                Title = "Провайдеры",
                Subtitle = "Лимиты и расход включённых провайдеров",
            },
        ];
        var dockBands = providers
            .Zip(_snapshotCaches, (provider, cache) => (provider, cache))
            .Zip(_pages, (pair, page) => CreateDockBand(pair.cache, LogMessage, page, _settings))
            .ToArray();
        _dockBandItems = dockBands.Select(pair => pair.Item).ToArray();
        _dockBands = dockBands.Select(pair => pair.Band).ToArray();
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[] GetDockBands()
    {
        return _dockBands;
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var dockBandItem in _dockBandItems)
        {
            dockBandItem.Dispose();
        }

        _overviewPage.Dispose();

        foreach (var page in _pages)
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
