using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Core.Providers.Codex;

/// <summary>
/// Adapts the existing Codex service to the provider-neutral monitoring contract.
/// The Codex API, authentication and fallback behavior remain in CodexUsageService.
/// </summary>
public sealed class CodexUsageProviderAdapter : IUsageProvider, IDisposable
{
    private const int PrimaryWindowSeconds = 5 * 60 * 60;
    private const int SecondaryWindowSeconds = 7 * 24 * 60 * 60;
    private readonly ICodexUsageProvider _codexProvider;
    private readonly bool _ownsCodexProvider;
    private int _disposed;

    public CodexUsageProviderAdapter(
        ICodexUsageProvider codexProvider,
        bool ownsCodexProvider = false)
    {
        _codexProvider = codexProvider ?? throw new ArgumentNullException(nameof(codexProvider));
        _ownsCodexProvider = ownsCodexProvider;
    }

    public UsageProviderDescriptor Descriptor => UsageProviderDescriptorRegistry.Codex;

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsCodexProvider)
        {
            (_codexProvider as IDisposable)?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

}
