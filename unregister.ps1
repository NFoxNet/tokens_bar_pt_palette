[CmdletBinding()]
param(
    [switch]$DeleteApplicationData
)

$ErrorActionPreference = "Stop"
$packages = @(Get-AppxPackage | Where-Object { $_.Name -match "TokensLimitsExtension" })
if ($packages.Count -eq 0) {
    Write-Host "No registered TokensLimitsExtension package was found."
    exit 0
}

if (-not $DeleteApplicationData) {
    $packagesWithoutDevelopmentRegistration = @($packages | Where-Object { -not $_.IsDevelopmentMode })
    if ($packagesWithoutDevelopmentRegistration.Count -gt 0) {
        $packageNames = $packagesWithoutDevelopmentRegistration | ForEach-Object PackageFullName
        throw "Cannot preserve application data for a signed Release MSIX. -PreserveApplicationData only supports development-mode loose-file registrations. Install a newer Release MSIX over the existing package instead. Packages: $($packageNames -join ', ')"
    }
}

foreach ($package in $packages) {
    Write-Host "Removing $($package.Name) version $($package.Version)..."
    if ($DeleteApplicationData) {
        Remove-AppxPackage -Package $package.PackageFullName
    } else {
        Remove-AppxPackage -Package $package.PackageFullName -PreserveApplicationData
    }
}

if ($DeleteApplicationData) {
    Write-Host "TokensLimitsExtension package registration and application data removed."
} else {
    Write-Host "TokensLimitsExtension package registration removed; application data preserved."
}
