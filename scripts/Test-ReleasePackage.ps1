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

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ExpectedCertificatePath,

    [string]$ExpectedClassId = 'd76e2329-7747-4ea9-893f-d0e907245b20'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
Add-Type -AssemblyName System.Formats.Asn1

function Read-ZipEntryBytes {
    param([Parameter(Mandatory)][System.IO.Compression.ZipArchiveEntry]$Entry)

    $memoryStream = [System.IO.MemoryStream]::new()
    $entryStream = $Entry.Open()
    try {
        $entryStream.CopyTo($memoryStream)
        return ,$memoryStream.ToArray()
    }
    finally {
        $entryStream.Dispose()
        $memoryStream.Dispose()
    }
}

function Get-Sha256Bytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ,$algorithm.ComputeHash($Bytes)
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-SignedDigest {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][hashtable]$Digests
    )

    if (-not $Digests.ContainsKey($Name)) {
        throw "MSIX signature does not contain the required '$Name' digest."
    }

    $actualDigest = Get-Sha256Bytes -Bytes $Bytes
    if ([Convert]::ToHexString($actualDigest) -ne [Convert]::ToHexString($Digests[$Name])) {
        throw "Package entry for '$Name' does not match its signed SHA-256 digest."
    }
}

function Read-SignedDigestHeader {
    param([Parameter(Mandatory)][byte[]]$P7xBytes)

    if ($P7xBytes.Length -le 4 -or [Convert]::ToHexString($P7xBytes[0..3]) -ne '504B4358') {
        throw 'AppxSignature.p7x has an invalid or missing PKCX header.'
    }

    $cmsBytes = [byte[]]::new($P7xBytes.Length - 4)
    [Array]::Copy($P7xBytes, 4, $cmsBytes, 0, $cmsBytes.Length)
    $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($cmsBytes)
    $cms.CheckSignature($true)

    if ($cms.ContentInfo.ContentType.Value -ne '1.3.6.1.4.1.311.2.1.4') {
        throw "AppxSignature.p7x has unexpected CMS content type '$($cms.ContentInfo.ContentType.Value)'."
    }

    if ($cms.SignerInfos.Count -ne 1 -or $null -eq $cms.SignerInfos[0].Certificate) {
        throw 'AppxSignature.p7x must contain exactly one signer certificate.'
    }

    $contentReader = [System.Formats.Asn1.AsnReader]::new(
        $cms.ContentInfo.Content,
        [System.Formats.Asn1.AsnEncodingRules]::DER)
    $indirectData = $contentReader.ReadSequence()
    $dataType = $indirectData.ReadSequence()
    $dataTypeOid = $dataType.ReadObjectIdentifier()
    if ($dataTypeOid -ne '1.3.6.1.4.1.311.2.1.30') {
        throw "AppxSignature.p7x has unexpected package digest type '$dataTypeOid'."
    }

    if ($dataType.HasData) {
        $null = $dataType.ReadEncodedValue()
    }
    if ($dataType.HasData) {
        throw 'AppxSignature.p7x contains unexpected package digest metadata.'
    }

    $digestInfo = $indirectData.ReadSequence()
    $digestAlgorithm = $digestInfo.ReadSequence()
    $digestAlgorithmOid = $digestAlgorithm.ReadObjectIdentifier()
    if ($digestAlgorithmOid -ne '2.16.840.1.101.3.4.2.1') {
        throw "AppxSignature.p7x uses unsupported digest algorithm '$digestAlgorithmOid'; expected SHA-256."
    }
    if ($digestAlgorithm.HasData) {
        $digestAlgorithm.ReadNull()
    }
    if ($digestAlgorithm.HasData) {
        throw 'AppxSignature.p7x contains unexpected digest algorithm parameters.'
    }

    $headerBytes = $digestInfo.ReadOctetString()
    if ($digestInfo.HasData -or $indirectData.HasData -or $contentReader.HasData) {
        throw 'AppxSignature.p7x contains trailing package digest data.'
    }
    if ($headerBytes.Length -lt 4 -or [Text.Encoding]::ASCII.GetString($headerBytes, 0, 4) -ne 'APPX') {
        throw 'AppxSignature.p7x has an invalid package digest header.'
    }

    $hashBytes = 32
    $remainingBytes = $headerBytes.Length - 4
    if (($remainingBytes % (4 + $hashBytes)) -ne 0) {
        throw 'AppxSignature.p7x has a malformed package digest header.'
    }

    $digestCount = $remainingBytes / (4 + $hashBytes)
    if ($digestCount -notin @(4, 5)) {
        throw "AppxSignature.p7x contains an unsupported number of package digests: $digestCount."
    }

    $digests = @{}
    $offset = 4
    for ($index = 0; $index -lt $digestCount; $index++) {
        $name = [Text.Encoding]::ASCII.GetString($headerBytes, $offset, 4)
        if ($name -notin @('AXPC', 'AXCD', 'AXCT', 'AXBM', 'AXCI') -or $digests.ContainsKey($name)) {
            throw "AppxSignature.p7x contains an invalid or repeated package digest '$name'."
        }

        $digest = [byte[]]::new($hashBytes)
        [Array]::Copy($headerBytes, $offset + 4, $digest, 0, $hashBytes)
        $digests.Add($name, $digest)
        $offset += 4 + $hashBytes
    }

    foreach ($requiredName in @('AXPC', 'AXCD', 'AXCT', 'AXBM')) {
        if (-not $digests.ContainsKey($requiredName)) {
            throw "AppxSignature.p7x does not contain the required '$requiredName' package digest."
        }
    }

    return @{
        Cms = $cms
        Digests = $digests
    }
}

function Assert-BlockMapPayload {
    param(
        [Parameter(Mandatory)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][byte[]]$BlockMapBytes,
        [Parameter(Mandatory)][hashtable]$Digests
    )

    $blockMapStream = [System.IO.MemoryStream]::new($BlockMapBytes, $false)
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 4194304
    $xmlReader = [System.Xml.XmlReader]::Create($blockMapStream, $settings)
    $blockMapDocument = [System.Xml.XmlDocument]::new()
    $blockMapDocument.XmlResolver = $null
    try {
        $blockMapDocument.Load($xmlReader)
    }
    finally {
        $xmlReader.Dispose()
        $blockMapStream.Dispose()
    }

    $namespace = 'http://schemas.microsoft.com/appx/2010/blockmap'
    $root = $blockMapDocument.DocumentElement
    if ($null -eq $root -or $root.LocalName -ne 'BlockMap' -or $root.NamespaceURI -ne $namespace) {
        throw 'AppxBlockMap.xml has an unexpected root element or namespace.'
    }
    if ($root.GetAttribute('HashMethod') -ne 'http://www.w3.org/2001/04/xmlenc#sha256') {
        throw "AppxBlockMap.xml uses unsupported hash method '$($root.GetAttribute('HashMethod'))'."
    }

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($blockMapDocument.NameTable)
    $namespaceManager.AddNamespace('bm', $namespace)
    $fileNodes = $root.SelectNodes('/bm:BlockMap/bm:File', $namespaceManager)
    if ($fileNodes.Count -eq 0) {
        throw 'AppxBlockMap.xml contains no payload files.'
    }

    $mappedNames = [System.Collections.Generic.List[string]]::new()
    $excludedNames = @('[Content_Types].xml', 'AppxBlockMap.xml', 'AppxSignature.p7x')
    $codeIntegrityEntry = $Archive.GetEntry('AppxMetadata/CodeIntegrity.cat')
    if ($null -ne $codeIntegrityEntry) {
        if (-not $Digests.ContainsKey('AXCI')) {
            throw 'AppxMetadata/CodeIntegrity.cat exists but AppxSignature.p7x has no AXCI digest.'
        }
        Assert-SignedDigest -Name 'AXCI' -Bytes (Read-ZipEntryBytes -Entry $codeIntegrityEntry) -Digests $Digests
        $excludedNames += 'AppxMetadata/CodeIntegrity.cat'
    }
    elseif ($Digests.ContainsKey('AXCI')) {
        throw 'AppxSignature.p7x has an AXCI digest but the package has no CodeIntegrity.cat.'
    }

    $blockSize = 65536
    foreach ($fileNode in $fileNodes) {
        $name = $fileNode.GetAttribute('Name')
        $canonicalName = $name.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($name) -or $canonicalName.StartsWith('/') -or
            @($canonicalName.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
            throw "AppxBlockMap.xml contains an invalid package path '$name'."
        }
        if (@($mappedNames | Where-Object { $_ -ieq $canonicalName }).Count -gt 0) {
            throw "AppxBlockMap.xml contains a duplicate payload path '$name'."
        }
        $mappedNames.Add($canonicalName)

        $entry = $Archive.GetEntry($canonicalName)
        if ($null -eq $entry) {
            throw "AppxBlockMap.xml lists missing package entry '$name'."
        }
        if (-not $fileNode.HasAttribute('Size') -or $fileNode.GetAttribute('Size') -notmatch '^\d+$') {
            throw "AppxBlockMap.xml has an invalid file size for '$name'."
        }
        $fileSize = [UInt64]$fileNode.GetAttribute('Size')
        if ($fileSize -ne [UInt64]$entry.Length) {
            throw "Package entry '$name' size differs from AppxBlockMap.xml."
        }

        $blocks = $fileNode.SelectNodes('./bm:Block', $namespaceManager)
        $expectedBlockCount = if ($fileSize -eq 0) { 0 } else { [Math]::Ceiling($fileSize / [double]$blockSize) }
        if ($blocks.Count -ne $expectedBlockCount) {
            throw "Package entry '$name' has $($blocks.Count) block hashes; expected $expectedBlockCount."
        }

        $hasCompressedBlockSizes = $false
        $hasMissingBlockSizes = $false
        [UInt64]$compressedBlockSizeTotal = 0
        $offset = [UInt64]0
        $entryStream = $entry.Open()
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            for ($index = 0; $index -lt $blocks.Count; $index++) {
                $block = $blocks[$index]
                if ($block.ChildNodes.Count -ne 0 -or $block.GetAttribute('Hash') -notmatch '^([A-Za-z0-9+/]{43}=)$') {
                    throw "AppxBlockMap.xml has a malformed block entry for '$name'."
                }

                if ($block.HasAttribute('Size')) {
                    $hasCompressedBlockSizes = $true
                    if ($block.GetAttribute('Size') -notmatch '^\d+$') {
                        throw "AppxBlockMap.xml has an invalid compressed block size for '$name'."
                    }
                    $compressedBlockSize = [UInt64]$block.GetAttribute('Size')
                    if ($compressedBlockSize -eq 0 -or $compressedBlockSize -gt $blockSize) {
                        throw "AppxBlockMap.xml has an out-of-range compressed block size for '$name'."
                    }
                    $compressedBlockSizeTotal += $compressedBlockSize
                }
                else {
                    $hasMissingBlockSizes = $true
                }

                if ($hasCompressedBlockSizes -and $hasMissingBlockSizes) {
                    throw "AppxBlockMap.xml mixes compressed and uncompressed block metadata for '$name'."
                }

                $expectedLength = [int][Math]::Min([UInt64]$blockSize, $fileSize - $offset)
                $chunk = [byte[]]::new($expectedLength)
                $bytesRead = 0
                while ($bytesRead -lt $chunk.Length) {
                    $read = $entryStream.Read($chunk, $bytesRead, $chunk.Length - $bytesRead)
                    if ($read -eq 0) {
                        throw "Package entry '$name' ended before its declared size."
                    }
                    $bytesRead += $read
                }

                $expectedHash = [Convert]::FromBase64String($block.GetAttribute('Hash'))
                if ($expectedHash.Length -ne 32) {
                    throw "AppxBlockMap.xml has a non-SHA-256 block hash for '$name'."
                }
                $actualHash = $algorithm.ComputeHash($chunk)
                if ([Convert]::ToHexString($actualHash) -ne [Convert]::ToHexString($expectedHash)) {
                    throw "Package entry '$name' block $index does not match its SHA-256 hash."
                }

                $offset += [UInt64]$expectedLength
            }

            if ($entryStream.ReadByte() -ne -1) {
                throw "Package entry '$name' contains data beyond the size declared in AppxBlockMap.xml."
            }
        }
        finally {
            $algorithm.Dispose()
            $entryStream.Dispose()
        }

        if ($hasCompressedBlockSizes) {
            $compressedLength = [UInt64]$entry.CompressedLength
            $validCompressedLength = $compressedBlockSizeTotal -eq $compressedLength
            if (-not $validCompressedLength -and $compressedLength -ge 2) {
                $validCompressedLength = $compressedBlockSizeTotal -eq ($compressedLength - 2)
            }
            if (-not $validCompressedLength) {
                throw "Package entry '$name' compressed size does not match AppxBlockMap.xml."
            }
        }
        elseif ([UInt64]$entry.CompressedLength -ne [UInt64]$entry.Length -and
            -not ($fileSize -eq 0 -and [UInt64]$entry.CompressedLength -eq 2)) {
            throw "Uncompressed block metadata for '$name' does not match the package entry."
        }
    }

    $actualPayloadNames = @($Archive.Entries | ForEach-Object FullName | Where-Object { $_ -notin $excludedNames })
    if ($actualPayloadNames.Count -ne $mappedNames.Count) {
        throw 'AppxBlockMap.xml does not describe every payload entry in the package.'
    }
    foreach ($name in $actualPayloadNames) {
        if (-not ($mappedNames | Where-Object { $_ -ceq $name })) {
            throw "Package payload entry '$name' is missing from AppxBlockMap.xml."
        }
    }
}

$package = Get-Item -LiteralPath $PackagePath -Force
$expectedCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $ExpectedCertificatePath))
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = @($archive.Entries)
    if ($entries.Count -eq 0 -or $entries[-1].FullName -ne 'AppxSignature.p7x') {
        throw "Package '$($package.Name)' does not end with AppxSignature.p7x."
    }

    for ($index = 0; $index -lt $entries.Count; $index++) {
        if (@($entries | Where-Object { $_.FullName -ieq $entries[$index].FullName }).Count -gt 1) {
            throw "Package '$($package.Name)' contains duplicate entry '$($entries[$index].FullName)'."
        }
    }

    $signatureEntry = $archive.GetEntry('AppxSignature.p7x')
    $signatureResult = Read-SignedDigestHeader -P7xBytes (Read-ZipEntryBytes -Entry $signatureEntry)
    $signerCertificate = $signatureResult.Cms.SignerInfos[0].Certificate
    if ([Convert]::ToBase64String($signerCertificate.RawData) -ne [Convert]::ToBase64String($expectedCertificate.RawData)) {
        throw "Package '$($package.Name)' signer certificate does not match '$ExpectedCertificatePath'."
    }
    if ($signerCertificate.Subject -ne $ExpectedPublisher -or $expectedCertificate.Subject -ne $ExpectedPublisher) {
        throw "Package '$($package.Name)' signer subject does not match expected publisher '$ExpectedPublisher'."
    }
    if ([DateTime]::UtcNow -lt $signerCertificate.NotBefore.ToUniversalTime() -or
        [DateTime]::UtcNow -gt $signerCertificate.NotAfter.ToUniversalTime()) {
        throw "Package '$($package.Name)' signer certificate is outside its validity period."
    }

    $digitalSignatureUsage = $false
    $codeSigningUsage = $false
    foreach ($extension in $signerCertificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension] -and
            ($extension.KeyUsages -band [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature)) {
            $digitalSignatureUsage = $true
        }
        elseif ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            $codeSigningUsage = @($extension.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
        }
    }
    if (-not $digitalSignatureUsage -or -not $codeSigningUsage) {
        throw "Package '$($package.Name)' signer certificate is not restricted to code signing."
    }

    $contentTypesEntry = $archive.GetEntry('[Content_Types].xml')
    $blockMapEntry = $archive.GetEntry('AppxBlockMap.xml')
    if ($null -eq $contentTypesEntry -or $null -eq $blockMapEntry) {
        throw "Package '$($package.Name)' is missing required MSIX signature metadata."
    }
    $digests = $signatureResult.Digests
    Assert-SignedDigest -Name 'AXCT' -Bytes (Read-ZipEntryBytes -Entry $contentTypesEntry) -Digests $digests
    $blockMapBytes = Read-ZipEntryBytes -Entry $blockMapEntry
    Assert-SignedDigest -Name 'AXBM' -Bytes $blockMapBytes -Digests $digests
    Assert-BlockMapPayload -Archive $archive -BlockMapBytes $blockMapBytes -Digests $digests

    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) {
        throw "Package '$($package.Name)' does not contain AppxManifest.xml."
    }

    $manifestBytes = Read-ZipEntryBytes -Entry $manifestEntry
    $manifestStream = [System.IO.MemoryStream]::new($manifestBytes, $false)
    $manifestSettings = [System.Xml.XmlReaderSettings]::new()
    $manifestSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $manifestSettings.XmlResolver = $null
    $manifestSettings.MaxCharactersInDocument = 4194304
    $manifestReader = [System.Xml.XmlReader]::Create($manifestStream, $manifestSettings)
    $manifestDocument = [System.Xml.XmlDocument]::new()
    $manifestDocument.XmlResolver = $null
    try {
        $manifestDocument.Load($manifestReader)
    }
    finally {
        $manifestReader.Dispose()
        $manifestStream.Dispose()
    }

    $identity = $manifestDocument.DocumentElement.Identity
    if ($identity.Version -ne $ExpectedVersion) {
        throw "Package '$($package.Name)' has version '$($identity.Version)', expected '$ExpectedVersion'."
    }

    if ($identity.Publisher -ne $ExpectedPublisher) {
        throw "Package '$($package.Name)' has publisher '$($identity.Publisher)', expected '$ExpectedPublisher'."
    }

    if ($identity.ProcessorArchitecture -ne $ExpectedArchitecture) {
        throw "Package '$($package.Name)' has architecture '$($identity.ProcessorArchitecture)', expected '$ExpectedArchitecture'."
    }

    $classNodes = $manifestDocument.SelectNodes("//*[local-name()='Class']")
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
    $expectedCertificate.Dispose()
}

Write-Host "Verified the signed CMS and pinned the signer for $($package.Name): version $ExpectedVersion, publisher $ExpectedPublisher, architecture $ExpectedArchitecture, signed block map and payload block hashes matched." -ForegroundColor Green
