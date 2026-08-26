namespace TokensLimitsExtension.Core.Services;

public interface IUsageRefreshSettings
{
    TimeSpan RefreshInterval { get; }

    event EventHandler? Changed;
}
