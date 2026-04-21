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

// FFI bridge to C# (via P/Invoke). All exported functions share the same memory
// contract: Rust allocates via Box<[u8]>, caller frees via free_rust_buffer.
// Panics are caught at the boundary and returned as null rather than unwinding into C#.
//
// Pixel format: all functions return straight (unpremultiplied) RGBA8, matching
// DirectXPixelFormat.R8G8B8A8UIntNormalized on the C# side.

// Font database is initialised once on first SVG render and reused for all
// subsequent calls. Loading system fonts on Windows can take tens to hundreds of
// milliseconds, making per-call initialisation unacceptable.
static FONT_DB: LazyLock<Arc<fontdb::Database>> = LazyLock::new(|| {
    let mut db = fontdb::Database::new();
    db.load_system_fonts();
    Arc::new(db)
});

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

/// Renders an SVG file into a heap-allocated straight RGBA8 buffer, scaled so
/// that the longer axis equals `max_dimension` (aspect ratio is preserved).
/// Caller receives the final pixel dimensions via `width`/`height` out-params.
///
/// tiny_skia produces premultiplied RGBA internally; this function unpremultiplies
/// before returning so the output format is consistent with the rawler functions
/// and matches DirectXPixelFormat.R8G8B8A8UIntNormalized on the C# side.
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
        let process = || -> Option<*mut u8> {
            // 1. Decode the path — reuses the same helper as the rawler functions.
            let path = ptr_to_str(path_ptr)?;

            let svg_data = std::fs::read(path).ok()?;

            // 2. Configure usvg with the global font database (initialised once on
            //    first call via LazyLock — avoids a multi-hundred-ms hit per render).
            let mut options = usvg::Options::default();
            options.fontdb = FONT_DB.clone();
            options.font_family = "Arial".to_string();

            // 3. Parse the SVG into a usvg::Tree.
            let tree = usvg::Tree::from_data(&svg_data, &options).ok()?;

            let svg_width = tree.size().width();
            let svg_height = tree.size().height();

            // 4. Aspect-ratio-preserving scale: fit the longer axis to max_dimension.
            let (w, h) = if svg_width >= svg_height {
                (max_dimension as f32, max_dimension as f32 * (svg_height / svg_width))
            } else {
                (max_dimension as f32 * (svg_width / svg_height), max_dimension as f32)
            };

            let (rw, rh) = (w.round() as u32, h.round() as u32);

            // Guard: a zero-area pixmap would produce an empty but non-null buffer
            // that the caller cannot usefully free via free_rust_buffer(ptr, 0).
            if rw == 0 || rh == 0 {
                return None;
            }

            // 5. Allocate the pixel buffer.
            let mut pixmap = tiny_skia::Pixmap::new(rw, rh)?;

            // 6. Render. resvg::render fills pixmap in-place with premultiplied RGBA.
            resvg::render(
                &tree,
                tiny_skia::Transform::from_scale(
                    rw as f32 / svg_width,
                    rh as f32 / svg_height,
                ),
                &mut pixmap.as_mut(),
            );

            // 7. Unpremultiply alpha in parallel (premultiplied → straight RGBA).
            //    Pixels with a == 0 (fully transparent) or a == 255 (fully opaque)
            //    need no correction and are left unchanged.
            let mut pixels = pixmap.take();
            pixels.par_chunks_exact_mut(4).for_each(|chunk| {
                let a = chunk[3];
                if a > 0 && a < 255 {
                    let a_f = a as f32 / 255.0;
                    chunk[0] = (chunk[0] as f32 / a_f).min(255.0) as u8;
                    chunk[1] = (chunk[1] as f32 / a_f).min(255.0) as u8;
                    chunk[2] = (chunk[2] as f32 / a_f).min(255.0) as u8;
                }
            });

            // 8. Write out-params through safe_write_int to guard against null pointers.
            unsafe {
                safe_write_int(width, rw as c_int);
                safe_write_int(height, rh as c_int);
            }

            // 9. Transfer ownership to the caller using the same Box<[u8]> contract
            //    as every other function in this file so free_rust_buffer works correctly.
            Some(Box::into_raw(pixels.into_boxed_slice()) as *mut u8)
        };

        process().unwrap_or(std::ptr::null_mut())
    });

    result.unwrap_or(std::ptr::null_mut())
}