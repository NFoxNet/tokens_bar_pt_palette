# Memory improvements for v0.0.5.8

Base: `7d8c029cb4c248dc816390e655f4069fd05339e3` (v0.0.5.7).

The approved scope is potential RAM reduction, with unchanged provider settings,
refresh cadence, transport safety, displayed data and COM hosting. Each task uses
an independent managed worktree and a separate tested commit.

| Task | Owned production files | Validation |
| --- | --- | --- |
| 1. Recent fallback events | `Core/Services/CodexLocalSessionFallback.cs` | Old-only valid history, 7d boundary, append/cumulative/partial lines, replacement, deletion, clock rollback; retained events and synthetic memory measurement |
| 2. Incremental overview | `Pages/UsageOverviewPage.cs` | Only changed row rebuilt; stable IDs/objects/actions; language, composition and busy/error/stale transitions |
| 3. Detail content allocation | `Pages/TokensLimitsPage.cs` | Avoid repeated graphs, delay content until GetItems if compatible; preserve independent normal/Dock pages, notifications, per-row Details and no I/O in GetItems |
| 4. UTF-8 response parsing | `Core/Services/CodexUsageClient.cs`, `Core/Providers/ConfiguredUsageProvider.cs`, `Core/Providers/UsageJsonParser.cs` | UTF-8/BOM, charset, prefix and JSONL compatibility, bounded reads/cancellation/errors; synthetic allocation measurement |
| 5. Offline measurements | Separate synthetic probe and results | Identical fixture/configuration on base and integrated commits; no user data/network and no claims about live COM working set |
| 6. Integration/version/docs | Version fields and project docs | Full locked restore, Debug x64 build and solution tests; independent patch and final reviews |

Task paths above are relative to the corresponding extension/Core directories.
Test ownership follows production ownership; add separate test files for new UI
allocation scenarios to avoid conflicting edits. No new dependencies, forced GC
in production, global cache framework, polling changes or installed-package update.

Preflight: the four production scopes are independent. UI host lifecycle is the
main uncertainty; use a bounded change supported by local SDK contracts and tests.
The user-approved resource report is the task authority. Review each patch for
correctness and allocation behavior, then the assembled branch for interactions.
Memory measurements distinguish live retained events from transient allocation.

Ruling: implement R01/R03/R05/R06 from the resource audit. Defer backoff,
discovery TTL, settings changes and freshness policies because their principal
benefit is CPU/I/O or they change functionality beyond this RAM-focused request.
