using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

public interface IUsageProviderConnectionValidator
{
    Task<UsageSnapshot> ValidateConnectionAsync(CancellationToken cancellationToken = default);
}
