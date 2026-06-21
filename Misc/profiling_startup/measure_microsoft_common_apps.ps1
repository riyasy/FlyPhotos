#Requires -Version 5.1
<#
.SYNOPSIS
    Benchmark common Windows app launch performance.

.DESCRIPTION
    Profiles the cold-launch time-to-first-window of Calculator, Photos, Paint, and
    Notepad, then prints a comparison table.

    If -Apps is not supplied, an interactive menu lets you pick which apps to test.

    The script does NOT change any power settings -- it only reports the Windows 11 Power
    Mode (the Best efficiency / Balanced / Best performance slider) that was active during
    the run, so you can record the conditions the numbers were measured under.

    Each app is measured via launch activation (IApplicationActivationManager), which
    returns the real PID; the script then times until a top-level window appears. It is
    fully self-contained -- no other scripts are required.

.PARAMETER Runs
    Measured launch iterations per app (default: 5).

.PARAMETER WarmupRuns
    Warm-up launches per app before recording (default: 1).

.PARAMETER Apps
    Which apps to test. Any of: Calculator, Photos, Paint, Notepad. If omitted, you are
    prompted to choose interactively.

.PARAMETER Force
    Skip the safety confirmation that warns before closing any of the selected apps that
    are already running. Useful for unattended runs. WARNING: benchmarking force-closes
    running instances of the tested apps (so it can measure a cold start), which will
    discard unsaved work in e.g. Notepad or Paint.

.EXAMPLE
    .\measure_microsoft_common_apps.ps1

.EXAMPLE
    .\measure_microsoft_common_apps.ps1 -Apps Calculator, Notepad -Runs 20

.NOTES
    To measure a true cold launch, this benchmark force-closes running instances of the
    apps it tests. Save and close any open Calculator / Photos / Paint / Notepad windows
    first. The script warns and asks for confirmation if it finds any already running
    (unless -Force is given). It never changes any system or power settings.
#>
param(
    [int]$Runs = 5,
    [int]$WarmupRuns = 1,

    [ValidateSet("Calculator", "Photos", "Paint", "Notepad")]
    [string[]]$Apps,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Launch activation interop (self-contained) -------------------------------
# IApplicationActivationManager::ActivateApplication returns the activated PID, the
# reliable way to get a handle on a packaged app's real process.
if (-not ([System.Management.Automation.PSTypeName]'CommonApps.Activator').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace CommonApps
{
    public enum ActivateOptions { None = 0 }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationActivationManager
    {
        IntPtr ActivateApplication(
            [In] string appUserModelId, [In] string arguments,
            [In] ActivateOptions options, [Out] out uint processId);
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

# --- Power Mode overlay (read-only) -------------------------------------------
# The Win11 Power Mode slider is the "overlay scheme". We only READ it to report the
# conditions of the run -- this script never changes any power setting. The whole feature
# is best-effort: if the type or API is unavailable, we just report "unknown".
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
} catch {
    $script:PowerApiOk = $false
}

$GUID_BALANCED        = [Guid]"00000000-0000-0000-0000-000000000000"
$GUID_BEST_PERF       = [Guid]"ded574b5-45a0-4f42-8737-46345c09c238"
$GUID_BEST_EFFICIENCY = [Guid]"961cca80-c73c-4eb8-92e2-13b4a4f3d610"

function Get-PowerModeReport {
    # Returns a display string. Never throws; reports "unknown" if the API is unavailable
    # or the call fails (an empty GUID would otherwise be mis-read as Balanced).
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
    } catch {
        return "unknown (read failed)"
    }
}

# --- Measurement --------------------------------------------------------------
function Clear-RunningInstances {
    param([string]$Name, [int]$SettleMs)
    if ([string]::IsNullOrEmpty($Name)) { return }
    $running = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds $SettleMs
    }
}

function Invoke-LaunchRun {
    # One cold launch via activation. Polls BOTH the returned PID and the process name --
    # single-instance apps (e.g. Notepad) hand off to a different PID, and some apps' PID
    # owns the window but their process name doesn't match Get-Process (e.g. Photos).
    param([string]$AppId, [string]$ProcName, [int]$TimeoutMs, [int]$KillDelayMs)

    $proc = $null; $windowMs = $null
    $sw   = [System.Diagnostics.Stopwatch]::StartNew()
    $appPid  = [CommonApps.Activator]::Activate($AppId, "")
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

    Start-Sleep -Milliseconds $KillDelayMs
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill() } catch {}
        $proc.WaitForExit(3000) | Out-Null
    }
    return [PSCustomObject]@{ SpawnMs = $spawnMs; WindowMs = $windowMs; TimedOut = ($null -eq $windowMs) }
}

function Measure-App {
    # Warm-up + measured runs for one app; prints progress and returns a result row.
    param($App, [int]$Runs, [int]$WarmupRuns, [int]$TimeoutMs, [int]$KillAfterMs, [int]$SettleMs)

    for ($i = 1; $i -le $WarmupRuns; $i++) {
        Clear-RunningInstances -Name $App.ProcessName -SettleMs $SettleMs
        [void](Invoke-LaunchRun -AppId $App.Aumid -ProcName $App.ProcessName -TimeoutMs $TimeoutMs -KillDelayMs $KillAfterMs)
        Start-Sleep -Milliseconds $SettleMs
    }

    $samples = @()
    for ($i = 1; $i -le $Runs; $i++) {
        Clear-RunningInstances -Name $App.ProcessName -SettleMs $SettleMs
        $r = Invoke-LaunchRun -AppId $App.Aumid -ProcName $App.ProcessName -TimeoutMs $TimeoutMs -KillDelayMs $KillAfterMs
        $samples += $r
        if ($r.TimedOut) {
            Write-Host ("      run {0,-3}: TIMEOUT" -f $i) -ForegroundColor Red
        } else {
            Write-Host ("      run {0,-3}: {1} ms" -f $i, $r.WindowMs) -ForegroundColor DarkGray
        }
        Start-Sleep -Milliseconds $SettleMs
    }

    $valid = @($samples | Where-Object { -not $_.TimedOut })
    if ($valid.Count -eq 0) {
        return [PSCustomObject]@{
            App = $App.Name; ValidRuns = "0/$Runs"; MedianMs = $null; AvgMs = $null
            MinMs = $null; MaxMs = $null; P95Ms = $null; LaunchMs = $null; Rating = "All timed out"
        }
    }

    $w        = @($valid | ForEach-Object { $_.WindowMs })
    $sorted   = @($w | Sort-Object)
    $avg      = [math]::Round(($w | Measure-Object -Average).Average, 1)
    $p95Index = [math]::Min([math]::Floor($sorted.Count * 0.95), $sorted.Count - 1)
    $spawnAvg = [math]::Round((@($valid | ForEach-Object { $_.SpawnMs }) | Measure-Object -Average).Average, 1)

    $rating = "Very slow  (>= 4s)"
    if     ($avg -lt 500)  { $rating = "Excellent  (< 0.5s)" }
    elseif ($avg -lt 1000) { $rating = "Good       (< 1s)"   }
    elseif ($avg -lt 2000) { $rating = "Acceptable (< 2s)"   }
    elseif ($avg -lt 4000) { $rating = "Slow       (< 4s)"   }

    return [PSCustomObject]@{
        App       = $App.Name
        ValidRuns = "$($valid.Count)/$Runs"
        MedianMs  = $sorted[[math]::Floor($sorted.Count / 2)]
        AvgMs     = $avg
        MinMs     = ($w | Measure-Object -Minimum).Minimum
        MaxMs     = ($w | Measure-Object -Maximum).Maximum
        P95Ms     = $sorted[$p95Index]
        LaunchMs  = $spawnAvg
        Rating    = $rating
    }
}

# --- Target apps --------------------------------------------------------------
# Launch activation only needs the AUMID; ProcessName is used for pre-launch cleanup.
$catalog = @(
    [PSCustomObject]@{ Name = "Calculator"; Aumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"; ProcessName = "CalculatorApp"   }
    [PSCustomObject]@{ Name = "Photos";     Aumid = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App";    ProcessName = "Microsoft.Photos" }
    [PSCustomObject]@{ Name = "Paint";      Aumid = "Microsoft.Paint_8wekyb3d8bbwe!App";             ProcessName = "mspaint"          }
    [PSCustomObject]@{ Name = "Notepad";    Aumid = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";    ProcessName = "Notepad"          }
)
function Select-AppsInteractive {
    param([object[]]$Catalog)

    Write-Host ""
    Write-Host "  Select apps to test:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $Catalog.Count; $i++) {
        Write-Host ("    [{0}] {1}" -f ($i + 1), $Catalog[$i].Name)
    }
    Write-Host "    [A] All"
    Write-Host ""

    while ($true) {
        $answer = Read-Host "  Enter numbers (e.g. 1,3), or A for all"
        $answer = $answer.Trim()

        if ($answer -eq '' -or $answer -match '^(?i:a|all)$') {
            return $Catalog
        }

        $picked = @()
        $bad    = $false
        foreach ($tok in ($answer -split '[,\s]+' | Where-Object { $_ -ne '' })) {
            if ($tok -match '^\d+$' -and [int]$tok -ge 1 -and [int]$tok -le $Catalog.Count) {
                $picked += $Catalog[[int]$tok - 1]
            } else {
                Write-Host ("  '{0}' is not a valid choice." -f $tok) -ForegroundColor Yellow
                $bad = $true
            }
        }

        if (-not $bad -and $picked.Count -gt 0) {
            # De-dupe by name while preserving order (Select-Object -Unique mis-handles
            # custom objects in PS 5.1).
            $seen   = @{}
            $unique = foreach ($p in $picked) {
                if (-not $seen.ContainsKey($p.Name)) { $seen[$p.Name] = $true; $p }
            }
            return @($unique)
        }
        Write-Host "  Please try again." -ForegroundColor Yellow
    }
}

if ($PSBoundParameters.ContainsKey('Apps') -and $Apps.Count -gt 0) {
    # Keep the user's requested order, restricted to the catalog.
    $targets = @($Apps | ForEach-Object { $name = $_; $catalog | Where-Object { $_.Name -eq $name } })
} else {
    $targets = @(Select-AppsInteractive -Catalog $catalog)
}

$hr = "=" * 64

# --- Environment report (best-effort; never aborts the run) -------------------
# WMI/CIM can be slow or broken on some managed machines, so each probe is guarded and
# degrades to a sensible default rather than throwing under $ErrorActionPreference=Stop.
function Get-CimSafe {
    param([string]$Class)
    try { return Get-CimInstance -ClassName $Class -ErrorAction Stop } catch { return $null }
}

$os       = Get-CimSafe Win32_OperatingSystem
$cs       = Get-CimSafe Win32_ComputerSystem
$battery  = @(Get-CimSafe Win32_Battery)
$chassis  = @((Get-CimSafe Win32_SystemEnclosure).ChassisTypes)
# Chassis types 8,9,10,14 = portable/laptop/notebook/sub-notebook.
$isLaptop = ($battery.Count -gt 0) -or (@($chassis | Where-Object { $_ -in 8,9,10,14 }).Count -gt 0)

$model   = if ($cs -and $cs.Model) { $cs.Model.Trim() } else { "unknown" }
$osName  = if ($os) { $os.Caption } else { "Windows" }
$osBuild = if ($os) { $os.BuildNumber } else { "?" }

Write-Host ""
Write-Host "  Common App Launch Benchmark" -ForegroundColor Cyan
Write-Host "  $hr" -ForegroundColor DarkGray
Write-Host ("  Machine    : {0} ({1})" -f $model, $(if ($isLaptop) { "laptop" } else { "desktop / no battery" })) -ForegroundColor White
Write-Host ("  OS         : {0} (build {1})" -f $osName, $osBuild) -ForegroundColor White
Write-Host ("  Power mode : {0}" -f (Get-PowerModeReport)) -ForegroundColor White
Write-Host ("  Apps       : {0}" -f ($targets.Name -join ", ")) -ForegroundColor White
Write-Host ("  Runs/app   : {0}  (+ {1} warm-up)" -f $Runs, $WarmupRuns) -ForegroundColor White
if (-not $isLaptop) {
    Write-Host "  Note       : No battery detected -- power-mode behavior may differ from a real laptop on DC." -ForegroundColor DarkYellow
}
Write-Host ""

# --- Safety: warn before force-closing the user's open app windows ------------
# Cleanup before each run uses Stop-Process -Force, which discards unsaved work (e.g. an
# open Notepad/Paint document). Only prompt if any selected app is actually running.
$alreadyRunning = @($targets | Where-Object {
    @(Get-Process -Name $_.ProcessName -ErrorAction SilentlyContinue).Count -gt 0
})
if ($alreadyRunning.Count -gt 0 -and -not $Force) {
    Write-Host "  WARNING: these selected apps are already running and will be FORCE-CLOSED" -ForegroundColor Yellow
    Write-Host "           (any unsaved work in them will be lost):" -ForegroundColor Yellow
    Write-Host ("           {0}" -f ($alreadyRunning.Name -join ", ")) -ForegroundColor Yellow
    Write-Host ""
    $confirm = Read-Host "  Save your work, then type Y to continue (anything else cancels)"
    if ($confirm.Trim() -notmatch '^(?i:y|yes)$') {
        Write-Host "  Cancelled. Nothing was changed." -ForegroundColor DarkGray
        return
    }
    Write-Host ""
}

$results = @()

# --- Measure each app ---------------------------------------------------------
foreach ($app in $targets) {
    Write-Host ""
    Write-Host ("  >>> {0}" -f $app.Name) -ForegroundColor Magenta

    try {
        $results += Measure-App -App $app -Runs $Runs -WarmupRuns $WarmupRuns `
                        -TimeoutMs 30000 -KillAfterMs 800 -SettleMs 1000
    } catch {
        Write-Warning ("{0} failed: {1}" -f $app.Name, $_.Exception.Message)
        $results += [PSCustomObject]@{
            App = $app.Name; ValidRuns = "0/$Runs"; MedianMs = $null; AvgMs = $null
            MinMs = $null; MaxMs = $null; P95Ms = $null; LaunchMs = $null; Rating = "ERROR"
        }
    }
}

# --- Combined results ---------------------------------------------------------
Write-Host ""
Write-Host "  $hr" -ForegroundColor DarkGray
Write-Host ("  BENCHMARK SUMMARY  --  Power mode: {0}" -f (Get-PowerModeReport)) -ForegroundColor Cyan
Write-Host "  $hr" -ForegroundColor DarkGray
Write-Host ""

$results |
    Sort-Object { if ($null -eq $_.MedianMs) { [double]::MaxValue } else { $_.MedianMs } } |
    Format-Table -AutoSize @(
        @{ Label = "App";        Expression = { $_.App } }
        @{ Label = "Valid";      Expression = { $_.ValidRuns } }
        @{ Label = "Median ms";  Expression = { $_.MedianMs }; Align = "Right" }
        @{ Label = "Avg ms";     Expression = { $_.AvgMs };    Align = "Right" }
        @{ Label = "Min ms";     Expression = { $_.MinMs };    Align = "Right" }
        @{ Label = "Max ms";     Expression = { $_.MaxMs };    Align = "Right" }
        @{ Label = "p95 ms";      Expression = { $_.P95Ms };    Align = "Right" }
        @{ Label = "Activate() ms"; Expression = { $_.LaunchMs }; Align = "Right" }
        @{ Label = "Rating";      Expression = { $_.Rating } }
    ) | Out-Host

Write-Host "  Median ms      = full time-to-first-window (the comparable launch metric)." -ForegroundColor DarkGray
Write-Host "  Activate() ms  = how long the synchronous ActivateApplication() call blocked." -ForegroundColor DarkGray
Write-Host "                   NOT comparable across apps: classic UWP apps (e.g. Calculator)" -ForegroundColor DarkGray
Write-Host "                   block until the window is nearly up, so it approaches the total;" -ForegroundColor DarkGray
Write-Host "                   WinUI desktop apps (Notepad/Paint) return at process creation." -ForegroundColor DarkGray
Write-Host ""
