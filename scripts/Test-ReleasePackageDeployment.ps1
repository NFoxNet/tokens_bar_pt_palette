[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CertificatePath,

    [Parameter(Mandatory)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory)]
    [string]$ExpectedPublisher,

    [Parameter(Mandatory)]
    [string]$ExpectedPackageName
)

$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Package deployment validation is restricted to an ephemeral GitHub Actions runner.'
}

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'The Windows deployment validation requires an x64 GitHub runner.'
}

$package = Get-Item -LiteralPath $PackagePath -Force
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $CertificatePath))
if ($certificate.HasPrivateKey) {
    $certificate.Dispose()
    throw 'The deployment validation certificate must not contain a private key.'
}
if ($certificate.Subject -ne $ExpectedPublisher) {
    $certificate.Dispose()
    throw "Certificate subject '$($certificate.Subject)' does not match expected publisher '$ExpectedPublisher'."
}

$thumbprint = $certificate.Thumbprint
$trustStorePath = 'Cert:\LocalMachine\TrustedPeople'
$trustedCertificate = @(Get-ChildItem -LiteralPath $trustStorePath | Where-Object Thumbprint -eq $thumbprint)
if ($trustedCertificate.Count -gt 1) {
    $certificate.Dispose()
    throw "The release certificate appears more than once in '$trustStorePath'."
}

$packageNameAlreadyInstalled = @(Get-AppxPackage -Name $ExpectedPackageName)
if ($packageNameAlreadyInstalled.Count -gt 0) {
    $certificate.Dispose()
    throw "Package '$ExpectedPackageName' is already installed for this runner account; refusing to update or remove pre-existing application data."
}

$importedCertificate = $false
$testDirectory = Join-Path $env:RUNNER_TEMP ([Guid]::NewGuid().ToString('N'))

function Get-TestPackage {
    param([Parameter(Mandatory)][string]$PackageName)

    return @(Get-AppxPackage -Name $PackageName | Where-Object {
        $_.Version.ToString() -eq $ExpectedVersion -and $_.Publisher -eq $ExpectedPublisher
    })
}

function Remove-TestPackage {
    param([Parameter(Mandatory)][string]$PackageName)

    foreach ($installedPackage in Get-TestPackage -PackageName $PackageName) {
        Remove-AppxPackage -Package $installedPackage.PackageFullName -PreserveApplicationData -ErrorAction Stop
    }
}

function Get-ZipCentralDirectoryTarget {
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][string]$EntryName
    )

    $tailLength = [Math]::Min($Bytes.Length, 65557)
    $tailOffset = $Bytes.Length - $tailLength
    $endRecordOffset = -1
    for ($index = $tailLength - 22; $index -ge 0; $index--) {
        if ([BitConverter]::ToUInt32($Bytes, $tailOffset + $index) -eq 0x06054b50) {
            $commentLength = [BitConverter]::ToUInt16($Bytes, $tailOffset + $index + 20)
            if ($index + 22 + $commentLength -eq $tailLength) {
                $endRecordOffset = $tailOffset + $index
                break
            }
        }
    }
    if ($endRecordOffset -lt 0) {
        throw 'The MSIX ZIP end-of-central-directory record could not be found.'
    }

    [UInt64]$entryCount = [BitConverter]::ToUInt16($Bytes, $endRecordOffset + 10)
    [UInt64]$centralDirectoryOffset = [BitConverter]::ToUInt32($Bytes, $endRecordOffset + 16)
    if ($entryCount -eq [UInt16]::MaxValue -or $centralDirectoryOffset -eq [UInt32]::MaxValue) {
        $zip64LocatorOffset = $endRecordOffset - 20
        if ($zip64LocatorOffset -lt 0 -or
            [BitConverter]::ToUInt32($Bytes, $zip64LocatorOffset) -ne 0x07064b50) {
            throw 'The MSIX ZIP64 locator is missing or malformed.'
        }

        $zip64RecordOffset = [BitConverter]::ToUInt64($Bytes, $zip64LocatorOffset + 8)
        if ($zip64RecordOffset -gt [UInt64]::MaxValue - 56 -or
            $zip64RecordOffset + 56 -gt [UInt64]$Bytes.Length -or
            [BitConverter]::ToUInt32($Bytes, [int]$zip64RecordOffset) -ne 0x06064b50) {
            throw 'The MSIX ZIP64 end-of-central-directory record is missing or malformed.'
        }

        $entryCount = [BitConverter]::ToUInt64($Bytes, [int]$zip64RecordOffset + 32)
        $centralDirectoryOffset = [BitConverter]::ToUInt64($Bytes, [int]$zip64RecordOffset + 48)
    }

    if ($centralDirectoryOffset -gt [UInt64]::MaxValue - 46 -or
        $centralDirectoryOffset + 46 -gt [UInt64]$Bytes.Length) {
        throw 'The MSIX ZIP central directory offset is outside the package.'
    }

    [UInt64]$offset = $centralDirectoryOffset
    for ([UInt64]$index = 0; $index -lt $entryCount; $index++) {
        if ([BitConverter]::ToUInt32($Bytes, [int]$offset) -ne 0x02014b50) {
            throw "The MSIX central directory entry $index is malformed."
        }

        $nameLength = [BitConverter]::ToUInt16($Bytes, [int]$offset + 28)
        $extraLength = [BitConverter]::ToUInt16($Bytes, [int]$offset + 30)
        $commentLength = [BitConverter]::ToUInt16($Bytes, [int]$offset + 32)
        $recordLength = [UInt64]46 + $nameLength + $extraLength + $commentLength
        if ($offset -gt [UInt64]::MaxValue - $recordLength -or
            $offset + $recordLength -gt [UInt64]$Bytes.Length) {
            throw "The MSIX central directory entry $index extends past the package."
        }

        $actualName = [Text.Encoding]::UTF8.GetString($Bytes, [int]$offset + 46, $nameLength)
        if ($actualName -ceq $EntryName) {
            $localHeaderOffset = [UInt64][BitConverter]::ToUInt32($Bytes, [int]$offset + 42)
            if ($localHeaderOffset -eq [UInt32]::MaxValue) {
                throw "ZIP64 local header offsets are not supported by the release tamper probe for '$EntryName'."
            }
            if ($localHeaderOffset -gt [UInt64]::MaxValue - 30 -or
                $localHeaderOffset + 30 -gt [UInt64]$Bytes.Length -or
                [BitConverter]::ToUInt32($Bytes, [int]$localHeaderOffset) -ne 0x04034b50) {
                throw "The local ZIP header for '$EntryName' is missing or malformed."
            }

            return @{
                CentralHeaderOffset = [int]$offset
                LocalHeaderOffset = [int]$localHeaderOffset
            }
        }

        $offset += $recordLength
    }

    throw "The MSIX central directory does not contain '$EntryName'."
}

function New-TamperedPackage {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][ValidateSet('PairedTimestamp', 'CentralDirectoryAttribute')][string]$Mutation
    )

    $bytes = [IO.File]::ReadAllBytes($SourcePath)
    $offsets = Get-ZipCentralDirectoryTarget -Bytes $bytes -EntryName 'AppxManifest.xml'
    if ($Mutation -eq 'PairedTimestamp') {
        # DOS timestamp is duplicated in both records. Keep both copies equal so the ZIP remains valid.
        $newMinuteByte = $bytes[$offsets.LocalHeaderOffset + 10] -bxor 0x20
        $bytes[$offsets.LocalHeaderOffset + 10] = $newMinuteByte
        $bytes[$offsets.CentralHeaderOffset + 12] = $newMinuteByte
    }
    else {
        # MakeAppx emits DOS-compatible central-directory attributes. Toggle only the archive bit;
        # the record remains structurally valid and its local payload is unchanged.
        $versionMadeByPlatform = $bytes[$offsets.CentralHeaderOffset + 5]
        if ($versionMadeByPlatform -ne 0) {
            throw "The central-directory attribute probe expects DOS-compatible entries; found platform $versionMadeByPlatform."
        }
        $bytes[$offsets.CentralHeaderOffset + 38] = $bytes[$offsets.CentralHeaderOffset + 38] -bxor 0x20
    }

    [IO.File]::WriteAllBytes($DestinationPath, $bytes)

    $originalArchive = [System.IO.Compression.ZipFile]::OpenRead($SourcePath)
    $tamperedArchive = [System.IO.Compression.ZipFile]::OpenRead($DestinationPath)
    try {
        if ($originalArchive.Entries.Count -ne $tamperedArchive.Entries.Count) {
            throw "The $Mutation tamper probe changed the ZIP entry count."
        }

        foreach ($entryName in @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml')) {
            $originalEntry = $originalArchive.GetEntry($entryName)
            $tamperedEntry = $tamperedArchive.GetEntry($entryName)
            if ($null -eq $originalEntry -or $null -eq $tamperedEntry) {
                throw "The $Mutation tamper probe lost required ZIP entry '$entryName'."
            }

            $originalStream = $originalEntry.Open()
            $tamperedStream = $tamperedEntry.Open()
            $originalHash = [System.Security.Cryptography.SHA256]::HashData($originalStream)
            $tamperedHash = [System.Security.Cryptography.SHA256]::HashData($tamperedStream)
            $originalStream.Dispose()
            $tamperedStream.Dispose()
            if ([Convert]::ToHexString($originalHash) -ne [Convert]::ToHexString($tamperedHash)) {
                throw "The $Mutation tamper probe changed the contents of '$entryName'."
            }
        }
    }
    finally {
        $originalArchive.Dispose()
        $tamperedArchive.Dispose()
    }
}

function Assert-InstallRejected {
    param(
        [Parameter(Mandatory)][string]$TamperedPackagePath,
        [Parameter(Mandatory)][string]$Mutation
    )

    $installFailure = $null
    try {
        Add-AppxPackage -Path $TamperedPackagePath -ErrorAction Stop
    }
    catch {
        $installFailure = $_
    }

    $registeredPackages = @(Get-TestPackage -PackageName $ExpectedPackageName)
    if ($null -eq $installFailure -or $registeredPackages.Count -gt 0) {
        Remove-TestPackage -PackageName $ExpectedPackageName
        throw "Windows deployment accepted the $Mutation-tampered package."
    }

    $failureDetails = [Collections.Generic.List[string]]::new()
    $failureDetails.Add($installFailure.ToString())
    if ($installFailure.Exception) {
        $failureDetails.Add($installFailure.Exception.ToString())
    }

    $activityMatch = [regex]::Match(($failureDetails -join "`n"), '(?i)\[ActivityId\]\s*(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})')
    if (-not $activityMatch.Success) {
        throw "Windows rejected the $Mutation-tampered package without an ActivityId; the failure cannot be classified. Details: $($failureDetails -join "`n")"
    }

    $activityId = [Guid]::Parse($activityMatch.Groups['id'].Value)
    $deploymentLog = @(Get-AppxLog -ActivityId $activityId -ErrorAction Stop)
    $failureDetails.Add(($deploymentLog | ForEach-Object { "[$($_.Id)] $($_.Message)" }) -join "`n")

    $signatureFailurePattern = '(?i)(0x800b0004|0x80073d04|0x80080205|0x80096010|\b800b0004\b|\b80073d04\b|\b80080205\b|\b80096010\b|TRUST_E_SUBJECT_NOT_TRUSTED|ERROR_INVALID_STAGED_SIGNATURE|APPX_E_INVALID_BLOCKMAP|TRUST_E_BAD_DIGEST)'
    $failureText = $failureDetails -join "`n"
    $signatureFailure = $failureText -match $signatureFailurePattern
    if (-not $signatureFailure) {
        $eventStart = (Get-Date).AddMinutes(-5)
        foreach ($logName in @('Microsoft-Windows-AppxPackaging/Operational', 'Microsoft-Windows-AppXDeploymentServer/Operational')) {
            $events = @(Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = $eventStart } -ErrorAction Stop)
            $relatedEvents = @($events | Where-Object {
                $_.ActivityId -eq $activityId -or ($_.Message -and $_.Message.Contains($activityId.ToString()))
            })
            $failureDetails.Add(($relatedEvents | ForEach-Object { "[$logName/$($_.Id)] $($_.Message)" }) -join "`n")
        }
        $failureText = $failureDetails -join "`n"
        $signatureFailure = $failureText -match $signatureFailurePattern
    }

    if (-not $signatureFailure) {
        throw "Windows rejected the $Mutation-tampered package, but the failure did not identify a signature or integrity problem. Details: $failureText"
    }

    Write-Host "Windows deployment rejected the $Mutation-tampered package for a signature or integrity failure."
}

try {
    New-Item -ItemType Directory -Path $testDirectory | Out-Null

    if ($trustedCertificate.Count -eq 0) {
        Import-Certificate -FilePath (Resolve-Path -LiteralPath $CertificatePath) -CertStoreLocation $trustStorePath | Out-Null
        $importedCertificate = $true
    }

    $validInstallSucceeded = $false
    try {
        Add-AppxPackage -Path $package.FullName -ErrorAction Stop
        $validInstallSucceeded = $true
    }
    catch {
        throw "Windows deployment rejected the signed x64 package: $($_.Exception.Message)"
    }

    $installedPackages = @(Get-TestPackage -PackageName $ExpectedPackageName)
    if (-not $validInstallSucceeded -or $installedPackages.Count -ne 1 -or
        $installedPackages[0].Architecture.ToString() -ne 'X64') {
        Remove-TestPackage -PackageName $ExpectedPackageName
        throw "Windows deployment did not register the expected x64 package '$ExpectedPackageName' v$ExpectedVersion."
    }

    Remove-TestPackage -PackageName $ExpectedPackageName

    foreach ($mutation in @('PairedTimestamp', 'CentralDirectoryAttribute')) {
        $tamperedPackagePath = Join-Path $testDirectory "tampered-$mutation.msix"
        New-TamperedPackage -SourcePath $package.FullName -DestinationPath $tamperedPackagePath -Mutation $mutation
        Assert-InstallRejected -TamperedPackagePath $tamperedPackagePath -Mutation $mutation
    }
}
finally {
    try {
        Remove-TestPackage -PackageName $ExpectedPackageName
    }
    finally {
        try {
            if ($importedCertificate) {
                Remove-Item -LiteralPath (Join-Path $trustStorePath $thumbprint) -Force -ErrorAction Stop
            }
        }
        finally {
            try {
                $certificate.Dispose()
            }
            finally {
                if (Test-Path -LiteralPath $testDirectory) {
                    $runnerTempPath = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                    $testDirectoryPath = [IO.Path]::GetFullPath($testDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                    $runnerTempPrefix = $runnerTempPath + [IO.Path]::DirectorySeparatorChar
                    if (-not $testDirectoryPath.StartsWith($runnerTempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Refusing to remove deployment test directory outside RUNNER_TEMP: '$testDirectoryPath'."
                    }
                    Remove-Item -LiteralPath $testDirectoryPath -Recurse -Force -ErrorAction Stop
                }
            }
        }
    }
}

Write-Host "Windows deployment validated $($package.Name) and rejected consistent ZIP timestamp and central-directory attribute changes as signature or integrity failures." -ForegroundColor Green
