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

    [Fact]
    public async Task EmptyRefreshingProviderShowsBusyStateAcrossNativeSurfaces()
    {
        using var userDirectory = new TestDirectory();
        var localization = new JsonLocalizationService(
            Path.Combine(AppContext.BaseDirectory, "lang"),
            userDirectory.Path,
            "en");
        var provider = new BlockingProvider();
        using var cache = new UsageSnapshotCache(provider);
        using var details = new TokensLimitsPage(cache, localization: localization);
        using var overview = new UsageOverviewPage([cache], [details], localization: localization);
        using var dock = new UsageDockBandItem(cache, localization: localization);
        var refresh = cache.RefreshAsync(force: true);
        await provider.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(localization.GetString("overview.loading"), Assert.IsType<ListItem>(Assert.Single(overview.GetItems())).Subtitle);
        Assert.Equal(localization.GetString("details.loading"), Assert.IsType<ListItem>(Assert.Single(details.GetItems())).Subtitle);
        Assert.Equal(localization.GetString("status.refreshingSubtitle"), dock.Subtitle);

        provider.Complete();
        await refresh;
    }

    [Theory]
    [InlineData("en", UsageProviderErrorKind.None)]
    [InlineData("ru", UsageProviderErrorKind.None)]
    [InlineData("en", UsageProviderErrorKind.Network)]
    [InlineData("ru", UsageProviderErrorKind.Network)]
    public void DockKeepsSnapshotSubtitleStableDuringRefresh(string language, UsageProviderErrorKind errorKind)
    {
        using var userDirectory = new TestDirectory();
        var localization = new JsonLocalizationService(
            Path.Combine(AppContext.BaseDirectory, "lang"), userDirectory.Path, language);
        var now = DateTimeOffset.UtcNow;
        var state = new UsageProviderState(CreateSnapshot(now), now, now, false, errorKind);
        var provider = new StateSourceProvider(state);
        using var dock = new UsageDockBandItem(provider, localization: localization);
        using var band = new TokensLimitsDockBandPage(localization);
        band.UpdateItems([dock]);
        var subtitle = dock.Subtitle;
        Assert.Contains("65%", subtitle, StringComparison.Ordinal);
        if (errorKind == UsageProviderErrorKind.Network)
        {
            Assert.Contains(localization.GetString("status.dock.network"), subtitle, StringComparison.Ordinal);
        }
        var itemsChanged = 0;
        band.ItemsChanged += (_, _) => itemsChanged++;

        provider.Publish(state with { IsRefreshing = true });

        Assert.Equal(subtitle, dock.Subtitle);
        Assert.Equal(subtitle, dock.DockSubtitle);
        Assert.Equal(0, itemsChanged);

        provider.Publish(state);

        Assert.Equal(subtitle, dock.Subtitle);
        Assert.Equal(0, itemsChanged);

        provider.Publish(state with
        {
            Snapshot = state.Snapshot! with { PrimaryWindow = new UsageWindow(45, now.AddHours(4), 5 * 60 * 60) },
            ErrorKind = UsageProviderErrorKind.None,
        });

        Assert.Contains("55%", dock.Subtitle, StringComparison.Ordinal);
        Assert.Equal(dock.Subtitle, dock.DockSubtitle);
        Assert.Equal(1, itemsChanged);
    }

    [Fact]
    public async Task OverviewDetailsStripQueryAndFragmentFromRelativeSources()
    {
        using var cache = new UsageSnapshotCache(new SnapshotProvider(
            DateTimeOffset.UtcNow,
            source: "usage.json?api_key=secret#session"));
        await cache.RefreshAsync(force: true);
        using var details = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [details]);
        var body = Assert.IsType<Details>(Assert.IsType<ListItem>(Assert.Single(overview.GetItems())).Details).Body;

        Assert.Contains("usage.json", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("session", body, StringComparison.Ordinal);
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

    [Fact]
    public async Task AzureConnectionValidationIsExplicitAndInvokableFromOverview()
    {
        var provider = new ValidatableProvider();
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        Assert.Equal(0, provider.ValidationCallCount);
        using var page = new TokensLimitsPage(cache);
        await page.RefreshAsync();
        using var overview = new UsageOverviewPage([cache], [page]);
        var row = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));
        var validation = Assert.IsType<CommandContextItem>(
            Assert.Single(row.MoreCommands!, command => ContextTitle(command) == "Validate connection"));
        var detailValidation = Assert.IsType<AnonymousCommand>(
            Assert.Single(page.GetItems(), item => item.Title == "Validate connection").Command);

        Assert.Equal("Send one deployment request to check connection; may consume quota.", validation.Subtitle);
        Assert.IsType<AnonymousCommand>(validation.Command).Invoke();
        await provider.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, provider.ValidationCallCount);
        Assert.IsType<AnonymousCommand>(detailValidation);

    }

    [Fact]
    public async Task RetainedValidationActionsDoNothingAfterProviderIsRemoved()
    {
        var provider = new ValidatableProvider();
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var coordinator = new UsageRefreshCoordinator(new TestRefreshSettings(TimeSpan.FromHours(1)));
        coordinator.UpdateProviders([cache]);
        await provider.RegularRefreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var page = new TokensLimitsPage(cache);
        await page.RefreshAsync();
        using var overview = new UsageOverviewPage([cache], [page], coordinator: coordinator);
        var overviewItem = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));
        var validation = Assert.IsType<AnonymousCommand>(Assert.IsType<CommandContextItem>(
            Assert.Single(overviewItem.MoreCommands!, command => ContextTitle(command) == "Validate connection")).Command);
        var detailValidation = Assert.IsType<AnonymousCommand>(
            Assert.Single(page.GetItems(), item => item.Title == "Validate connection").Command);

        coordinator.UpdateProviders([]);
        page.SetActive(false);
        validation.Invoke();
        detailValidation.Invoke();
        Assert.Equal(0, provider.ValidationCallCount);
    }

    [Fact]
    public async Task ProviderWithoutValidationCapabilityHasNoValidationAction()
    {
        var provider = new CountingProvider();
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var page = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [page]);
        await page.RefreshAsync();
        var row = Assert.IsType<ListItem>(Assert.Single(overview.GetItems()));

        Assert.DoesNotContain(row.MoreCommands!, command => ContextTitle(command) == "Validate connection");
        Assert.DoesNotContain(page.GetItems(), item => item.Title == "Validate connection");
    }

    [Fact]
    public async Task DetailsShowStaleAndRefreshingTogetherWhileRetainingSnapshot()
    {
        var provider = new StateSourceProvider(new UsageProviderState(
            CreateSnapshot(DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            IsRefreshing: true,
            ErrorKind: UsageProviderErrorKind.Network));
        using var page = new TokensLimitsPage(provider);
        provider.Publish();

        var item = Assert.IsType<ListItem>(Assert.Single(page.GetItems(), candidate => candidate.Title == "Last updated"));
        var body = Assert.IsType<Details>(item.Details).Body;
        Assert.Contains("Stale", body, StringComparison.Ordinal);
        Assert.Contains("Refreshing", body, StringComparison.Ordinal);
        Assert.Contains("Network error", body, StringComparison.Ordinal);

    }

    [Fact]
    public void EmptyOverviewUpdatesItsLocalizedContentWhenLanguageChanges()
    {
        using var userDirectory = new TestDirectory();
        var localization = new JsonLocalizationService(
            Path.Combine(AppContext.BaseDirectory, "lang"),
            userDirectory.Path,
            "en");
        using var overview = new UsageOverviewPage([], [], localization: localization);
        Assert.Equal("No providers enabled", Assert.IsType<ListItem>(Assert.Single(overview.GetItems())).Title);

        localization.ApplyPreference("ru");

        Assert.Equal("Нет включённых провайдеров", Assert.IsType<ListItem>(Assert.Single(overview.GetItems())).Title);
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

    private sealed class ValidatableProvider : IUsageProvider, IUsageProviderConnectionValidator
    {
        private int _validationCallCount;

        public UsageProviderDescriptor Descriptor { get; } = new("azure-openai", "Azure OpenAI");
        public bool SupportsConnectionValidation => true;
        public TaskCompletionSource ValidationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RegularRefreshCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ValidationCallCount => Volatile.Read(ref _validationCallCount);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            RegularRefreshCompleted.TrySetResult();
            return Task.FromResult(CreateSnapshot(DateTimeOffset.UtcNow));
        }

        public Task<UsageSnapshot> ValidateConnectionAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _validationCallCount);
            ValidationStarted.TrySetResult();
            return Task.FromResult(CreateSnapshot(DateTimeOffset.UtcNow) with
            {
                Metrics = [new UsageMetric("connectionStatus", "validated")],
            });
        }
    }

    private sealed class BlockingProvider : IUsageProvider
    {
        private readonly TaskCompletionSource<UsageSnapshot> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UsageProviderDescriptor Descriptor { get; } = new("ux-test", "UX Test");
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult();
            return _result.Task;
        }

        public void Complete() => _result.TrySetResult(CreateSnapshot(DateTimeOffset.UtcNow));
    }

    private sealed class StateSourceProvider(UsageProviderState state) : IUsageProviderStateSource
    {
        public UsageProviderDescriptor Descriptor { get; } = new("ux-test", "UX Test");
        public UsageProviderState State { get; private set; } = state;
        public event EventHandler? StateChanged;

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(State.Snapshot!);
        public bool TryGetSnapshot(out UsageSnapshot snapshot) { snapshot = State.Snapshot!; return true; }
        public void Invalidate() { }
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Publish(UsageProviderState? state = null)
        {
            if (state is not null) State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TestRefreshSettings(TimeSpan refreshInterval) : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval { get; } = refreshInterval;
        public event EventHandler? Changed { add { } remove { } }
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
