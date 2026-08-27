using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Providers.Codex;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Providers;

public static class UsageProviderRegistryFactory
{
    public static UsageProviderRegistry CreateDefault(
        ICodexUsageProvider codexProvider,
        IUsageProviderConfiguration configuration,
        HttpClient httpClient,
        bool ownsCodexProvider = true,
        Action<string>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(codexProvider);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(httpClient);

        var providers = new List<IUsageProvider>
        {
            // Constructing adapters is side-effect free. Network requests begin
            // only when a provider is enabled and its page/Dock surface exists.
            new CodexUsageProviderAdapter(codexProvider, ownsCodexProvider),
        };

        foreach (var descriptor in UsageProviderDescriptorRegistry.All.Skip(1))
        {
            providers.Add(new ConfiguredUsageProvider(descriptor, configuration, httpClient, logger));
        }

        return new UsageProviderRegistry(providers);
    }
}
