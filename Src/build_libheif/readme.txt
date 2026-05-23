It should be run using 01_patch_vcpkg_and_build.ps1
Otherwise the default vcpkg install will yield heif.dll which links with aom.dll instead of dav1d.dll

how to update the vcpkg baseline
    Run "vcpkg x-update-baseline"
    When there is a major release in libheif