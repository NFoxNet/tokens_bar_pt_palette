Set-StrictMode -Version Latest

function Get-ValidatedReleaseOutputDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )

    try {
        if (-not [System.IO.Path]::IsPathFullyQualified($RepositoryRoot)) {
            throw [System.ArgumentException]::new('The repository root must be an absolute path.', 'RepositoryRoot')
        }

        $normalizedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
        $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $normalizedRepositoryRoot 'artifacts'))
        $normalizedOutput = if ([System.IO.Path]::IsPathFullyQualified($OutputDirectory)) {
            [System.IO.Path]::GetFullPath($OutputDirectory)
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $normalizedRepositoryRoot $OutputDirectory))
        }
    }
    catch {
        throw [System.InvalidOperationException]::new('The release output directory is not a valid file-system path.', $_.Exception)
    }

    if ($normalizedOutput.StartsWith('\\?\', [System.StringComparison]::Ordinal) -or
        $normalizedOutput.StartsWith('\\.\', [System.StringComparison]::Ordinal)) {
        throw [System.InvalidOperationException]::new('Extended device paths are not valid release output directories.')
    }

    $artifactsPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $normalizedOutput.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw [System.InvalidOperationException]::new("The release output directory must be a child of '$artifactsRoot'.")
    }

    Assert-NoReparsePoints -ArtifactsRoot $artifactsRoot -OutputDirectory $normalizedOutput
    return $normalizedOutput
}

function Remove-ValidatedReleaseOutputDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )

    $validatedOutput = Get-ValidatedReleaseOutputDirectory -RepositoryRoot $RepositoryRoot -OutputDirectory $OutputDirectory
    if (-not (Test-Path -LiteralPath $validatedOutput)) {
        return
    }

    $item = Get-Item -LiteralPath $validatedOutput -Force
    if (-not $item.PSIsContainer) {
        throw [System.InvalidOperationException]::new("The release output path '$validatedOutput' is not a directory.")
    }

    $normalizedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $normalizedRepositoryRoot 'artifacts'))
    Assert-NoReparsePoints -ArtifactsRoot $artifactsRoot -OutputDirectory $validatedOutput
    Assert-DirectoryContainsNoReparsePoints -Path $validatedOutput
    Remove-Item -LiteralPath $validatedOutput -Recurse -Force
}

function Assert-NoReparsePoints {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactsRoot,

        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )

    $currentPath = $ArtifactsRoot
    Assert-PathIsNotReparsePoint -Path $currentPath

    $relativePath = $OutputDirectory.Substring($ArtifactsRoot.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    foreach ($segment in $relativePath.Split([char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar), [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        Assert-PathIsNotReparsePoint -Path $currentPath
    }
}

function Assert-PathIsNotReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $attributes = (Get-Item -LiteralPath $Path -Force).Attributes
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw [System.InvalidOperationException]::new("The release output path contains a reparse point: '$Path'.")
    }
}

function Assert-DirectoryContainsNoReparsePoints {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $directory = [System.IO.DirectoryInfo]::new($Path)
    foreach ($entry in $directory.EnumerateFileSystemInfos()) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw [System.InvalidOperationException]::new("The release output directory contains a reparse point: '$($entry.FullName)'.")
        }

        if ($entry -is [System.IO.DirectoryInfo]) {
            Assert-DirectoryContainsNoReparsePoints -Path $entry.FullName
        }
    }
}
