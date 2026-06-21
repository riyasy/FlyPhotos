# App Launch Profiler

A small set of PowerShell tools for measuring **how long Windows apps take to show their
first window** — useful for comparing builds, machines, or power conditions (for example,
profiling FlyPhotos against the inbox Photos app).

Each script is **self-contained** — copy or run any one on its own; none depends on the
others.

| Script | Profiles | Launch method |
|--------|----------|---------------|
| [`measure_a_store_app.ps1`](#measure_a_store_appps1) | One Store / UWP / MSIX‑packaged app | Activation broker, or open a file with it |
| [`measure_an_exe.ps1`](#measure_an_exeps1) | One regular `.exe` app | `Process.Start` (optionally with a file argument) |
| [`measure_microsoft_common_apps.ps1`](#measure_microsoft_common_appsps1) | Calculator, Photos, Paint, Notepad (batch) | Activation broker |

---

## Requirements

- **Windows 11** (also works on Windows 10 1809+; the Power Mode line reports "unknown" on
  editions without the overlay API).
- **Windows PowerShell 5.1** (the version shipped with Windows). Run with:

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\measure_a_store_app.ps1
  ```

  or, from an open PowerShell session, first allow scripts for the session:

  ```powershell
  Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
  .\measure_a_store_app.ps1
  ```

- No admin rights required. The scripts **never change any system or power settings** —
  they only *read* the current Power Mode to record the conditions of the run.

---

## How a measurement works

For each run the script:

1. Closes any running instance of the target app (so the launch is a cold start).
2. Launches the app and starts a stopwatch.
3. Waits until the app owns a top‑level window (`MainWindowHandle != 0`).
4. Closes the app and repeats.

It does a few **warm‑up** runs first (not recorded), then the measured runs, and reports
the statistics.

### Metrics

| Column | Meaning |
|--------|---------|
| **Median ms** | Full time‑to‑first‑window. **This is the number to compare.** |
| Avg / Min / Max / p95 ms | Distribution of the per‑run times. |
| **Activate() ms** *(store/common)* | How long the synchronous `ActivateApplication()` call blocked. Varies by app model and is **not** comparable across apps — for classic UWP apps (e.g. Calculator) it approaches the total; for WinUI desktop apps (Notepad/Paint) it returns at process creation. |
| **Spawn ms** *(exe)* | How long `Process.Start` took to return (process‑creation overhead). |
| Rating | `Excellent < 0.5s`, `Good < 1s`, `Acceptable < 2s`, `Slow < 4s`, `Very slow ≥ 4s` (based on the average). |

> **Note on "window up":** detection fires when the window is *created*, slightly before
> first paint. The bias is the same every run, so relative comparisons are valid. For true
> perceived latency, use a high‑FPS screen recording and count frames.

### Power Mode

Every run reports the Windows 11 **Power Mode** (the *Best power efficiency / Balanced /
Best performance* slider) that was active, so you can record the conditions your numbers
were measured under. On a laptop, run on AC and again on battery to compare. The scripts
only read this value — they do not change it.

### Safety — unsaved work

To measure a cold start, the scripts **force‑close** running instances of the app being
tested. If an instance is already running when you start, you'll get a warning and a
confirmation prompt (skip it with `-Force`). **Save your work first** — an open Notepad or
Paint document will be closed.

---

## `measure_a_store_app.ps1`

Profiles a single packaged (Store/UWP/MSIX) app. Packaged apps can't be launched by exe
path; they activate through the Windows activation broker and are identified by an **AUMID**
(Application User Model ID).

### Interactive (no parameters)

```powershell
.\measure_a_store_app.ps1
```

1. Type part of an app name → the script searches your Start apps and lists matches.
2. Pick one and confirm.
3. Enter a file path to open with it (the real "double‑click a file with this app" path,
   via `Launcher.LaunchFileAsync`), **or press Enter** to launch the app with no file.

### Non‑interactive

```powershell
# Launch the app with no file:
.\measure_a_store_app.ps1 -Aumid "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" -Runs 10

# Open a file with a specific package (e.g. profile packaged FlyPhotos opening a photo):
.\measure_a_store_app.ps1 -PackageFamilyName "RYFTools.FlyPhotos_xxxxxxx" `
    -File "C:\pics\sample.jpg" -ProcessName "FlyPhotos" -Runs 20
```

Find an app's AUMID with:

```powershell
Get-StartApps | Where-Object Name -like '*fly*'   # Name + AppID (AUMID) columns
```

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Aumid` | *(prompt)* | App's AUMID. Skips the interactive prompts when supplied. |
| `-File` | *(none)* | File to open against `-PackageFamilyName` via `LaunchFileAsync`. Empty = launch with no file. |
| `-PackageFamilyName` | *(derived)* | Package to open `-File` with. Derived automatically in interactive mode. |
| `-AppArgs` | `""` | Activation arguments (launch‑without‑file mode only). |
| `-ProcessName` | *(derived)* | Process name used to find the window and clean up. Derived from the package manifest in interactive mode. |
| `-Runs` | `5` | Measured iterations. |
| `-WarmupRuns` | `1` | Warm‑up iterations. |
| `-TimeoutSeconds` | `30` | Give up on a run after this long. |
| `-KillAfterMs` | `2000` | Wait this long after the window appears before closing the app. |
| `-SettleMs` | `1500` | Pause between runs. |
| `-PassThru` | *(off)* | Emit a summary object (for scripting); suppresses the banner/table. |
| `-Force` | *(off)* | Skip the "already running" confirmation. |

---

## `measure_an_exe.ps1`

Profiles a single regular (non‑Store) `.exe`. For Store/packaged apps use
`measure_a_store_app.ps1` instead.

### Interactive (no parameters)

```powershell
.\measure_an_exe.ps1
```

1. Enter the path to an `.exe` (loops until a real file is given).
2. Enter a file or argument to pass to it, **or press Enter** for none.

### Non‑interactive

```powershell
.\measure_an_exe.ps1 -AppPath "C:\Tools\iview\i_view64.exe" -AppArgs "C:\pics\a.jpg" -Runs 10
```

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-AppPath` | *(prompt)* | Full path to the `.exe`. Skips the interactive prompt when supplied. |
| `-AppArgs` | `""` | Argument (e.g. a file to open) passed to the exe. |
| `-Runs` | `5` | Measured iterations. |
| `-WarmupRuns` | `1` | Warm‑up iterations. |
| `-TimeoutSeconds` | `30` | Give up on a run after this long. |
| `-KillAfterMs` | `2000` | Wait this long after the window appears before closing the app. |
| `-SettleMs` | `1500` | Pause between runs. |
| `-PassThru` | *(off)* | Emit a summary object (for scripting); suppresses the banner/table. |
| `-Force` | *(off)* | Skip the "already running" confirmation. |

---

## `measure_microsoft_common_apps.ps1`

Benchmarks several inbox apps in one go and prints a comparison table. Good for a quick
"how is this machine performing" snapshot.

### Usage

```powershell
# Interactive menu to choose which apps:
.\measure_microsoft_common_apps.ps1

# Specific apps, more runs:
.\measure_microsoft_common_apps.ps1 -Apps Calculator, Notepad -Runs 20

# Unattended (no prompts even if apps are running):
.\measure_microsoft_common_apps.ps1 -Force
```

If `-Apps` is omitted, an interactive menu lets you pick (numbers like `1,3`, or `A` for
all).

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Apps` | *(prompt)* | Any of `Calculator`, `Photos`, `Paint`, `Notepad`. |
| `-Runs` | `5` | Measured iterations per app. |
| `-WarmupRuns` | `1` | Warm‑up iterations per app. |
| `-Force` | *(off)* | Skip the "already running" confirmation. |

### Example output

```
  Common App Launch Benchmark
  ================================================================
  Machine    : Z170M-D3H (desktop / no battery)
  OS         : Microsoft Windows 11 Home (build 26100)
  Power mode : Balanced (Recommended)
  Apps       : Calculator, Notepad, Photos
  Runs/app   : 5  (+ 1 warm-up)

  ...

  BENCHMARK SUMMARY  --  Power mode: Balanced (Recommended)
  ================================================================

App        Valid Median ms Avg ms Min ms Max ms p95 ms Activate() ms Rating
---------- ----- --------- ------ ------ ------ ------ ------------- ------
Calculator 5/5         365    355    330    370    370         355   Excellent  (< 0.5s)
Notepad    5/5         406    402    372    428    428          73   Excellent  (< 0.5s)
Photos     5/5        1159   1181   1107   1277   1277          83   Acceptable (< 2s)
```

---

## Tips

- **Run several iterations.** Use `-Runs 20` or more; the **median** over many runs is the
  meaningful figure. Single launches are noisy.
- **Close background load** (browsers, indexers) for repeatable numbers.
- **Compare like for like.** Note the Power Mode in the output; on laptops, AC vs battery
  can change results substantially.
- **Scripting:** `measure_a_store_app.ps1` and `measure_an_exe.ps1` accept `-PassThru` to
  return a result object instead of printing a table, e.g.:

  ```powershell
  $r = .\measure_an_exe.ps1 -AppPath "C:\Tools\app.exe" -Runs 20 -PassThru
  $r.MedianMs
  ```
