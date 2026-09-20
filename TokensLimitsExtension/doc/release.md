# Public releases and installation

## Release channel

The first public channel is a GitHub Release containing one signed MSIX for each supported CPU architecture, the public signing certificate (`.cer`) and `SHA256SUMS.txt`. This makes the project installable without placing the private signing key in the repository.

The package requires:

- Windows 10 version 2004 (build 19041) or later;
- Microsoft PowerToys with Command Palette enabled;
- the architecture-appropriate package: `x64` for most Intel/AMD PCs, `ARM64` for Windows on ARM.

## Installing a GitHub Release

1. Download the matching `.msix`, `NFoxNet.TokensLimitsExtension.cer`, `Install-TokensLimitsExtension.cmd`, `Install-TokensLimitsExtension.ps1` and `SHA256SUMS.txt` from the [latest release](https://github.com/NFoxNet/tokens_bar_pt_palette/releases/latest), keeping them in one directory.
2. Optionally verify the downloaded checksums:

   ```powershell
   Get-FileHash .\TokensLimitsExtension_*.msix -Algorithm SHA256
   ```

3. Run `Install-TokensLimitsExtension.cmd`. It selects the correct package for the PC, asks for UAC elevation, imports the public release certificate into `LocalMachine\\TrustedPeople`, then installs the MSIX. No PowerShell execution-policy change is required.

   The `.cmd` bootstrap is intentional: a downloaded `.ps1` may be blocked by an `AllSigned` policy before it has an opportunity to import the certificate that would establish trust for the package.

4. For a managed environment where `.cmd` launchers are disallowed, an administrator can invoke the helper explicitly:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-TokensLimitsExtension.ps1 `
    -PackagePath .\TokensLimitsExtension_<version>_x64.msix `
     -CertificatePath .\NFoxNet.TokensLimitsExtension.cer
   ```

   The helper imports the **public** self-signed certificate only into `LocalMachine\TrustedPeople`, then invokes `Add-AppxPackage`. It never receives or installs a private key. This grants device-level trust to the publisher certificate, so install it only after verifying the release source and checksum.
5. Open PowerToys Command Palette and run **Reload Command Palette extensions** if the extension does not appear immediately.

To remove a package while retaining configured providers and protected secrets, use:

```powershell
Get-AppxPackage TokensLimitsExtension | Remove-AppxPackage -PreserveApplicationData
```

## Trust and signing model

The current release uses a self-signed `CN=NFoxNet` code-signing certificate. This is a transparent sideload distribution mechanism: the installer imports the release `.cer` into `LocalMachine\TrustedPeople` after the administrator accepts UAC, which is an explicit device-level trust decision. The certificate subject must exactly match the MSIX `Identity/Publisher`.

For a frictionless production channel, the next distribution step is either:

- Microsoft Store: Partner Center assigns the package identity and signs submissions; or
- a publicly trusted code-signing/Trusted Signing certificate, with its signing material stored as GitHub Actions secrets.

The release workflow is already prepared for the latter through `MSIX_CERTIFICATE_BASE64` and `MSIX_CERTIFICATE_PASSWORD`; it intentionally cannot create or use those secrets automatically.

## Maintainer release checklist

1. Set the manifest and project version to the next four-part MSIX version.
2. Generate/import a code-signing PFX outside the repository. Its subject must be the manifest publisher.
3. Run:

   ```powershell
   $password = Read-Host 'PFX password' -AsSecureString
   .\scripts\Build-Release.ps1 -CertificatePath C:\secure\tokens-limits.pfx -CertificatePassword $password
   ```

4. For this thumbprint-based build path, set `AppxPackageSigningEnabled=true` and pass the imported certificate thumbprint through `PackageCertificateThumbprint` to enable build-time signing. Do not run `signtool sign` on the completed `.msix` again. Run `scripts/Test-ReleasePackage.ps1` for both packages with `-ExpectedCertificatePath` set to the public `.cer`. It verifies the embedded CMS signature, exact signer certificate, Publisher, version, architecture, COM CLSID, required assets and language files; it also checks the signed `[Content_Types].xml` and block map, then compares every payload block to its SHA-256 hash. It does not establish Windows trust. Verify checksums and install on suitable machines, then attach `artifacts/release/` files to a `vX.Y.Z.W` GitHub Release.
5. For automated releases, add the PFX encoded as Base64 to `MSIX_CERTIFICATE_BASE64` and the password to `MSIX_CERTIFICATE_PASSWORD`, then push the matching annotated tag.

The automated workflow validates both package architectures, checksums and the release certificate, then creates a draft release. A prerelease can be published for field testing before host-level acceptance; keep the stable release pending until Command Palette UI/COM lifecycle checks and signed upgrade with application data preservation have passed. The [v0.0.5.3 release notes](release-notes-v0.0.5.3.md) record the current acceptance scope.

Do not commit a PFX, password, tokens, cookies or provider settings. A public `.cer` is safe to distribute.

## Release output directory

`Build-Release.ps1` writes to `artifacts/release/` by default. It accepts only a strict child directory of the repository's `artifacts/` directory and refuses the repository root, `artifacts/` itself, other source paths, paths outside the repository and existing reparse points. This limits recursive cleanup to the intended release directory.

The GitHub release workflow repeats the CMS, certificate, block-map, payload, manifest and checksum checks before publication and compares the output directory with an explicit allowlist. On its disposable x64 runner, it temporarily adds the public leaf certificate to `LocalMachine\TrustedPeople`, installs the x64 MSIX, confirms its identity and version, and removes it using `-PreserveApplicationData`. It then creates two structurally readable ZIP mutations: a timestamp changed consistently in both ZIP records, and a DOS archive attribute changed only in the central directory. For each, Windows must report a signature or integrity failure, while the manifest, block map and payload bytes remain readable. The test removes its temporary directory and only the certificate it imported. The ARM64 package receives static signature, content and identity checks; ARM64 runtime validation remains a field check.

Build and validation run with read-only repository permissions; a separate publish job receives only the validated payload and has `contents: write`. It creates a draft release so a maintainer can inspect the generated assets before making them public. The release cannot publish an extra file, a stale checksum or an MSIX with a different version, publisher, architecture or payload.

The CMS and block-map checks establish that the package signature matches the bundled public certificate and that payload hashes match the signed block map. The deployment test also exercises Windows trust and package validation: it first installs the unmodified package, then requires Windows to identify the signature or integrity failure for each ZIP mutation. The installer imports the public `.cer` into `LocalMachine\TrustedPeople` on the user's device. See Microsoft's [MSIX self-signed distribution guide](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide) and [package signing overview](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview).

An explicitly supplied relative `-OutputDirectory` is resolved from the repository root. The script validates the directory again immediately before deletion and rejects any existing reparse point inside it. PowerShell cannot bind this scan-and-delete sequence to a verified directory handle, so this is not a defence against a concurrent process with write access to `artifacts/`: it could replace a path during validation or deletion. Run releases only from a trusted local checkout with exclusive write access to the output tree, and do not modify the output path while a build is running.
