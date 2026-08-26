[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$packages = @(Get-AppxPackage | Where-Object { $_.Name -match "TokensLimitsExtension" })
if ($packages.Count -eq 0) {
    Write-Host "No registered TokensLimitsExtension package was found."
    exit 0
}

foreach ($package in $packages) {
    Write-Host "Removing $($package.Name) version $($package.Version)..."
    Remove-AppxPackage -Package $package.PackageFullName
}

Write-Host "TokensLimitsExtension package registration removed."
