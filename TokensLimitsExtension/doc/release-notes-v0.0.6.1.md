# TokensLimitsExtension 0.0.6.1

This package includes the compact settings interface and all features prepared for 0.0.6.0. Settings are organized into General, Providers, Dock, Notifications, and History; provider credentials open in separate cards. Existing setting keys and protected secrets are preserved.

The version is increased because Windows rejects a rebuilt MSIX with the same identity and version but different content (0x80073CFB). Use 0.0.6.1 to upgrade an installed 0.0.6.0 in place. Do not uninstall the existing extension first.

Signed x64 and ARM64 installation packages use the existing publisher certificate. Local preparation does not publish or install the release. Live PowerToys rendering, ARM64 runtime, and preservation of real settings during an installed upgrade remain separate checks.

Local validation: 406 Release x64 tests passed. Both signed packages passed version, architecture, publisher, pinned certificate, CMS signature, payload block hashes, and SHA256SUMS verification. Existing SDK/WinRT trim and missing-symbol-tool warnings remain.
