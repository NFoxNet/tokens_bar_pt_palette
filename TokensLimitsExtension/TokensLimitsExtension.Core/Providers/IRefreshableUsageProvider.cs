using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// A usage provider that supports explicit cache invalidation and exposes its
/// last known snapshot without starting another request.
/// </summary>
public interface IRefreshableUsageProvider : IUsageProvider
{
    bool TryGetSnapshot(out UsageSnapshot snapshot);

    void Invalidate();
}
