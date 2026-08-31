[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'

$package = Get-Item -LiteralPath $PackagePath
$certificate = Get-Item -LiteralPath $CertificatePath
$principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window. Self-signed MSIX packages require LocalMachine\\TrustedPeople trust.'
}

if ($package.Extension -notin '.msix', '.msixbundle') {
    throw 'PackagePath must point to a .msix or .msixbundle file.'
}

if ($certificate.Extension -ne '.cer') {
    throw 'CertificatePath must point to the public .cer file from the same release.'
}

Import-Certificate -FilePath $certificate.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Add-AppxPackage -Path $package.FullName

Write-Host 'Tokens Limits was installed. Open PowerToys Command Palette and run Reload Command Palette extensions if it does not appear immediately.' -ForegroundColor Green
