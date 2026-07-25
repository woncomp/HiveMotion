"""Hive Motion icon renderer v8 - 4 hive cells + search bar, edge-to-edge.

Final design (user-iterated):
  - Four pointy-top regular hexagons in a 2-2 rhombus block:
        TL  TR      top row
      BL  BR        bottom row, offset left by half a cell
  - Active cell (TL) = honey-gold gradient; 3 inactive cells = dark glass
    WITH gold borders (w = 0.13r, drawn inside the cell boundary)
  - Search bar below the cluster (= the space-triggered search box):
      * width = BAR_WR (0.70) x cluster width, centered
      * height = BAR_HR (0.30) x r; per-size px heights for tray in TRAY_BAR
      * two fill models (BAR_GOLD_FILL):
          False "dark": dark glass pill + gold border (border <= 30% bar height)
          True  "fill": SOLID rounded gold pill (no border concept), with a
                        darker-gold shadow row at the bottom
                        (height = FILL_SHADOW_FRAC x bar height; at 16px this
                        is ~1 pixel row)
  - EDGE-TO-EDGE: cell cluster touches the left/right canvas edges
    (fit_w = 1.0); vertical is centered (fit_h = 0.995)
  - Gap rules: tray sizes (16/20/24/32) keep 1px visible gaps; >= 48px use
    gap = border width.

Usage:
  pip install pillow numpy
  python hive_icon.py --out out              # dark bar (default)
  python hive_icon.py --out out --gold-bar   # solid gold bar + bottom shadow row
  python hive_icon.py --out out --sheet      # also preview-sheet.png

Requires: Pillow, numpy.
"""

import argparse
import math
import os

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

GOLD_TOP, GOLD_MID, GOLD_BOT = (255, 216, 112), (241, 181, 65), (226, 145, 18)
GLOW = (240, 171, 30)
GLASS_TOP, GLASS_BOT = (46, 46, 58), (17, 17, 22)
HONEY_TOP, HONEY_BOT = (255, 220, 124), (238, 158, 23)
TRAY_GLASS_TOP, TRAY_GLASS_BOT = (38, 41, 50), (26, 28, 35)
FILL_DARK = (198, 118, 12)     # darker gold for fill-mode shadow row

SQ3 = math.sqrt(3)
ICO_SIZES = [256, 128, 64, 48, 32, 24, 20, 16]
# size: (border px, bar height px, cluster->bar gap px); inter-cell gap = 1px
TRAY_BAR = {32: (1.25, 3.75, 2.5), 24: (1.0, 3.0, 2.0), 20: (1.0, 2.6, 1.5), 16: (1.0, 2.25, 1.0)}
TRAY_GAP = 1.0
LARGE_WR = 0.13          # cell border w = LARGE_WR * r
BAR_HR = 0.30            # bar height = BAR_HR * r   (75% of the original 0.40)
BAR_GR = 0.16            # cluster->bar gap = BAR_GR * r
BAR_WR = 0.50            # bar width = BAR_WR * cluster width
BAR_GOLD_FILL = False    # True: solid gold pill; False: dark glass + gold border
FILL_SHADOW_FRAC = 1 / 3  # fill-mode bottom shadow row height / bar height
ACTIVE = 0               # TL cell


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
    ImageDraw.Draw(m).polygon(
        [(cx + r * math.cos(math.radians(60 * i - 90)),
          cy + r * math.sin(math.radians(60 * i - 90))) for i in range(6)], fill=255)
    return m


def pill_mask(W, x0, y0, x1, y1):
    m = Image.new("L", (W, W), 0)
    ImageDraw.Draw(m).rounded_rectangle([x0, y0, x1, y1], radius=(y1 - y0) / 2, fill=255)
    return m


# ----------------------------------------------------------------- layout ---
def hive_layout(r, g):
    ex = SQ3 * r + g
    sw = (-(SQ3 * r + g) / 2, (3 * r + SQ3 * g) / 2)
    return [(0.0, 0.0), (ex, 0.0), sw, (sw[0] + ex, sw[1])]  # TL, TR, BL, BR


def cluster_dims(r, g):
    return (5 * SQ3 / 2) * r + 1.5 * g, (7 * r + SQ3 * g) / 2  # Wc, Hc


# ----------------------------------------------------------------- render ---
def render_icon(S, w, gvis, bar_h, gap_b, detail=True, active=ACTIVE,
                bar_gold=BAR_GOLD_FILL):
    """Edge-to-edge render at canvas S. w: cell border width; gvis: visible gap."""
    lo, hi = 0.1, S
    for _ in range(80):
        mid = (lo + hi) / 2
        Wc, Hc = cluster_dims(mid, gvis)
        if Wc <= S * 0.999 and Hc + gap_b + bar_h <= S * 0.995:
            lo = mid
        else:
            hi = mid
    r = lo
    Wc, Hc = cluster_dims(r, gvis)
    top = (S - (Hc + gap_b + bar_h)) / 2
    cells = hive_layout(r, gvis)
    x_min = min(c[0] for c in cells) - SQ3 / 2 * r
    x_max = max(c[0] for c in cells) + SQ3 / 2 * r
    offx = S / 2 - (x_min + x_max) / 2
    offy = top + r

    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    if detail:
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

    # search bar
    bw = Wc * BAR_WR
    bx0 = offx + x_min + (Wc - bw) / 2
    by0 = offy + (Hc - r) + gap_b
    by1 = by0 + bar_h
    outer = pill_mask(S, bx0, by0, bx0 + bw, by1)
    if bar_gold:
        # solid rounded bar, no border; darker-gold shadow row at bottom
        canvas.alpha_composite(with_alpha(vgrad_rgb(S, HONEY_TOP, HONEY_BOT), outer))
        strip = Image.new("L", (S, S), 0)
        ImageDraw.Draw(strip).rectangle([0, by1 - bar_h * FILL_SHADOW_FRAC, S, by1], fill=255)
        strip = ImageChops.multiply(strip, outer)
        canvas.alpha_composite(with_alpha(Image.new("RGB", (S, S), FILL_DARK), strip))
    else:
        canvas.alpha_composite(with_alpha(Image.new("RGB", (S, S), FILL_DARK), outer))
        wb2 = min(w, bar_h * 0.30)
        inner = pill_mask(S, bx0 + wb2, by0 + wb2, bx0 + bw - wb2, by1 - wb2)
        canvas.alpha_composite(with_alpha(Image.new("RGB", (S, S), GOLD_MID),
                          ImageChops.difference(outer, inner)))
    if detail:
        a = 40 if bar_gold else 26
        sheen = ImageChops.multiply(outer, sheen_gradient(S, by0, by0 + bar_h * 0.6, a))
        canvas.alpha_composite(with_alpha(
            Image.new("RGB", (S, S), (255, 246, 224) if bar_gold else (230, 236, 255)), sheen))
    return canvas


def solve_large(S):
    r = 1.0
    for _ in range(40):
        Wc, Hc = cluster_dims(r, LARGE_WR * r)
        r = min(S * 0.999 / (Wc / r), (S * 0.995) / ((Hc + BAR_GR * r + BAR_HR * r) / r))
    return r


def render_at(size):
    """Native render of one target size (RGBA)."""
    if size >= 48:
        S = size * 8
        r = solve_large(S)
        return render_icon(S, LARGE_WR * r, LARGE_WR * r, BAR_HR * r, BAR_GR * r,
                           detail=True).resize((size, size), Image.LANCZOS)
    bw, bh, gb = TRAY_BAR[size]
    return render_icon(size * 16, bw * 16, TRAY_GAP * 16, bh * 16, gb * 16,
                       detail=False).resize((size, size), Image.LANCZOS)


# ------------------------------------------------------------ proof sheet ---
_FONT_CANDIDATES = [
    "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
    "C:/Windows/Fonts/msyh.ttc",
    "/System/Library/Fonts/PingFang.ttc",
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
    d.text((24, 20), "Hive Motion icon - size ladder", font=label_font(34),
           fill=(240, 240, 240, 255))
    d.text((24, 66), "bar 0.30r | fill mode = solid pill + bottom shadow row | tray 1px gaps",
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
    ap.add_argument("--sheet", action="store_true")
    ap.add_argument("--gold-bar", action="store_true",
                    help="override BAR_GOLD_FILL: solid gold bar + bottom shadow row")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    if args.gold_bar:
        global BAR_GOLD_FILL
        BAR_GOLD_FILL = True

    r = solve_large(4096)
    master = render_icon(4096, LARGE_WR * r, LARGE_WR * r, BAR_HR * r, BAR_GR * r,
                         detail=True)
    master.resize((1024, 1024), Image.LANCZOS).save(f"{args.out}/hive-icon-1024.png")

    frames = [render_at(s) for s in ICO_SIZES]
    frames[0].save(f"{args.out}/hive-icon.ico", format="ICO",
                   append_images=frames[1:], sizes=[(s, s) for s in ICO_SIZES])

    if args.sheet:
        build_sheet().save(f"{args.out}/preview-sheet.png")
    print("done ->", os.path.abspath(args.out))


if __name__ == "__main__":
    main()
