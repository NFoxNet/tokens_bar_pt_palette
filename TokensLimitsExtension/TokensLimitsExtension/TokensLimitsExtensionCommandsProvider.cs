// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TokensLimitsExtension;

public partial class TokensLimitsExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public TokensLimitsExtensionCommandsProvider()
    {
        DisplayName = "TokensLimitsExtension";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        _commands = [
            new CommandItem(new TokensLimitsExtensionPage()) { Title = DisplayName },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

}
