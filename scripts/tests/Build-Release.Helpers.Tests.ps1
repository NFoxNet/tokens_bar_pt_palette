[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$helperPath = Join-Path $PSScriptRoot '..\Build-Release.Helpers.ps1'
. $helperPath

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "tokens-limits-release-tests-$([guid]::NewGuid().ToString('N'))"
$junctionPaths = [System.Collections.Generic.List[string]]::new()
try {
    $repositoryRoot = Join-Path $testRoot 'repository'
    $artifactsRoot = Join-Path $repositoryRoot 'artifacts'
    $sourceDirectory = Join-Path $repositoryRoot 'TokensLimitsExtension'
    $siblingDirectory = Join-Path $testRoot 'repository-copy'
    New-Item -ItemType Directory -Path $artifactsRoot, $sourceDirectory, $siblingDirectory -Force | Out-Null

    $validOutput = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory (Join-Path $artifactsRoot 'v0.0.5.0')
    if ($validOutput -ne [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'v0.0.5.0'))) {
        throw 'A release output directory under artifacts was not accepted.'
    }

    $relativeOutput = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory 'artifacts\v0.0.5.1'
    if ($relativeOutput -ne [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'v0.0.5.1'))) {
        throw 'A relative release output directory was not resolved from the repository root.'
    }

    $deletableOutput = Join-Path $artifactsRoot 'deletable-release'
    $marker = Join-Path $deletableOutput 'marker.txt'
    New-Item -ItemType Directory -Path $deletableOutput -Force | Out-Null
    Set-Content -LiteralPath $marker -Value 'temporary test artifact' -Encoding utf8
    Remove-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory $deletableOutput
    if (Test-Path -LiteralPath $deletableOutput) {
        throw 'A validated release output directory was not removed.'
    }

    if (-not (Test-Path -LiteralPath $sourceDirectory)) {
        throw 'Removing a release output directory affected a sibling source directory.'
    }

    $rejected = $false
    try {
        $null = Get-ValidatedReleaseOutputDirectory -RepositoryRoot 'relative-repository-root' -OutputDirectory 'artifacts\release'
    }
    catch [System.InvalidOperationException] {
        $rejected = $true
    }

    if (-not $rejected) {
        throw 'A relative repository root was accepted.'
    }

    foreach ($unsafeOutput in @($repositoryRoot, $sourceDirectory, $siblingDirectory, $artifactsRoot)) {
        $rejected = $false
        try {
            $null = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory $unsafeOutput
        }
        catch [System.InvalidOperationException] {
            $rejected = $true
        }

        if (-not $rejected) {
            throw "Unsafe output directory was accepted: $unsafeOutput"
        }
    }

    $intermediateDirectory = Join-Path $artifactsRoot 'releases'
    $outsideIntermediate = Join-Path $testRoot 'outside-intermediate'
    New-Item -ItemType Directory -Path $outsideIntermediate -Force | Out-Null
    $junctionPaths.Add($intermediateDirectory)
    New-Item -ItemType Junction -Path $intermediateDirectory -Target $outsideIntermediate | Out-Null
    $rejected = $false
    try {
        $null = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory (Join-Path $intermediateDirectory 'v0.0.5.2')
    }
    catch [System.InvalidOperationException] {
        $rejected = $true
    }

    if (-not $rejected) {
        throw 'An intermediate reparse-point directory was accepted.'
    }

    Remove-Item -LiteralPath $intermediateDirectory -Force

    $candidateJunction = Join-Path $artifactsRoot 'linked-release'
    $junctionPaths.Add($candidateJunction)
    New-Item -ItemType Junction -Path $candidateJunction -Target $outsideIntermediate | Out-Null
    $rejected = $false
    try {
        $null = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory $candidateJunction
    }
    catch [System.InvalidOperationException] {
        $rejected = $true
    }

    if (-not $rejected) {
        throw 'A release output directory that is a reparse point was accepted.'
    }

    Remove-Item -LiteralPath $candidateJunction -Force

    $outputWithLinkedChild = Join-Path $artifactsRoot 'release-with-linked-child'
    New-Item -ItemType Directory -Path $outputWithLinkedChild -Force | Out-Null
    $outsideMarker = Join-Path $outsideIntermediate 'marker.txt'
    Set-Content -LiteralPath $outsideMarker -Value 'outside release output' -Encoding utf8
    $linkedChild = Join-Path $outputWithLinkedChild 'linked-child'
    $junctionPaths.Add($linkedChild)
    New-Item -ItemType Junction -Path $linkedChild -Target $outsideIntermediate | Out-Null
    $rejected = $false
    try {
        Remove-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory $outputWithLinkedChild
    }
    catch [System.InvalidOperationException] {
        $rejected = $true
    }

    if (-not $rejected -or -not (Test-Path -LiteralPath $outsideMarker) -or -not (Test-Path -LiteralPath $outputWithLinkedChild)) {
        throw 'A release output directory containing a reparse point was not safely rejected.'
    }

    Remove-Item -LiteralPath $linkedChild -Force
    Remove-Item -LiteralPath $outputWithLinkedChild -Recurse -Force

    $outsideArtifacts = Join-Path $testRoot 'outside-artifacts'
    New-Item -ItemType Directory -Path $outsideArtifacts -Force | Out-Null
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
    $junctionPaths.Add($artifactsRoot)
    New-Item -ItemType Junction -Path $artifactsRoot -Target $outsideArtifacts | Out-Null

    $rejected = $false
    try {
        $null = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $repositoryRoot -OutputDirectory (Join-Path $artifactsRoot 'v0.0.5.0')
    }
    catch [System.InvalidOperationException] {
        $rejected = $true
    }

    if (-not $rejected) {
        throw 'A reparse-point artifacts directory was accepted.'
    }
}
finally {
    for ($index = $junctionPaths.Count - 1; $index -ge 0; $index--) {
        $junctionPath = $junctionPaths[$index]
        $junctionItem = Get-Item -LiteralPath $junctionPath -Force -ErrorAction SilentlyContinue
        if ($null -ne $junctionItem -and (($junctionItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            Remove-Item -LiteralPath $junctionPath -Force
        }
    }

    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
