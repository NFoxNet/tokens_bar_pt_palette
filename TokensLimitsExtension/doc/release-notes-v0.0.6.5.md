# TokensLimitsExtension v0.0.6.5

Changes since the previous published release, **v0.0.5.9**.

## Settings and usability

- Open settings in a dedicated WinUI 3 window from the PowerToys settings button.
- Navigate five native groups: General, Providers, Dock, Notifications and History. Provider cards show their bundled logos and use native Fluent controls and expansion animations.
- Start the settings UI only when requested and create provider fields on first expansion. Repeated opening activates the same window.
- Save each section or provider independently. Existing setting keys, storage format and protected credentials are preserved; closing the window discards unsaved edits.
- Connect providers through a guided setup page with explicit configuration and data checks. Azure deployment inference remains an explicit action.

## Dock and optional features

- Choose provider visibility, ordering and displayed usage metrics independently of whether a provider is enabled.
- Enable an optional cached quick-summary flyout from Dock. It is disabled by default.
- Enable optional Command Palette notifications for confirmed remaining-limit threshold crossings and confirmed fixed-window resets. Estimates and unavailable quotas are excluded.
- Enable local numeric usage history with 7, 14 or 30 days of retention. History is disabled by default; turning it off stops recording and retains existing records. Credentials, raw API responses and account identifiers are not recorded.
- View conservative usage-pace estimates only when fresh, continuous samples and confirmed fixed-window boundaries support them. Rolling windows, local estimates and insufficient data do not produce a forecast.

## Fixes and reliability

- Fix native settings startup in trimmed Release builds by preserving the WinRT.Runtime interop code required by WinUI.
- Report secret-store write failures and retain the previous protected value instead of falsely reporting success.
- Handle history errors, account/source changes, stale data and notification cycles without mixing incompatible measurements.
- Refresh Dock summary metrics and native settings rows when their configuration changes.
- Add a Release-package WinUI startup regression check and keep framework dependencies out of the public product-package selection.

## Installation

Requires **Windows 11 build 22000 or newer**, PowerToys Command Palette and **Windows App Runtime 1.8**. The runtime is available from [Microsoft](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

Download the MSIX for your architecture, the public certificate and both installer files into the same folder. Run `Install-TokensLimitsExtension.cmd` as your normal user and accept the installation prompt. The installer does not install Windows App Runtime itself.

**Upgrade over the existing package; do not uninstall it first.** Keeping the package identity preserves settings and protected provider credentials. `SHA256SUMS.txt` contains checksums for the five distribution files.

## Validation

All **419 offline tests** passed locally in Debug and Release x64. Signed x64 and ARM64 packages passed signature, signer, identity, version, payload block hash and checksum checks. The x64 native window passed isolated startup, layout, repeated activation and shutdown checks; the startup regression also uses the actual Release WinUI/WinRT libraries and package XAML resources.

The release workflow runs tests and package/deployment checks before publication. Live PowerToys UI/COM behavior after upgrade, visual animation/accessibility checks, ARM64 execution and an upgrade with real user settings remain field checks.