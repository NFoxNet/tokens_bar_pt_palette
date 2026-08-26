// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Providers.Codex;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

public partial class TokensLimitsExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;
    private readonly TokensLimitsPage _limitsPage;
    private readonly UsageDockBandItem[] _dockBandItems;
    private int _disposed;

    public TokensLimitsExtensionCommandsProvider(
        ICodexUsageProvider? usageService = null,
        UsageProviderRegistry? providerRegistry = null)
    {
        DisplayName = "Tokens Limits";
        Id = "com.tokenslimits.extension";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        usageService ??= CreateDefaultService();
        _limitsPage = new TokensLimitsPage(usageService, LogMessage);
        _commands = [
            new CommandItem(_limitsPage) { Title = DisplayName },
        ];

        providerRegistry ??= CreateDefaultProviderRegistry(usageService);
        var providers = providerRegistry.Providers;
        _dockBandItems = providers
            .Select(provider => new UsageDockBandItem(provider, LogMessage))
            .ToArray();
        _dockBands = providers
            .Zip(_dockBandItems)
            .Select(pair => (ICommandItem)new WrappedDockItem(
                [pair.Second],
                $"com.tokenslimits.provider.{pair.First.Descriptor.Id}.band",
                pair.First.Descriptor.DisplayName)
            {
                Icon = pair.Second.Icon,
            })
            .ToArray();
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

        _limitsPage.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    private static UsageProviderRegistry CreateDefaultProviderRegistry(ICodexUsageProvider usageService)
        => new([new CodexUsageProviderAdapter(usageService)]);

    private static CodexUsageService CreateDefaultService()
    {
        var codexHome = (Environment.GetEnvironmentVariable("CODEX_HOME") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        codexHome ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var logger = LogMessage;
        var auth = new CodexFileAuthTokenProvider(Path.Combine(codexHome, "auth.json"));
        return new CodexUsageService(
            auth,
            new CodexUsageClient(logger: logger, accountIdProvider: () => auth.AccountId),
            new CodexLocalSessionFallback(codexHome, logger: logger),
            logger);
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }

}
