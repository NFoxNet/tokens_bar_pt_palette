using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public interface ICodexUsageProvider
{
    Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
