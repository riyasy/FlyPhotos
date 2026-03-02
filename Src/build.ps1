<#
.SYNOPSIS
    Local build script matching the GitHub Actions workflow.
.DESCRIPTION
    Builds the native C++ libraries, copies dependencies, and publishes the .NET project.
#>

param(
    [ValidateSet("x64", "ARM64", "Both")]
    [string]$TargetPlatform = "x64"
)

$ErrorActionPreference = "Stop"

# Script lives inside Src → treat this as root
$Root = $PSScriptRoot

$matrix = @(
    @{ platform = "x64";  rid = "win-x64";  triplet = "x64-windows" },
    @{ platform = "ARM64"; rid = "win-arm64"; triplet = "arm64-windows" }
)

if ($TargetPlatform -ne "Both") {
    $matrix = $matrix | Where-Object { $_.platform -eq $TargetPlatform }
}

Write-Host "Starting Local Build Process..." -ForegroundColor Cyan

foreach ($job in $matrix) {

    $platform = $job.platform
    $rid      = $job.rid
    $triplet  = $job.triplet

    Write-Host "`n========================================" -ForegroundColor Magenta
    Write-Host " Building for Platform: $platform" -ForegroundColor Magenta
    Write-Host "========================================" -ForegroundColor Magenta

    # ------------------------------------------------------------
    # 1. Restore vcpkg Dependencies (Manifest Mode Explicit)
    # ------------------------------------------------------------
    Write-Host "`n--> Restoring vcpkg packages ($triplet)..." -ForegroundColor Yellow

    Push-Location (Join-Path $Root "build_libheif")
    try {
        vcpkg install --triplet $triplet --x-manifest-root=.
    }
    finally {
        Pop-Location
    }

    # ------------------------------------------------------------
    # 2. Build Native C++ Projects
    # ------------------------------------------------------------
    Write-Host "`n--> Building C++ Projects ($platform)..." -ForegroundColor Yellow

    $nativeProjects = @(
        "FlyNativeLib\FlyNativeLib.vcxproj",
        "FlyNativeLibHeif\FlyNativeLibHeif.vcxproj",
        "FlyContextMenuHelper\FlyContextMenuHelper.vcxproj"
    )

    foreach ($proj in $nativeProjects) {
        $projPath = Join-Path $Root $proj
        Push-Location (Split-Path $projPath)
        try {
            msbuild (Split-Path $projPath -Leaf) `
                /p:Configuration=Release `
                /p:Platform=$platform `
                /p:PlatformToolset=v143
        }
        finally {
            Pop-Location
        }
    }

    # ------------------------------------------------------------
    # 3. Copy Native Binaries
    # ------------------------------------------------------------
    Write-Host "`n--> Copying native binaries..." -ForegroundColor Yellow

    $externalDir = Join-Path $Root "FlyPhotos\External\$platform"

    # Ensure directory exists
    New-Item -ItemType Directory -Path $externalDir -Force | Out-Null

    # Clean existing files (non-recursive safety)
    if (Test-Path $externalDir) {
        Get-ChildItem $externalDir -File -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    # Copy built native outputs
    Copy-Item (Join-Path $Root "FlyNativeLib\$platform\Release\FlyNativeLib.dll") `
        -Destination $externalDir -Force

    Copy-Item (Join-Path $Root "FlyNativeLibHeif\$platform\Release\FlyNativeLibHeif.dll") `
        -Destination $externalDir -Force

    Copy-Item (Join-Path $Root "FlyContextMenuHelper\$platform\Release\FlyContextMenuHelper.exe") `
        -Destination $externalDir -Force

    # Copy vcpkg-built DLLs
    $vcpkgBinDir = Join-Path $Root "build_libheif\vcpkg_installed\$triplet\bin"

    if (Test-Path $vcpkgBinDir) {
        Get-ChildItem (Join-Path $vcpkgBinDir "*.dll") |
            Copy-Item -Destination $externalDir -Force
    }

    # ------------------------------------------------------------
    # 4. Publish .NET Project
    # ------------------------------------------------------------
    Write-Host "`n--> Publishing .NET App ($rid)..." -ForegroundColor Yellow

    $publishOut = Join-Path $Root "publish\$rid"

    dotnet publish (Join-Path $Root "FlyPhotos\FlyPhotos.csproj") `
        -c Release `
        -r $rid `
        /p:Platform=$platform `
        -o $publishOut
        # Add --no-build here later if desired

    Write-Host "`n[SUCCESS] Build completed for $platform!" -ForegroundColor Green
}

Write-Host "`nAll builds finished." -ForegroundColor Cyan