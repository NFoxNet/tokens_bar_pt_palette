using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Core.Providers.Codex;

/// <summary>
/// Adapts the existing Codex service to the provider-neutral monitoring contract.
/// The Codex API, authentication and fallback behavior remain in CodexUsageService.
/// </summary>
public sealed class CodexUsageProviderAdapter : IUsageProvider
{
    private const int PrimaryWindowSeconds = 5 * 60 * 60;
    private const int SecondaryWindowSeconds = 7 * 24 * 60 * 60;
    private readonly ICodexUsageProvider _codexProvider;

    public CodexUsageProviderAdapter(ICodexUsageProvider codexProvider)
    {
        _codexProvider = codexProvider ?? throw new ArgumentNullException(nameof(codexProvider));
    }

    public UsageProviderDescriptor Descriptor => UsageProviderDescriptorRegistry.Codex;

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _codexProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            snapshot.HasPrimaryWindow
                ? new UsageWindow(
                    snapshot.PrimaryUsedPercent,
                    snapshot.PrimaryResetAt,
                    PrimaryWindowSeconds)
                : null,
            snapshot.HasSecondaryWindow
                ? new UsageWindow(
                    snapshot.SecondaryUsedPercent,
                    snapshot.SecondaryResetAt,
                    SecondaryWindowSeconds)
                : null,
            snapshot.Plan,
            snapshot.IsEstimate)
        {
            AdditionalRateLimits = snapshot.AdditionalRateLimits
                .Select(limit => new AdditionalUsageLimit(
                    limit.Name,
                    limit.PrimaryWindow is null
                        ? null
                        : new UsageWindow(
                            limit.PrimaryWindow.UsedPercent,
                            limit.PrimaryWindow.ResetAt,
                            limit.PrimaryWindow.LimitWindowSeconds),
                    limit.SecondaryWindow is null
                        ? null
                        : new UsageWindow(
                            limit.SecondaryWindow.UsedPercent,
                            limit.SecondaryWindow.ResetAt,
                            limit.SecondaryWindow.LimitWindowSeconds)))
                .ToArray(),
        };
    }

}
