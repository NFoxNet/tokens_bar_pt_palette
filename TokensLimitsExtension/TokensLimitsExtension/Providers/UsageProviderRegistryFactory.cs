using System;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Providers.Codex;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Providers;

public static class UsageProviderRegistryFactory
{
    public static UsageProviderRegistry CreateDefault(ICodexUsageProvider codexProvider)
    {
        ArgumentNullException.ThrowIfNull(codexProvider);
        return new UsageProviderRegistry([new CodexUsageProviderAdapter(codexProvider)]);
    }
}
