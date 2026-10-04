# Synthetic memory baseline (v0.0.5.7)

Captured 2026-10-05 on base `7d8c029cb4c248dc816390e655f4069fd05339e3`, Windows 10.0.26200.0, .NET 10.0.12, x64, Release. This probe is a local managed measurement; it does not launch PowerToys, activate COM, or measure host-owned object retention. It uses temporary synthetic JSONL, fake synchronous providers, stub HTTP handlers, and literal API-key/access-token strings with `synthetic-probe` names. It does not read user settings, session files, credentials, or make network requests.

The solution baseline build and 221 passing tests were reported by the parent task. This standalone probe was independently restored in locked mode and built without warnings or errors. There are no direct package dependencies; the generated lock file is checked in for locked restore.

## Reproduce

From the repository root:

```powershell
dotnet restore .\TokensLimitsExtension\research\2026-10-05-memory\MemoryProbe\MemoryProbe.csproj -p:Platform=x64 --locked-mode
dotnet build .\TokensLimitsExtension\research\2026-10-05-memory\MemoryProbe\MemoryProbe.csproj --configuration Release -p:Platform=x64 --no-restore
dotnet run --project .\TokensLimitsExtension\research\2026-10-05-memory\MemoryProbe\MemoryProbe.csproj --configuration Release -p:Platform=x64 --no-build
```

Async allocation figures use `GC.GetTotalAllocatedBytes(true)` around completed operations, with one warmup and five measured repeats except for the deliberately cold cache read (one run, no warmup). Elapsed time covers only the measured operation; forced GC and finalizer waits happen before timing starts. The Codex JSONL size cases create a fresh fallback parser/cache per operation. `codex-client-json` separately measures `CodexUsageClient.FetchUsageAsync` against an HTTP stub and does not refer to JSONL. HTTP response bodies use `StringContent`; its allocation/encoding overhead is included identically in each size sample. The generic provider JSON measurements also include that stub content overhead. Process-wide allocation totals can include unrelated runtime/background allocations; treat these as comparative signals, not precise per-method costs. Exact sizes and the parsed values are printed and checked so invalid fixtures fail the run. Forced GC is used only between benchmark samples.

## Baseline output

Full raw probe stdout is also saved in [`BEFORE.txt`](BEFORE.txt).

```text
runtime=10.0.12; os=Microsoft Windows NT 10.0.26200.0; arch=X64; warmups=1; repeats=5
codex-cache fileBytes=1515003 events=10000old+100recent coldAllocBytes=6,121,184 coldMs=264.04 coldReadBytes=1515004 cachedEventsAfterCold=10100 warmAllocBytes=3,576 warmMs=0.99 warmReadBytes=0 cachedEventsAfterWarm=10100 parsedTokens5h=200 parsedTokens7d=200
codex-jsonl targetBytes=10240 actualBytes=10240 allocBytes=170,979 elapsedMs=1.63 parsedTokens5h=3
codex-jsonl targetBytes=102400 actualBytes=102400 allocBytes=854,925 elapsedMs=3.49 parsedTokens5h=3
codex-jsonl targetBytes=1047552 actualBytes=1047552 allocBytes=3,662,909 elapsedMs=12.35 parsedTokens5h=3
codex-client-json targetBytes=10240 bodyBytes=10240 allocBytes=166,965 elapsedMs=0.37 requests=6 parsedPrimaryUsed=2 parsedPrimaryReset=1790000000 parsedPrimarySeconds=18000 parsedSecondary=False
codex-client-json targetBytes=102400 bodyBytes=102400 allocBytes=545,416 elapsedMs=0.52 requests=6 parsedPrimaryUsed=2 parsedPrimaryReset=1790000000 parsedPrimarySeconds=18000 parsedSecondary=False
codex-client-json targetBytes=1047552 bodyBytes=1047552 allocBytes=6,293,224 elapsedMs=2.05 requests=6 parsedPrimaryUsed=2 parsedPrimaryReset=1790000000 parsedPrimarySeconds=18000 parsedSecondary=False
generic-json targetBytes=10240 bodyBytes=10240 allocBytes=453,712 elapsedMs=1.46 requests=12 parsedPrimaryUsed=15 parsedWeeklyUsed=28 parsedMetric=used percent:15
generic-json targetBytes=102400 bodyBytes=102400 allocBytes=1,358,846 elapsedMs=2.20 requests=12 parsedPrimaryUsed=15 parsedWeeklyUsed=28 parsedMetric=used percent:15
generic-json targetBytes=1047552 bodyBytes=1047552 allocBytes=13,670,005 elapsedMs=7.63 requests=12 parsedPrimaryUsed=15 parsedWeeklyUsed=28 parsedMetric=used percent:15
ui providers=5 initialRows=5 overviewRows=5 providerRefreshFanoutAllocBytes=1,286,102 providerRefreshFanoutElapsedMs=4.82 detailsUnopenedInitialRows=1 detailsUnopenedAfterRefreshRows=8 detailsOpenedInitialRows=8 oneProviderFanoutRefreshAllocBytes=264,830 detailsOpenedRepeatRows=8 detailsReferencesStableAfterOneProviderRefresh=0/4
ui providers=15 initialRows=15 overviewRows=15 providerRefreshFanoutAllocBytes=7,814,984 providerRefreshFanoutElapsedMs=39.87 detailsUnopenedInitialRows=1 detailsUnopenedAfterRefreshRows=8 detailsOpenedInitialRows=8 oneProviderFanoutRefreshAllocBytes=559,517 detailsOpenedRepeatRows=8 detailsReferencesStableAfterOneProviderRefresh=0/14
```

The fallback cold read parses and retains 10,100 events; the warmed fallback still retains 10,100, reads zero bytes, and recomputes totals from them. The 10,000 older events are ten days old and excluded from both windows; the 100 recent events each add two tokens, yielding 200 for five hours and seven days. `codex-jsonl` is the local fallback JSONL parser. The separate `codex-client-json` uses valid Codex API window JSON (2% used, reset epoch 1790000000, 18,000-second window), no secondary window, and ignored padding. The Codex client and generic JSON cases exercise 10 KiB, 100 KiB, and 1023 KiB inputs; the generic parser returns 15% used in the five-hour window and 28% in the weekly window. Each Codex client size performs six successful stub requests (one warmup plus five measured runs); generic parsing makes twelve stub requests (two endpoints each run).

For the page scenario, `Details` starts with one loading row. Before calling `GetItems()` on the details page, provider-state events have already materialized eight retained rows. The provider refresh allocation totals include cache, overview, and all details pages subscribed in this baseline. An opened refresh of one provider includes the overview update plus that provider's details update; it keeps eight rows. A refresh of one provider currently replaces all four/fourteen unaffected overview `Details` references. These results cover the managed page objects only; they do not establish how Command Palette retains or renders them.
