using System;
using System.Diagnostics;
using System.Timers;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

public sealed partial class TokensLimitsPage : ListPage, IDisposable
{
    private readonly ICodexUsageProvider _usageService;
    private readonly Action<string> _logger;
    private readonly Timer _refreshTimer;
    private bool _disposed;

    public TokensLimitsPage(CodexUsageService usageService, Action<string>? logger = null)
        : this((ICodexUsageProvider)usageService, logger)
    {
    }

    public TokensLimitsPage(ICodexUsageProvider usageService, Action<string>? logger = null)
    {
        _usageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _logger = logger ?? LogMessage;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Tokens Limits";
        Name = "Show Codex usage limits";
        PlaceholderText = "Codex limits";
        ShowDetails = true;

        _refreshTimer = new Timer(TimeSpan.FromMinutes(1));
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();
    }

    public override IListItem[] GetItems()
    {
        if (_disposed)
        {
            return [];
        }

        try
        {
            IsLoading = true;
            var snapshot = _usageService.GetSnapshotAsync().GetAwaiter().GetResult();
            IsLoading = false;
            return CreateItems(snapshot);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            _logger($"[TokensLimits] ERROR: {ex.Message}");
            return [new ListItem(new NoOpCommand())
            {
                Title = "Tokens Limits",
                Subtitle = $"Не удалось получить лимиты: {ex.Message}",
            }];
        }
    }

    private static IListItem[] CreateItems(CodexUsageSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var estimatePrefix = snapshot.IsEstimate ? "Оценка: " : string.Empty;
        var primary = new ListItem(new NoOpCommand())
        {
            Title = "5-часовой лимит",
            Subtitle = $"{estimatePrefix}{CodexUsageNormalizer.FormatRemainingPercent(snapshot.PrimaryUsedPercent)} · "
                + CodexUsageNormalizer.FormatTimeUntilReset(snapshot.PrimaryResetAt, now),
        };
        var secondary = new ListItem(new NoOpCommand())
        {
            Title = "Недельный лимит",
            Subtitle = $"{estimatePrefix}{CodexUsageNormalizer.FormatRemainingPercent(snapshot.SecondaryUsedPercent)} · "
                + CodexUsageNormalizer.FormatTimeUntilReset(snapshot.SecondaryResetAt, now),
        };

        return [primary, secondary];
    }

    private void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_disposed)
        {
            RaiseItemsChanged(2);
        }
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Dispose();
    }
}
