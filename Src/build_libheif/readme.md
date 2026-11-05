# build_libheif: the HEIC / AVIF decoder DLLs

This folder builds the third-party DLLs that FlyPhotos uses to open **HEIC/HEIF** and **AVIF** files:

| DLL | Library | What it does |
|---|---|---|
| `heif.dll` | [libheif](https://github.com/strukturag/libheif) | Reads the HEIF/AVIF container and drives the decoders |
| `libde265.dll` | [libde265](https://github.com/strukturag/libde265) | HEVC decoder (HEIC photos from phones and cameras) |
| `dav1d.dll` | [dav1d](https://code.videolan.org/videolan/dav1d) | AV1 decoder (AVIF, still and animated) |
| `libpng16.dll` | [libpng](https://github.com/pnggroup/libpng) | PNG support, used by FlyNativeLib |
| `z.dll` | [zlib](https://github.com/madler/zlib) | Compression library that libpng16.dll needs |

They are built with [vcpkg](https://github.com/microsoft/vcpkg) and copied to
`Src\FlyPhotos\External\x64\` and `Src\FlyPhotos\External\ARM64\`. Our own `FlyNativeLibHeif.dll`
links against `heif.dll` and is the only thing the C# app talks to.

> **The one rule:** `heif.dll` must import `dav1d.dll` **and** `libde265.dll` directly, and never
> `aom.dll`. A plain `vcpkg install libheif` does not give you that. Read [Why this needs care](#why-this-needs-care) before changing anything.

---

## 1. Building (nothing changed, you just need the DLLs)

### Prerequisites

- Visual Studio 2022 or later, with the **Desktop development with C++** workload, including the
  ARM64 build tools if you build ARM64.
- Git.
- Run everything from a **Developer PowerShell for VS**, so `msbuild` and `dumpbin` are on PATH.

You do **not** need to install vcpkg. The script clones its own copy into `build_libheif\vcpkg\`.

### Steps

```powershell
cd Src
.\01_vcpkg_libheif.ps1 -TargetPlatform Both   # vcpkg build, copies DLLs to FlyPhotos\External\<Platform>
.\03_native_dlls.ps1   -TargetPlatform Both   # rebuilds FlyNativeLibHeif.dll against them
```

(`00_build_all.ps1` runs these as part of the full build.)

The first run downloads and compiles everything and takes about 10 minutes per platform. Later runs use
vcpkg's binary cache and take seconds. The script ends with `[OK] heif.dll imports dav1d.dll +
libde265.dll` for each platform. If it fails with `heif.dll does not import ...` instead, see
[Troubleshooting](#7-troubleshooting).

> If `VCPKG_ROOT` is set on your machine (Visual Studio sets it), vcpkg prints a "mismatched
> VCPKG_ROOT" warning. It is harmless. If a script that redirects output turns it into a failure,
> run `$env:VCPKG_ROOT = $null` first.

### What the script does

`01_vcpkg_libheif.ps1`:

1. Clones and bootstraps vcpkg into `build_libheif\vcpkg\` if it is not there yet.
2. If `ports\libheif\` does not exist, copies vcpkg's built-in libheif port there. This is the
   **overlay port**: our patched copy, which vcpkg uses instead of its own (`--overlay-ports`).
3. Patches the overlay port (see [Our changes to the libheif port](#4-our-changes-to-the-libheif-port)).
   Every patch is idempotent, so running the script again is safe.
4. Rewrites the project manifest `build_libheif\vcpkg.json` (what we ask vcpkg for).
5. Creates the `arm64-windows-release` triplet in `triplets\` (vcpkg has no built-in one).
6. Runs `vcpkg install` once per platform, into its own folder (`vcpkg_installed_x64\`,
   `vcpkg_installed_arm64\`), so one platform never deletes the other's files.
7. Copies the DLLs to `FlyPhotos\External\<Platform>\` and checks the imports of `heif.dll`.

Options: `-TargetPlatform x64|ARM64|Both`, `-SkipPatch` (build from `ports\libheif\` exactly as it
is, which is what CI does), `-IncludeDebug` (also build debug binaries).

---

## 2. Files in this folder

| Path | Committed | Purpose |
|---|---|---|
| `vcpkg.json` | yes | Project manifest: which packages and **features** we want |
| `vcpkg-configuration.json` | yes | The vcpkg **baseline** (a commit of microsoft/vcpkg). It fixes the version of every package except libheif. |
| `ports\libheif\` | yes | The patched overlay port. CI builds from this folder as committed. |
| `fly-patches\` | yes | Our fixes to libheif's own source code (see [section 5](#5-temporary-fixes-to-libheifs-source-fly-patches)) |
| `triplets\arm64-windows-release.cmake` | yes | Release-only ARM64 triplet |
| `vcpkg\` | no | Our private vcpkg clone (git-ignored) |
| `vcpkg_installed_*\` | no | Build output (git-ignored) |

Package versions come from two places:

- **libheif**: the overlay port (`ports\libheif\vcpkg.json`, `"version"`).
- **Everything else** (dav1d, libde265, libpng, zlib): the baseline in `vcpkg-configuration.json`.

---

## 3. Upgrading (new libheif, dav1d, libde265, ...)

Always upgrade **libheif and the baseline together**, from the same vcpkg commit. Do not edit
the version or SHA512 in `ports\libheif\` by hand: the upstream port's patches and build options change
between releases, and a hand edit fails or, worse, builds something subtly wrong.

### Step by step

1. **Pick a vcpkg commit.** Usually the latest `master` of microsoft/vcpkg. Check that its
   `ports/libheif/vcpkg.json` has the version you want.

2. **Move our vcpkg clone to it:**
   ```powershell
   cd Src\build_libheif\vcpkg
   git fetch origin
   git checkout <commit>
   .\bootstrap-vcpkg.bat -disableMetrics
   ```

3. **Set the same commit as the baseline** in `vcpkg-configuration.json` (`"baseline": "<commit>"`).

4. **Read what changed upstream** before building. This is the step that prevents surprises:
   ```powershell
   git diff <old-baseline> <new-commit> -- ports/libheif ports/libde265 ports/dav1d ports/libpng ports/zlib
   ```
   Look in particular at `ports/libheif/portfile.cmake` and `ports/libheif/vcpkg.json` for:
   - anything mentioning `DAV1D`, `PLUGIN`, `LIBDE265` or `aom`
   - features that were added, renamed or made default
   - new files in the `PATCHES` list (they must be committed later)

   Then compare against [section 4](#4-our-changes-to-the-libheif-port): does each of our changes still
   apply, and is any of them now unnecessary?

5. **Check our source patches.** Read the libheif release notes. If a release fixes a problem one
   of our `fly-patches` works around, delete that patch. If a patch no longer applies, the build
   fails with an error naming the patch.

6. **Re-seed the overlay:** delete `ports\libheif\`, then run
   ```powershell
   .\01_vcpkg_libheif.ps1 -TargetPlatform Both
   .\03_native_dlls.ps1   -TargetPlatform Both
   ```

7. **Verify** ([section 6](#6-verifying-a-build)). Do not skip the real-file test: the unit tests did
   not catch the 1.23.7 regression.

8. **Clean up stale DLLs.** The scripts copy over existing files but never delete. If a library's DLL
   was renamed (zlib went from `zlib1.dll` to `z.dll` once), delete the old name from
   `FlyPhotos\External\x64\` and `ARM64\` yourself.

9. **Commit:** `vcpkg.json`, `vcpkg-configuration.json`, everything in `ports\libheif\` (**including
   new `.patch`/`.diff` files**, or CI fails with "patch not found"), `fly-patches\`, and the DLLs in
   `FlyPhotos\External\`. If `FlyNativeLibHeif`'s own source changed, bump its `version.h`.

---

## 4. Our changes to the libheif port

### Why this needs care

libheif can decode AVIF with **aom** or **dav1d**, and each decoder can be linked into `heif.dll` or
loaded at runtime as a **plugin**. We want:

- **dav1d, not aom.** FlyPhotos only views images. aom is a ~10 MB encoder+decoder; dav1d is a ~1 MB
  decoder that is several times faster.
- **Linked, not plugin.** A plugin DLL is loaded from a plugin folder at runtime. We set no plugin
  folder, so a plugin build "succeeds" but cannot decode AVIF. Linked means `dav1d.dll` appears in
  `heif.dll`'s import table and Windows loads it automatically.
- **libde265 linked in**, for HEIC.

All of these failures **build without errors**. Only the import table of `heif.dll` (or a failing
image) shows them. That is why the script and both CI workflows check it with `dumpbin`.

### The changes

| Where | Change | Why |
|---|---|---|
| `ports\libheif\vcpkg.json` | Add a `dav1d` feature, if upstream has none | Lets us request `libheif[dav1d]`. Upstream has had it since 1.23.1, so this step is now skipped. |
| `ports\libheif\portfile.cmake` | Map `dav1d` to `WITH_DAV1D` / `VCPKG_LOCK_FIND_PACKAGE_DAV1D` | Same, already upstream since 1.23.1 |
| `ports\libheif\portfile.cmake` | Remove `-DWITH_DAV1D=OFF` | Older ports hard-disabled dav1d |
| `ports\libheif\portfile.cmake` | Remove upstream's `-DWITH_DAV1D_PLUGIN=${DAV1D_PLUGIN}`, add `-DWITH_DAV1D_PLUGIN=OFF` | Upstream makes dav1d a **plugin** for DLL builds (and its line comes later on the command line, so it would override ours) |
| `ports\libheif\find-modules.diff` | Add `NAMES_PER_DIR` to `FindDAV1D.cmake` | Without it CMake can pick the wrong architecture's `.lib` on ARM64. Upstream already does this for aom/libde265/x265. |
| `ports\libheif\portfile.cmake` | Add each `fly-patches\*.patch` to `PATCHES` | Our source fixes ([section 5](#5-temporary-fixes-to-libheifs-source-fly-patches)) |
| `vcpkg.json` (project) | `libheif` with `"default-features": false` and `"features": ["dav1d", "libde265"]` | `default-features: false` keeps out `hevc` (x265, an encoder). Since 1.23.1 libde265 is a feature, so it must be listed, or HEIC stops working even though `libde265.dll` is still installed. |

### Per-upgrade history

**1.21.2 → 1.23.0** (June 2026)
- The `LIBHEIF_STATIC_BUILD` guard moved from `heif_library.h` to `heif_export.h`, and
  `cxx-linkage-pkgconfig.diff` changed. A hand-edited version/SHA512 would have failed; re-seeding
  handled it.
- zlib's DLL was renamed `zlib1.dll` → `z.dll` (imported by `libpng16.dll`). The stale `zlib1.dll`
  had to be deleted from `External\` by hand.
- The `-DWITH_AOM_PLUGIN` line we used as an anchor was gone; the script anchors on
  `-DWITH_LIBSHARPYUV=OFF` instead.

**1.23.0 → 1.23.7** (November 2026; dav1d 1.5.3 → 1.5.4, libde265 1.1.1 → 1.1.3, libpng 1.6.58 → 1.6.59)
- Upstream added a `dav1d` feature, but with **plugin mode on** for DLL builds. Without our removal
  of `-DWITH_DAV1D_PLUGIN=${DAV1D_PLUGIN}`, AVIF would silently have stopped working.
- libde265 became a (default) **feature**. Without adding it to our manifest, HEIC would have
  stopped working.
- New upstream patch `vvc-pkgconfig.patch` in the port. It must be committed.
- New libheif bug that breaks animated AVIF; worked around with a source patch (next section).
- Gains: many security fixes in libheif and libde265 (crafted-file crashes, memory corruption);
  cropped HEIC/AVIF that failed with "Security limit exceeded" since 1.23.0 open again
  (libheif #1856); faster AV1 decoding in dav1d.

---

## 5. Temporary fixes to libheif's source (`fly-patches\`)

Each `.patch` here is a fix to libheif's C++ source that upstream does not have yet. The script copies
it into `ports\libheif\` and adds it to the port's `PATCHES` list, so vcpkg applies it when it unpacks
the libheif source. CI uses the copy in `ports\libheif\`.

Format: a normal `git diff` against the libheif release (paths `a/libheif/...`, `b/libheif/...`),
LF line endings (enforced by `.gitattributes`). To check one applies to a new release before building:

```powershell
# in an empty folder holding the file from that release at the same relative path
git init; git apply --check -v <path-to>.patch
```

### `sequence-config-only-on-decoder-change.patch` (added for libheif 1.23.7)

- **Symptom:** animated AVIFs whose frames are stored in several *chunks* stop playing after 10–16
  frames, at the first chunk boundary. dav1d prints `Error parsing OBU data`. Animations stored in
  a single chunk (e.g. from ezgif) are fine.
- **Cause:** libheif commit `0b42dd718` ("Drain sequence decoders before switching sample
  descriptions", in 1.23.7) changed
  `libheif/sequences/track_visual.cc` to send the decoder configuration (the AV1 sequence header
  from the `av1C` box) at the start of **every chunk** instead of once. The next sample is usually
  not a keyframe. When that sequence header is not byte-identical to the one in the stream, dav1d
  treats it as a new video sequence that must start with a keyframe, and rejects the frame.
- **Fix:** send the configuration at sample 0 (as in 1.23.0), and at a chunk start only when the
  chunk uses a different decoder (the case the upstream change was made for).
- **Proof:** with the patch, all 185 HEIF/AVIF test files decode exactly as with 1.23.0 (same
  pixels, frame counts and durations), plus the two cropped files that 1.23.0 could not open.
- **When to remove:** when a libheif release fixes it. Check `track_visual.cc` for the
  `is_first_sample` line, or test the multi-chunk files in `_testdata\avif_animated` (`1.avif`,
  `2.avif`, `3.avif`, `5.avif`, `6.avif`, `7.avif`) without the patch. Then delete the file from
  `fly-patches\` and re-seed.

---

## 6. Verifying a build

1. **Imports.** The script does this. To check by hand:
   ```powershell
   dumpbin /dependents FlyPhotos\External\x64\heif.dll     # must list dav1d.dll and libde265.dll, not aom.dll
   dumpbin /dependents FlyPhotos\External\x64\libpng16.dll # must list z.dll
   ```
   Also check that every DLL a library imports exists in `External\<Platform>\`.

2. **Unit tests** (x64). `HeifDecoderTests` decode small HEIC/AVIF fixtures through
   `FlyNativeLibHeif.dll`:
   ```powershell
   cd Src
   dotnet build FlyPhotos.Tests -c Release
   .\FlyPhotos.Tests\bin\Release\net10.0\FlyPhotos.Tests.exe --filter "FullyQualifiedName~Heif"
   ```
   Note: the build copies the DLLs from `External\x64` only when they are newer than the ones already
   in the `bin` folder. If in doubt, copy them over by hand.

3. **Real files: old vs. new.** `HeifSweepTests` decodes every HEIF/AVIF file under a folder (primary
   image, thumbnail, all animation frames) and writes one line per file. Run it once with the old DLLs
   and once with the new ones, then compare the two files. Any difference must be explainable.
   ```powershell
   $env:FLY_HEIF_SWEEP_DIR = "M:\Curr.Projects\Projects-Tech\02_Fly\_testdata"
   $env:FLY_HEIF_SWEEP_OUT = "$env:TEMP\sweep_new.tsv"
   .\FlyPhotos.Tests\bin\Release\net10.0\FlyPhotos.Tests.exe --filter "FullyQualifiedName~HeifSweepTests"
   ```
   The old DLLs are in git: `git cat-file blob HEAD:Src/FlyPhotos/External/x64/heif.dll > heif.dll`
   (from `cmd`, not PowerShell, which would corrupt the binary).

4. **In the app**: open a few HEIC photos, a still AVIF and an animated AVIF on both platforms.

---

## 7. Troubleshooting

| Symptom | Likely cause |
|---|---|
| `heif.dll does not import dav1d.dll` | dav1d built as a plugin or not at all. Check `-DWITH_DAV1D_PLUGIN` in `ports\libheif\portfile.cmake` (must be `OFF`, exactly once) and `"dav1d"` in the project `vcpkg.json` features. |
| `heif.dll does not import libde265.dll` | `"libde265"` missing from the project `vcpkg.json` features, or upstream renamed it |
| `heif.dll imports aom.dll` | The `aom` feature got enabled; the overlay port was not used (`--overlay-ports`), or the manifest lost `"default-features": false` |
| HEIC/AVIF fails to open but the build was fine | Run the import check; then look for a missing DLL in `External\<Platform>\` |
| `Pattern not found in portfile.cmake: ...` | Upstream changed the line the script anchors on. Read the new portfile and update the anchor in `01_vcpkg_libheif.ps1`. |
| vcpkg: patch failed to apply | A `fly-patches` file no longer matches the new libheif source. Either upstream fixed it (delete the patch) or regenerate it against the new source. |
| CI: `patch not found` / file missing | A new `.patch`/`.diff` in `ports\libheif\` was not committed |
| ARM64 picks up x64 libraries | `NAMES_PER_DIR` missing from a `Find*.cmake` (see `find-modules.diff`) |
| `Invalid triplet: arm64-windows-release` | `triplets\` folder missing or not passed as `--overlay-triplets` |

---

## 8. CI

Both `.github/workflows/build.yml` and `build_installer.yaml` (with "Build fresh" selected) run
`vcpkg install` in this folder with `--overlay-ports=ports --overlay-triplets=triplets`. They do not
run `01_vcpkg_libheif.ps1`, so **everything the build needs must be committed here**. Their vcpkg
cache key hashes the manifests and `ports\libheif\**`, so an upgrade rebuilds automatically. After
copying the DLLs they run the same `dumpbin` import check as the script.
