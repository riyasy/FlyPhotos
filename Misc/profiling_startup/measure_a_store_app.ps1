#Requires -Version 5.1
<#
.SYNOPSIS
    Measures the launch performance of a Windows Store / UWP / MSIX-packaged app.

.DESCRIPTION
    Packaged apps cannot be launched by exe path -- they activate through the Windows
    activation broker and are identified by an AUMID (Application User Model ID).

    Run with no target and the script is interactive:
      1. Asks for an app name, searches the Start apps, and asks you to confirm the match
         (loops until you pick one).
      2. Asks for a file path.
           * If you give a file, it is opened against the SELECTED package via WinRT
             Launcher.LaunchFileAsync -- the real "double-click a file with this app" path.
           * If you leave it empty, the app is launched with no file via the activation
             broker (IApplicationActivationManager).
      3. Times each launch until a top-level window appears and reports the results,
         including the active Power Mode.

    It can also be driven non-interactively (this is how Measure-CommonApps.ps1 calls it):
        -Aumid <AUMID> [-ProcessName <name>] [-PassThru]

.PARAMETER Aumid
    The Application User Model ID of the packaged app, e.g.
    "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App". When supplied, the interactive
    prompts are skipped.

.PARAMETER AppArgs
    Optional activation arguments passed to the app (launch-without-file mode only).

.PARAMETER File
    A file to open against -PackageFamilyName via Launcher.LaunchFileAsync. Empty = launch
    the app without a file.

.PARAMETER PackageFamilyName
    Package Family Name (the AUMID part before '!') used for file activation. Derived
    automatically in interactive mode.

.PARAMETER ProcessName
    Process name (without .exe) of the app. Helps locate the window and is used to close
    running instances before each run (for a clean cold start). Derived automatically in
    interactive mode; optional otherwise.

.PARAMETER Runs
    Number of measured launch iterations (default: 5).

.PARAMETER WarmupRuns
    Number of warm-up runs before recording (default: 1).

.PARAMETER TimeoutSeconds
    Max seconds to wait for a window to appear (default: 30).

.PARAMETER KillAfterMs
    Milliseconds to wait before closing the app after its window appears (default: 2000).

.PARAMETER SettleMs
    Pause after each run so the activation broker can release the previous process
    (default: 1500).

.PARAMETER PassThru
    Emit a summary object on the success stream (for callers like Measure-CommonApps.ps1).
    Suppresses the extra environment/power-mode banner and final table.

.PARAMETER Force
    Skip the safety confirmation shown when the target app is already running (its
    instances are force-closed before each run for a clean cold start, discarding unsaved
    work). The prompt only appears in interactive/standalone use, never under -PassThru.

.EXAMPLE
    .\Measure-StoreAppLaunch.ps1
    # Interactive: search an app, optionally give a file, measure.

.EXAMPLE
    .\Measure-StoreAppLaunch.ps1 -Aumid "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" -Runs 5

.NOTES
    "Window up" is detected via MainWindowHandle != 0, which fires when the window is
    created (slightly before first paint). The bias is identical across runs, so it is a
    valid relative measure. The script never changes any system or power settings.
#>
param(
    [string]$Aumid = "",
    [string]$AppArgs = "",
    [string]$File = "",
    [string]$PackageFamilyName = "",
    [string]$ProcessName = "",

    [int]$Runs = 5,
    [int]$WarmupRuns = 1,
    [int]$TimeoutSeconds = 30,
    [int]$KillAfterMs = 2000,
    [int]$SettleMs = 1500,

    [switch]$PassThru,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- COM activation interop (launch without a file) ---------------------------
# IApplicationActivationManager::ActivateApplication returns the activated PID, the
# reliable way to get a handle on a packaged app's real process.
if (-not ([System.Management.Automation.PSTypeName]'StoreLaunch.ActivationManager').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace StoreLaunch
{
    public enum ActivateOptions { None = 0, DesignMode = 1, NoErrorUI = 2, NoSplashScreen = 4 }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationActivationManager
    {
        IntPtr ActivateApplication(
            [In] string appUserModelId,
            [In] string arguments,
            [In] ActivateOptions options,
            [Out] out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    public class ApplicationActivationManager { }

    public static class Activator
    {
        public static uint Activate(string aumid, string args)
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            uint pid;
            IntPtr hr = mgr.ActivateApplication(aumid, args, ActivateOptions.None, out pid);
            if (hr.ToInt64() != 0)
                throw new Exception("ActivateApplication failed, HRESULT=0x" + hr.ToInt64().ToString("X8"));
            return pid;
        }
    }
}
"@
}

# --- WinRT launcher (open a file against a specific package) -------------------
# Launcher.LaunchFileAsync can target a specific package via PackageFamilyName. WinRT
# returns IAsyncOperation, so we convert to a .NET Task (AsTask) and block on it -- the
# PowerShell equivalent of C# 'await'.
$script:WinRtReady = $false
function Initialize-WinRtLauncher {
    if ($script:WinRtReady) { return }
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $script:AsTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]
    [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]    | Out-Null
    [Windows.System.Launcher,Windows.System,ContentType=WindowsRuntime]         | Out-Null
    [Windows.System.LauncherOptions,Windows.System,ContentType=WindowsRuntime]  | Out-Null
    $script:WinRtReady = $true
}

function Invoke-AwaitOp {
    param($Operation, [Type]$ResultType)
    $asTask  = $script:AsTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($Operation))
    $netTask.Wait(-1) | Out-Null
    return $netTask.Result
}

function Start-PackagedFile {
    param([string]$Pfn, [string]$FilePath)
    $file = Invoke-AwaitOp ([Windows.Storage.StorageFile]::GetFileFromPathAsync($FilePath)) ([Windows.Storage.StorageFile])
    $options = New-Object Windows.System.LauncherOptions
    $options.TargetApplicationPackageFamilyName = $Pfn
    $options.DisplayApplicationPicker           = $false
    $ok = Invoke-AwaitOp ([Windows.System.Launcher]::LaunchFileAsync($file, $options)) ([bool])
    if (-not $ok) {
        throw "Launcher.LaunchFileAsync returned false for PackageFamilyName '$Pfn' (is the package installed and does it handle this file type?)."
    }
}

# --- Power Mode overlay (read-only; best-effort) ------------------------------
$script:PowerApiOk = $false
try {
    if (-not ([System.Management.Automation.PSTypeName]'PowerMode.Overlay').Type) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace PowerMode
{
    public static class Overlay
    {
        [DllImport("powrprof.dll")]
        public static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayGuid);
    }
}
"@
    }
    $script:PowerApiOk = $true
} catch { $script:PowerApiOk = $false }

$GUID_BALANCED        = [Guid]"00000000-0000-0000-0000-000000000000"
$GUID_BEST_PERF       = [Guid]"ded574b5-45a0-4f42-8737-46345c09c238"
$GUID_BEST_EFFICIENCY = [Guid]"961cca80-c73c-4eb8-92e2-13b4a4f3d610"

function Get-PowerModeReport {
    if (-not $script:PowerApiOk) { return "unknown (API unavailable)" }
    try {
        $g  = [Guid]::Empty
        $rc = [PowerMode.Overlay]::PowerGetEffectiveOverlayScheme([ref]$g)
        if ($rc -ne 0) { return "unknown (rc=$rc)" }
        switch ($g) {
            $GUID_BALANCED        { return "Balanced (Recommended)" }
            $GUID_BEST_PERF       { return "Best performance" }
            $GUID_BEST_EFFICIENCY { return "Best power efficiency" }
            default               { return "Custom/Other ($g)" }
        }
    } catch { return "unknown (read failed)" }
}

# --- App resolution helpers ---------------------------------------------------
function Get-AppProcessName {
    # Derive the runtime process name (no .exe) from a packaged app's manifest.
    param([string]$AumidValue)
    try {
        $pfn   = $AumidValue.Split('!')[0]
        $appId = $AumidValue.Split('!')[1]
        $pkg   = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.PackageFamilyName -eq $pfn } | Select-Object -First 1
        if (-not $pkg) { return "" }
        $apps = @((Get-AppxPackageManifest $pkg).Package.Applications.Application)
        $a    = $apps | Where-Object { $_.Id -eq $appId } | Select-Object -First 1
        if (-not $a) { $a = $apps | Select-Object -First 1 }
        if (-not $a) { return "" }
        return [System.IO.Path]::GetFileNameWithoutExtension([string]$a.Executable)
    } catch { return "" }
}

function Select-AppInteractive {
    # Loop: ask for a name, search packaged Start apps, list, confirm. Returns the chosen
    # Get-StartApps entry (Name + AppID).
    $all = @(Get-StartApps -ErrorAction SilentlyContinue | Where-Object { $_.AppID -like '*!*' } | Sort-Object Name)
    if ($all.Count -eq 0) {
        throw "Could not enumerate Start apps (Get-StartApps returned nothing)."
    }

    while ($true) {
        $term = (Read-Host "  App name to test (partial, e.g. 'fly')").Trim()
        if ($term -eq '') { Write-Host "  Type part of an app name." -ForegroundColor Yellow; continue }

        $hits = @($all | Where-Object { $_.Name -like "*$term*" })
        if ($hits.Count -eq 0) { Write-Host "  No app matched '$term'. Try again." -ForegroundColor Yellow; continue }
        if ($hits.Count -gt 25) { Write-Host ("  {0} matches -- be more specific." -f $hits.Count) -ForegroundColor Yellow; continue }

        Write-Host ""
        for ($i = 0; $i -lt $hits.Count; $i++) {
            Write-Host ("    [{0}] {1}" -f ($i + 1), $hits[$i].Name)
        }
        Write-Host ""
        $pick = (Read-Host "  Number to select (Enter to search again)").Trim()
        if ($pick -notmatch '^\d+$' -or [int]$pick -lt 1 -or [int]$pick -gt $hits.Count) { continue }

        $sel = $hits[[int]$pick - 1]
        $confirm = (Read-Host ("  Use '{0}'? (Y/N)" -f $sel.Name)).Trim()
        if ($confirm -match '^(?i:y|yes)$') { return $sel }
        # otherwise loop
    }
}

# --- Window / process helpers -------------------------------------------------
function Clear-RunningInstances {
    param([string]$Name, [int]$SettleMs)
    if ([string]::IsNullOrEmpty($Name)) { return }
    $running = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds $SettleMs
    }
}

function Invoke-SingleRun {
    param(
        [string]$AppId,
        [string]$ExeArgs,
        [string]$Pfn,
        [string]$FilePath,
        [string]$ProcName,
        [int]$TimeoutMs,
        [int]$KillDelayMs
    )

    $proc     = $null
    $windowMs = $null
    $sw       = [System.Diagnostics.Stopwatch]::StartNew()

    if ($FilePath -ne '') {
        # File mode: open the file against the chosen package. LaunchFileAsync is fire-and-
        # forget (no PID), so detect the window as a NEW windowed process after launch --
        # robust even when the runtime process name differs from the manifest name.
        $prePids = @(Get-Process -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        Start-PackagedFile -Pfn $Pfn -FilePath $FilePath
        $spawnMs = $sw.ElapsedMilliseconds

        while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
            $cands = @(Get-Process -ErrorAction SilentlyContinue |
                       Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $prePids -notcontains $_.Id })
            if ($cands.Count -gt 0) {
                $p = $null
                if ($ProcName -ne '') { $p = $cands | Where-Object { $_.ProcessName -eq $ProcName } | Select-Object -First 1 }
                if (-not $p)          { $p = $cands | Select-Object -First 1 }
                $proc = $p; $windowMs = $sw.ElapsedMilliseconds; break
            }
            Start-Sleep -Milliseconds 15
        }
    }
    else {
        # Launch activation: broker returns a PID. Poll BOTH the returned PID and (if given)
        # the process name -- single-instance apps (e.g. Notepad) hand off to a different
        # PID, so a name lookup finds the real window.
        $appPid  = [StoreLaunch.Activator]::Activate($AppId, $ExeArgs)
        $spawnMs = $sw.ElapsedMilliseconds

        $pidProc = $null
        try { $pidProc = [System.Diagnostics.Process]::GetProcessById([int]$appPid) } catch {}

        while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
            if ($pidProc) {
                try { $pidProc.Refresh() } catch { $pidProc = $null }
                if ($pidProc -and -not $pidProc.HasExited -and $pidProc.MainWindowHandle -ne [IntPtr]::Zero) {
                    $proc = $pidProc; $windowMs = $sw.ElapsedMilliseconds; break
                }
            }
            if ($ProcName -ne '') {
                $p = Get-Process -Name $ProcName -ErrorAction SilentlyContinue |
                     Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
                     Select-Object -First 1
                if ($p) { $proc = $p; $windowMs = $sw.ElapsedMilliseconds; break }
            }
            if (-not $pidProc -and $ProcName -eq '') { break }
            Start-Sleep -Milliseconds 15
        }
    }

    Start-Sleep -Milliseconds $KillDelayMs
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill() } catch {}
        $proc.WaitForExit(3000) | Out-Null
    }

    return [PSCustomObject]@{
        SpawnMs  = $spawnMs
        WindowMs = $windowMs
        TimedOut = ($null -eq $windowMs)
    }
}

# --- Resolve the target -------------------------------------------------------
$rich        = -not $PassThru          # rich = env banner + final table (standalone/interactive)
$displayName = ""

if ($Aumid -eq '' -and $PackageFamilyName -eq '') {
    # Interactive flow.
    Write-Host ""
    Write-Host "  Store App Launch Profiler" -ForegroundColor Cyan
    Write-Host ("  {0}" -f ("-" * 62)) -ForegroundColor DarkGray

    $sel         = Select-AppInteractive
    $Aumid       = $sel.AppID
    $displayName = $sel.Name
    $PackageFamilyName = $Aumid.Split('!')[0]
    $ProcessName = Get-AppProcessName $Aumid

    $rawPath = (Read-Host "  File path to open with it (Enter = launch the app with no file)").Trim().Trim('"')
    if ($rawPath -ne '') {
        if (Test-Path -LiteralPath $rawPath) {
            $File = (Resolve-Path -LiteralPath $rawPath).Path
        } else {
            Write-Host "  File not found -- launching the app without a file instead." -ForegroundColor Yellow
            $File = ""
        }
    }
}

if ($displayName -eq '') { $displayName = if ($Aumid -ne '') { $Aumid } else { $ProcessName } }

# --- Validation ---------------------------------------------------------------
if ($File -ne '') {
    if ($PackageFamilyName -eq '') {
        Write-Error "-File requires -PackageFamilyName (the package to open the file with)."
        exit 1
    }
    if (-not (Test-Path -LiteralPath $File)) {
        Write-Error "File not found: $File"
        exit 1
    }
    Initialize-WinRtLauncher
}
elseif ($Aumid -eq '') {
    Write-Error "Provide -Aumid, or run with no parameters to choose interactively."
    exit 1
}

$timeoutMs = $TimeoutSeconds * 1000
$hr        = "-" * 62
$mode      = if ($File -ne '') { "file activation (LaunchFileAsync)" } else { "launch activation" }
$target    = if ($File -ne '') { "$displayName (via $([System.IO.Path]::GetFileName($File)))" } else { $displayName }

# --- Banner -------------------------------------------------------------------
Write-Host ""
Write-Host "  Store App Launch Profiler" -ForegroundColor Cyan
Write-Host "  $hr" -ForegroundColor DarkGray
if ($rich) {
    Write-Host ("  Power mode : {0}" -f (Get-PowerModeReport)) -ForegroundColor White
}
Write-Host ("  App     : {0}" -f $displayName) -ForegroundColor White
if ($Aumid -ne '')             { Write-Host ("  AUMID   : {0}" -f $Aumid) -ForegroundColor White }
if ($File  -ne '')             { Write-Host ("  File    : {0}" -f $File) -ForegroundColor White }
if ($PackageFamilyName -ne '' -and $File -ne '') { Write-Host ("  Package : {0}" -f $PackageFamilyName) -ForegroundColor White }
Write-Host ("  Mode    : {0}" -f $mode) -ForegroundColor White
if ($ProcessName -ne '') { Write-Host ("  Cleanup : close '{0}' before each run" -f $ProcessName) -ForegroundColor White }
Write-Host ("  Runs    : {0}  (+ {1} warm-up)" -f $Runs, $WarmupRuns) -ForegroundColor White
Write-Host ("  Timeout : {0}s per run" -f $TimeoutSeconds) -ForegroundColor White
Write-Host ""

# --- Safety: warn before force-closing an already-running instance ------------
if ($rich -and -not $Force -and $ProcessName -ne '' -and
    @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Host ("  WARNING: '{0}' is already running and will be FORCE-CLOSED before each" -f $ProcessName) -ForegroundColor Yellow
    Write-Host "           run (any unsaved work will be lost)." -ForegroundColor Yellow
    $confirm = Read-Host "  Save your work, then type Y to continue (anything else cancels)"
    if ($confirm.Trim() -notmatch '^(?i:y|yes)$') {
        Write-Host "  Cancelled. Nothing was changed." -ForegroundColor DarkGray
        return
    }
    Write-Host ""
}

# --- Warm-up ------------------------------------------------------------------
if ($WarmupRuns -gt 0) {
    Write-Host "  Warm-up runs..." -ForegroundColor DarkYellow
    for ($i = 1; $i -le $WarmupRuns; $i++) {
        Clear-RunningInstances -Name $ProcessName -SettleMs $SettleMs
        $r      = Invoke-SingleRun -AppId $Aumid -ExeArgs $AppArgs -Pfn $PackageFamilyName -FilePath $File -ProcName $ProcessName -TimeoutMs $timeoutMs -KillDelayMs $KillAfterMs
        $status = if ($r.TimedOut) { "TIMEOUT" } else { "$($r.WindowMs) ms" }
        Write-Host "    Warm-up $i : $status" -ForegroundColor DarkGray
        Start-Sleep -Milliseconds $SettleMs
    }
    Write-Host ""
}

# --- Measured runs ------------------------------------------------------------
$results = @()
Write-Host "  Measuring..." -ForegroundColor DarkYellow
for ($i = 1; $i -le $Runs; $i++) {
    Clear-RunningInstances -Name $ProcessName -SettleMs $SettleMs
    $r       = Invoke-SingleRun -AppId $Aumid -ExeArgs $AppArgs -Pfn $PackageFamilyName -FilePath $File -ProcName $ProcessName -TimeoutMs $timeoutMs -KillDelayMs $KillAfterMs
    $results += $r

    if ($r.TimedOut) {
        Write-Host ("    Run {0,-3}: TIMEOUT  (no window handle in {1}s)" -f $i, $TimeoutSeconds) -ForegroundColor Red
    } else {
        Write-Host ("    Run {0,-3}: window = {1,-8} ms   Activate() = {2} ms" -f $i, $r.WindowMs, $r.SpawnMs) -ForegroundColor Green
    }
    Start-Sleep -Milliseconds $SettleMs
}

# --- Statistics ---------------------------------------------------------------
$valid    = @($results | Where-Object { -not $_.TimedOut })
$nTimeout = @($results | Where-Object { $_.TimedOut }).Count

$wMin = $wMax = $wAvg = $wMed = $wP95 = $spawnAvg = $null
$rating = "All timed out"

if ($valid.Count -gt 0) {
    $windowTimes = @($valid | ForEach-Object { $_.WindowMs })
    $spawnTimes  = @($valid | ForEach-Object { $_.SpawnMs  })

    $wMin     = ($windowTimes | Measure-Object -Minimum).Minimum
    $wMax     = ($windowTimes | Measure-Object -Maximum).Maximum
    $wAvg     = [math]::Round(($windowTimes | Measure-Object -Average).Average, 1)
    $sorted   = @($windowTimes | Sort-Object)
    $wMed     = $sorted[[math]::Floor($sorted.Count / 2)]
    $p95Index = [math]::Min([math]::Floor($sorted.Count * 0.95), $sorted.Count - 1)
    $wP95     = $sorted[$p95Index]
    $spawnAvg = [math]::Round(($spawnTimes | Measure-Object -Average).Average, 1)

    $rating = "Very slow  (>= 4s)"
    if     ($wAvg -lt 500)  { $rating = "Excellent  (< 0.5s)" }
    elseif ($wAvg -lt 1000) { $rating = "Good       (< 1s)"   }
    elseif ($wAvg -lt 2000) { $rating = "Acceptable (< 2s)"   }
    elseif ($wAvg -lt 4000) { $rating = "Slow       (< 4s)"   }
}

# --- Summary (rich: power mode + table, like the common-apps test) ------------
if ($rich) {
    $eq = "=" * 64
    Write-Host ""
    Write-Host "  $eq" -ForegroundColor DarkGray
    Write-Host ("  RESULT  --  Power mode: {0}" -f (Get-PowerModeReport)) -ForegroundColor Cyan
    Write-Host "  $eq" -ForegroundColor DarkGray
    Write-Host ""

    if ($valid.Count -eq 0) {
        Write-Host "  All runs timed out -- no window appeared. If you opened a file, the app may" -ForegroundColor Red
        Write-Host "  not handle that file type; otherwise check the app name / AUMID." -ForegroundColor Red
        Write-Host ""
    } else {
        ,([PSCustomObject]@{
            App = $displayName; Valid = "$($valid.Count)/$Runs"
            MedianMs = $wMed; AvgMs = $wAvg; MinMs = $wMin; MaxMs = $wMax; P95Ms = $wP95
            ActivateMs = $spawnAvg; Rating = $rating
        }) | Format-Table -AutoSize @(
            @{ Label = "App";          Expression = { $_.App } }
            @{ Label = "Valid";        Expression = { $_.Valid } }
            @{ Label = "Median ms";    Expression = { $_.MedianMs };   Align = "Right" }
            @{ Label = "Avg ms";       Expression = { $_.AvgMs };      Align = "Right" }
            @{ Label = "Min ms";       Expression = { $_.MinMs };      Align = "Right" }
            @{ Label = "Max ms";       Expression = { $_.MaxMs };      Align = "Right" }
            @{ Label = "p95 ms";       Expression = { $_.P95Ms };      Align = "Right" }
            @{ Label = "Activate() ms"; Expression = { $_.ActivateMs }; Align = "Right" }
            @{ Label = "Rating";       Expression = { $_.Rating } }
        ) | Out-Host

        Write-Host "  Median ms      = full time-to-first-window (the comparable launch metric)." -ForegroundColor DarkGray
        Write-Host "  Activate() ms  = how long the launch call blocked (varies by app model)." -ForegroundColor DarkGray
        Write-Host ""
    }
}
else {
    # Concise summary for -PassThru callers.
    Write-Host ""
    Write-Host "  $hr" -ForegroundColor DarkGray
    Write-Host "  Results Summary -- $target" -ForegroundColor Cyan
    Write-Host "  $hr" -ForegroundColor DarkGray
    if ($valid.Count -eq 0) {
        Write-Host "  All runs timed out." -ForegroundColor Red
    } else {
        Write-Host ("  Valid {0}/{1}  Median {2} ms  Avg {3} ms  p95 {4} ms  -- {5}" -f `
            $valid.Count, $Runs, $wMed, $wAvg, $wP95, $rating) -ForegroundColor White
    }
    Write-Host "  $hr" -ForegroundColor DarkGray
    Write-Host ""
}

if ($PassThru) {
    [PSCustomObject]@{
        Target     = $target
        Mode       = $mode
        Runs       = $Runs
        ValidRuns  = $valid.Count
        TimedOut   = $nTimeout
        MinMs      = $wMin
        MaxMs      = $wMax
        AvgMs      = $wAvg
        MedianMs   = $wMed
        P95Ms      = $wP95
        SpawnAvgMs = $spawnAvg
        Rating     = $rating.Trim()
    }
}
