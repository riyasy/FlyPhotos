// build.rs
//
// Stamps fly_rust_bridge.dll with a VERSIONINFO resource like the native C++ projects get.
// The DLL is versioned on its own: the version is Cargo.toml's `version` field, so bump it
// there only when the DLL changes.

fn main() {
    println!("cargo:rerun-if-changed=build.rs");

    warn_if_paths_not_remapped();

    let field = |key: &str| -> u64 {
        std::env::var(key).unwrap().parse().unwrap_or_else(|_| panic!("{key} is not a number"))
    };

    // FILEVERSION packs the four parts into one u64, 16 bits each, high to low.
    // Cargo versions have three parts, so the fourth (build) is always 0.
    let version = (field("CARGO_PKG_VERSION_MAJOR") << 48)
        | (field("CARGO_PKG_VERSION_MINOR") << 32)
        | (field("CARGO_PKG_VERSION_PATCH") << 16);

    let ver_string = format!("{}.0", env!("CARGO_PKG_VERSION"));

    let mut res = winresource::WindowsResource::new();
    // CompanyName must match the subject name on the code-signing certificate once signing is in place.
    res.set("CompanyName", "RYFTools")
        .set("ProductName", "FlyPhotos")
        .set("LegalCopyright", "Copyright (C) 2026 RYFTools")
        .set("FileDescription", "FlyPhotos RAW Decoder and SVG Renderer")
        .set("InternalName", "fly_rust_bridge")
        .set("OriginalFilename", "fly_rust_bridge.dll")
        .set("FileVersion", &ver_string)
        .set("ProductVersion", &ver_string)
        .set_version_info(winresource::VersionInfo::FILEVERSION, version)
        .set_version_info(winresource::VersionInfo::PRODUCTVERSION, version);

    res.compile().expect("failed to compile the version resource");
}

/// Warns when a release build is about to bake absolute build paths into the DLL.
///
/// Rust records the source path of every panic site, so a release build with no
/// `--remap-path-prefix` embeds the cargo registry path -- including the builder's user
/// name -- roughly 465 times. That leaks a real name to anyone who runs `strings` on the
/// DLL, and antivirus heuristics score binaries carrying a personal build path (issue #238).
///
/// The right fix is Cargo's own `[profile.release] trim-paths`, which needs no environment
/// setup at all. It is still nightly-gated as of 1.98, verified by trying it, so until it
/// stabilises the remapping has to come from CARGO_ENCODED_RUSTFLAGS -- which the release
/// pipeline sets and a bare `cargo build --release` does not. Warn rather than fail: a local
/// build for personal use is perfectly fine, it is only redistribution that matters.
fn warn_if_paths_not_remapped() {
    if std::env::var("PROFILE").as_deref() != Ok("release") {
        return;
    }

    let remapped = ["CARGO_ENCODED_RUSTFLAGS", "RUSTFLAGS"]
        .iter()
        .filter_map(|key| std::env::var(key).ok())
        .any(|flags| flags.contains("--remap-path-prefix"));

    if !remapped {
        println!(
            "cargo:warning=release build without --remap-path-prefix: this DLL will embed \
             your cargo registry path, including your user name. Fine for local use."
        );
        println!(
            "cargo:warning=Before redistributing, build through the repo's Rust build script, \
             or set CARGO_ENCODED_RUSTFLAGS to remap CARGO_HOME and RUSTUP_HOME."
        );
    }
}
