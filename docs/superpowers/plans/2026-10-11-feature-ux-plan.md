# Tokens Limits Feature UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Implement the approved native PowerToys feature roadmap with opt-in summary, notifications and history.
**Architecture:** Reuse cached snapshots and the single refresh coordinator. Core owns bounded persistence and calculations; native pages/settings and notification delivery stay in the extension. Independent workers use separate managed worktrees; the coordinator integrates contracts and commits each completed subsystem.
**Tech Stack:** C#/.NET 10, existing Command Palette toolkit, Windows App SDK, xUnit; no new dependencies.
**Spec:** docs/superpowers/specs/2026-10-11-feature-ux-design.md (approved 2026-10-11).

## Global Constraints

- Summary setting `tokensLimits.dock.summaryEnabled` defaults to `false`.
- Notifications and history default off; notification remaining threshold 10%.
- History retention 7/14/30 days, default 14; sample interval 5 minutes; maximum 8640 records per provider.
- Pace requires three comparable samples spanning at least 15 minutes from a confirmed fixed window.
- No network from GetItems/GetContent, no new timer/service/dependency, no secrets or raw responses in stored data.
- Preserve provider/settings/command IDs, stable Dock band ID, COM and package identity; EN/RU strings through existing localization.
- Preserve unrelated files. No release/push in this implementation task.

## Review Focus

- Mode switches preserve previously pinned band/provider IDs and dispose summary subscriptions.
- Display-only settings do not trigger provider fetches or disable overview membership.
- Source/account/configuration changes isolate historical and notification state, including unknown identity.
- Corrupt/oversized history is reported and not silently overwritten; writes remain atomic.
- Rolling-window/reset/gapped/stale samples never yield a misleading forecast or notification.

## Task 1: Personal Dock and opt-in native summary

**Files:** Modify `TokensLimitsExtension/TokensLimitsExtension/Settings/TokensLimitsSettings.cs`, `TokensLimitsExtension/TokensLimitsExtension/TokensLimitsExtensionCommandsProvider.cs`, `TokensLimitsExtension/TokensLimitsExtension/Dock/UsageDockBandItem.cs`, Core `UsageDisplayFormatter.cs`; create `TokensLimitsExtension/TokensLimitsExtension/Dock/UsageSummaryPage.cs`; tests `TokensLimitsExtension.IntegrationTests/NativeUsageUxTests.cs`; EN/RU dictionaries and catalog; `doc/operations.md`.
**Interfaces:** Consume `UsageSnapshotCache` and existing settings/localization. Produce `DockSummaryEnabled`, `IsDockVisible(string providerId)`, `DockProviderOrder`, `DockMetric` settings and native summary composed from visible cached providers. Preserve `TokensLimitsDockBandPage.StableId` in both modes.
- [x] Add/run failing tests: defaults unchanged, filtered ordered Dock while overview unchanged, duplicate/unknown order IDs, unavailable selected metric, stable IDs across mode switches, no cached-refresh flicker, disabled summary releases subscriptions.
- [x] Implement settings and native IContentPage using SDK contracts; no custom window or network during rendering.
- [x] Run focused integration/unit/localization tests; update operations documentation.
- [x] Commit focused feature milestone.

## Task 2: Guided provider connection

**Files:** Create `TokensLimitsExtension/TokensLimitsExtension/Pages/ProviderSetupPage.cs` and `TokensLimitsExtension.IntegrationTests/ProviderSetupPageTests.cs`; coordinator integrates entry point and localization keys into overview/catalog/lang files; update `doc/providers.md`.
**Interfaces:** Produce `ProviderSetupPage(TokensLimitsSettings settings, IReadOnlyList<UsageSnapshotCache> caches, UsageRefreshCoordinator coordinator)`; use existing SettingsPage and provider descriptors, no second secret editor. Stable command ID `com.tokenslimits.setup`.
- [x] Add/run failing tests for required fields, secret redaction, disabled/missing credentials, quota-unavailable versus connection failure, Azure explicit validation only.
- [x] Implement native provider list and per-provider instructions/status/actions using cached state and existing settings/refresh commands.
- [x] Run focused tests; document connection workflow; commit milestone.

## Task 3: Threshold/reset notifications

**Files:** Create Core `Services/UsageNotificationEvaluator.cs`, Core tests `UsageNotificationEvaluatorTests.cs`, UI `Notifications/UsageNotificationController.cs` and integration tests; coordinator wires settings/cache lifecycle; `doc/operations.md`.
**Interfaces:** Core evaluator consumes provider/session segment ID, `UsageSnapshot`, current UTC and remaining threshold; returns typed threshold/reset events. `UsageNotificationController` observes existing caches only when enabled, resets on provider configuration change and delivers through verified native Windows API; injectable test delivery avoids OS/network in tests.
- [x] Verify installed native notification API and process lifecycle against official sources; document limitations.
- [x] Add/run failing tests for one crossing per cycle, confirmed reset, no startup/reset invention, estimates/stale/error suppression, configuration/source isolation and disposal.
- [x] Implement evaluator/delivery without extra background service; add opt-in settings and localized copy.
- [x] Run focused tests, document process-running limitation; commit milestone.

## Task 4: Bounded history and native history page

**Files:** Create Core `Models/UsageHistoryEntry.cs`, `Services/UsageHistoryStore.cs`, tests `UsageHistoryStoreTests.cs`, UI `Pages/UsageHistoryPage.cs` and Core `Services/UsageHistoryController.cs`, integration tests. Coordinator owns settings/wiring/catalog/lang. Update `doc/operations.md` and `doc/architecture.md`.
**Interfaces:** Produce `UsageHistoryEntry` containing numeric windows/metrics, timestamp, provider ID, source, segment ID and fixed-window eligibility (unknown is ineligible); `UsageHistoryStore(string directory, int retentionDays = 14)` exposes `Append(UsageHistoryEntry entry)`, `Read(string providerId)`, `Clear()`. Controller starts a random per-process/config segment when identity is unknown; no credentials retained. UI constructor `UsageHistoryPage(UsageHistoryController controller, IReadOnlyList<UsageSnapshotCache> caches, ILocalizationService? localization = null)` uses the shared controller and bounded recent records.
- [x] Add/run failing tests for 5-minute throttling, 7/14/30 retention, 8640 cap, numeric-only serialization, source/segment separation, oversized/corrupt input surfaced without overwrite, atomic writes and explicit clear.
- [x] Implement bounded storage and opt-in lifecycle, native recent-measurement list and clear action; expose file failures safely.
- [x] Run focused tests and update docs; commit milestone.

## Task 5: Conservative pace estimates

**Files:** Create Core `Services/UsagePaceEstimator.cs`, tests `UsagePaceEstimatorTests.cs`; update history page and localization/documentation.
**Interfaces:** `UsagePaceEstimator.Estimate(IReadOnlyList<UsageHistoryEntry> entries, DateTimeOffset now)` returns nullable estimate containing used percent per hour and estimated exhaustion; consumes Task 4 numeric history contract. Eligibility requires explicitly confirmed fixed windows; do not infer from duration/reset alone.
- [x] Add/run failing tests: three same-cycle samples spanning 15 minutes, positive rate, insufficient/stale/gapped data, rolling/unknown windows, mixed source/segment and reset rejected.
- [x] Implement calculation and localized estimated/insufficient-data presentation on history page.
- [x] Run focused tests, document eligibility; commit milestone.

## Task 6: Integration, documentation and review

**Files:** Shared settings/command provider/localization/catalog/overview and relevant tests; `doc/README.md`, `doc/operations.md`, `doc/architecture.md`; this plan and spec.
- [x] Integrate independent worktree commits with explicit staging and resolve shared interfaces against the approved spec.
- [x] Test settings defaults/serialization/change handling, disabled-feature allocations/subscriptions, existing compatibility and EN/RU keys.
- [x] Run `dotnet restore TokensLimitsExtension.sln`; Debug x64 build and test with `--no-restore`; compare exit codes and test results. Expected: all checks pass.
- [ ] Review each subsystem and final branch with independent agents; fix important findings with regression tests.
- [x] Update plan progress, docs and evidence; commit integration milestone. Do not claim live Dock/ARM64 acceptance without actual checks.

## Execution and ownership

User selected parallel subagents in separate worktrees and milestone commits. Dock worker owns existing settings/commands/formatter/Dock/lang/catalog files initially. Other workers create isolated new files and supply requested localization keys; coordinator integrates common files after Dock completion. Pace starts after the history contract is committed. No worker may modify unrelated files or publish.

## Verification progress

Implemented Tasks 1–5 and integration in isolated worktrees with milestone commits. Subsystem reviews are clean after fixes; whole-branch review is pending. At product commit `649eb99`, restore succeeded, Debug x64 build completed with 0 warnings/errors, and all 257 Core +116 integration tests passed. Live PowerToys/COM, ARM64 and real-settings upgrade acceptance remain unverified.
