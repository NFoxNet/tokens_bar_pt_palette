namespace TokensLimitsExtension.Core.Services;

public interface ICodexAuthTokenProvider
{
    Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken);
}
