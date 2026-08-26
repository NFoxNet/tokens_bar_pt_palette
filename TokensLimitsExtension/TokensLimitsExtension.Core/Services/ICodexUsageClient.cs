using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public interface ICodexUsageClient
{
    Task<CodexUsageSnapshot> FetchUsageAsync(string accessToken, CancellationToken cancellationToken);
}
