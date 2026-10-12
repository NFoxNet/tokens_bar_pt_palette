[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$SmokeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh smoke output directory.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$smoke = (Resolve-Path -LiteralPath $SmokeDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output | Out-Null
$extracted = Join-Path $output 'package'
$hostDirectory = Join-Path $output 'host'
[IO.Compression.ZipFile]::ExtractToDirectory($package, $extracted)
New-Item -ItemType Directory -Path $hostDirectory | Out-Null
# Keep the test bootstrap/.NET files; use only package XAML resources and production projections.
Get-ChildItem -LiteralPath $smoke -File | Copy-Item -Destination $hostDirectory
Get-ChildItem -LiteralPath $extracted -Filter '*.dll' | Where-Object {
    $_.Name -like 'TokensLimitsExtension*' -or $_.Name -like 'CommunityToolkit.*' -or
    $_.Name -like 'JPSoftworks.*'
} | Copy-Item -Destination $hostDirectory -Force
foreach ($name in @('Microsoft.WinUI.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll')) {
    Copy-Item -LiteralPath (Join-Path $extracted $name) -Destination $hostDirectory -Force
}
foreach ($name in @('resources.pri', 'Assets', 'lang')) {
    Copy-Item -LiteralPath (Join-Path $extracted $name) -Destination $hostDirectory -Recurse -Force
}
& (Join-Path $hostDirectory 'TokensLimitsExtension.NativeSmokeTests.exe') --startup-only
if ($LASTEXITCODE -ne 0) { throw "Release native settings startup failed: $LASTEXITCODE" }