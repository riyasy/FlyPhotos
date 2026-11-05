vcpkg manifest for DirectXTex, linked STATICALLY into FlyNativeLibDds.dll.
Installed by Src\02_vcpkg_directxtex.ps1 (also run by 00_build_all.ps1), and by both CI workflows.

Kept apart from build_libheif on purpose:
  - build_libheif builds DLLs (dynamic triplets); DirectXTex must be static with the static CRT
    (/MT, matching FlyNativeLibDds), so its triplets are all-static. One manifest would need
    per-port linkage overrides in every triplet.
  - Its baseline can move independently of libheif's.
  - Static libs produce no DLLs, so nothing from here is copied to FlyPhotos\External.

Triplets (triplets\ is passed as --overlay-triplets):
  x64-windows-static-release, arm64-windows-static-release   release only (default)
  x64-windows-static, arm64-windows-static                   built-in, with debug libs (-IncludeDebug)

DirectXTex is requested with default-features off: the only default, "dx11", adds D3D11 helpers
FlyNativeLibDds never calls. Its one dependency is directxmath (header only).

How to update DirectXTex
    Run "vcpkg x-update-baseline" in this folder (updates vcpkg-configuration.json), rerun
    02_vcpkg_directxtex.ps1, rebuild FlyNativeLibDds for x64 and ARM64, and bump
    FlyNativeLibDds\version.h.
