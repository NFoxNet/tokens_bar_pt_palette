# TokensLimitsExtension v0.0.5.9

## Fixed

- Prevent Dock flicker during background provider refreshes. When cached usage
  is available, the widget keeps its existing subtitle instead of temporarily
  appending `Refreshing…` and changing its width.
- Publish the final subtitle once, including any stale-data warning, so an
  unchanged warning does not trigger intermediate Dock repaints.
- New usage values still update the widget. Initial loading and refresh status
  on provider pages remain visible. The fix applies to all provider widgets.

## Validation

The local Debug x64 build passed with warnings treated as errors, and all
262 tests passed (191 unit and 71 integration).

The regression reproduces the previous subtitle change in English and Russian,
with fresh and stale snapshots. It checks that refresh transitions produce no
Dock list invalidations and that changed usage produces one update.

The existing release workflow runs tests, builds signed x64 and ARM64 packages,
verifies signatures, manifest and COM identity, block maps and checksums, and
passes an x64 Windows deployment gate before publishing.

Live PowerToys UI/COM behavior, ARM64 runtime and an upgrade with real user
settings remain field checks. Install over the existing package with the same
identity; do not uninstall before updating, to preserve settings and protected
provider credentials.
