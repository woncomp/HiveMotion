"""Hive Switcher icon renderer - pointy-top 2-2 rhombus, gold-edged cells.

Reproduces the final icon set:
  - Layout: pointy-top regular hexagons in a 2-2 rhombus block (~1.24:1):
        TL  TR      top row
      BL  BR        bottom row, offset left by half a cell
    BL = SW(TL), BR = SE(TL), TR = E(TL) = NE(BR)
  - Coloring:
      * active cell (TL) = solid honey-gold gradient + top sheen + soft glow
      * 3 inactive cells = dark glass fill WITH gold border
  - Gap rules: tray sizes (16/20/24/32) keep exactly 1px visible gap;
    >= 48px use gap = border width (border w = 0.13r).
  - Border ring is drawn INSIDE the cell boundary:
    ring = hex(r) minus hex(r - 2w/sqrt(3))  (w measured perpendicular to edges)

Usage:
  pip install pillow numpy
  python hive_icon.py --out out            # writes hive-icon-1024.png + hive-icon.ico
  python hive_icon.py --out out --sheet    # also writes preview-sheet.png

Requires: Pillow, numpy.
"""

import argparse
import math
import os

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

GOLD_TOP, GOLD_BOT = (255, 216, 112), (226, 145, 18)
GLOW = (240, 171, 30)
GLASS_TOP, GLASS_BOT = (46, 46, 58), (17, 17, 22)
HONEY_TOP, HONEY_BOT = (255, 220, 124), (238, 158, 23)
TRAY_GLASS_TOP, TRAY_GLASS_BOT = (38, 41, 50), (26, 28, 35)

SQ3 = math.sqrt(3)
ICO_SIZES = [256, 128, 64, 48, 32, 24, 20, 16]
TRAY_BORDER = {16: 1.0, 20: 1.0, 24: 1.0, 32: 1.25}  # px at target size
TRAY_GAP = 1.0                                       # px at target size
LARGE_WR = 0.13                                      # border w = LARGE_WR * r
ACTIVE = 0                                           # TL cell


# ------------------------------------------------------------- primitives ---
def vgrad_rgb(W, top, bottom):
    ys = np.linspace(0, 1, W)[:, None, None]
    arr = np.array(top, float) * (1 - ys) + np.array(bottom, float) * ys
    return Image.fromarray(np.broadcast_to(arr, (W, W, 3)).astype(np.uint8), "RGB")


def with_alpha(rgb, mask):
    im = rgb.convert("RGBA")
    im.putalpha(mask)
    return im


def sheen_gradient(S, y0, y1, a0, a1=0):
    col = np.zeros(S, np.uint8)
    i0, i1 = int(y0), int(max(y1, y0 + 1))
    col[i0:i1] = np.linspace(a0, a1, i1 - i0).astype(np.uint8)
    return Image.fromarray(np.repeat(col[:, None], S, axis=1), "L")


def hex_pt(W, cx, cy, r):
    """Pointy-top regular hexagon mask."""
    m = Image.new("L", (W, W), 0)
    d = ImageDraw.Draw(m)
    pts = [(cx + r * math.cos(math.radians(60 * i - 90)),
            cy + r * math.sin(math.radians(60 * i - 90))) for i in range(6)]
    d.polygon(pts, fill=255)
    return m


# ----------------------------------------------------------------- layout ---
def hive_layout(r, g):
    ex = SQ3 * r + g
    sw = (-(SQ3 * r + g) / 2, (3 * r + SQ3 * g) / 2)
    return [(0.0, 0.0), (ex, 0.0), sw, (sw[0] + ex, sw[1])]  # TL, TR, BL, BR


def hive_bbox(r, g):
    cells = hive_layout(r, g)
    xs = [c[0] for c in cells]
    ys = [c[1] for c in cells]
    return min(xs) - SQ3 / 2 * r, max(xs) + SQ3 / 2 * r, min(ys) - r, max(ys) + r


def solve_r(S, g, fit=0.92):
    lo, hi = 0.1, S
    for _ in range(60):
        mid = (lo + hi) / 2
        x0, x1, y0, y1 = hive_bbox(mid, g)
        if max(x1 - x0, y1 - y0) <= fit * S:
            lo = mid
        else:
            hi = mid
    return lo


# ----------------------------------------------------------------- render ---
def render_hive(S, w, gvis, detail=True, active=ACTIVE):
    r = solve_r(S, gvis)
    cells = hive_layout(r, gvis)
    x0, x1, y0, y1 = hive_bbox(r, gvis)
    offx, offy = S / 2 - (x0 + x1) / 2, S / 2 - (y0 + y1) / 2
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    if detail:  # soft glow behind the active cell
        ax, ay = cells[active][0] + offx, cells[active][1] + offy
        gm = hex_pt(S, ax, ay, r * 1.12).filter(
            ImageFilter.GaussianBlur(S * 0.010)).point(lambda a: int(a * 0.28))
        g = Image.new("RGBA", (S, S), GLOW + (0,))
        g.putalpha(gm)
        canvas.alpha_composite(g)

    for i, (cx, cy) in enumerate(cells):
        cx += offx
        cy += offy
        cell_m = hex_pt(S, cx, cy, r)
        if i == active:
            canvas.alpha_composite(with_alpha(vgrad_rgb(S, HONEY_TOP, HONEY_BOT), cell_m))
            if detail:
                sheen = ImageChops.multiply(hex_pt(S, cx, cy, r * 0.96),
                                            sheen_gradient(S, cy - r, cy - r * 0.1, 60))
                canvas.alpha_composite(with_alpha(Image.new("RGB", (S, S), (255, 248, 228)), sheen))
        else:
            body = vgrad_rgb(S, GLASS_TOP, GLASS_BOT) if detail \
                else vgrad_rgb(S, TRAY_GLASS_TOP, TRAY_GLASS_BOT)
            canvas.alpha_composite(with_alpha(body, cell_m))
            ring = ImageChops.difference(cell_m, hex_pt(S, cx, cy, max(r - 2 * w / SQ3, 0.05)))
            canvas.alpha_composite(with_alpha(vgrad_rgb(S, GOLD_TOP, GOLD_BOT), ring))
            if detail:
                sheen = ImageChops.multiply(hex_pt(S, cx, cy, r * 0.9),
                                            sheen_gradient(S, cy - r, cy - r * 0.2, 30))
                canvas.alpha_composite(with_alpha(Image.new("RGB", (S, S), (230, 236, 255)), sheen))
    return canvas


def render_hive_wr(S, wr, detail=True, active=ACTIVE):
    r = 1.0
    for _ in range(40):
        r = solve_r(S, wr * r)
    return render_hive(S, wr * r, wr * r, detail=detail, active=active)


def render_at(size):
    """Native render of one target size (RGBA)."""
    if size >= 48:
        return render_hive_wr(size * 8, LARGE_WR, detail=True).resize((size, size), Image.LANCZOS)
    return render_hive(size * 16, TRAY_BORDER[size] * 16, TRAY_GAP * 16,
                       detail=False).resize((size, size), Image.LANCZOS)


# ------------------------------------------------------------ proof sheet ---
_FONT_CANDIDATES = [
    "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",  # Linux (Noto CJK)
    "C:/Windows/Fonts/msyh.ttc",                               # Windows (Microsoft YaHei)
    "/System/Library/Fonts/PingFang.ttc",                      # macOS
]


def label_font(px):
    for path in _FONT_CANDIDATES:
        if os.path.exists(path):
            return ImageFont.truetype(path, px)
    return ImageFont.load_default()


def build_sheet():
    sizes = [256, 64, 48, 32, 24, 20, 16]
    cell = 300
    W = cell * len(sizes)

    def row(bg_color):
        im = Image.new("RGBA", (W, cell), bg_color)
        d = ImageDraw.Draw(im)
        tc = (200, 200, 200, 255) if sum(bg_color[:3]) < 380 else (60, 60, 60, 255)
        for i, s in enumerate(sizes):
            ic = render_at(s)
            im.alpha_composite(ic, (i * cell + (cell - s) // 2, (cell - s) // 2 - 14))
            t = f"{s}px"
            tb = d.textbbox((0, 0), t, font=label_font(26))
            d.text((i * cell + (cell - (tb[2] - tb[0])) / 2, cell - 40), t,
                   font=label_font(26), fill=tc)
        return im

    sheet = Image.new("RGBA", (W, 110 + 300 * 2 + 320), (18, 18, 20, 255))
    d = ImageDraw.Draw(sheet)
    d.text((24, 20), "Hive Switcher icon - size ladder", font=label_font(34),
           fill=(240, 240, 240, 255))
    d.text((24, 66), "dark/light bg at true size -> tray sizes x7 magnified",
           font=label_font(24), fill=(160, 160, 160, 255))
    y = 110
    sheet.alpha_composite(row((26, 26, 30, 255)), (0, y))
    y += 300
    sheet.alpha_composite(row((238, 238, 242, 255)), (0, y))
    y += 300
    strip = Image.new("RGBA", (cell * 4, cell), (128, 128, 128, 255))
    ds = ImageDraw.Draw(strip)
    for i, s in enumerate([32, 24, 20, 16]):
        big = render_at(s).resize((s * 7, s * 7), Image.NEAREST)
        strip.alpha_composite(big, (i * cell + (cell - s * 7) // 2, (cell - s * 7) // 2 - 12))
        t = f"{s}px x7"
        tb = ds.textbbox((0, 0), t, font=label_font(24))
        ds.text((i * cell + (cell - (tb[2] - tb[0])) / 2, cell - 38), t,
                font=label_font(24), fill=(240, 240, 240, 255))
    d.text((24, y + 14), "tray sizes x7", font=label_font(24), fill=(255, 220, 130, 255))
    sheet.alpha_composite(strip, (300, y))
    return sheet.convert("RGB")


# ------------------------------------------------------------------- main ---
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="out")
    ap.add_argument("--sheet", action="store_true", help="also render the size-ladder proof sheet")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    master = render_hive_wr(4096, LARGE_WR, detail=True)
    master.resize((1024, 1024), Image.LANCZOS).save(f"{args.out}/hive-icon-1024.png")

    frames = [render_at(s) for s in ICO_SIZES]
    frames[0].save(f"{args.out}/hive-icon.ico", format="ICO",
                   append_images=frames[1:], sizes=[(s, s) for s in ICO_SIZES])

    if args.sheet:
        build_sheet().save(f"{args.out}/preview-sheet.png")

    print("done ->", os.path.abspath(args.out))


if __name__ == "__main__":
    main()
