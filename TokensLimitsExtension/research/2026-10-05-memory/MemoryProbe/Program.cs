using System.Diagnostics;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

const int Warmups = 1;
const int Repeats = 5;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
var utcNow = DateTimeOffset.Parse("2026-10-05T00:00:00Z", CultureInfo.InvariantCulture);
Console.WriteLine($"runtime={Environment.Version}; os={Environment.OSVersion}; arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; warmups={Warmups}; repeats={Repeats}");

await MeasureCodexCacheAsync(utcNow);
await MeasureCodexClientJsonAsync();
await MeasureGenericJsonAsync(utcNow);
await MeasureOverviewAndDetailsAsync(5, utcNow);
await MeasureOverviewAndDetailsAsync(15, utcNow);

static async Task MeasureCodexCacheAsync(DateTimeOffset now)
{
    var home = Path.Combine(Path.GetTempPath(), "TokensLimitsMemoryProbe", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(home, "sessions"));
    var sessionPath = Path.Combine(home, "sessions", "synthetic.jsonl");
    try
    {
        await using (var writer = new StreamWriter(sessionPath, false, Encoding.UTF8))
        {
            for (var i = 0; i < 10_000; i++)
            {
                var time = now.AddDays(-10).AddSeconds(i);
                await writer.WriteLineAsync(TokenEventJson(time, 1));
            }

            for (var i = 0; i < 100; i++)
            {
                var time = now.AddMinutes(-i);
                await writer.WriteLineAsync(TokenEventJson(time, 2));
            }
        }

        var bytes = new FileInfo(sessionPath).Length;
        var observedReadBytes = 0L;
        CodexUsageSnapshot? result = null;
        using var fallback = new CodexLocalSessionFallback(
            home,
            timeProvider: new FixedTimeProvider(now),
            readBytesObserver: read => Interlocked.Add(ref observedReadBytes, read));
        var cold = await MeasureAsync(async () => result = await fallback.GetSnapshotAsync(CancellationToken.None), warmups: 0, repeats: 1);
        var coldReadBytes = Interlocked.Exchange(ref observedReadBytes, 0);
        var cachedEventsAfterCold = GetCachedCodexEventCount(fallback);
        var hot = await MeasureAsync(async () => result = await fallback.GetSnapshotAsync(CancellationToken.None));
        var hotReadBytes = Interlocked.Read(ref observedReadBytes);
        var cachedEventsAfterWarm = GetCachedCodexEventCount(fallback);
        var fiveHour = result!.Metrics.Single(metric => metric.SemanticKey == "tokens5h").Value;
        var weekly = result.Metrics.Single(metric => metric.SemanticKey == "tokens7d").Value;
        if (fiveHour != "200" || weekly != "200" || cachedEventsAfterCold != cachedEventsAfterWarm)
            throw new InvalidOperationException($"Unexpected synthetic Codex fallback result: 5h={fiveHour}, 7d={weekly}, cached={cachedEventsAfterCold}.");
        Console.WriteLine($"codex-cache fileBytes={bytes} events=10000old+100recent coldAllocBytes={cold.Mean:N0} coldMs={cold.ElapsedMs:N2} coldReadBytes={coldReadBytes} cachedEventsAfterCold={cachedEventsAfterCold} warmAllocBytes={hot.Mean:N0} warmMs={hot.ElapsedMs:N2} warmReadBytes={hotReadBytes} cachedEventsAfterWarm={cachedEventsAfterWarm} parsedTokens5h={fiveHour} parsedTokens7d={weekly}");

        foreach (var size in new[] { 10 * 1024, 100 * 1024, 1023 * 1024 })
        {
            var sizedHome = Path.Combine(home, $"size-{size}");
            Directory.CreateDirectory(sizedHome);
            var sizedPath = Path.Combine(sizedHome, $"codex-{size}.jsonl");
            await WriteCodexFileOfSizeAsync(sizedPath, size, now);
            var measure = await MeasureAsync(async () =>
            {
                using var sizedFallback = new CodexLocalSessionFallback(
                    codexHome: sizedHome,
                    timeProvider: new FixedTimeProvider(now));
                result = await sizedFallback.GetSnapshotAsync(CancellationToken.None);
            });
            var actualBytes = new FileInfo(sizedPath).Length;
            var parsed = result!.Metrics.Single(metric => metric.SemanticKey == "tokens5h").Value;
            Console.WriteLine($"codex-jsonl targetBytes={size} actualBytes={actualBytes} allocBytes={measure.Mean:N0} elapsedMs={measure.ElapsedMs:N2} parsedTokens5h={parsed}");
        }
    }
    finally
    {
        Directory.Delete(home, recursive: true);
    }
}

static async Task MeasureCodexClientJsonAsync()
{
    const int expectedRequests = Warmups + Repeats;
    foreach (var size in new[] { 10 * 1024, 100 * 1024, 1023 * 1024 })
    {
        const string prefix = "{\"plan_type\":\"synthetic\",\"rate_limit\":{\"primary_window\":{\"used_percent\":2,\"reset_at\":1790000000,\"limit_window_seconds\":18000}},\"padding\":\"";
        const string suffix = "\"}";
        var json = PadJson(prefix, suffix, size);
        var handler = new StubHandler(json);
        using var client = new CodexUsageClient(
            handler,
            options: new CodexUsageClientOptions(maxAttempts: 1, maxResponseBodyBytes: 1_048_576));
        CodexUsageSnapshot? snapshot = null;
        var measure = await MeasureAsync(async () => snapshot = await client.FetchUsageAsync("synthetic-probe-token", CancellationToken.None));
        var expectedReset = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        if (!snapshot!.HasPrimaryWindow || snapshot.HasSecondaryWindow || snapshot.PrimaryUsedPercent != 2
            || snapshot.PrimaryResetAt != expectedReset || snapshot.PrimaryWindowSeconds != 18_000
            || handler.RequestCount != expectedRequests)
            throw new InvalidOperationException($"Unexpected Codex usage response: primary={snapshot.PrimaryUsedPercent}, secondary={snapshot.HasSecondaryWindow}, reset={snapshot.PrimaryResetAt:O}, seconds={snapshot.PrimaryWindowSeconds}, requests={handler.RequestCount}.");
        Console.WriteLine($"codex-client-json targetBytes={size} bodyBytes={Encoding.UTF8.GetByteCount(json)} allocBytes={measure.Mean:N0} elapsedMs={measure.ElapsedMs:N2} requests={handler.RequestCount} parsedPrimaryUsed={snapshot.PrimaryUsedPercent} parsedPrimaryReset={snapshot.PrimaryResetAt.ToUnixTimeSeconds()} parsedPrimarySeconds={snapshot.PrimaryWindowSeconds} parsedSecondary={snapshot.HasSecondaryWindow}");
    }
}

static async Task MeasureGenericJsonAsync(DateTimeOffset now)
{
    foreach (var size in new[] { 10 * 1024, 100 * 1024, 1023 * 1024 })
    {
        var reset = now.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var prefix = "{\"data\":{\"five_hour\":{\"used_percent\":15,\"reset_at\":\"" + reset
            + "\",\"window_seconds\":18000},\"weekly\":{\"remaining_percent\":72,\"reset_at\":\"" + reset
            + "\",\"window_seconds\":604800},\"input_tokens\":1200,\"plan\":\"synthetic\",\"padding\":\"";
        const string suffix = "\"}}";
        var json = PadJson(prefix, suffix, size);
        var handler = new StubHandler(json);
        using var client = new HttpClient(handler);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "openrouter"),
            new ProbeConfiguration(),
            client,
            logger: null,
            requestTimeout: TimeSpan.FromSeconds(15),
            maxResponseBodyBytes: 1_048_576);
        UsageSnapshot? snapshot = null;
        var measure = await MeasureAsync(async () => snapshot = await provider.GetUsageSnapshotAsync());
        var firstMetric = snapshot!.Metrics.Count == 0 ? null : snapshot.Metrics[0];
        if (snapshot.PrimaryWindow?.UsedPercent != 15 || snapshot.SecondaryWindow?.UsedPercent != 28
            || handler.RequestCount != 2 * (Warmups + Repeats))
            throw new InvalidOperationException($"Unexpected generic API result: primary={snapshot.PrimaryWindow?.UsedPercent}, weekly={snapshot.SecondaryWindow?.UsedPercent}, requests={handler.RequestCount}.");
        Console.WriteLine($"generic-json targetBytes={size} bodyBytes={Encoding.UTF8.GetByteCount(json)} allocBytes={measure.Mean:N0} elapsedMs={measure.ElapsedMs:N2} requests={handler.RequestCount} parsedPrimaryUsed={snapshot.PrimaryWindow?.UsedPercent} parsedWeeklyUsed={snapshot.SecondaryWindow?.UsedPercent} parsedMetric={firstMetric?.Name}:{firstMetric?.Value}");
    }
}

static async Task MeasureOverviewAndDetailsAsync(int providerCount, DateTimeOffset now)
{
    var caches = new List<UsageSnapshotCache>(providerCount);
    var pages = new List<TokensLimitsPage>(providerCount);
    try
    {
        for (var i = 0; i < providerCount; i++)
        {
            var provider = new ImmediateProvider(i, now);
            var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider(now));
            caches.Add(cache);
            pages.Add(new TokensLimitsPage(cache));
        }

        var overview = new UsageOverviewPage(caches, pages);
        try
        {
            var initialArray = overview.GetItems();
            var detailsUnopened = pages[0];
            var itemsField = typeof(TokensLimitsPage).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var unmaterializedRows = ((IListItem[])itemsField.GetValue(detailsUnopened)!).Length;
            var load = await MeasureAsync(async () => await Task.WhenAll(caches.Select(cache => cache.RefreshAsync(force: true))));
            var overviewLoaded = overview.GetItems();
            var unopenedAfterRefreshRows = ((IListItem[])itemsField.GetValue(detailsUnopened)!).Length;
            var openedPage = pages[0];
            var openedInitialRows = openedPage.GetItems().Length;
            var openedReload = await MeasureAsync(async () => await caches[0].RefreshAsync(force: true));
            var openedRepeatedRows = openedPage.GetItems().Length;
            var before = overview.GetItems();
            var detailsBefore = before.Select(item => (item as ListItem)?.Details).ToArray();
            await caches[0].RefreshAsync(force: true);
            var after = overview.GetItems();
            var preserved = Math.Max(0, Math.Min(detailsBefore.Length, after.Length) - 1);
            var stable = Enumerable.Range(0, Math.Min(detailsBefore.Length, after.Length))
                .Skip(1)
                .Count(i => ReferenceEquals(detailsBefore[i], (after[i] as ListItem)?.Details));
            Console.WriteLine($"ui providers={providerCount} initialRows={initialArray.Length} overviewRows={overviewLoaded.Length} providerRefreshFanoutAllocBytes={load.Mean:N0} providerRefreshFanoutElapsedMs={load.ElapsedMs:N2} detailsUnopenedInitialRows={unmaterializedRows} detailsUnopenedAfterRefreshRows={unopenedAfterRefreshRows} detailsOpenedInitialRows={openedInitialRows} oneProviderFanoutRefreshAllocBytes={openedReload.Mean:N0} detailsOpenedRepeatRows={openedRepeatedRows} detailsReferencesStableAfterOneProviderRefresh={stable}/{preserved}");
        }
        finally
        {
            overview.Dispose();
        }
    }
    finally
    {
        foreach (var page in pages) page.Dispose();
        foreach (var cache in caches) cache.Dispose();
    }
}

static string TokenEventJson(DateTimeOffset at, int delta)
    => "{\"timestamp\":\"" + at.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":"
        + delta.ToString(CultureInfo.InvariantCulture) + "}}}}";

static async Task WriteCodexFileOfSizeAsync(string path, int targetBytes, DateTimeOffset now)
{
    var lines = new List<string> { TokenEventJson(now.AddMinutes(-1), 3) };
    var bytes = Encoding.UTF8.GetByteCount(lines[0]) + 1;
    const string prefix = "{\"type\":\"noise\",\"padding\":\"";
    const string suffix = "\"}";
    while (targetBytes - bytes > 240_100)
    {
        var padding = new string('x', 240_000);
        var line = prefix + padding + suffix;
        lines.Add(line);
        bytes += Encoding.UTF8.GetByteCount(line) + 1;
    }

    var finalPaddingLength = Math.Max(0, targetBytes - bytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix) - 1);
    var finalLine = prefix + new string('z', finalPaddingLength) + suffix;
    lines.Add(finalLine);
    await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
}

static string PadJson(string prefix, string suffix, int targetBytes)
{
    var paddingLength = targetBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
    if (paddingLength < 0) throw new InvalidOperationException("Target JSON size is too small.");
    return prefix + new string('x', paddingLength) + suffix;
}

static int GetCachedCodexEventCount(CodexLocalSessionFallback fallback)
{
    var cache = typeof(CodexLocalSessionFallback)
        .GetField("_fileCache", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(fallback)!;
    var values = (IEnumerable)cache.GetType().GetProperty("Values")!.GetValue(cache)!;
    var cachedFile = values.Cast<object>().Single();
    var events = cachedFile.GetType().GetProperty("Events")!.GetValue(cachedFile)!;
    return (int)events.GetType().GetProperty("Count")!.GetValue(events)!;
}

static async Task<Measurement> MeasureAsync(Func<Task> operation, int warmups = Warmups, int repeats = Repeats)
{
    for (var i = 0; i < warmups; i++) await operation();
    var samples = new long[repeats];
    long stopwatchTicks = 0;
    for (var i = 0; i < repeats; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true);
        var operationTimer = Stopwatch.StartNew();
        await operation();
        operationTimer.Stop();
        stopwatchTicks += operationTimer.ElapsedTicks;
        samples[i] = GC.GetTotalAllocatedBytes(true) - before;
    }

    return new Measurement(samples.Average(), TimeSpan.FromSeconds((double)stopwatchTicks / Stopwatch.Frequency).TotalMilliseconds / repeats);
}

sealed record Measurement(double Mean, double ElapsedMs);

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class ProbeConfiguration : IUsageProviderConfiguration
{
    public bool IsEnabled(string providerId) => true;

    public string? GetValue(string providerId, string key)
        => key == "apiKey" ? "synthetic-probe-key" : null;
}

sealed class StubHandler(string body) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

sealed class ImmediateProvider(int index, DateTimeOffset now) : IUsageProvider
{
    public UsageProviderDescriptor Descriptor { get; } = new($"probe-{index:D2}", $"Synthetic Provider {index:D2}");

    public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            new UsageWindow(15, now.AddHours(2), 5 * 60 * 60),
            new UsageWindow(30, now.AddDays(3), 7 * 24 * 60 * 60),
            "Synthetic",
            false)
        {
            Metrics = [new UsageMetric("Synthetic tokens", "1200", "tokens", SemanticKey: "tokens5h")],
            Source = "synthetic",
        });
}
