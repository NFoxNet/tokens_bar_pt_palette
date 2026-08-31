[CmdletBinding()]
param(
    [string]$ProjectPath = ".\TokensLimitsExtension\TokensLimitsExtension\TokensLimitsExtension.csproj",
    [ValidateSet("x64", "arm64")]
    [string]$Platform = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$projectFullPath = if ([IO.Path]::IsPathRooted($ProjectPath)) {
    [IO.Path]::GetFullPath($ProjectPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
}
$projectDirectory = Split-Path -Parent $projectFullPath

if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
    throw "Project file was not found: $projectFullPath"
}

$extensionProcesses = @(Get-Process -Name "TokensLimitsExtension" -ErrorAction SilentlyContinue)
foreach ($process in $extensionProcesses) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}

$stopDeadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    $runningProcesses = @(Get-Process -Name "TokensLimitsExtension" -ErrorAction SilentlyContinue)
    if ($runningProcesses.Count -eq 0) {
        break
    }

    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $stopDeadline)

if ($runningProcesses.Count -gt 0) {
    throw "Unable to stop TokensLimitsExtension before deployment."
}

Write-Host "Publishing $projectFullPath ($Configuration/$Platform)..."
& dotnet msbuild $projectFullPath /t:Publish "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:EnableMsixTooling=true" "/p:AppxBundle=Never" "/p:UapAppxPackageBuildMode=SideloadOnly" "/p:GenerateAppInstallerFile=false"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet msbuild failed with exit code $LASTEXITCODE"
}

$runtimeIdentifier = "win-$($Platform.ToLowerInvariant())"
$configurationBin = Join-Path $projectDirectory "bin\$Platform\$Configuration"
$manifestCandidates = @(Get-ChildItem -LiteralPath $configurationBin -Filter AppxManifest.xml -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like "*\$runtimeIdentifier" })
if ($manifestCandidates.Count -ne 1) {
    throw "Expected exactly one published AppxManifest.xml for $Configuration/$runtimeIdentifier below $configurationBin, found $($manifestCandidates.Count)."
}
$manifest = $manifestCandidates[0]

Write-Host "Registering package manifest: $($manifest.FullName)"
# Register over the existing package. Removing it first would delete LocalState
# and all DPAPI-protected provider credentials.
Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
$registeredPackage = @(Get-AppxPackage -Name "TokensLimitsExtension" -ErrorAction SilentlyContinue |
    Where-Object { $_.InstallLocation -eq $manifest.DirectoryName })
if ($registeredPackage.Count -eq 0) {
    throw "Package registration completed without a discoverable TokensLimitsExtension package at $($manifest.DirectoryName)."
}

Write-Host "TokensLimitsExtension was published and registered successfully."
