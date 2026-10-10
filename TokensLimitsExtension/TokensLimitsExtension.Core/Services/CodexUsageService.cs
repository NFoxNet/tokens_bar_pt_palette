using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public sealed class CodexUsageService : ICodexUsageProvider, IDisposable
{
    private readonly ICodexAuthTokenProvider _authTokenProvider;
    private readonly ICodexUsageClient _usageClient;
    private readonly ICodexUsageFallback _fallback;
    private readonly Action<string>? _logger;
    private readonly IDisposable? _ownedResource;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _disposed;

    public CodexUsageService(
        ICodexAuthTokenProvider authTokenProvider,
        ICodexUsageClient usageClient,
        ICodexUsageFallback fallback,
        Action<string>? logger = null,
        IDisposable? ownedResource = null)
    {
        _authTokenProvider = authTokenProvider ?? throw new ArgumentNullException(nameof(authTokenProvider));
        _usageClient = usageClient ?? throw new ArgumentNullException(nameof(usageClient));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger;
        _ownedResource = ownedResource;
    }

    public async Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await GetSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<CodexUsageSnapshot> GetSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await _authTokenProvider.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var accountId = (_authTokenProvider as ICodexAccountIdentityProvider)?.AccountId;
            var accountScope = string.IsNullOrWhiteSpace(accountId)
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)));
            var snapshot = await _usageClient.FetchUsageAsync(accessToken, cancellationToken).ConfigureAwait(false);
            _logger?.Invoke(string.Format(
                CultureInfo.InvariantCulture,
                "[TokensLimits] Snapshot fetched: primary={0}% secondary={1}%",
                snapshot.PrimaryUsedPercent,
                snapshot.SecondaryUsedPercent));
            return snapshot with { AccountScope = accountScope };
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
                return (await _fallback.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)) with { AccountScope = null };
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        (_authTokenProvider as IDisposable)?.Dispose();
        (_usageClient as IDisposable)?.Dispose();
        (_fallback as IDisposable)?.Dispose();
        _ownedResource?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
