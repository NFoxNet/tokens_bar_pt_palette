[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'evidence.json')
)

# Audit artifact for commit 2e6314a. These probes reproduce existing defects;
# they are not product regression tests and must be reviewed after fixes.
# All providers are existing synthetic test fixtures. HTTP is stubbed.
$ErrorActionPreference = 'Stop'
$solutionDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$unitDirectory = Join-Path $solutionDirectory 'TokensLimitsExtension.Tests\bin\x64\Debug\net10.0'
$uiDirectory = Join-Path $solutionDirectory 'TokensLimitsExtension.IntegrationTests\bin\x64\Debug\net10.0-windows10.0.26100.0'

foreach ($requiredPath in @(
    (Join-Path $unitDirectory 'TokensLimitsExtension.Core.dll'),
    (Join-Path $unitDirectory 'TokensLimitsExtension.Tests.dll'),
    (Join-Path $uiDirectory 'TokensLimitsExtension.dll')
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw 'Build the solution in Debug x64 before running this audit probe.'
    }
}

[void][Reflection.Assembly]::LoadFrom((Join-Path $unitDirectory 'TokensLimitsExtension.Core.dll'))
$testAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $unitDirectory 'TokensLimitsExtension.Tests.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $uiDirectory 'Microsoft.CommandPalette.Extensions.Toolkit.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $uiDirectory 'TokensLimitsExtension.dll'))

function New-AuditFixture {
    param([string]$Name, [object[]]$Arguments = @())
    $fixtureType = $testAssembly.GetType("TokensLimitsExtension.Tests.$Name", $true)
    if ($Arguments.Count -eq 0) {
        return [Activator]::CreateInstance($fixtureType, $true)
    }
    return [Activator]::CreateInstance($fixtureType, $Arguments)
}

function Assert-AuditObservation {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Audit observation changed: $Message" }
}

$results = [ordered]@{ Commit = '2e6314a04d251f432ed182871831a71a34d97e3f'; SyntheticOnly = $true }
$provider = New-AuditFixture 'UsageSnapshotCacheTests+CountingProvider'
$cache = [TokensLimitsExtension.Core.Services.UsageSnapshotCache]::new($provider)
$page = [TokensLimitsExtension.TokensLimitsPage]::new($cache)
try {
    $null = $page.RefreshAsync().GetAwaiter().GetResult()
    $initial = $provider.CallCount
    $refreshItem = @($page.GetItems() | Where-Object Title -eq 'Refresh')
    Assert-AuditObservation ($refreshItem.Count -eq 1) 'refresh action must exist'
    $null = $refreshItem[0].Command.Invoke()
    $afterAction = $provider.CallCount
    $null = $cache.RefreshAsync($true).GetAwaiter().GetResult()
    Assert-AuditObservation ($initial -eq 1 -and $afterAction -eq 1 -and $provider.CallCount -eq 2) 'manual refresh uses fresh cache'
    $results.ManualRefresh = [ordered]@{ InitialCalls = $initial; AfterAction = $afterAction; AfterForce = $provider.CallCount }
}
finally { $page.Dispose(); $cache.Dispose() }

$provider = New-AuditFixture 'UsageRefreshCoordinatorTests+RateLimitedProvider'
$settings = New-AuditFixture 'UsageRefreshCoordinatorTests+TestSettings' @([TimeSpan]::FromSeconds(60))
$cache = [TokensLimitsExtension.Core.Services.UsageSnapshotCache]::new($provider, $settings)
$coordinator = [TokensLimitsExtension.Core.Services.UsageRefreshCoordinator]::new($settings)
try {
    $coordinator.UpdateProviders([TokensLimitsExtension.Core.Services.IUsageProviderStateSource[]]@($cache))
    $initial = $provider.CallCount
    $null = $coordinator.RefreshProviderAsync($cache, $true).GetAwaiter().GetResult()
    $afterForce = $provider.CallCount
    $null = $coordinator.RefreshProviderAsync($cache, $false).GetAwaiter().GetResult()
    Assert-AuditObservation ($initial -eq 1 -and $afterForce -eq 1 -and $provider.CallCount -eq 2) 'default entry point bypasses cooldown'
    $results.Cooldown = [ordered]@{ InitialCalls = $initial; AfterForce = $afterForce; AfterDefault = $provider.CallCount; RetryAfterSeconds = $cache.State.RetryAfter.TotalSeconds }
}
finally { $coordinator.Dispose(); $cache.Dispose() }

$localization = [TokensLimitsExtension.Core.Services.InvariantLocalizationService]::Instance
$window = [TokensLimitsExtension.Core.Models.UsageWindow]::new(30, [DateTimeOffset]::UtcNow.AddDays(20), 2592000)
$snapshot = [TokensLimitsExtension.Core.Models.UsageSnapshot]::new('example', 'Example', $window, $null, $null, $false)
$label = [TokensLimitsExtension.Core.Services.UsageDisplayFormatter]::GetWindowLabel($window, 'Primary', $localization)
$subtitle = [TokensLimitsExtension.Core.Services.UsageDisplayFormatter]::FormatDockBandSubtitle($snapshot, $localization)
Assert-AuditObservation ($label -eq '30д' -and $subtitle -eq '5ч\70%, 7д\—') 'Dock labels ignore window duration'
$results.WindowLabel = [ordered]@{ ActualWindow = $label; Dock = $subtitle }

$snapshot = [TokensLimitsExtension.Core.Models.UsageSnapshot]::new('example', 'Example', $null, $null, $null, $true)
$snapshot.Metrics = [TokensLimitsExtension.Core.Models.UsageMetric[]]@(
    [TokensLimitsExtension.Core.Models.UsageMetric]::new('Balance', '12', 'USD', $null, $null, $null, $null, 'totalBalance', 12, 'USD')
)
$subtitle = [TokensLimitsExtension.Core.Services.UsageDisplayFormatter]::FormatDockBandSubtitle($snapshot, $localization)
Assert-AuditObservation ($subtitle -eq 'Total balance: 12 USD') 'estimated balance loses estimate label'
$results.EstimatedBalance = [ordered]@{ IsEstimate = $snapshot.IsEstimate; Dock = $subtitle; CurrentProviderCombinationConfirmed = $false }

$timeoutResults = @()
foreach ($providerId in @('aiand', 'deepseek')) {
    $entries = [ValueTuple[string,string,string][]]@([ValueTuple[string,string,string]]::new($providerId, 'apiKey', 'synthetic-audit-key'))
    $configuration = New-AuditFixture 'ProviderCatalogTests+TestConfiguration' ([object[]]@(, $entries))
    $stream = New-AuditFixture 'ProviderCatalogTests+DelayedReadStream'
    $handler = New-AuditFixture 'ProviderCatalogTests+ContentHandler' @([Net.Http.StreamContent]::new($stream))
    $client = [Net.Http.HttpClient]::new($handler)
    # No descriptor environment mappings: do not inspect the user's API keys.
    $descriptor = [TokensLimitsExtension.Core.Providers.UsageProviderDescriptor]::new($providerId, $providerId)
    $provider = [TokensLimitsExtension.Core.Providers.ConfiguredUsageProvider]::new($descriptor, $configuration, $client, $null, [TimeSpan]::FromMilliseconds(25), 1048576)
    $cache = [TokensLimitsExtension.Core.Services.UsageSnapshotCache]::new($provider)
    try {
        $null = $cache.RefreshAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        $actualKind = $cache.State.ErrorKind.ToString()
        $observedKind = if ($providerId -eq 'aiand') { 'UnsupportedResponse' } else { 'MissingConfiguration' }
        Assert-AuditObservation ($actualKind -eq $observedKind) "generic timeout classification for $providerId"
        $timeoutResults += [ordered]@{ Provider = $providerId; ActualKind = $actualKind; DesiredTransportKind = 'Timeout'; StubHttp = $true }
    }
    finally { $cache.Dispose(); $provider.Dispose(); $client.Dispose() }
}
$results.Timeouts = $timeoutResults
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Current defect observations reproduced. Synthetic evidence saved to $OutputPath"
