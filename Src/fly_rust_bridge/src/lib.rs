use libc::{c_char, c_int};
use rawler::decoders::{Orientation, RawDecodeParams};
use rawler::imgop::develop::{Intermediate, RawDevelop};
use rawler::rawsource::RawSource;
use rayon::prelude::*;
use std::ffi::CStr;
use std::panic::catch_unwind;
use std::path::Path;
use std::slice;
use std::sync::{Arc, LazyLock};
use resvg::{usvg, tiny_skia};
use usvg::fontdb;

mod svg_scene;

// FFI bridge to C# (via P/Invoke). All exported functions share the same memory
// contract: Rust allocates via Box<[u8]>, caller frees via free_rust_buffer.
// Panics are caught at the boundary and returned as null rather than unwinding into C#.
//
// Pixel format: all functions return RGBA8 matching DirectXPixelFormat.R8G8B8A8UIntNormalized
// on the C# side. The rawler functions return opaque pixels (straight == premultiplied);
// resvg_render_svg returns premultiplied alpha, which CanvasBitmap.CreateFromBytes expects.

// Font database is initialised once, by the first SVG that has text (see parse_svg_data), and reused
// for all subsequent calls. Loading system fonts on Windows can take tens to hundreds of
// milliseconds, making per-call initialisation unacceptable.
static FONT_DB: LazyLock<Arc<fontdb::Database>> = LazyLock::new(|| {
    let mut db = fontdb::Database::new();
    db.load_system_fonts();
    add_font_substitutes(&mut db);
    Arc::new(db)
});

/// Windows' FontSubstitutes (HKLM\...\Windows NT\CurrentVersion\FontSubstitutes) that matter for SVG:
/// Chrome honours them, so "Helvetica" draws in Arial there, while fontdb alone falls through to the
/// serif default. Registers each target face again under the alias name, so every lookup sees it.
fn add_font_substitutes(db: &mut fontdb::Database) {
    let aliases: Vec<fontdb::FaceInfo> = db.faces().filter_map(|f| {
        let alias = match f.families.first()?.0.as_str() {
            "Arial" => "Helvetica",
            "Times New Roman" => "Times",
            _ => return None,
        };
        let mut face = f.clone();
        face.families = vec![(alias.to_string(), fontdb::Language::English_UnitedStates)];
        Some(face)
    }).collect();
    for face in aliases { db.push_face_info(face); }
}

/// Borrows a C string as a &str for the duration of the calling scope.
/// SAFETY: Caller must ensure `ptr` remains valid for 'a. Lifetime is unconstrained
/// by the type system — do not store the return value beyond the current call frame.
fn ptr_to_str<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().ok()
}

#[inline(always)]
fn f32_to_u8(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * 255.0) as u8
}

/// Writes `value` to `ptr` only if `ptr` is non-null.
unsafe fn safe_write_int(ptr: *mut c_int, value: c_int) {
    if !ptr.is_null() {
        *ptr = value;
    }
}

/// Fully decodes a RAW file through the complete rawler pipeline
/// (demosaicing, white balance, tone mapping) and returns a heap-allocated
/// RGBA8 buffer. Caller receives width/height/rotation via out-params.
///
/// Returns null on any failure (bad path, unsupported format, decode error, panic).
/// On success, caller MUST free the buffer with `free_rust_buffer(ptr, width*height*4)`.
///
/// Note: `raw_image.orientation` is hardcoded to Normal in rawler 0.7.x, so
/// orientation is read from `raw_metadata` instead.
#[unsafe(no_mangle)]
pub extern "C" fn rawler_render_raw(
    path_ptr: *const c_char,
    width: *mut c_int,
    height: *mut c_int,
    rotation: *mut c_int,
) -> *mut u8 {
    let result = catch_unwind(|| {
        // Inner closure allows us to use the `?` operator safely.
        let process = || -> Option<*mut u8> {
            let path = ptr_to_str(path_ptr)?;

            let rawsource = RawSource::new(Path::new(path)).ok()?;
            let decoder = rawler::get_decoder(&rawsource).ok()?;
            let params = RawDecodeParams::default();

            // `raw_image.orientation` is hardcoded to `Normal` in rawler 0.7.x,
            // so we read orientation from raw_metadata instead.
            let metadata = decoder.raw_metadata(&rawsource, &params).ok()?;
            let orientation_tag = metadata.exif.orientation.unwrap_or(1);
            let rotation_degrees = match Orientation::from_u16(orientation_tag) {
                Orientation::Rotate90 => 90,
                Orientation::Rotate180 => 180,
                Orientation::Rotate270 => 270,
                _ => 0,
            };

            // false = full demosaicing decode.
            let raw_image = decoder.raw_image(&rawsource, &params, false).ok()?;

            let intermediate = RawDevelop::default().develop_intermediate(&raw_image).ok()?;
            let dim = intermediate.dim();
            let w = dim.w;
            let h = dim.h;

            let mut rgba = vec![0u8; w * h * 4];

            // Parallelised over rayon; ThreeColor/FourColor kept as separate arms
            // to allow divergent handling in future.
            match intermediate {
                Intermediate::ThreeColor(img) => {
                    rgba.par_chunks_exact_mut(4)
                        .zip(img.data.par_iter())
                        .for_each(|(out, px)| {
                            out[0] = f32_to_u8(px[0]); // R
                            out[1] = f32_to_u8(px[1]); // G
                            out[2] = f32_to_u8(px[2]); // B
                            out[3] = 255;              // A
                        });
                }
                Intermediate::FourColor(img) => {
                    rgba.par_chunks_exact_mut(4)
                        .zip(img.data.par_iter())
                        .for_each(|(out, px)| {
                            out[0] = f32_to_u8(px[0]); // R
                            out[1] = f32_to_u8(px[1]); // G
                            out[2] = f32_to_u8(px[2]); // B
                            out[3] = 255;              // A
                        });
                }
                Intermediate::Monochrome(img) => {
                    rgba.par_chunks_exact_mut(4)
                        .zip(img.data.par_iter())
                        .for_each(|(out, v)| {
                            let val = f32_to_u8(*v);
                            out[0] = val;
                            out[1] = val;
                            out[2] = val;
                            out[3] = 255;
                        });
                }
            }

            unsafe {
                safe_write_int(width, w as c_int);
                safe_write_int(height, h as c_int);
                safe_write_int(rotation, rotation_degrees);
            }

            Some(Box::into_raw(rgba.into_boxed_slice()) as *mut u8)
        };

        process().unwrap_or(std::ptr::null_mut())
    });

    result.unwrap_or(std::ptr::null_mut())
}

/// Extracts the embedded JPEG preview from a RAW file without full demosaicing.
/// Returns a heap-allocated RGBA8 buffer of the preview image.
/// Also writes the primary RAW dimensions (pw, ph) for aspect-ratio scaling on the C# side.
///
/// Returns null on any failure. On success, caller MUST free with
/// `free_rust_buffer(ptr, width*height*4)`.
#[unsafe(no_mangle)]
pub extern "C" fn rawler_get_embedded_preview_from_raw(
    path_ptr: *const c_char,
    width: *mut c_int,
    height: *mut c_int,
    rotation: *mut c_int,
    primary_width: *mut c_int,
    primary_height: *mut c_int,
) -> *mut u8 {
    let result = catch_unwind(|| {
        let process = || -> Option<*mut u8> {
            let path = ptr_to_str(path_ptr)?;

            let rawsource = RawSource::new(Path::new(path)).ok()?;
            let decoder = rawler::get_decoder(&rawsource).ok()?;
            let params = RawDecodeParams::default();
            let metadata = decoder.raw_metadata(&rawsource, &params).ok()?;

            let orientation_tag = metadata.exif.orientation.unwrap_or(1);
            let rotation_degrees = match Orientation::from_u16(orientation_tag) {
                Orientation::Rotate90 => 90,
                Orientation::Rotate180 => 180,
                Orientation::Rotate270 => 270,
                _ => 0,
            };

            // true = fast thumbnail extraction mode, skips demosaicing.
            let (pw, ph) = match decoder.raw_image(&rawsource, &params, true) {
                Ok(ri) => (ri.width as c_int, ri.height as c_int),
                Err(_) => (0, 0),
            };

            let dynamic_img = rawler::analyze::extract_thumbnail_pixels(path, &params).ok()?;
            
            // Convert any internal format to an RGBA byte buffer efficiently
            let rgba = dynamic_img.into_rgba8();
            let img_w = rgba.width() as c_int;
            let img_h = rgba.height() as c_int;
            let pixels = rgba.into_raw(); // Consumes it into a flat Vec<u8>

            if pixels.is_empty() {
                return None;
            }

            unsafe {
                safe_write_int(width, img_w);
                safe_write_int(height, img_h);
                safe_write_int(rotation, rotation_degrees);
                safe_write_int(primary_width, pw);
                safe_write_int(primary_height, ph);
            }

            Some(Box::into_raw(pixels.into_boxed_slice()) as *mut u8)
        };

        process().unwrap_or(std::ptr::null_mut())
    });

    result.unwrap_or(std::ptr::null_mut())
}

/// Frees a buffer previously returned by `rawler_render_raw`,
/// `rawler_get_embedded_preview_from_raw`, or `resvg_render_svg`.
/// `size` MUST equal width * height * 4 — the exact byte count used at allocation.
/// Passing the wrong size causes Rust to reconstruct a Box with incorrect length,
/// resulting in heap corruption or a double-free.
#[unsafe(no_mangle)]
pub extern "C" fn free_rust_buffer(ptr: *mut u8, size: usize) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        // slice_from_raw_parts_mut constructs the fat pointer directly without
        // creating an intermediate &mut reference, avoiding strict-provenance UB.
        let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, size));
    }
}

/// Returns a heap-allocated array of null-terminated C strings listing every
/// RAW file extension that rawler can decode (e.g. "ARW\0", "CR2\0", …).
/// The number of entries is written to `*size`.
///
/// All strings are upper-case (as defined by rawler's `SUPPORTED_FILES_EXT`).
///
/// # Memory contract
/// The caller MUST free the returned pointer with `free_formats_buffer(ptr, size)`.
/// Do NOT call `free_rust_buffer` on this pointer — they use different allocation layouts.
#[unsafe(no_mangle)]
pub extern "C" fn rawler_get_supported_formats(size: *mut c_int) -> *mut *mut c_char {
    use rawler::decoders::supported_extensions;
    use std::ffi::CString;

    let exts = supported_extensions();

    // Allocate each extension as an independent CString on the heap.
    let ptrs: Vec<*mut c_char> = exts
        .iter()
        .map(|s| {
            // unwrap: rawler extension strings are pure ASCII, no interior NULs.
            CString::new(*s).unwrap().into_raw()
        })
        .collect();

    let len = ptrs.len() as c_int;

    // Box<[T]> carries its own length in the fat pointer and has no capacity/length
    // mismatch concern — safer than Vec::forget which relies on shrink_to_fit succeeding.
    let boxed: Box<[*mut c_char]> = ptrs.into_boxed_slice();
    let out_ptr = Box::into_raw(boxed) as *mut *mut c_char;

    unsafe {
        safe_write_int(size, len);
    }

    out_ptr
}

/// Frees a pointer previously returned by `rawler_get_supported_formats`.
/// `size` MUST be the same value that was written to the `size` out-param
/// of `rawler_get_supported_formats`.
#[unsafe(no_mangle)]
pub extern "C" fn free_formats_buffer(ptr: *mut *mut c_char, size: c_int) {
    if ptr.is_null() || size <= 0 {
        return;
    }
    unsafe {
        // Reconstruct the boxed slice so Rust drops it correctly.
        let slice = slice::from_raw_parts_mut(ptr, size as usize);
        for p in slice.iter() {
            if !p.is_null() {
                // Reclaim each CString so its memory is freed.
                let _ = std::ffi::CString::from_raw(*p);
            }
        }
        // Drop the outer array using the same Box<[T]> that allocated it.
        let _ = Box::from_raw(slice as *mut [*mut c_char]);
    }
}

/// Renders an SVG file into a heap-allocated premultiplied RGBA8 buffer, scaled so
/// that the longer axis equals `max_dimension` (aspect ratio is preserved).
/// Caller receives the final pixel dimensions via `width`/`height` out-params.
///
/// The buffer is tiny_skia's premultiplied output, returned unchanged. It matches
/// DirectXPixelFormat.R8G8B8A8UIntNormalized with CanvasBitmap.CreateFromBytes'
/// default (premultiplied) alpha mode on the C# side.
///
/// Returns null on any failure (bad path, parse error, zero dimensions, panic).
/// On success, caller MUST free the buffer with `free_rust_buffer(ptr, width*height*4)`.
#[unsafe(no_mangle)]
pub extern "C" fn resvg_render_svg(
    path_ptr: *const c_char,
    max_dimension: c_int,
    width: *mut c_int,
    height: *mut c_int,
) -> *mut u8 {
    let result = catch_unwind(|| {
        let tree = parse_svg(ptr_to_str(path_ptr)?)?;
        let (rw, rh, pixels) = render_tree(&tree, max_dimension, 0, 0)?;
        unsafe {
            safe_write_int(width, rw as c_int);
            safe_write_int(height, rh as c_int);
        }
        // Same Box<[u8]> contract as every other function here, so free_rust_buffer works.
        Some(Box::into_raw(pixels.into_boxed_slice()) as *mut u8)
    });
    result.ok().flatten().unwrap_or(std::ptr::null_mut())
}

/// Renders `tree` with resvg, the longer axis fitted to `max_dimension` (aspect preserved). Returns the
/// pixel size and tiny_skia's premultiplied RGBA, kept as-is: CanvasBitmap.CreateFromBytes assumes
/// premultiplied alpha (unpremultiplying made anti-aliased edges too bright). None for a zero-area result.
/// The pixels start `prefix` bytes into the buffer (zeroed, for the caller's header), and `reserve` more
/// bytes of capacity are left for the caller to append, so no full-size copy is ever made.
fn render_tree(tree: &usvg::Tree, max_dimension: c_int, prefix: usize, reserve: usize) -> Option<(u32, u32, Vec<u8>)> {
    let (sw, sh) = (tree.size().width(), tree.size().height());
    let m = max_dimension as f32;
    let (w, h) = if sw >= sh { (m, m * (sh / sw)) } else { (m * (sw / sh), m) };
    let (rw, rh) = (w.round() as u32, h.round() as u32);
    if rw == 0 || rh == 0 { return None; }
    let n = (rw as usize).checked_mul(rh as usize)?.checked_mul(4)?;
    let mut buf = Vec::with_capacity(prefix + n + reserve);
    buf.resize(prefix + n, 0);
    let mut pixmap = tiny_skia::PixmapMut::from_bytes(&mut buf[prefix..], rw, rh)?;
    resvg::render(tree, tiny_skia::Transform::from_scale(rw as f32 / sw, rh as f32 / sh), &mut pixmap);
    Some((rw, rh, buf))
}

/// Parses an SVG with the shared font database (loaded once on first call via LazyLock —
/// avoids a multi-hundred-ms hit per file).
fn parse_svg(path: &str) -> Option<usvg::Tree> {
    parse_svg_data(&std::fs::read(path).ok()?)
}

/// Parses SVG bytes the way both the GPU path and the resvg fallback need them: the Chrome-matching
/// textual fixups (`preprocess`), the shared font database (loaded on first use) with its Windows substitutes, and browser
/// defaults for text with no font set (Times New Roman, 16 px). None if usvg can't parse the file.
fn parse_svg_data(svg_data: &[u8]) -> Option<usvg::Tree> {
    let fixed = preprocess(svg_data);
    let svg_data = fixed.as_deref().map(str::as_bytes).unwrap_or(svg_data);
    let mut options = usvg::Options::default();
    // The system fonts are loaded only when a file has text: usvg starts from an empty database, and the
    // first font lookup swaps in the shared one. A file without text never waits for the font load.
    static NO_FONTS: LazyLock<Arc<fontdb::Database>> = LazyLock::new(|| Arc::new(fontdb::Database::new()));
    options.fontdb = NO_FONTS.clone();
    let (select, fallback) = (usvg::FontResolver::default_font_selector(), usvg::FontResolver::default_fallback_selector());
    options.font_resolver = usvg::FontResolver {
        select_font: Box::new(move |font, db| {
            if db.is_empty() { *db = FONT_DB.clone(); }
            select(font, db)
        }),
        select_fallback: Box::new(move |c, used, db| {
            if db.is_empty() { *db = FONT_DB.clone(); }
            fallback(c, used, db)
        }),
    };
    // Browsers render text with no font-family in Times New Roman; match them.
    options.font_family = "Times New Roman".to_string();
    options.font_size = 16.0;
    usvg::Tree::from_data(svg_data, &options).ok()
}

/// Parses an SVG and serialises it as a draw-command stream for GPU replay (see svg_scene.rs).
/// Returns a buffer whose first byte is a status: 0 = OK, followed by the stream (format version first);
/// 1 = the file uses something the GPU path can't draw yet: followed by width (i32 LE), height (i32 LE),
/// width*height*4 bytes of resvg's premultiplied RGBA fitted to `max_dimension` (from the same parse, so
/// the caller needn't read the file again), then a UTF-8 reason to the end.
/// Returns null if the file can't be read or parsed. The length is written to `len`.
/// On non-null return, caller MUST free with `free_rust_buffer(ptr, len)`.
#[unsafe(no_mangle)]
pub extern "C" fn svg_build_scene(path_ptr: *const c_char, max_dimension: c_int, len: *mut c_int) -> *mut u8 {
    let result = catch_unwind(|| {
        let tree = parse_svg(ptr_to_str(path_ptr)?)?;
        let out = match svg_scene::write_tree(&tree) {
            Ok(s) => s, // already starts with the OK status byte
            Err(reason) => {
                // Rendered straight into the reply, after its 9-byte header.
                let (w, h, mut s) = render_tree(&tree, max_dimension, 9, reason.len())?;
                s[0] = 1;
                s[1..5].copy_from_slice(&(w as i32).to_le_bytes());
                s[5..9].copy_from_slice(&(h as i32).to_le_bytes());
                s.extend_from_slice(reason.as_bytes());
                s
            }
        };
        // The length goes over as an i32 and comes back to free_rust_buffer: one that doesn't fit would
        // free with the wrong size (heap corruption), so such a reply is dropped here instead.
        if out.len() > c_int::MAX as usize { return None; }
        unsafe { safe_write_int(len, out.len() as c_int); }
        Some(Box::into_raw(out.into_boxed_slice()) as *mut u8)
    });
    result.ok().flatten().unwrap_or(std::ptr::null_mut())
}

/// Textual fixes applied before usvg parses the file, so usvg matches Chrome. Returns None when nothing
/// needs changing (or the file isn't UTF-8 text — svgz is gzip and is left alone).
fn preprocess(data: &[u8]) -> Option<String> {
    let text = std::str::from_utf8(data).ok()?;
    let ids_fixed = fix_spaced_ids(text);
    let current = ids_fixed.as_deref().unwrap_or(text);
    strip_non_text_xml_space(current).or(ids_fixed)
}

/// Chrome (Blink) honours `xml:space` only on text content elements (`<text>`, `<tspan>`, `<textPath>`,
/// where it becomes CSS `white-space` and carries into their children); on `<svg>`, `<g>` and everything
/// else it is ignored. usvg inherits it from any ancestor, so a file with `xml:space="preserve"` on the root
/// (Illustrator, Batik) turns the source indentation inside every `<text>` into real spaces and scatters
/// the text (`batik_mathMetal`). Removes the attribute from every element that isn't a text element.
/// Returns None when nothing was removed. The scan only starts when "xml:space" occurs in the file.
fn strip_non_text_xml_space(text: &str) -> Option<String> {
    const ATTR: &str = "xml:space";
    if !text.contains(ATTR) { return None; }
    let mut out = String::with_capacity(text.len());
    let mut copied = 0;
    let mut search = 0;
    while let Some(rel) = text[search..].find(ATTR) {
        let at = search + rel;
        search = at + ATTR.len();
        // The tag the attribute sits in: the nearest '<' before it, with no '>' in between.
        let Some(lt) = text[..at].rfind('<') else { continue };
        if text[lt..at].contains('>') { continue; }
        let name = text[lt + 1..].split(|c: char| c.is_whitespace() || c == '>' || c == '/').next().unwrap_or("");
        let local = name.rsplit(':').next().unwrap_or(name);
        if matches!(local, "text" | "tspan" | "textPath") { continue; }
        // The attribute's extent: xml:space = "…" or '…'.
        let after_name = &text[search..];
        let Some(value) = after_name.trim_start().strip_prefix('=').map(str::trim_start) else { continue };
        let Some(quote) = value.chars().next().filter(|q| *q == '"' || *q == '\'') else { continue };
        let Some(close) = value[1..].find(quote) else { continue };
        let end = search + (after_name.len() - value.len()) + 1 + close + 1;
        // Drop the whitespace in front of the attribute too, but never past the tag name.
        let start = text[..at].trim_end().len().max(lt + 1 + name.len());
        out.push_str(&text[copied..start]);
        copied = end;
        search = end;
    }
    if copied == 0 { return None; }
    out.push_str(&text[copied..]);
    Some(out)
}

/// Inkscape names its stock patterns with spaces (id="lines 36") and references them as
/// xlink:href="#lines 36". Browsers resolve that; usvg drops the reference, so the pattern
/// paints nothing. Rewrites such ids and every `#id` reference to them with underscores.
/// Returns None when nothing needs fixing.
///
/// Root cause is svgtypes' IRI parser, which stops at the first space (funciri.rs `parse_iri`); this
/// textual workaround should go once that is fixed upstream. Its ceiling: it ignores XML structure (a
/// matching string in text content is rewritten too), only finds ` id=` after a space, and doesn't handle
/// an existing id that already uses the underscore form.
fn fix_spaced_ids(text: &str) -> Option<String> {
    let mut ids = std::collections::HashSet::new();
    for quote in ['"', '\''] {
        let open = format!(" id={quote}");
        let mut rest = text;
        while let Some(i) = rest.find(&open) {
            rest = &rest[i + open.len()..];
            let end = rest.find(quote)?;
            let id = &rest[..end];
            if id.contains(char::is_whitespace) { ids.insert(id); }
            rest = &rest[end..];
        }
    }
    if ids.is_empty() { return None; }

    // Every id or reference that names a spaced id, as the byte range of the name. One scan per
    // pattern, then one copy: rewriting the whole file per id was quadratic (1 s on a world map).
    let mut spans = std::collections::BTreeMap::new();
    for (pre, post) in [
        ("id=\"", "\""), ("id='", "'"), ("\"#", "\""), ("'#", "'"),
        ("url(#", ")"), ("url('#", "')"), ("url(\"#", "\")"), ("url(&quot;#", "&quot;)"),
    ] {
        for (i, _) in text.match_indices(pre) {
            let start = i + pre.len();
            if let Some(len) = text[start..].find(post) {
                if ids.contains(&text[start..start + len]) { spans.insert(start, start + len); }
            }
        }
    }
    let mut out = String::with_capacity(text.len());
    let mut copied = 0;
    for (start, end) in spans {
        if start < copied { continue; }
        out.push_str(&text[copied..start]);
        out.push_str(&text[start..end].replace(char::is_whitespace, "_"));
        copied = end;
    }
    out.push_str(&text[copied..]);
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Inkscape's stock patterns have ids with spaces ("lines 36") that other patterns href.
    const SPACED_ID: &str = r##"<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="20" height="20">
        <defs>
          <pattern id="lines 36" patternUnits="userSpaceOnUse" width="4" height="4"><rect width="2" height="4"/></pattern>
          <pattern id="p" xlink:href="#lines 36"/>
        </defs>
        <rect width="20" height="20" fill="url(#p)"/>
    </svg>"##;

    /// Whether any top-level path in `tree` is filled with a pattern (i.e. the pattern reference resolved).
    fn first_path_has_pattern(tree: &usvg::Tree) -> bool {
        tree.root().children().iter().any(|n| matches!(n, usvg::Node::Path(p)
            if matches!(p.fill().map(|f| f.paint()), Some(usvg::Paint::Pattern(_)))))
    }

    /// "Helvetica" and "Times" resolve to the same faces as Arial and Times New Roman (Windows'
    /// FontSubstitutes); an unrelated missing family still resolves to nothing.
    #[test]
    fn helvetica_is_substituted_like_windows() {
        let query = |name| FONT_DB.query(&fontdb::Query { families: &[fontdb::Family::Name(name)], ..Default::default() })
            .and_then(|id| FONT_DB.face(id).map(|f| f.post_script_name.clone()));
        assert_eq!(query("Helvetica"), query("Arial"));
        assert_eq!(query("Times"), query("Times New Roman"));
        assert!(query("Helvetica Neue").is_none());
    }

    /// xml:space is removed from <svg> and <g> (either quote style) but kept on <text>, <tspan> and a
    /// prefixed <svg:textPath>; a file with only text-level xml:space is left alone.
    #[test]
    fn xml_space_is_kept_only_on_text_elements() {
        let src = r#"<svg xmlns="http://www.w3.org/2000/svg" xml:space="preserve"><g xml:space='preserve' id="g"><text xml:space="preserve">a<tspan xml:space="default">b</tspan></text><svg:textPath xml:space="preserve"/></g></svg>"#;
        let out = strip_non_text_xml_space(src).unwrap();
        assert_eq!(out, r#"<svg xmlns="http://www.w3.org/2000/svg"><g id="g"><text xml:space="preserve">a<tspan xml:space="default">b</tspan></text><svg:textPath xml:space="preserve"/></g></svg>"#);
        assert!(strip_non_text_xml_space(r#"<svg><text xml:space="preserve">a</text></svg>"#).is_none());
    }

    /// End to end through parse_svg_data: a root xml:space="preserve" no longer turns the text's
    /// indentation into leading spaces (batik_mathMetal).
    #[test]
    fn root_xml_space_does_not_preserve_text_indentation() {
        // Chrome ignores xml:space on <svg>: the padded text starts at x, like unpadded text.
        let first_glyph_x = |svg: &str| {
            let tree = parse_svg_data(svg.as_bytes()).unwrap();
            tree.root().abs_bounding_box().x()
        };
        let padded = r#"<svg xmlns="http://www.w3.org/2000/svg" width="200" height="50" xml:space="preserve"><text x="10" y="30" font-family="Arial">
            Ab</text></svg>"#;
        let plain = r#"<svg xmlns="http://www.w3.org/2000/svg" width="200" height="50"><text x="10" y="30" font-family="Arial">Ab</text></svg>"#;
        assert!((first_glyph_x(padded) - first_glyph_x(plain)).abs() < 0.01);
    }

    /// A pattern that hrefs an id with spaces still paints (inkscape_about10's hair textures).
    #[test]
    fn pattern_href_to_id_with_spaces_resolves() {
        let tree = parse_svg_data(SPACED_ID.as_bytes()).unwrap();
        assert!(first_path_has_pattern(&tree), "pattern fill was dropped");
    }

    /// A file the GPU path can't draw comes back as status 1, the raster size, the pixels (rendered in
    /// place after the header) and the reason, in one buffer of exactly that length.
    #[test]
    fn fallback_reply_is_header_pixels_then_reason() {
        let path = std::env::temp_dir().join("fly_rust_bridge_fallback_test.svg");
        std::fs::write(&path, r#"<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><filter id="f"><feTile/></filter><rect width="20" height="10" fill="red"/><rect x="15" y="5" width="5" height="5" filter="url(#f)"/></svg>"#).unwrap();
        let c_path = std::ffi::CString::new(path.to_str().unwrap()).unwrap();
        let mut len = 0;
        let ptr = svg_build_scene(c_path.as_ptr(), 40, &mut len);
        assert!(!ptr.is_null());
        let b = unsafe { slice::from_raw_parts(ptr, len as usize) }.to_vec();
        free_rust_buffer(ptr, len as usize);
        let _ = std::fs::remove_file(&path);

        assert_eq!(b[0], 1);
        let (w, h) = (i32::from_le_bytes(b[1..5].try_into().unwrap()), i32::from_le_bytes(b[5..9].try_into().unwrap()));
        assert_eq!((w, h), (40, 20));
        let reason = std::str::from_utf8(&b[9 + (w * h * 4) as usize..]).unwrap();
        assert!(reason.contains("feTile"), "{reason}");
        assert_eq!(&b[9..13], &[255, 0, 0, 255], "the red rect's first pixel, premultiplied RGBA");
    }
}
