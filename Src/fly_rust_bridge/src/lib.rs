use libc::{c_char, c_int};
use rawler::decoders::Orientation;
use rawler::imgop::develop::{Intermediate, RawDevelop};
use rayon::prelude::*;
use std::ffi::CStr;
use std::slice;

fn ptr_to_string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    unsafe { CStr::from_ptr(ptr).to_string_lossy().into_owned() }
}

#[inline(always)]
fn f32_to_u8(v: f32) -> u8 {
    let v = if v < 0.0 {
        0.0
    } else if v > 1.0 {
        1.0
    } else {
        v
    };
    (v * 255.0) as u8
}

/// Extracts the fully decoded high-quality image from a RAW file.
/// This runs the complete image processing pipeline (demosaicing, white balance, etc.)
/// and returns a 32-bit BGRA byte array.
#[unsafe(no_mangle)]
pub extern "C" fn get_hq_image(
    path_ptr: *const c_char,
    width: *mut c_int,
    height: *mut c_int,
    rotation: *mut c_int,
) -> *mut u8 {
    let path = ptr_to_string(path_ptr);

    let Ok(raw_image) = rawler::decode_file(&path) else {
        return std::ptr::null_mut();
    };
    
    // Since `raw_image.orientation` is hardcoded to `Normal` in rawler 0.7.x,
    // we need to get the orientation from the decoder's raw_metadata.
    
    // Test if we can extract orientation metadata via Decoder directly
    let rawsource = rawler::rawsource::RawSource::new(std::path::Path::new(&path)).map_err(|e| e.to_string()).unwrap();
    let decoder = rawler::get_decoder(&rawsource).unwrap();
    let metadata = decoder.raw_metadata(&rawsource, &rawler::decoders::RawDecodeParams::default()).unwrap();
    
    let orientation_tag = metadata.exif.orientation.unwrap_or(1);
    let orientation_enum = rawler::decoders::Orientation::from_u16(orientation_tag);

    let rotation_degrees = match orientation_enum {
        Orientation::Normal => 0,
        Orientation::Rotate90 => 90,
        Orientation::Rotate180 => 180,
        Orientation::Rotate270 => 270,
        Orientation::HorizontalFlip => 0,
        Orientation::VerticalFlip => 0,
        Orientation::Transpose => 0,
        Orientation::Transverse => 0,
        Orientation::Unknown => 0,
    };

    let Ok(intermediate) = RawDevelop::default().develop_intermediate(&raw_image) else {
        return std::ptr::null_mut();
    };

    let dim = intermediate.dim();
    let w = dim.w;
    let h = dim.h;

    let mut bgra = vec![0u8; w * h * 4];

    match intermediate {
        Intermediate::ThreeColor(img) => {
            bgra.par_chunks_exact_mut(4)
                .zip(img.data.par_iter())
                .for_each(|(out, px)| {
                    out[0] = f32_to_u8(px[2]); // B
                    out[1] = f32_to_u8(px[1]); // G
                    out[2] = f32_to_u8(px[0]); // R
                    out[3] = 255;
                });
        }
        Intermediate::FourColor(img) => {
            bgra.par_chunks_exact_mut(4)
                .zip(img.data.par_iter())
                .for_each(|(out, px)| {
                    out[0] = f32_to_u8(px[2]); // B
                    out[1] = f32_to_u8(px[1]); // G
                    out[2] = f32_to_u8(px[0]); // R
                    out[3] = 255;
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
        *width = w as c_int;
        *height = h as c_int;
        *rotation = rotation_degrees;
    }

    Box::into_raw(bgra.into_boxed_slice()) as *mut u8
}

/// Extracts the embedded JPEG preview from a RAW file.
/// This bypasses full demosaicing and directly extracts the embedded JPEG preview.
/// The preview is decoded and returned as a 32-bit BGRA byte array.
#[unsafe(no_mangle)]
pub extern "C" fn get_embedded_preview(
    path_ptr: *const c_char,
    width: *mut c_int,
    height: *mut c_int,
    rotation: *mut c_int,
    primary_width: *mut c_int,
    primary_height: *mut c_int,
) -> *mut u8 {
    let path = ptr_to_string(path_ptr);

    let rawsource = match rawler::rawsource::RawSource::new(std::path::Path::new(&path)) {
        Ok(rs) => rs,
        Err(_) => return std::ptr::null_mut(),
    };
    
    let decoder = match rawler::get_decoder(&rawsource) {
        Ok(d) => d,
        Err(_) => return std::ptr::null_mut(),
    };

    let metadata = match decoder.raw_metadata(&rawsource, &rawler::decoders::RawDecodeParams::default()) {
        Ok(m) => m,
        Err(_) => return std::ptr::null_mut(),
    };
    
    let orientation_tag = metadata.exif.orientation.unwrap_or(1);
    let orientation_enum = rawler::decoders::Orientation::from_u16(orientation_tag);

    let rotation_degrees = match orientation_enum {
        Orientation::Normal => 0,
        Orientation::Rotate90 => 90,
        Orientation::Rotate180 => 180,
        Orientation::Rotate270 => 270,
        Orientation::HorizontalFlip => 0,
        Orientation::VerticalFlip => 0,
        Orientation::Transpose => 0,
        Orientation::Transverse => 0,
        Orientation::Unknown => 0,
    };

    let rawimage_res = decoder.raw_image(&rawsource, &rawler::decoders::RawDecodeParams::default(), true);
    let (pw, ph) = match rawimage_res {
        Ok(ri) => (ri.width as c_int, ri.height as c_int),
        Err(_) => (0, 0),
    };

    let dynamic_img = match rawler::analyze::extract_thumbnail_pixels(&path, &rawler::decoders::RawDecodeParams::default()) {
        Ok(img) => img,
        Err(_) => return std::ptr::null_mut(),
    };

    let rgba = dynamic_img.to_rgba8();
    let img_w = rgba.width() as c_int;
    let img_h = rgba.height() as c_int;
    
    let mut bgra = rgba.into_raw();
    if bgra.is_empty() {
        return std::ptr::null_mut();
    }

    bgra.par_chunks_exact_mut(4).for_each(|chunk| {
        let r = chunk[0];
        let b = chunk[2];
        chunk[0] = b;
        chunk[2] = r;
    });

    unsafe {
        *width = img_w;
        *height = img_h;
        *rotation = rotation_degrees;
        *primary_width = pw;
        *primary_height = ph;
    }

    Box::into_raw(bgra.into_boxed_slice()) as *mut u8
}

/// Frees the unmanaged memory buffer previously allocated by Rust.
/// This MUST be called by the C# consumer to prevent memory leaks.
#[unsafe(no_mangle)]
pub extern "C" fn free_rust_buffer(ptr: *mut u8, size: usize) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        let _ = Box::from_raw(slice::from_raw_parts_mut(ptr, size));
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
