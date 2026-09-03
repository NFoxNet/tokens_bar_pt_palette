[CmdletBinding()]
param(
    [string]$PackagePath,

    [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'

$releaseDirectory = (Resolve-Path -LiteralPath $PSScriptRoot).Path

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($architecture -notin 'x64', 'arm64') {
        throw "This release supports x64 and ARM64 Windows only. Detected architecture: $architecture."
    }

    $packages = @(Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object { $_.Extension -in '.msix', '.msixbundle' -and $_.Name -match "_$architecture\.(msix|msixbundle)$" })
    if ($packages.Count -ne 1) {
        throw "Expected exactly one $architecture MSIX package next to the installer, found $($packages.Count)."
    }

    $PackagePath = $packages[0].FullName
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $certificates = @(Get-ChildItem -LiteralPath $releaseDirectory -File -Filter '*.cer')
    if ($certificates.Count -ne 1) {
        throw "Expected exactly one public .cer certificate next to the installer, found $($certificates.Count)."
    }

    $CertificatePath = $certificates[0].FullName
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "PackagePath does not exist: $PackagePath"
}

if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "CertificatePath does not exist: $CertificatePath"
}

$package = Get-Item -LiteralPath $PackagePath
$certificate = Get-Item -LiteralPath $CertificatePath

if ($package.Extension -notin '.msix', '.msixbundle') {
    throw 'PackagePath must point to a .msix or .msixbundle file.'
}

if ($certificate.Extension -ne '.cer') {
    throw 'CertificatePath must point to the public .cer file from the same release.'
}

$principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $windowsPowerShell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
        $windowsPowerShell = (Get-Command powershell.exe -ErrorAction Stop).Source
    }

    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $PSCommandPath,
        '-PackagePath',
        $package.FullName,
        '-CertificatePath',
        $certificate.FullName
    ) | ForEach-Object { '"{0}"' -f $_.Replace('"', '\"') }

    $process = Start-Process -FilePath $windowsPowerShell -ArgumentList ($arguments -join ' ') -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installation was cancelled or failed with exit code $($process.ExitCode)."
    }

    return
}

Import-Certificate -FilePath $certificate.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Add-AppxPackage -Path $package.FullName

Write-Host 'Tokens Limits was installed. Open PowerToys Command Palette and run Reload Command Palette extensions if it does not appear immediately.' -ForegroundColor Green
