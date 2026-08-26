using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

public interface IUsageProvider
{
    UsageProviderDescriptor Descriptor { get; }

    Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default);
}
