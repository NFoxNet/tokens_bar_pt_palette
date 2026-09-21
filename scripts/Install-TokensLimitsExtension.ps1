[CmdletBinding()]
param(
    [string]$PackagePath,

    [string]$CertificatePath,

    [switch]$ElevatedInstallOnly,

    [string]$ExpectedUserSid,

    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'

function Get-PowerToysRunner {
    $currentSessionId = (Get-Process -Id $PID -ErrorAction Stop).SessionId
    $processes = @(Get-Process -Name 'PowerToys' -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $currentSessionId)

    if ($processes.Count -gt 1) {
        throw "Found $($processes.Count) PowerToys runner processes in the current session; refusing an ambiguous restart."
    }

    if ($processes.Count -eq 0) {
        return $null
    }

    $process = $processes[0]
    if ([string]::IsNullOrWhiteSpace($process.Path) -or -not (Test-Path -LiteralPath $process.Path -PathType Leaf)) {
        throw "Could not resolve the executable path for running PowerToys process $($process.Id)."
    }

    return [pscustomobject]@{
        ProcessId = $process.Id
        SessionId = $process.SessionId
        ExecutablePath = $process.Path
    }
}

function Initialize-InstallerNativeMethods {
    if ($null -eq ('TokensLimitsInstallerNativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TokensLimitsInstallerNativeMethods
{
    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr windowHandle, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr FindWindowForProcess(string expectedClassName, int expectedProcessId)
    {
        IntPtr matchingWindow = IntPtr.Zero;
        EnumWindows((windowHandle, state) =>
        {
            uint processId;
            GetWindowThreadProcessId(windowHandle, out processId);
            if (processId != expectedProcessId)
            {
                return true;
            }

            var className = new System.Text.StringBuilder(256);
            GetClassName(windowHandle, className, className.Capacity);
            if (String.Equals(className.ToString(), expectedClassName, StringComparison.Ordinal))
            {
                matchingWindow = windowHandle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return matchingWindow;
    }
}
'@
    }
}

function Get-PowerToysProcessById {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)

    return Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
}

function Get-PowerToysTrayWindowHandle {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)

    Initialize-InstallerNativeMethods
    return [TokensLimitsInstallerNativeMethods]::FindWindowForProcess('PToyTrayIconWindow', $ProcessId)
}

function Get-PowerToysTrayWindowOwnerProcessId {
    [CmdletBinding()]
    param([Parameter(Mandatory)][IntPtr]$WindowHandle)

    Initialize-InstallerNativeMethods
    [uint32]$processId = 0
    [void][TokensLimitsInstallerNativeMethods]::GetWindowThreadProcessId($WindowHandle, [ref]$processId)
    return [int]$processId
}

function Send-PowerToysCloseRequest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][IntPtr]$WindowHandle)

    Initialize-InstallerNativeMethods
    return [TokensLimitsInstallerNativeMethods]::PostMessage($WindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
}

function Stop-PowerToysRunner {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Runner)

    $process = Get-PowerToysProcessById -ProcessId $Runner.ProcessId
    if ($null -eq $process) {
        return
    }

    if ($process.SessionId -ne $Runner.SessionId -or $process.Path -ne $Runner.ExecutablePath) {
        throw "The PowerToys process $($Runner.ProcessId) changed since it was detected; refusing to close an unrelated process."
    }

    $windowHandle = Get-PowerToysTrayWindowHandle -ProcessId $Runner.ProcessId
    if ($windowHandle -eq [IntPtr]::Zero) {
        throw 'Could not find the PowerToys tray window to request a graceful shutdown. Close PowerToys manually and run the installer again.'
    }

    $windowProcessId = Get-PowerToysTrayWindowOwnerProcessId -WindowHandle $windowHandle
    if ($windowProcessId -ne $Runner.ProcessId) {
        throw "The PowerToys tray window belongs to process $windowProcessId, not the detected runner $($Runner.ProcessId); refusing to close it."
    }

    $process = Get-PowerToysProcessById -ProcessId $Runner.ProcessId
    if ($null -eq $process) {
        return
    }

    if ($process.SessionId -ne $Runner.SessionId -or $process.Path -ne $Runner.ExecutablePath) {
        throw "The PowerToys process $($Runner.ProcessId) changed before shutdown; refusing to close an unrelated process."
    }

    $windowProcessId = Get-PowerToysTrayWindowOwnerProcessId -WindowHandle $windowHandle
    if ($windowProcessId -ne $Runner.ProcessId) {
        throw "The PowerToys tray window changed owners before shutdown; refusing to close process $windowProcessId."
    }

    if (-not (Send-PowerToysCloseRequest -WindowHandle $windowHandle)) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Could not request PowerToys shutdown (Win32 error $errorCode). Close PowerToys manually and run the installer again."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -ne (Get-PowerToysProcessById -ProcessId $Runner.ProcessId) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 1
    }

    if ($null -ne (Get-PowerToysProcessById -ProcessId $Runner.ProcessId)) {
        throw 'PowerToys did not close within 30 seconds. The package was not installed; close PowerToys manually and run the installer again.'
    }
}

function Start-PowerToysRunner {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Runner)

    $currentSessionId = (Get-Process -Id $PID -ErrorAction Stop).SessionId
    $existingRunner = Get-Process -Name 'PowerToys' -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $currentSessionId
    if ($existingRunner) {
        return
    }

    Start-Process -FilePath $Runner.ExecutablePath -WorkingDirectory (Split-Path -Parent $Runner.ExecutablePath) -ErrorAction Stop | Out-Null
}

function Invoke-PowerToysUpdateWorkflow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock]$InstallAction)

    $runner = Get-PowerToysRunner
    if ($null -ne $runner) {
        Write-Host 'Stopping PowerToys before installing the extension...' -ForegroundColor Cyan
        Stop-PowerToysRunner -Runner $runner
    }

    $installError = $null
    try {
        & $InstallAction
    }
    catch {
        $installError = $_
    }

    $restartError = $null
    if ($null -ne $runner) {
        try {
            Write-Host 'Starting PowerToys...' -ForegroundColor Cyan
            Start-PowerToysRunner -Runner $runner
        }
        catch {
            $restartError = $_
        }
    }

    if ($null -ne $installError -and $null -ne $restartError) {
        throw "Installation failed: $($installError.Exception.Message) PowerToys could not be restarted: $($restartError.Exception.Message)"
    }

    if ($null -ne $installError) {
        throw $installError
    }

    if ($null -ne $restartError) {
        throw "The extension was installed, but PowerToys could not be restarted: $($restartError.Exception.Message)"
    }
}

function Install-ReleasePackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ResolvedPackagePath,
        [Parameter(Mandatory)][string]$ResolvedCertificatePath
    )

    Import-Certificate -FilePath $ResolvedCertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop | Out-Null
    Add-AppxPackage -Path $ResolvedPackagePath -ErrorAction Stop
}

function Assert-InstallerUserSid {
    [CmdletBinding()]
    param(
        [string]$ExpectedUserSid,
        [Parameter(Mandatory)][string]$ActualUserSid
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedUserSid) -or $ActualUserSid -ne $ExpectedUserSid) {
        throw 'The elevation prompt used a different Windows account. The package is per-user; approve UAC for the same account that runs PowerToys.'
    }
}

function Invoke-ElevatedReleaseInstall {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ResolvedPackagePath,
        [Parameter(Mandatory)][string]$ResolvedCertificatePath
    )

    $windowsPowerShell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
        $windowsPowerShell = (Get-Command powershell.exe -ErrorAction Stop).Source
    }

    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $PSCommandPath,
        '-PackagePath',
        $ResolvedPackagePath,
        '-CertificatePath',
        $ResolvedCertificatePath,
        '-ElevatedInstallOnly',
        '-ExpectedUserSid',
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    ) | ForEach-Object { '"{0}"' -f $_.Replace('"', '\"') }

    $process = Start-Process -FilePath $windowsPowerShell -ArgumentList ($arguments -join ' ') -Verb RunAs -Wait -PassThru -ErrorAction Stop
    if ($process.ExitCode -ne 0) {
        throw "Installation was cancelled or failed with exit code $($process.ExitCode)."
    }
}

if ($DefineFunctionsOnly) {
    return
}

$releaseDirectory = (Resolve-Path -LiteralPath $PSScriptRoot).Path

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($architecture -notin 'x64', 'arm64') {
        throw "This release supports x64 and ARM64 Windows only. Detected architecture: $architecture."
    }

    $packages = @(Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object { $_.Extension -in '.msix', '.msixbundle' -and $_.Name -match "_$architecture\.(msix|msixbundle)$" })
    if ($packages.Count -ne 1) {
        throw "Expected exactly one $architecture MSIX package next to the installer, found $($packages.Count)."
    }

    $PackagePath = $packages[0].FullName
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $certificates = @(Get-ChildItem -LiteralPath $releaseDirectory -File -Filter '*.cer')
    if ($certificates.Count -ne 1) {
        throw "Expected exactly one public .cer certificate next to the installer, found $($certificates.Count)."
    }

    $CertificatePath = $certificates[0].FullName
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "PackagePath does not exist: $PackagePath"
}

if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "CertificatePath does not exist: $CertificatePath"
}

$package = Get-Item -LiteralPath $PackagePath
$certificate = Get-Item -LiteralPath $CertificatePath

if ($package.Extension -notin '.msix', '.msixbundle') {
    throw 'PackagePath must point to a .msix or .msixbundle file.'
}

if ($certificate.Extension -ne '.cer') {
    throw 'CertificatePath must point to the public .cer file from the same release.'
}

if ($ElevatedInstallOnly) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    Assert-InstallerUserSid -ExpectedUserSid $ExpectedUserSid -ActualUserSid $identity.User.Value

    $principal = [Security.Principal.WindowsPrincipal] $identity
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The internal elevated install mode must run as an administrator.'
    }

    Install-ReleasePackage -ResolvedPackagePath $package.FullName -ResolvedCertificatePath $certificate.FullName
    Write-Host 'Tokens Limits was installed.' -ForegroundColor Green
    return
}

$principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Do not run the installer as administrator. Start it normally so it can restart PowerToys without elevation.'
}

Invoke-PowerToysUpdateWorkflow -InstallAction {
    Invoke-ElevatedReleaseInstall -ResolvedPackagePath $package.FullName -ResolvedCertificatePath $certificate.FullName
}

Write-Host 'Tokens Limits was installed. PowerToys has been restarted and can load the updated extension.' -ForegroundColor Green
