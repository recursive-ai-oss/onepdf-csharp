"""Writes the small JPEG files used by the image-decoder tests (testdata/generated/images).

Each image is a 16x16 gradient so decoded colours can be compared with tolerance. Run with Pillow installed:
    python3 tools/make_test_jpegs.py testdata/generated/images
CMYK files carry an Adobe APP14 marker and store ink inverted, as Photoshop-style writers do.
"""
import sys
from PIL import Image

out = sys.argv[1] if len(sys.argv) > 1 else "."
W = H = 16


def grad(mode):
    img = Image.new(mode, (W, H))
    px = img.load()
    for y in range(H):
        for x in range(W):
            r, g, b = x * 16, y * 16, 128
            if mode == "L":
                px[x, y] = (x * 16 + y * 8) & 255
            elif mode == "RGB":
                px[x, y] = (r, g, b)
            else:  # CMYK: cyan by x, magenta by y, yellow 0, key 64
                px[x, y] = (x * 16, y * 16, 0, 64)
    return img


grad("L").save(f"{out}/gray16.jpg", quality=95, subsampling=0)
grad("RGB").save(f"{out}/rgb16.jpg", quality=95, subsampling=0)
grad("CMYK").save(f"{out}/cmyk16-adobe.jpg", quality=95, subsampling=0)
