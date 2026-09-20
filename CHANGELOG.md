# Changelog

All notable changes are documented here. This project follows [Semantic Versioning](https://semver.org/).

## [0.0.5.3] - 2026-09-21 (prerelease candidate)

### Fixed

- Validate the embedded MSIX CMS signature, pin it to the release certificate, and verify the signed block map and every payload block without depending on a public trust chain.
- Exercise x64 package deployment on an ephemeral Windows runner with the release certificate temporarily in `LocalMachine\TrustedPeople`; reject tampered ZIP file-record and central-directory metadata.

### Field validation pending

- Live PowerToys/COM navigation and shutdown, ARM64 runtime, retained-object/allocation measurements and signed upgrade with settings/secrets preservation remain field acceptance checks. See [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.3.md).

## [0.0.5.2] - 2026-09-20 (not published)

### Added

- Safe provider recovery actions, typed status guidance and shared stale-state presentation across overview, details and Dock.
- Bounded and incremental reads for Codex session/auth files, bounded provider HTTP responses and external CLI execution.
- Locked dependency restore, x64/ARM64 Release validation, signed MSIX structural checks, artifact allowlisting and separated release permissions.

### Fixed

- Reused provider detail and Dock surfaces across repeated settings toggles with bounded retention; disabled providers cannot refresh through retained UI references.
- Preserved incomplete Codex JSONL tails and stale values during transient refresh failures.
- Enabled MSIX signing and passed the certificate thumbprint as a plain MSBuild property value; aligned locked restore with the exact SDK declared in `global.json`.

### Field validation pending

- Live PowerToys/COM navigation and shutdown, ARM64 runtime, retained-object/allocation measurements and signed upgrade with settings/secrets preservation remain field acceptance checks. See [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.2.md).

## [0.0.5.1] - 2026-09-20 (not published)

- The GitHub release workflow stopped before producing packages because PowerShell passed the formatted certificate object instead of its thumbprint to MSBuild. The issue was fixed in v0.0.5.2; no v0.0.5.1 artifacts were published. See [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.1.md).

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
