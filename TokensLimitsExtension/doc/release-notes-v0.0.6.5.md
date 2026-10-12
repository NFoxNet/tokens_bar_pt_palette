# 0.0.6.5 — Native settings startup fix

- Fix the settings button crashing the extension before the native window appears in Release builds.
- Preserve WinRT.Runtime interop code required by WinUI startup while keeping other Release trimming enabled.
- Add a startup-only regression check using the actual Release WinUI/WinRT libraries.
- Settings identifiers, package identity and protected secrets are unchanged. Upgrade over the existing package; do not uninstall it.

Requires Windows 11 and Windows App Runtime 1.8. This is a local candidate; publication is a separate step.