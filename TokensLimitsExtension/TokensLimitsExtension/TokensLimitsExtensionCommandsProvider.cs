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
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

public partial class TokensLimitsExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public TokensLimitsExtensionCommandsProvider(ICodexUsageProvider? usageService = null)
    {
        DisplayName = "Tokens Limits";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        usageService ??= CreateDefaultService();
        _commands = [
            new CommandItem(new TokensLimitsPage(usageService)) { Title = DisplayName },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    private static CodexUsageService CreateDefaultService()
    {
        var codexHome = (Environment.GetEnvironmentVariable("CODEX_HOME") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        codexHome ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var logger = LogMessage;
        return new CodexUsageService(
            new CodexFileAuthTokenProvider(Path.Combine(codexHome, "auth.json")),
            new CodexUsageClient(logger: logger),
            new CodexLocalSessionFallback(codexHome),
            logger);
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }

}
