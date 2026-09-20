[CmdletBinding()]
param(
    [string]$ProcessName = 'TokensLimitsExtension',
    [int]$ProcessId,
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 30,
    [ValidateRange(100, 60000)]
    [int]$SampleIntervalMilliseconds = 1000,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Get-ProcessSample {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction SilentlyContinue
    [pscustomobject]@{
        TimestampUtc = [DateTimeOffset]::UtcNow
        CpuSeconds = [math]::Round($Process.CPU, 6)
        PrivateBytes = $Process.PrivateMemorySize64
        WorkingSetBytes = $Process.WorkingSet64
        Handles = $Process.HandleCount
        Threads = $Process.Threads.Count
        PageFaults = if ($null -ne $cim) { [int64]$cim.PageFaults } else { $null }
        ReadBytes = if ($null -ne $cim) { [int64]$cim.ReadTransferCount } else { $null }
        WriteBytes = if ($null -ne $cim) { [int64]$cim.WriteTransferCount } else { $null }
    }
}

if ($ProcessId -gt 0) {
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
}
else {
    $process = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
}

$samples = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
    try {
        $samples.Add((Get-ProcessSample -Process $process))
    }
    catch [System.InvalidOperationException] {
        break
    }

    $remainingMilliseconds = [math]::Max(1, [int](($DurationSeconds - $stopwatch.Elapsed.TotalSeconds) * 1000))
    Start-Sleep -Milliseconds ([math]::Min($SampleIntervalMilliseconds, $remainingMilliseconds))
}

try {
    $samples.Add((Get-ProcessSample -Process $process))
}
catch [System.InvalidOperationException] {
    # The process can exit naturally while the final sample is collected.
}

if ($samples.Count -lt 2) {
    throw "Process '$($process.ProcessName)' exited before two baseline samples were collected."
}

$first = $samples[0]
$last = $samples[$samples.Count - 1]
$privatePeak = ($samples | Measure-Object -Property PrivateBytes -Maximum).Maximum
$workingSetPeak = ($samples | Measure-Object -Property WorkingSetBytes -Maximum).Maximum
$handlesPeak = ($samples | Measure-Object -Property Handles -Maximum).Maximum
$threadsPeak = ($samples | Measure-Object -Property Threads -Maximum).Maximum
$elapsedSeconds = ($last.TimestampUtc - $first.TimestampUtc).TotalSeconds
$cpuDelta = [math]::Max(0, $last.CpuSeconds - $first.CpuSeconds)
$processorCount = [math]::Max(1, [Environment]::ProcessorCount)

$summary = [ordered]@{
    SchemaVersion = 1
    ProcessName = $process.ProcessName
    ProcessId = $process.Id
    MachineName = [Environment]::MachineName
    ProcessorCount = $processorCount
    StartedUtc = $startedAt
    FirstSampleUtc = $first.TimestampUtc
    LastSampleUtc = $last.TimestampUtc
    DurationSeconds = [math]::Round($elapsedSeconds, 3)
    SampleIntervalMilliseconds = $SampleIntervalMilliseconds
    SampleCount = $samples.Count
    CpuSecondsDelta = [math]::Round($cpuDelta, 3)
    CpuPercentSingleCore = [math]::Round(($cpuDelta / [math]::Max(0.001, $elapsedSeconds)) * 100, 3)
    CpuPercentMachine = [math]::Round(($cpuDelta / [math]::Max(0.001, $elapsedSeconds) / $processorCount) * 100, 3)
    PrivateBytesStart = $first.PrivateBytes
    PrivateBytesEnd = $last.PrivateBytes
    PrivateBytesPeak = $privatePeak
    PrivateBytesDelta = $last.PrivateBytes - $first.PrivateBytes
    WorkingSetStart = $first.WorkingSetBytes
    WorkingSetEnd = $last.WorkingSetBytes
    WorkingSetPeak = $workingSetPeak
    ReadBytesDelta = if ($null -ne $first.ReadBytes -and $null -ne $last.ReadBytes) { $last.ReadBytes - $first.ReadBytes } else { $null }
    WriteBytesDelta = if ($null -ne $first.WriteBytes -and $null -ne $last.WriteBytes) { $last.WriteBytes - $first.WriteBytes } else { $null }
    PageFaultsDelta = if ($null -ne $first.PageFaults -and $null -ne $last.PageFaults) { $last.PageFaults - $first.PageFaults } else { $null }
    HandlesStart = $first.Handles
    HandlesEnd = $last.Handles
    HandlesPeak = $handlesPeak
    ThreadsStart = $first.Threads
    ThreadsEnd = $last.Threads
    ThreadsPeak = $threadsPeak
    Samples = @($samples)
}

$json = $summary | ConvertTo-Json -Depth 5
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $resolvedOutput
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding utf8NoBOM
}

$json
