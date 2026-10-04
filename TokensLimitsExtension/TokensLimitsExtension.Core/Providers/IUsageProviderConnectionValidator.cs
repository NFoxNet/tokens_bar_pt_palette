using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

public interface IUsageProviderConnectionValidator
{
    bool SupportsConnectionValidation { get; }

    Task<UsageSnapshot> ValidateConnectionAsync(CancellationToken cancellationToken = default);
}
