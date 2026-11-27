# TokensLimitsExtension 0.0.6.4

Unpublished installation candidate for Windows 11 (build 22000 or newer).

## Changes

- Settings now open in a separate WinUI 3 window. PowerToys keeps one **Open settings** button.
- Five native SettingsExpander groups and SettingsCard rows use Fluent styling, native chevrons and built-in expansion animations. Provider cards retain their bundled logos.
- Settings UI starts only when explicitly opened; provider fields are created on first expansion. Repeated opening activates the existing window; closing allows it to be opened again.
- Existing setting keys, JSON storage and DPAPI-protected credentials are preserved. Each section/provider saves its own fields. Closing discards unsaved edits.
- Secret write failures show an error and retain the previous stored value in memory and on disk. Validation uses the existing settings pipeline.

## Installation and validation

Requires Windows App Runtime 1.8. Keep the same package identity and publisher when upgrading; **do not uninstall the previous signed package**. The installer does not install the framework runtime itself; obtain it from [Microsoft](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

Offline tests cover settings metadata, scope validation, existing keys and secrets, failed replacement/deletion and successful retry, and Command Palette COM interfaces. The opt-in x64 smoke test opens real WinUI controls with isolated temporary settings, checks a narrow window and close/reopen/shutdown.

Live PowerToys activation, visual animation quality, keyboard navigation, high contrast/reduced motion, ARM64 execution and an upgrade using real user settings remain manual acceptance checks. Packages are local candidates, not a published release.