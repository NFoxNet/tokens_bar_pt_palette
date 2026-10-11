# Native WinUI settings window implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the simulated Adaptive Cards accordion with a separate Windows 11 WinUI 3 settings window; keep one open button in PowerToys.

**Architecture:** A lazily started STA WinUI dispatcher inside the existing COM process opens one settings window and reactivates it on repeat requests. The window edits the same TokensLimitsSettings instance through the existing SettingsForm save/validation path. Native SettingsExpander/SettingsCard, ToggleSwitch, ComboBox, TextBox and PasswordBox provide theme, accessibility and animations.

**Tech stack:** Existing .NET 10/Windows App SDK plus CommunityToolkit.WinUI.Controls.SettingsControls for the actual controls.

**Spec:** User-approved design in this conversation, 2026-10-12: separate native window, host open button, preserve settings/secrets, plan/tests/documentation.

## Constraints

- Preserve COM entry argument, CLSID, package identity, provider IDs and setting keys.
- No second settings store, IPC or secret migration. Never display stored plaintext secrets.
- Keep the existing five sections and provider logos. Save each section/provider explicitly; closing discards unsaved edits.
- Opening and expanding cards perform no provider/network work.
- Windows 11 target; avoid custom chevrons, colors or animation substitutes.
- Keep user files, installed package and secrets untouched; installation/publication are separate actions.

## Tasks

- [ ] 1. Add a small editor adapter over native SettingsForm: extract current input metadata, group keys, scoped save, mask handling and validation. Add offline regression tests for all keys, secret preservation, scoped saves and invalid input.
- [ ] 2. Add lazy STA WinUI Application/window lifecycle without replacing the MTA COM hosting path. Add the toolkit dependency and resources; handle repeat opening, closing/reopening and disposal.
- [ ] 3. Build NativeSettingsWindow with five real SettingsExpander groups and SettingsCard rows. Provider expanders use existing logos; localized labels and input controls derive from existing settings metadata. Show errors and keep edited section open after save. Retain unsaved edits within a section; never expose credentials.
- [ ] 4. Replace GroupedSettingsPage simulated accordion with a localized open button. All existing settings entry points use the same page/window service. Adapt integration tests to host launch and editor contract.
- [ ] 5. Review lifecycle/secret handling, run restore/build/full tests and Release x64/ARM64 packaging checks. Update architecture, operations and verification docs; bump the package version if producing changed installation artifacts.

## Review focus

- No WinUI initialization from GetContent or tests; bootstrap only on explicit open.
- UI shutdown must not race ProtectedSecretStore disposal or hang COM shutdown.
- Only one window; closed window references/subscriptions released and repeat activation supported.
- Native validation and secret masks preserved; errors visible without raw credential logs.
- Keyboard/focus, theme/high contrast, resizing/overflow, reduced-motion settings and real host launch require runtime acceptance; record any gaps honestly.
