using System.Globalization;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public sealed class CodexUsageService : ICodexUsageProvider
{
    private readonly ICodexAuthTokenProvider _authTokenProvider;
    private readonly ICodexUsageClient _usageClient;
    private readonly ICodexUsageFallback _fallback;
    private readonly Action<string>? _logger;

    public CodexUsageService(
        ICodexAuthTokenProvider authTokenProvider,
        ICodexUsageClient usageClient,
        ICodexUsageFallback fallback,
        Action<string>? logger = null)
    {
        _authTokenProvider = authTokenProvider ?? throw new ArgumentNullException(nameof(authTokenProvider));
        _usageClient = usageClient ?? throw new ArgumentNullException(nameof(usageClient));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger;
    }

    public async Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var accessToken = await _authTokenProvider.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await _usageClient.FetchUsageAsync(accessToken, cancellationToken).ConfigureAwait(false);
            _logger?.Invoke(string.Format(
                CultureInfo.InvariantCulture,
                "[TokensLimits] Snapshot fetched: primary={0}% secondary={1}%",
                snapshot.PrimaryUsedPercent,
                snapshot.SecondaryUsedPercent));
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[TokensLimits] Fallback triggered: {ex.Message}");
            try
            {
                return await _fallback.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallbackException)
            {
                _logger?.Invoke($"[TokensLimits] ERROR: {fallbackException.Message}");
                throw new InvalidOperationException(
                    "Unable to obtain Codex usage from the API or local session logs.",
                    fallbackException);
            }
        }
    }
}
