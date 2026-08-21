// build.rs
//
// Stamps fly_rust_bridge.dll with the same VERSIONINFO resource the native C++ projects get.
// The values are parsed out of Src/build/version.h rather than from Cargo.toml, so the whole
// repo keeps one source of truth for the version and publisher strings. Cargo.toml's own
// `version` field is deliberately not used here - it tracks the crate, not the product.

use std::fs;
use std::path::Path;

fn main() {
    let header = Path::new("../build/version.h");
    println!("cargo:rerun-if-changed={}", header.display());
    println!("cargo:rerun-if-changed=build.rs");

    warn_if_paths_not_remapped();

    let src = fs::read_to_string(header)
        .unwrap_or_else(|e| panic!("cannot read {}: {e}", header.display()));

    let field = |key: &str| -> u64 {
        define(&src, key)
            .parse()
            .unwrap_or_else(|_| panic!("{key} is not a number"))
    };

    // FILEVERSION packs the four parts into one u64, 16 bits each, high to low.
    let version = (field("FLY_VER_MAJOR") << 48)
        | (field("FLY_VER_MINOR") << 32)
        | (field("FLY_VER_PATCH") << 16)
        | field("FLY_VER_BUILD");

    let ver_string = unquote(&src, "FLY_VER_STRING");

    let mut res = winresource::WindowsResource::new();
    res.set("CompanyName", &unquote(&src, "FLY_COMPANY"))
        .set("ProductName", &unquote(&src, "FLY_PRODUCT"))
        .set("LegalCopyright", &unquote(&src, "FLY_COPYRIGHT"))
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

/// Returns the token following `#define <key>` in version.h.
///
/// The trailing whitespace check stops a lookup for a short key from matching a longer one
/// that starts with the same text (FLY_VER_MAJOR vs a hypothetical FLY_VER_MAJOR_EXTRA).
fn define(src: &str, key: &str) -> String {
    src.lines()
        .find_map(|line| {
            let rest = line.trim_start().strip_prefix("#define")?.trim_start();
            let rest = rest.strip_prefix(key)?;
            if !rest.starts_with(char::is_whitespace) {
                return None;
            }
            Some(rest.trim().to_string())
        })
        .unwrap_or_else(|| panic!("{key} not found in version.h"))
}

/// Same as [`define`], with the surrounding double quotes stripped.
fn unquote(src: &str, key: &str) -> String {
    define(src, key).trim_matches('"').to_string()
}
