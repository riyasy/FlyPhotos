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
