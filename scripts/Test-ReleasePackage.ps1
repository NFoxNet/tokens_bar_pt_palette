[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$ExpectedArchitecture,

    [Parameter(Mandatory)]
    [string]$ExpectedPublisher,

    [string]$ExpectedClassId = 'd76e2329-7747-4ea9-893f-d0e907245b20'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$package = Get-Item -LiteralPath $PackagePath -Force
$signature = Get-AuthenticodeSignature -LiteralPath $package.FullName
if ($signature.Status -ne 'Valid') {
    throw "Package '$($package.Name)' signature is '$($signature.Status)': $($signature.StatusMessage)"
}

if ($signature.SignerCertificate.Subject -ne $ExpectedPublisher) {
    throw "Package '$($package.Name)' is signed by '$($signature.SignerCertificate.Subject)', expected '$ExpectedPublisher'."
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) {
        throw "Package '$($package.Name)' does not contain AppxManifest.xml."
    }

    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try {
        [xml]$manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $identity = $manifest.Package.Identity
    if ($identity.Version -ne $ExpectedVersion) {
        throw "Package '$($package.Name)' has version '$($identity.Version)', expected '$ExpectedVersion'."
    }

    if ($identity.Publisher -ne $ExpectedPublisher) {
        throw "Package '$($package.Name)' has publisher '$($identity.Publisher)', expected '$ExpectedPublisher'."
    }

    if ($identity.ProcessorArchitecture -ne $ExpectedArchitecture) {
        throw "Package '$($package.Name)' has architecture '$($identity.ProcessorArchitecture)', expected '$ExpectedArchitecture'."
    }

    $classNodes = $manifest.SelectNodes("//*[local-name()='Class']")
    if (-not ($classNodes | Where-Object { $_.Id -eq $ExpectedClassId })) {
        throw "Package '$($package.Name)' does not contain the expected COM class '$ExpectedClassId'."
    }

    foreach ($requiredEntry in @(
        'Assets/StoreLogo.png',
        'Assets/Square150x150Logo.scale-200.png',
        'Assets/Square44x44Logo.scale-200.png',
        'Assets/SplashScreen.scale-200.png',
        'lang/en.json',
        'lang/ru.json')) {
        if ($null -eq $archive.GetEntry($requiredEntry)) {
            throw "Package '$($package.Name)' is missing required entry '$requiredEntry'."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Validated $($package.Name): version $ExpectedVersion, publisher $ExpectedPublisher, architecture $ExpectedArchitecture." -ForegroundColor Green
