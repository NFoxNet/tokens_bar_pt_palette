namespace TokensLimitsExtension.Core.Services;

public interface ICodexAuthTokenProvider
{
    Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken);
}

public interface ICodexAccountIdentityProvider
{
    string? AccountId { get; }
}
