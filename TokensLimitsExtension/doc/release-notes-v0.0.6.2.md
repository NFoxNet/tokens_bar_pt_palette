# TokensLimitsExtension 0.0.6.2

Settings sections now use wide theme-aware cards with an icon, a bold wrapping heading, a short description, and a right-aligned Configure action. Provider cards share the same layout. Notifications, history, and individual providers display their saved On/Off state; the Providers section shows the enabled count.

The existing five sections, native controls, scoped saves, validation, setting keys, and protected secrets are preserved. Change switches inside an expanded card and save to update its header state. Colors and spacing follow the Command Palette renderer; this is not a replacement for PowerToys' native settings controls.

Upgrade in place using the signed package for your architecture and the existing release installer. The version is increased to avoid Windows rejecting different package content under an already installed version. Do not uninstall the extension first.

Live PowerToys rendering, ARM64 runtime, and an installed upgrade with real settings remain separate validation steps. Local package preparation does not install or publish the release.

Local validation: 408 tests passed in Debug x64 and Release x64. Both signed packages passed version, architecture, publisher, pinned certificate, CMS signature, payload block hashes, and SHA256SUMS verification. Configure is right-aligned below the heading to preserve full-width expanded fields. Existing SDK/WinRT trim and missing-symbol-tool warnings remain.
