# Builds the downed-teammate marker from the game's own world-map Ori head (worldMapTexturesAtlas0, bottom-right).
# Usage: python make-dead-marker.py <worldMapTexturesAtlas0.png> <outdir>
# Writes dead_marker_<variant>.png (256x256 RGBA) plus a preview sheet. The plugin embeds dead_marker.png
# (copy the chosen variant to that name); a file at BepInEx\plugins\OriCoop_Marks\dead_marker.png overrides it.
import sys, os
from PIL import Image, ImageDraw, ImageFilter, ImageEnhance

atlas, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
S = 8                                            # work at 8x
src = Image.open(atlas).convert("RGBA").crop((942, 1982, 1018, 2048))   # head + glow, 76x66
W, H = src.size
big = src.resize((W * S, H * S), Image.LANCZOS)

# Eyes (in crop pixels), measured on the 6x preview: left ~(29.7, 43), right ~(42, 43.5); radius ~3.
EYES = [(29.7, 43.0), (42.0, 43.5)]
EYE_R = 3.4

def body_mask(img):
    # The bright head itself (not the soft glow): high luminance pixels.
    g = img.convert("L")
    return g.point(lambda v: 255 if v > 150 else 0).filter(ImageFilter.GaussianBlur(S * 0.4))

def make(name, tint, glow_keep, x_color, x_width, alpha, x_size=1.0):
    img = big.copy()
    r, g, b, a = img.split()
    lum = img.convert("L")
    # Recolour: keep the shape's light/dark, re-tint it.
    col = Image.merge("RGB", [lum.point(lambda v, t=t: int(min(255, v * t / 255))) for t in tint])
    mask = body_mask(img)
    # Glow (everything that is not the head body) faded to glow_keep.
    glow_a = a.point(lambda v: int(v * glow_keep))
    a2 = Image.composite(a, glow_a, mask)
    img = Image.merge("RGBA", (*col.split(), a2))
    d = ImageDraw.Draw(img)
    face = tuple(int(255 * t / 255) for t in tint) + (255,)
    for ex, ey in EYES:
        cx, cy = ex * S, ey * S
        rr = EYE_R * S
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), fill=face)          # cover the old eye
        k = rr * 1.05 * x_size
        w = int(x_width * S)
        d.line((cx - k, cy - k, cx + k, cy + k), fill=x_color, width=w)
        d.line((cx - k, cy + k, cx + k, cy - k), fill=x_color, width=w)
    # Overall translucency.
    r, g, b, a = img.split()
    img = Image.merge("RGBA", (r, g, b, a.point(lambda v: int(v * alpha))))
    # Square canvas just big enough for the icon and its glow, centred (the plugin sizes the whole square).
    img = img.resize((W * 3, H * 3), Image.LANCZOS)
    side = max(img.width, img.height)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(img, ((side - img.width) // 2, (side - img.height) // 2))
    canvas.save(os.path.join(out, "dead_marker_" + name + ".png"))
    return canvas

variants = [
    # name,    tint (head colour),  glow kept, X colour,            X width, alpha
    ("ghost",  (215, 228, 240),     0.35,      (40, 52, 70, 255),   1.3,     0.70),
    ("redx",   (225, 232, 240),     0.30,      (200, 30, 40, 255),  1.4,     0.75),
    ("stone",  (150, 155, 165),     0.00,      (30, 30, 36, 255),   1.3,     0.65),
    ("blue",   (255, 255, 255),     0.80,      (20, 50, 90, 255),   1.3,     0.70),
]
tiles = [make(*v) for v in variants]

# Preview sheet: each variant over a dark and a light background, at 1x and 3x.
side = tiles[0].width
sheet = Image.new("RGBA", (len(tiles) * (side + 24), 2 * (side + 24)), (0, 0, 0, 255))
for i, t in enumerate(tiles):
    for j, bg in enumerate([(25, 35, 45, 255), (120, 150, 110, 255)]):
        tile = Image.new("RGBA", t.size, bg); tile.alpha_composite(t)
        sheet.alpha_composite(tile, (i * (side + 24) + 12, 12 + j * (side + 24)))
sheet.save(os.path.join(out, "preview.png"))
print("ok", [v[0] for v in variants])
