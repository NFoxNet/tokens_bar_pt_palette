[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$global:TokensLimitsTestPackages = @()
$global:TokensLimitsRemovalCalls = [System.Collections.Generic.List[object]]::new()

function global:Get-AppxPackage {
    return $global:TokensLimitsTestPackages
}

function global:Remove-AppxPackage {
    param(
        [Parameter(Mandatory)][string]$Package,
        [switch]$PreserveApplicationData
    )

    $global:TokensLimitsRemovalCalls.Add([pscustomobject]@{
        Package = $Package
        PreserveApplicationData = $PreserveApplicationData.IsPresent
    })
}

try {
    $unregisterPath = Join-Path $PSScriptRoot '..\..\unregister.ps1'
    $global:TokensLimitsTestPackages = @([pscustomobject]@{
        Name = 'TokensLimitsExtension'
        PackageFullName = 'TokensLimitsExtension_0.0.5.4_x64__test'
        Version = [version]'0.0.5.4'
        IsDevelopmentMode = $false
    })

    $signedPackageRejected = $false
    try {
        & $unregisterPath
    }
    catch {
        $signedPackageRejected = $_.Exception.Message -match 'development-mode|signed Release MSIX'
    }

    if (-not $signedPackageRejected -or $global:TokensLimitsRemovalCalls.Count -ne 0) {
        throw 'Default unregister did not reject a signed MSIX before invoking package removal.'
    }

    $global:TokensLimitsTestPackages[0].IsDevelopmentMode = $true
    & $unregisterPath
    if ($global:TokensLimitsRemovalCalls.Count -ne 1 -or
        $global:TokensLimitsRemovalCalls[0].Package -ne $global:TokensLimitsTestPackages[0].PackageFullName -or
        -not $global:TokensLimitsRemovalCalls[0].PreserveApplicationData) {
        throw 'Default unregister did not preserve data when removing a development-mode package.'
    }

    $global:TokensLimitsRemovalCalls.Clear()
    $global:TokensLimitsTestPackages[0].IsDevelopmentMode = $false
    & $unregisterPath -DeleteApplicationData
    if ($global:TokensLimitsRemovalCalls.Count -ne 1 -or
        $global:TokensLimitsRemovalCalls[0].PreserveApplicationData) {
        throw 'Explicit DeleteApplicationData did not remove the signed package without preservation.'
    }

    Write-Host 'Unregister tests passed.' -ForegroundColor Green
}
finally {
    Remove-Item Function:\Get-AppxPackage -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Remove-AppxPackage -Force -ErrorAction SilentlyContinue
    Remove-Variable TokensLimitsTestPackages, TokensLimitsRemovalCalls -Scope Global -Force -ErrorAction SilentlyContinue
}
