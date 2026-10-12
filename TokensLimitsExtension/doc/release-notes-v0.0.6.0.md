# TokensLimitsExtension 0.0.6.0

- Navigate compact settings through General, Providers, Dock, Notifications, and History sections. Provider credentials open in separate cards; dependent options appear after enabling and saving their feature. Existing settings and protected secrets are preserved.
- Customize Dock provider visibility, ordering, and displayed metrics independently of provider availability.
- Connect providers through a guided setup page with explicit configuration validation.
- Enable an optional native quick summary from Dock. Disabled by default.
- Enable optional threshold notifications for confirmed remaining limits. Disabled by default.
- Enable bounded local numeric usage history with 7, 14, or 30 days of retention. Disabled by default.
- Show conservative usage pace only when fresh samples and confirmed fixed window boundaries support it.

## Installation

Use the signed package matching your Windows architecture together with the public certificate and release installer. Upgrade in place; do not uninstall the existing package first, to preserve encrypted provider credentials.

## Validation limits

Local package preparation does not publish a GitHub release. Live PowerToys UI/COM, ARM64 runtime, and upgrades preserving real user settings require separate validation.

Local validation after settings grouping: 406 tests passed; signed x64 and ARM64 packages passed CMS, manifest, payload block hash, and checksum verification. Release builds retain Windows SDK/WinRT trim warnings and do not include a separate symbols package.
