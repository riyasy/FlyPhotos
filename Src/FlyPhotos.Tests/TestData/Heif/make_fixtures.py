"""Regenerates the HEIC/AVIF fixtures for HeifDecoderTests.cs.

    python -m pip install pillow pillow-heif
    python make_fixtures.py

Generated with Pillow 13.0 (AVIF via libavif/aom) and pillow-heif 1.8.1 (libheif 1.23.6, x265).
Flat colour blocks on 16-px boundaries keep lossy-coding error small; the tests allow a tolerance.
"""
import os

import pillow_heif
from PIL import Image

OUT = os.path.dirname(os.path.abspath(__file__))
RED, GREEN, BLUE, WHITE = (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)


def path(name):
    return os.path.join(OUT, name)


def quadrants(size=64):
    img = Image.new("RGB", (size, size))
    h = size // 2
    for box, colour in (((0, 0, h, h), RED), ((h, 0, size, h), GREEN),
                        ((0, h, h, size), BLUE), ((h, h, size, size), WHITE)):
        img.paste(colour, box)
    return img


def half_alpha():
    """Left half opaque red, right half blue at alpha 128."""
    img = Image.new("RGBA", (32, 32), RED + (255,))
    img.paste((0, 0, 255, 128), (16, 0, 32, 32))
    return img


def heic(img, name, **kw):
    pillow_heif.from_pillow(img).save(path(name), quality=90, chroma=444, **kw)


heic(quadrants(), "quadrants.heic")
quadrants().save(path("quadrants.avif"), quality=90, subsampling="4:4:4")

heic(half_alpha(), "alpha.heic")
half_alpha().save(path("alpha.avif"), quality=90, subsampling="4:4:4")

# Odd size with 4:2:0 chroma: the chroma planes are rounded up, the output must not be.
pillow_heif.from_pillow(Image.new("RGB", (33, 17), GREEN)).save(path("odd_size_420.heic"), quality=90)

# Monochrome (4:0:0): libheif must still hand back interleaved RGBA.
pillow_heif.from_pillow(Image.new("L", (16, 16), 128)).save(path("monochrome.heic"), quality=90)

# 10-bit: 16-bit input is stored as 10 bits per channel; the decoder must give 8-bit RGBA.
ten = pillow_heif.from_bytes(mode="RGB;16", size=(16, 16), data=bytes([0xFF, 0xFF, 0, 0, 0, 0]) * 256)
ten.save(path("10bit_red.heic"), quality=90, chroma=444)

def heic_with_thumbnail(name, width, height, colour, thumb_size):
    """pillow-heif 1.x can't write thumbnails, so call its bundled libheif (which has x265) directly."""
    import ctypes
    import glob

    site = os.path.dirname(os.path.dirname(pillow_heif.__file__))
    lib = ctypes.CDLL(glob.glob(os.path.join(site, "libheif-*.dll"))[0])

    class Err(ctypes.Structure):
        _fields_ = [("code", ctypes.c_int), ("subcode", ctypes.c_int), ("message", ctypes.c_char_p)]

    for fn in ("heif_context_get_encoder_for_format", "heif_encoder_set_lossy_quality", "heif_image_create",
               "heif_image_add_plane", "heif_context_encode_image", "heif_context_encode_thumbnail",
               "heif_context_write_to_file"):
        getattr(lib, fn).restype = Err
    lib.heif_context_alloc.restype = ctypes.c_void_p
    lib.heif_image_get_plane.restype = ctypes.POINTER(ctypes.c_uint8)

    def ok(e):
        if e.code:
            raise RuntimeError(e.message)

    HEVC, RGB, INTERLEAVED_RGB, INTERLEAVED = 1, 1, 10, 10
    ctx = ctypes.c_void_p(lib.heif_context_alloc())
    enc, img, handle, thumb = (ctypes.c_void_p() for _ in range(4))
    ok(lib.heif_context_get_encoder_for_format(ctx, HEVC, ctypes.byref(enc)))
    ok(lib.heif_encoder_set_lossy_quality(enc, 90))
    ok(lib.heif_image_create(width, height, RGB, INTERLEAVED_RGB, ctypes.byref(img)))
    ok(lib.heif_image_add_plane(img, INTERLEAVED, width, height, 8))
    stride = ctypes.c_int()
    plane = lib.heif_image_get_plane(img, INTERLEAVED, ctypes.byref(stride))
    for y in range(height):
        for x in range(width):
            for c in range(3):
                plane[y * stride.value + x * 3 + c] = colour[c]
    ok(lib.heif_context_encode_image(ctx, img, enc, None, ctypes.byref(handle)))
    ok(lib.heif_context_encode_thumbnail(ctx, img, handle, enc, None, thumb_size, ctypes.byref(thumb)))
    ok(lib.heif_context_write_to_file(ctx, path(name).encode()))


# Embedded thumbnail: 256x128 primary with a 64-px thumbnail (64x32).
heic_with_thumbnail("with_thumbnail.heic", 256, 128, BLUE, 64)

# No thumbnail and longer side > 800: ExtractThumbnail scales the primary image to 800 px.
heic(Image.new("RGB", (1000, 500), RED), "large_no_thumbnail.heic")

# Animated AVIF: red, green, blue frames of 100, 200 and 300 ms.
frames = [Image.new("RGB", (32, 32), c) for c in (RED, GREEN, BLUE)]
frames[0].save(path("animated_rgb.avif"), save_all=True, append_images=frames[1:],
               duration=[100, 200, 300], loop=0, quality=90, subsampling="4:4:4")
# Same frames, played once: libheif reaches the end of the sequence instead of wrapping around.
# Pillow always writes "repeat forever" (mvhd duration all 1s + elst repeat flag), so patch both boxes.
once = bytearray(open(path("animated_rgb.avif"), "rb").read())
mvhd = once.find(b"mvhd")  # version 1: version/flags, creation, modification, timescale, then 8-byte duration
once[mvhd + 28:mvhd + 36] = (600).to_bytes(8, "big")
elst = once.find(b"elst")
once[elst + 5:elst + 8] = b"\0\0\0"  # clear the repeat flag
open(path("animated_rgb_once.avif"), "wb").write(once)

for f in sorted(os.listdir(OUT)):
    if not f.endswith(".py"):
        print(f"{f:28} {os.path.getsize(path(f)):7} bytes")
