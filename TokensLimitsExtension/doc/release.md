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
     -PackagePath .\TokensLimitsExtension_0.0.4.1_x64.msix `
     -CertificatePath .\NFoxNet.TokensLimitsExtension.cer
   ```

   The helper imports the **public** self-signed certificate only into `LocalMachine\TrustedPeople`, then invokes `Add-AppxPackage`. It never receives or installs a private key. This grants device-level trust to the publisher certificate, so install it only after verifying the release source and checksum.
5. Open PowerToys Command Palette and run **Reload Command Palette extensions** if the extension does not appear immediately.

To remove a package while retaining configured providers and protected secrets, use:

```powershell
Get-AppxPackage TokensLimitsExtension | Remove-AppxPackage -PreserveApplicationData
```

## Trust and signing model

`v0.0.4.1` uses a self-signed `CN=NFoxNet` code-signing certificate. This is a transparent sideload distribution mechanism: the installer imports the release `.cer` into `LocalMachine\TrustedPeople` after the administrator accepts UAC, which is an explicit device-level trust decision. The certificate subject must exactly match the MSIX `Identity/Publisher`.

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

4. Verify the resulting MSIX signatures and checksums, test both package architectures on suitable machines, then attach `artifacts/` files to a `vX.Y.Z.W` GitHub Release.
5. For automated releases, add the PFX encoded as Base64 to `MSIX_CERTIFICATE_BASE64` and the password to `MSIX_CERTIFICATE_PASSWORD`, then push the matching annotated tag.

Do not commit a PFX, password, tokens, cookies or provider settings. A public `.cer` is safe to distribute.
