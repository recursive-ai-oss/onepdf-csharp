"""Generates the CCITT test fixtures: bilevel images encoded by libtiff (through Pillow) as Group 3 and Group 4.

Run with Pillow installed:  python3 make_fixtures.py
For every image and mode it writes <name>_<w>x<h>.<mode>.bin (the raw strip as libtiff produced it) and
<name>_<w>x<h>.truth (the source picture: rows packed MSB first, 1 = black).
The TIFFs are written WhiteIsZero (tag 262 = 0), which is what makes libtiff emit the T.4 colours as the standard defines them
(the first run of every row is white).
Modes: g4, g3-1d (EOL before every row), g3-2d (mixed 1-D/2-D, libtiff Group 3 2-D), g3-1d-fill (EOLs padded to a byte boundary).
The images are made here from a seeded generator, so the files are ours and carry no third-party terms.
"""
import os
import random
import sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MODES = {
    "g4": ("group4", {}),
    "g3-1d": ("group3", {}),
    "g3-2d": ("group3", {292: 1}),
    "g3-1d-fill": ("group3", {292: 4}),
}


def blank(w, h, black=False):
    return Image.new("1", (w, h), 0 if black else 255)


def rects(w, h, seed):
    r = random.Random(seed)
    im = blank(w, h)
    for _ in range(max(3, w * h // 400)):
        x0, y0 = r.randrange(w), r.randrange(h)
        x1, y1 = min(w, x0 + r.randrange(1, max(2, w // 3))), min(h, y0 + r.randrange(1, max(2, h // 3)))
        for y in range(y0, y1):
            for x in range(x0, x1):
                im.putpixel((x, y), 0)
    return im


def noise(w, h, seed):
    r = random.Random(seed)
    im = blank(w, h)
    for y in range(h):
        for x in range(w):
            if r.random() < 0.5:
                im.putpixel((x, y), 0)
    return im


def stripes(w, h):
    im = blank(w, h)
    for y in range(h):
        for x in range(w):
            if (x // 7 + y // 3) % 3 == 0:
                im.putpixel((x, y), 0)
    return im


def longruns(w, h):
    # Runs well past 64 and (for wide images) past 1792, so make-up and extended make-up codes are used.
    im = blank(w, h)
    for y in range(h):
        for x in range(w):
            if (y % 4 == 1 and x > 100) or (y % 4 == 2 and 10 < x < w - 30) or (y % 4 == 3 and x % 300 < 150):
                im.putpixel((x, y), 0)
    return im


IMAGES = {
    "rects": rects(200, 60, 1),
    "noise": noise(37, 30, 2),
    "stripes": stripes(129, 40),
    "white": blank(64, 10),
    "black": blank(64, 10, True),
    "narrow": rects(1, 12, 3),
    "wide": longruns(2600, 12),
    "fax": longruns(1728, 16),
}


def main():
    for name, im in IMAGES.items():
        w, h = im.size
        for mode, (comp, extra) in MODES.items():
            tif = os.path.join(HERE, "tmp.tif")
            im.save(tif, compression=comp, tiffinfo={**extra, 262: 0})
            t = Image.open(tif)
            off, cnt = t.tag_v2[273], t.tag_v2[279]
            assert len(off) == 1, "expected a single strip"
            with open(tif, "rb") as f:
                f.seek(off[0])
                strip = f.read(cnt[0])
            rows = bytearray()
            for y in range(h):
                bits = 0
                n = 0
                for x in range(w):
                    bits = (bits << 1) | (1 if im.getpixel((x, y)) == 0 else 0)
                    n += 1
                    if n == 8:
                        rows.append(bits)
                        bits = 0
                        n = 0
                if n:
                    rows.append(bits << (8 - n))
            base = os.path.join(HERE, f"{name}_{w}x{h}")
            with open(f"{base}.{mode}.bin", "wb") as f:
                f.write(strip)
            with open(f"{base}.truth", "wb") as f:
                f.write(rows)
        os.remove(os.path.join(HERE, "tmp.tif"))


if __name__ == "__main__":
    sys.exit(main())
