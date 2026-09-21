[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$installerPath = Join-Path $PSScriptRoot '..\Install-TokensLimitsExtension.ps1'
. $installerPath -DefineFunctionsOnly
$actualStopPowerToysRunner = (Get-Command Stop-PowerToysRunner -CommandType Function).ScriptBlock

$global:TokensLimitsInstallerEvents = [System.Collections.Generic.List[string]]::new()
$global:TokensLimitsInstallerRunner = $null
$global:TokensLimitsInstallerStopFailure = $false
$global:TokensLimitsInstallerStartFailure = $false
$global:TokensLimitsInstallerProcess = $null
$global:TokensLimitsInstallerWindowOwnerProcessId = 0
$global:TokensLimitsInstallerCloseRequestCount = 0

function Get-PowerToysProcessById {
    return $global:TokensLimitsInstallerProcess
}

function Get-PowerToysTrayWindowHandle {
    param([int]$ProcessId)
    return [IntPtr]77
}

function Get-PowerToysTrayWindowOwnerProcessId {
    return $global:TokensLimitsInstallerWindowOwnerProcessId
}

function Send-PowerToysCloseRequest {
    $global:TokensLimitsInstallerCloseRequestCount++
    $global:TokensLimitsInstallerProcess = $null
    return $true
}

function Import-Certificate {
    [CmdletBinding()]
    param([string]$FilePath, [string]$CertStoreLocation)
    $global:TokensLimitsInstallerEvents.Add("certificate:${FilePath}:${CertStoreLocation}")
}

function Add-AppxPackage {
    [CmdletBinding()]
    param([string]$Path)
    $global:TokensLimitsInstallerEvents.Add("package:$Path")
}

function Get-PowerToysRunner {
    return $global:TokensLimitsInstallerRunner
}

function Stop-PowerToysRunner {
    param([Parameter(Mandatory)]$Runner)
    $global:TokensLimitsInstallerEvents.Add("stop:$($Runner.ExecutablePath)")
    if ($global:TokensLimitsInstallerStopFailure) {
        throw 'simulated PowerToys shutdown failure'
    }
}

function Start-PowerToysRunner {
    param([Parameter(Mandatory)]$Runner)
    $global:TokensLimitsInstallerEvents.Add("start:$($Runner.ExecutablePath)")
    if ($global:TokensLimitsInstallerStartFailure) {
        throw 'simulated PowerToys restart failure'
    }
}

function Assert-Events {
    param([Parameter(Mandatory)][string[]]$Expected)

    $actual = @($global:TokensLimitsInstallerEvents)
    if (($actual -join '|') -ne ($Expected -join '|')) {
        throw "Unexpected installer event order. Expected '$($Expected -join '|')', got '$($actual -join '|')'."
    }
}

try {
    Assert-InstallerUserSid -ExpectedUserSid 'S-1-5-21-current' -ActualUserSid 'S-1-5-21-current'
    $differentUserRejected = $false
    try {
        Assert-InstallerUserSid -ExpectedUserSid 'S-1-5-21-current' -ActualUserSid 'S-1-5-21-admin'
    }
    catch {
        $differentUserRejected = $_.Exception.Message -like '*different Windows account*'
    }

    if (-not $differentUserRejected) {
        throw 'The installer accepted an elevated Windows account different from the PowerToys user.'
    }

    $runner = [pscustomobject]@{
        ProcessId = 412
        SessionId = 3
        ExecutablePath = 'C:\Program Files\PowerToys\PowerToys.exe'
    }
    $global:TokensLimitsInstallerProcess = [pscustomobject]@{
        Id = $runner.ProcessId
        SessionId = $runner.SessionId
        Path = $runner.ExecutablePath
    }
    $global:TokensLimitsInstallerWindowOwnerProcessId = 999
    $windowOwnerRejected = $false
    try {
        & $actualStopPowerToysRunner -Runner $runner
    }
    catch {
        $windowOwnerRejected = $_.Exception.Message -like '*belongs to process 999*'
    }

    if (-not $windowOwnerRejected -or $global:TokensLimitsInstallerCloseRequestCount -ne 0) {
        throw 'The installer did not verify tray window ownership before requesting shutdown.'
    }

    $global:TokensLimitsInstallerWindowOwnerProcessId = $runner.ProcessId
    & $actualStopPowerToysRunner -Runner $runner
    if ($global:TokensLimitsInstallerCloseRequestCount -ne 1 -or $null -ne $global:TokensLimitsInstallerProcess) {
        throw 'The installer did not close the verified PowerToys runner.'
    }

    $global:TokensLimitsInstallerEvents.Clear()
    Install-ReleasePackage -ResolvedPackagePath 'C:\release\TokensLimitsExtension.msix' -ResolvedCertificatePath 'C:\release\publisher.cer'
    Assert-Events @(
        'certificate:C:\release\publisher.cer:Cert:\LocalMachine\TrustedPeople',
        'package:C:\release\TokensLimitsExtension.msix'
    )
    $global:TokensLimitsInstallerEvents.Clear()

    $global:TokensLimitsInstallerRunner = [pscustomobject]@{
        ProcessId = 412
        ExecutablePath = 'C:\Program Files\PowerToys\PowerToys.exe'
    }

    Invoke-PowerToysUpdateWorkflow -InstallAction {
        $global:TokensLimitsInstallerEvents.Add('install')
    }

    Assert-Events @(
        'stop:C:\Program Files\PowerToys\PowerToys.exe',
        'install',
        'start:C:\Program Files\PowerToys\PowerToys.exe'
    )

    $global:TokensLimitsInstallerEvents.Clear()
    $uacCancellationPropagated = $false
    try {
        Invoke-PowerToysUpdateWorkflow -InstallAction {
            $global:TokensLimitsInstallerEvents.Add('uac-canceled')
            throw 'simulated UAC cancellation'
        }
    }
    catch {
        $uacCancellationPropagated = $_.Exception.Message -eq 'simulated UAC cancellation'
    }

    if (-not $uacCancellationPropagated) {
        throw 'The UAC cancellation was not propagated.'
    }

    Assert-Events @(
        'stop:C:\Program Files\PowerToys\PowerToys.exe',
        'uac-canceled',
        'start:C:\Program Files\PowerToys\PowerToys.exe'
    )

    $global:TokensLimitsInstallerEvents.Clear()
    $global:TokensLimitsInstallerStopFailure = $true
    $installStarted = $false
    $stopFailed = $false
    try {
        Invoke-PowerToysUpdateWorkflow -InstallAction {
            $installStarted = $true
            $global:TokensLimitsInstallerEvents.Add('install')
        }
    }
    catch {
        $stopFailed = $_.Exception.Message -eq 'simulated PowerToys shutdown failure'
    }

    if (-not $stopFailed -or $installStarted) {
        throw 'The installer continued after PowerToys failed to close.'
    }

    Assert-Events @('stop:C:\Program Files\PowerToys\PowerToys.exe')
    $global:TokensLimitsInstallerStopFailure = $false

    $global:TokensLimitsInstallerEvents.Clear()
    $global:TokensLimitsInstallerRunner = $null
    Invoke-PowerToysUpdateWorkflow -InstallAction {
        $global:TokensLimitsInstallerEvents.Add('install')
    }
    Assert-Events @('install')

    $global:TokensLimitsInstallerEvents.Clear()
    $global:TokensLimitsInstallerRunner = [pscustomobject]@{
        ProcessId = 412
        ExecutablePath = 'C:\Program Files\PowerToys\PowerToys.exe'
    }
    $installFailed = $false
    try {
        Invoke-PowerToysUpdateWorkflow -InstallAction {
            $global:TokensLimitsInstallerEvents.Add('install')
            throw 'simulated package deployment failure'
        }
    }
    catch {
        $installFailed = $_.Exception.Message -eq 'simulated package deployment failure'
    }

    if (-not $installFailed) {
        throw 'The package deployment failure was not propagated.'
    }

    Assert-Events @(
        'stop:C:\Program Files\PowerToys\PowerToys.exe',
        'install',
        'start:C:\Program Files\PowerToys\PowerToys.exe'
    )

    $global:TokensLimitsInstallerEvents.Clear()
    $global:TokensLimitsInstallerStartFailure = $true
    $restartFailed = $false
    try {
        Invoke-PowerToysUpdateWorkflow -InstallAction {
            $global:TokensLimitsInstallerEvents.Add('install')
        }
    }
    catch {
        $restartFailed = $_.Exception.Message -like '*PowerToys could not be restarted*'
    }

    if (-not $restartFailed) {
        throw 'The PowerToys restart failure was not reported.'
    }

    Assert-Events @(
        'stop:C:\Program Files\PowerToys\PowerToys.exe',
        'install',
        'start:C:\Program Files\PowerToys\PowerToys.exe'
    )

    Write-Host 'Installer restart tests passed.' -ForegroundColor Green
}
finally {
    Remove-Item Function:\Get-PowerToysRunner -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Stop-PowerToysRunner -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Start-PowerToysRunner -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Assert-Events -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-PowerToysProcessById -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-PowerToysTrayWindowHandle -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-PowerToysTrayWindowOwnerProcessId -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Send-PowerToysCloseRequest -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Import-Certificate -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Add-AppxPackage -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Assert-InstallerUserSid -Force -ErrorAction SilentlyContinue
    Remove-Variable TokensLimitsInstallerEvents, TokensLimitsInstallerRunner, TokensLimitsInstallerStopFailure, TokensLimitsInstallerStartFailure, TokensLimitsInstallerProcess, TokensLimitsInstallerWindowOwnerProcessId, TokensLimitsInstallerCloseRequestCount -Scope Global -Force -ErrorAction SilentlyContinue
}
