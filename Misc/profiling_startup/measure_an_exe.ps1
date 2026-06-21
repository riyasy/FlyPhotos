#Requires -Version 5.1
<#
.SYNOPSIS
    Measures the launch performance of a regular (non-Store) Windows .exe application.

.DESCRIPTION
    Run with no target and the script is interactive:
      1. Asks for the path to an .exe (loops until a real file is given).
      2. Asks for a file/argument to pass to it (Enter = launch with no arguments).
      3. Times each launch until a top-level window appears and reports the results,
         including the active Power Mode.

    It can also be driven non-interactively with -AppPath / -AppArgs.

    (For Store / UWP / MSIX-packaged apps -- which cannot be launched by exe path -- use
    Measure-StoreAppLaunch.ps1 instead.)

.PARAMETER AppPath
    Full path to the executable to profile. When supplied, the interactive prompts are
    skipped.

.PARAMETER AppArgs
    Optional argument (e.g. a file to open) passed to the executable.

.PARAMETER Runs
    Number of measured launch iterations (default: 5).

.PARAMETER WarmupRuns
    Number of warm-up runs before recording (default: 1).

.PARAMETER TimeoutSeconds
    Max seconds to wait for a window to appear (default: 30).

.PARAMETER KillAfterMs
    Milliseconds to wait before closing the app after its window appears (default: 2000).

.PARAMETER SettleMs
    Pause after each run before the next launch (default: 1500).

.PARAMETER PassThru
    Emit a summary object on the success stream. Suppresses the rich power-mode banner and
    final table.

.PARAMETER Force
    Skip the safety confirmation shown when the executable is already running (its
    instances are force-closed before each run for a clean cold start, discarding unsaved
    work). The prompt only appears in interactive/standalone use, never under -PassThru.

.EXAMPLE
    .\Measure-AppLaunch.ps1
    # Interactive: enter an exe path, optionally a file, measure.

.EXAMPLE
    .\Measure-AppLaunch.ps1 -AppPath "C:\Tools\iview\i_view64.exe" -AppArgs "C:\pics\a.jpg" -Runs 5

.NOTES
    "Window up" is detected via MainWindowHandle != 0, which fires when the window is
    created (slightly before first paint). To measure a clean cold start, running instances
    of the same executable are closed before each run -- save your work first. The script
    never changes any system or power settings.
#>
param(
    [string]$AppPath = "",
    [string]$AppArgs = "",

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
        [string]$ExePath,
        [string]$ExeArgs,
        [string]$ProcName,
        [int]$TimeoutMs,
        [int]$KillDelayMs
    )

    # Quote a single argument that contains spaces (the common "one file path" case).
    $quotedArgs = $ExeArgs
    if ($quotedArgs -ne '' -and $quotedArgs -match ' ' -and $quotedArgs -notmatch '^"') {
        $quotedArgs = '"' + $quotedArgs + '"'
    }

    $startInfo                 = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName        = $ExePath
    $startInfo.Arguments       = $quotedArgs
    $startInfo.UseShellExecute = $true

    $proc     = $null
    $started  = $null
    $windowMs = $null
    $spawnMs  = $null
    $sw       = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $started = [System.Diagnostics.Process]::Start($startInfo)
        $spawnMs = $sw.ElapsedMilliseconds
    } catch {
        # Launch failed (e.g. UAC elevation cancelled, bad arguments). Treat as a timeout
        # for this run rather than crashing the whole benchmark.
        return [PSCustomObject]@{ SpawnMs = $null; WindowMs = $null; TimedOut = $true }
    }

    # Poll BOTH the process we started and (by name) any sibling it handed off to -- some
    # exes are launcher stubs that spawn the real windowed process under the same name.
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ($started) {
            try { $started.Refresh() } catch { $started = $null }
            if ($started -and -not $started.HasExited -and $started.MainWindowHandle -ne [IntPtr]::Zero) {
                $proc = $started; $windowMs = $sw.ElapsedMilliseconds; break
            }
        }
        if ($ProcName -ne '') {
            $p = Get-Process -Name $ProcName -ErrorAction SilentlyContinue |
                 Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
                 Select-Object -First 1
            if ($p) { $proc = $p; $windowMs = $sw.ElapsedMilliseconds; break }
        }
        if (-not $started -and $ProcName -eq '') { break }
        Start-Sleep -Milliseconds 15
    }

    Start-Sleep -Milliseconds $KillDelayMs

    foreach ($pr in @($proc, $started)) {
        if ($pr -and -not $pr.HasExited) {
            try { $pr.Kill() } catch {}
            $pr.WaitForExit(3000) | Out-Null
        }
    }

    return [PSCustomObject]@{
        SpawnMs  = $spawnMs
        WindowMs = $windowMs
        TimedOut = ($null -eq $windowMs)
    }
}

# --- Resolve the target -------------------------------------------------------
$rich = -not $PassThru

if ($AppPath -eq '') {
    # Interactive flow.
    Write-Host ""
    Write-Host "  App Launch Profiler (.exe)" -ForegroundColor Cyan
    Write-Host ("  {0}" -f ("-" * 62)) -ForegroundColor DarkGray

    while ($true) {
        $raw = (Read-Host "  Path to the .exe to test").Trim().Trim('"')
        if ($raw -eq '') { Write-Host "  Please enter a path." -ForegroundColor Yellow; continue }
        if (-not (Test-Path -LiteralPath $raw -PathType Leaf)) { Write-Host "  File not found. Try again." -ForegroundColor Yellow; continue }
        if ([System.IO.Path]::GetExtension($raw) -ne '.exe') {
            Write-Host "  That isn't an .exe. For Store apps use Measure-StoreAppLaunch.ps1." -ForegroundColor Yellow
            continue
        }
        $AppPath = (Resolve-Path -LiteralPath $raw).Path
        break
    }

    $rawArg = (Read-Host "  File / argument to pass (Enter = none)").Trim().Trim('"')
    if ($rawArg -ne '') { $AppArgs = $rawArg }
}

# --- Validation ---------------------------------------------------------------
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    Write-Error "Executable not found: $AppPath"
    exit 1
}

$appName   = [System.IO.Path]::GetFileName($AppPath)
$procName  = [System.IO.Path]::GetFileNameWithoutExtension($AppPath)
$timeoutMs = $TimeoutSeconds * 1000
$hr        = "-" * 62

# --- Banner -------------------------------------------------------------------
Write-Host ""
Write-Host "  App Launch Profiler (.exe)" -ForegroundColor Cyan
Write-Host "  $hr" -ForegroundColor DarkGray
if ($rich) {
    Write-Host ("  Power mode : {0}" -f (Get-PowerModeReport)) -ForegroundColor White
}
Write-Host ("  App     : {0}" -f $appName) -ForegroundColor White
Write-Host ("  Path    : {0}" -f $AppPath) -ForegroundColor White
if ($AppArgs -ne '') { Write-Host ("  Argument: {0}" -f $AppArgs) -ForegroundColor White }
Write-Host ("  Cleanup : close '{0}' before each run" -f $procName) -ForegroundColor White
Write-Host ("  Runs    : {0}  (+ {1} warm-up)" -f $Runs, $WarmupRuns) -ForegroundColor White
Write-Host ("  Timeout : {0}s per run" -f $TimeoutSeconds) -ForegroundColor White
Write-Host ""

# --- Safety: warn before force-closing an already-running instance ------------
if ($rich -and -not $Force -and
    @(Get-Process -Name $procName -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Host ("  WARNING: '{0}' is already running and will be FORCE-CLOSED before each" -f $procName) -ForegroundColor Yellow
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
        Clear-RunningInstances -Name $procName -SettleMs $SettleMs
        $r      = Invoke-SingleRun -ExePath $AppPath -ExeArgs $AppArgs -ProcName $procName -TimeoutMs $timeoutMs -KillDelayMs $KillAfterMs
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
    Clear-RunningInstances -Name $procName -SettleMs $SettleMs
    $r       = Invoke-SingleRun -ExePath $AppPath -ExeArgs $AppArgs -ProcName $procName -TimeoutMs $timeoutMs -KillDelayMs $KillAfterMs
    $results += $r

    if ($r.TimedOut) {
        Write-Host ("    Run {0,-3}: TIMEOUT  (no window handle in {1}s)" -f $i, $TimeoutSeconds) -ForegroundColor Red
    } else {
        Write-Host ("    Run {0,-3}: window = {1,-8} ms   spawn = {2} ms" -f $i, $r.WindowMs, $r.SpawnMs) -ForegroundColor Green
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
        Write-Host "  All runs timed out -- no window appeared within ${TimeoutSeconds}s. The app may" -ForegroundColor Red
        Write-Host "  not open a visible window, or it needs different arguments." -ForegroundColor Red
        Write-Host ""
    } else {
        ,([PSCustomObject]@{
            App = $appName; Valid = "$($valid.Count)/$Runs"
            MedianMs = $wMed; AvgMs = $wAvg; MinMs = $wMin; MaxMs = $wMax; P95Ms = $wP95
            SpawnMs = $spawnAvg; Rating = $rating
        }) | Format-Table -AutoSize @(
            @{ Label = "App";       Expression = { $_.App } }
            @{ Label = "Valid";     Expression = { $_.Valid } }
            @{ Label = "Median ms"; Expression = { $_.MedianMs }; Align = "Right" }
            @{ Label = "Avg ms";    Expression = { $_.AvgMs };    Align = "Right" }
            @{ Label = "Min ms";    Expression = { $_.MinMs };    Align = "Right" }
            @{ Label = "Max ms";    Expression = { $_.MaxMs };    Align = "Right" }
            @{ Label = "p95 ms";    Expression = { $_.P95Ms };    Align = "Right" }
            @{ Label = "Spawn ms";  Expression = { $_.SpawnMs };  Align = "Right" }
            @{ Label = "Rating";    Expression = { $_.Rating } }
        ) | Out-Host

        Write-Host "  Median ms = full time-to-first-window (the comparable launch metric)." -ForegroundColor DarkGray
        Write-Host "  Spawn ms  = time for Process.Start to return (process-creation overhead)." -ForegroundColor DarkGray
        Write-Host ""
    }
}
else {
    Write-Host ""
    Write-Host "  $hr" -ForegroundColor DarkGray
    Write-Host "  Results Summary -- $appName" -ForegroundColor Cyan
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
        Target     = $appName
        Mode       = "exe launch"
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
