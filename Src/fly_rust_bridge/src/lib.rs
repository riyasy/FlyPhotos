use libc::{c_char, c_int};
use rawler::decoders::{Orientation, RawDecodeParams};
use rawler::imgop::develop::{Intermediate, RawDevelop};
use rawler::rawsource::RawSource;
use rayon::prelude::*;
use std::ffi::CStr;
use std::panic::catch_unwind;
use std::path::Path;
use std::slice;

// FFI bridge to C# (via P/Invoke). All three exported functions share the same
// memory contract: Rust allocates via Box<[u8]>, caller frees via free_rust_buffer.
// Panics are caught at the boundary and returned as null rather than unwinding into C#.

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
/// BGRA8 buffer. Caller receives width/height/rotation via out-params.
///
/// Returns null on any failure (bad path, unsupported format, decode error, panic).
/// On success, caller MUST free the buffer with `free_rust_buffer(ptr, width*height*4)`.
///
/// Note: `raw_image.orientation` is hardcoded to Normal in rawler 0.7.x, so
/// orientation is read from `raw_metadata` instead.
#[unsafe(no_mangle)]
pub extern "C" fn get_hq_image(
    path_ptr: *const c_char,
    width: *mut c_int,
    height: *mut c_int,
    rotation: *mut c_int,
) -> *mut u8 {
    let result = catch_unwind(|| {
        // Inner closure allows us to use the `?` operator safely
        let process = || -> Option<*mut u8> {
            let path = ptr_to_str(path_ptr)?;

            let rawsource = RawSource::new(Path::new(path)).ok()?;
            let decoder = rawler::get_decoder(&rawsource).ok()?;
            let params = RawDecodeParams::default();
            
		    // Since `raw_image.orientation` is hardcoded to `Normal` in rawler 0.7.x,
		    // we need to get the orientation from the decoder's raw_metadata.
            let metadata = decoder.raw_metadata(&rawsource, &params).ok()?;
            let orientation_tag = metadata.exif.orientation.unwrap_or(1);
            let rotation_degrees = match Orientation::from_u16(orientation_tag) {
                Orientation::Rotate90 => 90,
                Orientation::Rotate180 => 180,
                Orientation::Rotate270 => 270,
                _ => 0,
            };

            // false = Full demosaicing decode
            let raw_image = decoder.raw_image(&rawsource, &params, false).ok()?;
            
            let intermediate = RawDevelop::default().develop_intermediate(&raw_image).ok()?;
            let dim = intermediate.dim();
            let w = dim.w;
            let h = dim.h;

            let mut bgra = vec![0u8; w * h * 4];

            // Parallelised over rayon; ThreeColor/FourColor kept as separate arms
            // to allow divergent handling in future (e.g. CMYK FourColor).
            match intermediate {
                Intermediate::ThreeColor(img) => {
                    bgra.par_chunks_exact_mut(4)
                        .zip(img.data.par_iter())
                        .for_each(|(out, px)| {
                            out[0] = f32_to_u8(px[2]); // B
                            out[1] = f32_to_u8(px[1]); // G
                            out[2] = f32_to_u8(px[0]); // R
                            out[3] = 255;              // A
                        });
                }
                Intermediate::FourColor(img) => {
                    bgra.par_chunks_exact_mut(4)
                        .zip(img.data.par_iter())
                        .for_each(|(out, px)| {
                            out[0] = f32_to_u8(px[2]); // B
                            out[1] = f32_to_u8(px[1]); // G
                            out[2] = f32_to_u8(px[0]); // R
                            out[3] = 255;              // A
                        });
                }
                Intermediate::Monochrome(img) => {
                    bgra.par_chunks_exact_mut(4)
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

            Some(Box::into_raw(bgra.into_boxed_slice()) as *mut u8)
        };

        process().unwrap_or(std::ptr::null_mut())
    });

    result.unwrap_or(std::ptr::null_mut())
}

/// Extracts the embedded JPEG preview from a RAW file without full demosaicing.
/// Returns a heap-allocated BGRA8 buffer of the preview image.
/// Also writes the primary RAW dimensions (pw, ph) for aspect-ratio scaling on the C# side.
///
/// Returns null on any failure. On success, caller MUST free with
/// `free_rust_buffer(ptr, width*height*4)`.
#[unsafe(no_mangle)]
pub extern "C" fn get_embedded_preview(
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

            // true = fast thumbnail extraction mode, skips demosaicing
            let (pw, ph) = match decoder.raw_image(&rawsource, &params, true) {
                Ok(ri) => (ri.width as c_int, ri.height as c_int),
                Err(_) => (0, 0),
            };

            let dynamic_img = rawler::analyze::extract_thumbnail_pixels(path, &params).ok()?;
            
            // Convert any internal format to an RGBA byte buffer efficiently
            let rgba = dynamic_img.into_rgba8();
            let img_w = rgba.width() as c_int;
            let img_h = rgba.height() as c_int;
            let mut bgra = rgba.into_raw(); // Consumes it into a flat Vec<u8>

            if bgra.is_empty() {
                return None;
            }

            // Fast single-thread channel swap (RGBA -> BGRA)
            bgra.chunks_exact_mut(4).for_each(|chunk| {
                chunk.swap(0, 2); 
            });

            unsafe {
                safe_write_int(width, img_w);
                safe_write_int(height, img_h);
                safe_write_int(rotation, rotation_degrees);
                safe_write_int(primary_width, pw);
                safe_write_int(primary_height, ph);
            }

            Some(Box::into_raw(bgra.into_boxed_slice()) as *mut u8)
        };

        process().unwrap_or(std::ptr::null_mut())
    });

    result.unwrap_or(std::ptr::null_mut())
}

/// Frees a buffer previously returned by `get_hq_image` or `get_embedded_preview`.
/// `size` MUST equal width * height * 4 — the exact byte count used at allocation.
/// Passing the wrong size causes Rust to reconstruct a Box with incorrect length,
/// resulting in heap corruption or a double-free.
#[unsafe(no_mangle)]
pub extern "C" fn free_rust_buffer(ptr: *mut u8, size: usize) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        let _ = Box::from_raw(slice::from_raw_parts_mut(ptr, size));
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
pub extern "C" fn get_supported_formats(size: *mut c_int) -> *mut *mut c_char {
    use rawler::decoders::supported_extensions;
    use std::ffi::CString;

    let exts = supported_extensions();

    // Allocate each extension as an independent CString on the heap.
    let ptrs: Vec<*mut c_char> = exts
        .iter()
        .map(|s| {
            // unwrap: rawler extension strings are pure ASCII, no interior NULs
            CString::new(*s).unwrap().into_raw()
        })
        .collect();

    let len = ptrs.len() as c_int;

    // Move the Vec's backing storage onto the heap as a boxed slice.
    let boxed: Box<[*mut c_char]> = ptrs.into_boxed_slice();
    let out_ptr = Box::into_raw(boxed) as *mut *mut c_char;

    unsafe {
        safe_write_int(size, len);
    }

    out_ptr
}

/// Frees a pointer previously returned by `get_supported_formats`.
/// `size` MUST be the same value that was written to the `size` out-param
/// of `get_supported_formats`.
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
        // Drop the outer array.
        let _ = Box::from_raw(slice as *mut [*mut c_char]);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CString;

    #[test]
    fn test_get_hq_image() {
        // Change this path to a real raw file path on your system to debug
        let test_file = CString::new("C:\\Users\\Riyas\\Desktop\\20250114_083349 (ILCE-6400).ARW").unwrap();
        let mut width = 0;
        let mut height = 0;
        let mut rotation = 0;

        let ptr = get_hq_image(
            test_file.as_ptr(),
            &mut width,
            &mut height,
            &mut rotation,
        );

        assert!(!ptr.is_null(), "Pointer returned was null — failed to load or decode image");
        println!("Got image: {}x{}, rotation: {}", width, height, rotation);

        // Don't forget to free the memory!
        let size = (width * height * 4) as usize;
        free_rust_buffer(ptr, size);
    }

    #[test]
    fn test_get_embedded_preview() {
        let test_file = CString::new("C:\\Users\\Riyas\\Desktop\\20250114_083349 (ILCE-6400).ARW").unwrap();
        let mut width = 0;
        let mut height = 0;
        let mut rotation = 0;
        let mut primary_width = 0;
        let mut primary_height = 0;

        let ptr = get_embedded_preview(
            test_file.as_ptr(),
            &mut width,
            &mut height,
            &mut rotation,
            &mut primary_width,
            &mut primary_height,
        );

        assert!(!ptr.is_null(), "Pointer returned was null — failed to load or decode embedded preview");
        println!("Got preview: {}x{}, primary: {}x{}, rotation: {}", width, height, primary_width, primary_height, rotation);

        // Don't forget to free the memory!
        let size = (width * height * 4) as usize;
        free_rust_buffer(ptr, size);
    }
}