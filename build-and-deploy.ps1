[CmdletBinding()]
param(
    [string]$ProjectPath = ".\TokensLimitsExtension\TokensLimitsExtension\TokensLimitsExtension.csproj",
    [ValidateSet("x64", "arm64")]
    [string]$Platform = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Get-Location).Path
$projectFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
$projectDirectory = Split-Path -Parent $projectFullPath

if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
    throw "Project file was not found: $projectFullPath"
}

$existingPackages = @(Get-AppxPackage -Name "TokensLimitsExtension" -ErrorAction SilentlyContinue)
if ($existingPackages.Count -gt 0) {
    Write-Host "Removing previous TokensLimitsExtension registration(s)..."
    Get-Process -Name "TokensLimitsExtension","Microsoft.CmdPal.UI","Microsoft.CmdPal.Ext.PowerToys" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 750
    foreach ($existingPackage in $existingPackages) {
        Remove-AppxPackage -Package $existingPackage.PackageFullName
    }
}

Write-Host "Publishing $projectFullPath ($Configuration/$Platform)..."
& dotnet msbuild $projectFullPath /t:Publish "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:EnableMsixTooling=true" "/p:AppxBundle=Never" "/p:UapAppxPackageBuildMode=SideloadOnly" "/p:GenerateAppInstallerFile=false"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet msbuild failed with exit code $LASTEXITCODE"
}

$manifestCandidates = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory "bin") -Filter AppxManifest.xml -File -Recurse -ErrorAction SilentlyContinue)
$manifest = $manifestCandidates |
    Where-Object { $_.FullName -match "\\publish\\AppxManifest\.xml$" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $manifest) {
    $manifest = $manifestCandidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if ($null -eq $manifest) {
    throw "Publish succeeded, but no AppxManifest.xml was found below $projectDirectory\bin"
}

Write-Host "Registering package manifest: $($manifest.FullName)"
Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "Add-AppxPackage failed with exit code $LASTEXITCODE"
}

Write-Host "TokensLimitsExtension was published and registered successfully."
