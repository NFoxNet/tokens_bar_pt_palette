# Changelog

All notable changes are documented here. This project follows [Semantic Versioning](https://semver.org/).

## [0.0.5.6] - 2026-09-21

### Fixed

- Migrate stale Tokens Limits Dock pins to the installed MSIX identity during an update, preserving global and customized per-monitor placements with an automatic backup.
- Skip Dock settings migration while Command Palette is still running and provide a manual recovery path rather than risk overwriting live settings.
- Publish the stable GitHub Release after the signed package and Windows deployment gates pass.

### Field checks to complete

- Confirm the Dock pin and Enabled providers page after in-place installation from v0.0.5.5. The blank-page symptom has not been reproduced or tied to a source defect; see [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.6.md).

## [0.0.5.5] - 2026-09-21

### Fixed

- Automatically stop a running PowerToys runner before MSIX deployment and restart it afterward, including when installation fails or UAC is canceled.
- Abort safely if PowerToys cannot close gracefully within 30 seconds; verify the tray window belongs to the detected runner and never force-kill a process.
- Reject an already-elevated launcher and a UAC identity mismatch, so per-user MSIX registration and the PowerToys restart stay with the intended Windows account.

### Field checks to complete

- Confirm the signed in-place upgrade, retained settings/secrets and ARM64 runtime on target devices. See [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.5.md).

## [0.0.5.4] - 2026-09-21 (prerelease candidate)

### Fixed

- Validate the embedded MSIX CMS signature, pin it to the release certificate, and verify the signed block map and every payload block without depending on a public trust chain.
- Exercise x64 package deployment on an ephemeral Windows runner with the release certificate temporarily in `LocalMachine\TrustedPeople`; reject tampered ZIP file-record and central-directory metadata before installing the intact package last.
- Keep signed MSIX upgrades on the same package identity so Windows retains provider settings and protected secrets; document that `-PreserveApplicationData` is limited to development-mode loose-file registrations.
- Make the default unregister helper refuse signed Release MSIX packages before removal; `-DeleteApplicationData` remains the explicit data-removal option.

### Field validation pending

- Live PowerToys/COM navigation and shutdown, ARM64 runtime, retained-object/allocation measurements and signed upgrade with settings/secrets preservation remain field acceptance checks. See [release notes](TokensLimitsExtension/doc/release-notes-v0.0.5.4.md).

## [0.0.5.3] - 2026-09-21 (not published)

- The signed packages passed structural validation, but the Windows deployment gate stopped while trying to remove a normally installed MSIX with `-PreserveApplicationData`. No GitHub Release or downloadable artifacts were created. The tag remains for history; v0.0.5.4 fixes the gate and supersedes this candidate.

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
