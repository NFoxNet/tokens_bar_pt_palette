using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Globalization;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Localization;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class NativeUsageUxTests
{
    [Fact]
    public async Task OverviewRefreshContextCommandForcesFreshSnapshot()
    {
        var provider = new CountingProvider();
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var details = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [details]);
        var providerRow = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));

        var refresh = Assert.IsType<CommandContextItem>(
            Assert.Single(providerRow.MoreCommands!, command => ContextTitle(command) == "Refresh"));
        Assert.IsType<AnonymousCommand>(refresh.Command).Invoke();
        await provider.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task DockRefreshContextCommandForcesFreshSnapshotAndRejectsHttpDashboard()
    {
        var provider = new CountingProvider();
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var dockItem = new UsageDockBandItem(cache);

        var commands = dockItem.MoreCommands!;
        var refresh = Assert.IsType<CommandContextItem>(
            Assert.Single(commands, command => ContextTitle(command) == "Refresh"));
        Assert.DoesNotContain(commands, command => ContextTitle(command) == "Open provider dashboard");
        Assert.IsType<AnonymousCommand>(refresh.Command).Invoke();
        await provider.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task DetailsAlwaysShowSnapshotFetchTime()
    {
        var fetchedAt = DateTimeOffset.UtcNow;
        using var page = new TokensLimitsPage(new SnapshotProvider(fetchedAt));

        await page.RefreshAsync();

        var updated = Assert.Single(page.GetItems(), item => item.Title == "Last updated");
        Assert.Equal(fetchedAt.ToLocalTime().ToString("g", CultureInfo.InvariantCulture), updated.Subtitle);
    }

    [Fact]
    public async Task DetailsRefreshRowCommandForcesFreshSnapshot()
    {
        var provider = new CountingProvider();
        using var cache = new UsageSnapshotCache(provider);
        using var page = new TokensLimitsPage(cache);
        await page.RefreshAsync();
        var refresh = Assert.IsType<AnonymousCommand>(
            Assert.Single(page.GetItems(), item => item.Title == "Refresh").Command);

        refresh.Invoke();
        await provider.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task OverviewDetailsShowPlanSafeSourceAndLatestTimestamp()
    {
        var provider = new SnapshotProvider(
            DateTimeOffset.UtcNow,
            source: "https://private-user:private-password@provider.example/usage?access_token=test-token",
            plan: "pro");
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var details = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [details]);

        var providerRow = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));
        var body = Assert.IsType<Details>(providerRow.Details).Body;

        Assert.Contains("pro", body, StringComparison.Ordinal);
        Assert.Contains("https://provider.example/usage", body, StringComparison.Ordinal);
        var publishedAt = cache.State.Snapshot!.FetchedAt!.Value;
        Assert.Contains(publishedAt.ToLocalTime().ToString("g", CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", UsageProviderErrorKind.MissingConfiguration, "Configuration required")]
    [InlineData("en", UsageProviderErrorKind.Authentication, "Authentication required")]
    [InlineData("en", UsageProviderErrorKind.RateLimited, "Rate limited")]
    [InlineData("en", UsageProviderErrorKind.Timeout, "Request timed out")]
    [InlineData("en", UsageProviderErrorKind.Network, "Network error")]
    [InlineData("en", UsageProviderErrorKind.UnsupportedResponse, "Unsupported response")]
    [InlineData("en", UsageProviderErrorKind.Unknown, "Unknown error")]
    [InlineData("ru", UsageProviderErrorKind.MissingConfiguration, "Нужно настроить провайдера")]
    [InlineData("ru", UsageProviderErrorKind.Authentication, "Нужна авторизация")]
    [InlineData("ru", UsageProviderErrorKind.RateLimited, "Ограничение частоты запросов")]
    [InlineData("ru", UsageProviderErrorKind.Timeout, "Истекло время ожидания")]
    [InlineData("ru", UsageProviderErrorKind.Network, "Ошибка сети")]
    [InlineData("ru", UsageProviderErrorKind.UnsupportedResponse, "Ответ не поддерживается")]
    [InlineData("ru", UsageProviderErrorKind.Unknown, "Неизвестная ошибка")]
    public async Task OverviewShowsDistinctLocalizedProviderError(
        string language,
        UsageProviderErrorKind errorKind,
        string expectedStatus)
    {
        using var userDirectory = new TestDirectory();
        var localization = new JsonLocalizationService(
            Path.Combine(AppContext.BaseDirectory, "lang"),
            userDirectory.Path,
            language);
        using var cache = new UsageSnapshotCache(new FailureProvider(errorKind));
        await cache.RefreshAsync(force: true);
        using var details = new TokensLimitsPage(cache, localization: localization);
        using var overview = new UsageOverviewPage([cache], [details], localization: localization);

        var providerRow = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));
        Assert.Contains(expectedStatus, providerRow.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverviewActionsExposeOnlyHttpsDashboardAndSafeDiagnostics()
    {
        var provider = new SnapshotProvider(
            DateTimeOffset.UtcNow,
            dashboardUrl: "https://provider.example/dashboard");
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var details = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [details]);
        var providerRow = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));
        var actions = providerRow.MoreCommands!;

        Assert.Contains(actions, command => ContextTitle(command) == "Open provider dashboard");
        var diagnostics = Assert.IsType<CopyTextCommand>(Assert.IsType<CommandContextItem>(
            Assert.Single(actions, command => ContextTitle(command) == "Copy safe diagnostics")).Command);
        Assert.Contains("provider=ux-test", diagnostics.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", diagnostics.Text, StringComparison.Ordinal);
    }

    private sealed class CountingProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new(
            "ux-test",
            "UX Test",
            dashboardUrl: "http://provider.example/dashboard");

        public TaskCompletionSource SecondCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 2) SecondCallStarted.TrySetResult();
            return Task.FromResult(CreateSnapshot(DateTimeOffset.UtcNow));
        }
    }

    private sealed class SnapshotProvider(
        DateTimeOffset fetchedAt,
        string? dashboardUrl = null,
        string? source = null,
        string? plan = null) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("ux-test", "UX Test", dashboardUrl: dashboardUrl);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSnapshot(fetchedAt, plan, source));
    }

    private sealed class FailureProvider(UsageProviderErrorKind errorKind) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("ux-test", "UX Test");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => errorKind switch
            {
                UsageProviderErrorKind.MissingConfiguration => Task.FromException<UsageSnapshot>(new UsageProviderConfigurationException("safe failure")),
                UsageProviderErrorKind.Authentication => Task.FromException<UsageSnapshot>(new UsageProviderRequestException("safe failure", failureKind: UsageProviderFailureKind.Authentication)),
                UsageProviderErrorKind.RateLimited => Task.FromException<UsageSnapshot>(new UsageProviderRequestException("safe failure", retryAfter: TimeSpan.FromSeconds(30), failureKind: UsageProviderFailureKind.RateLimited)),
                UsageProviderErrorKind.Timeout => Task.FromException<UsageSnapshot>(new TimeoutException("safe failure")),
                UsageProviderErrorKind.Network => Task.FromException<UsageSnapshot>(new HttpRequestException("safe failure")),
                UsageProviderErrorKind.UnsupportedResponse => Task.FromException<UsageSnapshot>(new InvalidDataException("safe failure")),
                _ => Task.FromException<UsageSnapshot>(new InvalidOperationException("safe failure")),
            };
    }

    private static UsageSnapshot CreateSnapshot(DateTimeOffset fetchedAt, string? plan = null, string? source = null)
        => new UsageSnapshot(
            "ux-test",
            "UX Test",
            new UsageWindow(35, fetchedAt.AddHours(4), 5 * 60 * 60),
            null,
            plan,
            false) with { FetchedAt = fetchedAt, Source = source };

    private static string ContextTitle(IContextItem item) => ((CommandContextItem)item).Title;

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TokensLimitsExtension.Ux.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
