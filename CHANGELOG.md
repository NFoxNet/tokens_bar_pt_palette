# Changelog

All notable changes are documented here. This project follows [Semantic Versioning](https://semver.org/).

## [0.0.4.2] - 2026-09-04

### Fixed

- Refreshed the persistent Dock band when a provider's shared usage snapshot changes, so it reflects a Codex limit reset without reopening Command Palette.

## [0.0.4.1] - 2026-09-04

### Documentation

- Replaced the root README with a concise English overview and added a separate Russian README.
- Updated release and architecture documentation for the shared refresh pipeline and current release process.

## [0.0.4.0] - 2026-09-04

### Added

- One shared refresh coordinator for Dock, overview and detail pages.
- Shared provider state with stale-snapshot handling and cancellation when a provider is disabled.
- Separate handling for language changes and provider configuration changes.

## [0.0.2.2] - 2026-09-03

### Fixed

- GitHub Release installation no longer requires directly running a downloaded unsigned PowerShell script under an `AllSigned` policy. The new `.cmd` bootstrap starts the installer with a process-local execution-policy bypass, requests UAC and retains MSIX signature verification.

## [0.0.2.1] - 2026-08-31

### Fixed

- Dock now uses one stable pinned band and dynamically displays every enabled provider, including DeepSeek after settings changes.
- Dock detail navigation is isolated from the main Command Palette navigation state.
- Provider settings and encrypted provider secrets use one stable local-data location across package registrations.

### Added

- Public-release documentation, automated Windows CI and reproducible signed-MSIX release tooling.
- OSS contribution, conduct and security policies.

## [0.0.2.0]

### Added

- Multi-provider usage overview, provider configuration and Dock integration.
