#!/usr/bin/env python3
"""A body's whole colour map (#1849) as a JPEG the colour bake can open.

Godot's Image reads no TIFF, and NASA's colour maps come as TIFFs (the CGI Moon Kit's
lroc_color_poles.tif: 27,360 x 13,680 RGBA, LZW). This keeps the picture's layout (equirectangular,
column 0 at 180 W, row 0 at 90 N, planetocentric latitudes), drops the alpha, scales it down to at most --width pixels across
(Godot's Image holds at most 2^28 pixels: 21,600 x 10,800 is Blue Marble's own size, ~500 m a pixel
on the Moon) and writes an RGB JPEG for

    <godot> --headless --path . -- --globealbedo <pyramid dir> --body moon --colour <out.jpg>

Usage: python tools/body_colour.py IN.tif OUT.jpg [--width 21600] [--quality 95] [--floor 0.55]
       python tools/body_colour.py --flat R,G,B OUT.jpg [--width 2048]
--floor F raises what is darker than F x the median to it: a mosaic's own shadows (Phobos' SRC mosaic,
#1853, its low sun's shadows and unlit corners), which the game's light would draw a second time.
--flat makes one colour over the whole body, for a body with no usable colour map (Deimos: only an
airbrushed relief, its shading drawn in): its relief is the game's light on its heights.
Needs Pillow only.
"""
import argparse

from PIL import Image


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--width", type=int, default=21600)
    ap.add_argument("--quality", type=int, default=95)
    ap.add_argument("--floor", type=float, default=0)
    ap.add_argument("--flat", action="store_true", help="SRC is an R,G,B colour, not a file")
    a = ap.parse_args()
    if a.flat:
        rgb = tuple(int(c) for c in a.src.split(","))
        w = min(a.width, 2048)
        Image.new("RGB", (w, w // 2), rgb).save(a.out, "JPEG", quality=a.quality)
        print(f"wrote {a.out}: {w} x {w // 2} of {rgb}")
        return
    Image.MAX_IMAGE_PIXELS = None  # a whole body is ~400 million pixels: not a decompression bomb
    with Image.open(a.src) as im:
        print(f"{a.src}: {im.size[0]} x {im.size[1]} {im.mode}")
        # USGS mosaics round their pixel counts (Mars' Viking 925 m: 23,059 x 11,530): a pixel or two off 2:1
        if abs(im.size[0] - 2 * im.size[1]) > 2:
            raise SystemExit("not a whole body (2:1)")
        rgb = im.convert("RGB")
    if a.floor > 0:
        grey = rgb.convert("L")
        hist = grey.histogram()
        half, seen, median = sum(hist) / 2, 0, 0
        for v, n in enumerate(hist):
            seen += n
            if seen >= half:
                median = v
                break
        low = int(median * a.floor)
        # a pixel under the floor is lifted to it, its hue kept
        mask = grey.point(lambda v: 255 if v < low else 0)
        lifted = Image.new("RGB", rgb.size, (low, low, low))
        rgb = Image.composite(lifted, rgb, mask)
        print(f"floor: median {median}, under {low} raised to it")
    if rgb.size[0] > a.width or rgb.size[0] != 2 * rgb.size[1]:
        w = min(a.width, rgb.size[0] // 2 * 2)
        rgb = rgb.resize((w, w // 2), Image.Resampling.LANCZOS)
        print(f"scaled to {rgb.size[0]} x {rgb.size[1]}")
    rgb.save(a.out, "JPEG", quality=a.quality, optimize=True)
    print(f"wrote {a.out}")


if __name__ == "__main__":
    main()
