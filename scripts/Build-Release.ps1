[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CertificatePath,

    [Parameter(Mandatory)]
    [securestring]$CertificatePassword,

    [ValidateSet('x64', 'ARM64')]
    [string[]]$Platform = @('x64', 'ARM64'),

    [string]$Configuration = 'Release',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$solutionPath = Join-Path $repositoryRoot 'TokensLimitsExtension\TokensLimitsExtension.sln'
$manifestPath = Join-Path $repositoryRoot 'TokensLimitsExtension\TokensLimitsExtension\Package.appxmanifest'
$certificate = Import-PfxCertificate -FilePath $CertificatePath -Password $CertificatePassword -CertStoreLocation 'Cert:\CurrentUser\My'
[xml]$manifest = Get-Content -LiteralPath $manifestPath
$publisher = $manifest.Package.Identity.Publisher

if ($certificate.Subject -ne $publisher) {
    throw "The certificate subject '$($certificate.Subject)' must exactly match manifest Publisher '$publisher'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    $resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
    $resolvedRepository = (Resolve-Path -LiteralPath $repositoryRoot).Path
    if (-not $resolvedOutput.StartsWith($resolvedRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an output directory outside the repository: $resolvedOutput"
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    foreach ($architecture in $Platform) {
        $runtimeIdentifier = "win-$($architecture.ToLowerInvariant())"
        dotnet restore $solutionPath -p:Platform=$architecture -p:RuntimeIdentifier=$runtimeIdentifier -p:PublishReadyToRun=true
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $architecture." }
    }

    foreach ($architecture in $Platform) {
        $packageDirectory = Join-Path $OutputDirectory "$architecture\\"
        dotnet build $solutionPath --configuration $Configuration --no-restore `
            -p:Platform=$architecture `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxPackageDir=$packageDirectory `
            -p:PackageCertificateThumbprint=$certificate.Thumbprint

        if ($LASTEXITCODE -ne 0) { throw "MSIX build failed for $architecture." }
    }
}
finally {
    Pop-Location
}

$packages = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Filter '*.msix' |
    Where-Object { $_.Name -notlike '*Dependencies*' })
if ($packages.Count -ne $Platform.Count) {
    throw "Expected $($Platform.Count) MSIX packages, found $($packages.Count)."
}

$publishedPackages = foreach ($package in $packages) {
    $destination = Join-Path $OutputDirectory $package.Name
    Copy-Item -LiteralPath $package.FullName -Destination $destination -Force
    Get-Item -LiteralPath $destination
}

$signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if ($null -eq $signTool) {
    throw 'Windows SDK signtool.exe was not found. Install the Windows SDK before creating a public release.'
}

foreach ($package in $publishedPackages) {
    & $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint /s My /v $package.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($package.Name)." }
}

$certificateOutput = Join-Path $OutputDirectory 'NFoxNet.TokensLimitsExtension.cer'
Export-Certificate -Cert $certificate -FilePath $certificateOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts\Install-TokensLimitsExtension.ps1') -Destination (Join-Path $OutputDirectory 'Install-TokensLimitsExtension.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts\Install-TokensLimitsExtension.cmd') -Destination (Join-Path $OutputDirectory 'Install-TokensLimitsExtension.cmd') -Force

$checksumFiles = @($publishedPackages | ForEach-Object FullName) + @(
    $certificateOutput,
    (Join-Path $OutputDirectory 'Install-TokensLimitsExtension.ps1'),
    (Join-Path $OutputDirectory 'Install-TokensLimitsExtension.cmd'))
Get-FileHash -Algorithm SHA256 -LiteralPath $checksumFiles |
    ForEach-Object { '{0} *{1}' -f $_.Hash, (Split-Path $_.Path -Leaf) } |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Release artifacts are available at $OutputDirectory" -ForegroundColor Green
