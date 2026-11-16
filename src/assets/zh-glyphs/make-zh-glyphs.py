"""Bake append glyphs or a complete replacement for adobeHeitiStdR.

PNG: top-down grayscale SDF; runtime copies red into Alpha8.
Table: id x y w h xOffset yOffset width height advance (y from top).
The '# mode=full' header selects replacement; absent/append headers append.
See README.md for regeneration, metric conventions and diagnostics.
"""
import argparse
import json
from pathlib import Path
import re
import runpy
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
ATLAS = 2048
SS = 8
SPREAD = 3.0  # +/-3 atlas pixels = ~42.5 levels/pixel
PAD = 3.0
BOLD = 0.0
REFS = "中国口田日月目回四面重里理国家的是不了人我在有他这为之大来以个"


def is_cjk(ch):
    n = ord(ch)
    return (0x3000 <= n <= 0x9fff or 0xf900 <= n <= 0xfaff or
            0xff00 <= n <= 0xffef or 0x20000 <= n <= 0x323af or n == 0xb7 or
            # Punctuation, arrows and shapes used in our Chinese UI texts (… — “ ” ↑ ↓ → ▶ ◀).
            0x2010 <= n <= 0x2027 or 0x2190 <= n <= 0x21ff or 0x25a0 <= n <= 0x25ff)


def wanted_chars(src, have, extra, translations=None, zh_text=None):
    chars = set(extra)
    # Tokenize comments and char literals so neither is mistaken for a string.
    tokens = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|\'(?:[^\'\\]|\\.)*\'|'
                        r'@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"')
    escapes = re.compile(r'\\(?:u([0-9a-fA-F]{4})|U([0-9a-fA-F]{8})|x([0-9a-fA-F]{1,4})|(.))')

    def decode(m):
        if any(m.group(i) for i in (1, 2, 3)):
            return chr(int(next(g for g in m.groups()[:3] if g), 16))
        return {"n": "\n", "r": "\r", "t": "\t", "0": "\0"}.get(m.group(4), m.group(4))

    def add(text):
        chars.update(ch for ch in text if is_cjk(ch))

    for path in sorted(Path(src).rglob("*.cs")):
        for m in tokens.finditer(path.read_text(encoding="utf-8-sig", errors="replace")):
            token = m.group()
            if token.startswith('@"'):
                add(token[2:-1].replace('""', '"'))
            elif token.startswith('"'):
                add(escapes.sub(decode, token[1:-1]))

    if translations:
        def visit(value):
            if isinstance(value, dict):
                for k, v in value.items():
                    if k == "zh" and isinstance(v, str):
                        add(v)
                    else:
                        visit(v)
            elif isinstance(value, list):
                for v in value:
                    visit(v)
        visit(json.loads(Path(translations).read_text(encoding="utf-8-sig")))
    if zh_text:
        for path in sorted(Path(zh_text).glob("*.txt")):
            add(path.read_text(encoding="utf-8-sig"))
    return sorted(c for c in chars if ord(c) not in have)


def select_chars(original, extra, full):
    return sorted(set(extra) | ({chr(n) for n in original} if full else set()))


def render_mask(font, ch, em_px):
    """Supersampled ink, left/top from baseline, crop box; retain descenders."""
    bx0, by0, bx1, by1 = font.getbbox(ch, anchor="ls")
    margin = int((PAD + SPREAD + 2) * SS)
    img = Image.new("L", (max(1, bx1-bx0) + 2*margin, max(1, by1-by0) + 2*margin))
    ox, oy = margin-bx0, margin-by0
    ImageDraw.Draw(img).text((ox, oy), ch, font=font, fill=255, anchor="ls")
    a = np.asarray(img) > 127
    ys, xs = np.nonzero(a)
    if not len(xs):
        return None
    x0, x1, y0, y1 = int(xs.min()), int(xs.max()+1), int(ys.min()), int(ys.max()+1)
    return a, (x0-ox)/SS, (oy-y0)/SS, (x0, x1, y0, y1)


def sdf_tile(a, box, pad):
    x0, x1, y0, y1 = box
    p = int(round(pad * SS))
    sub = a[y0-p:y1+p, x0-p:x1+p]
    h, w = [int(np.ceil(n / SS)) * SS for n in sub.shape]
    m = np.zeros((h, w), bool)
    m[:sub.shape[0], :sub.shape[1]] = sub
    d = (ndimage.distance_transform_edt(m) - ndimage.distance_transform_edt(~m)) / SS
    d = d.reshape(h//SS, SS, w//SS, SS).mean(axis=(1, 3)) + BOLD
    return np.clip(128 + d * (127.5 / SPREAD), 0, 255).astype(np.uint8)


def pack_tiles(tiles, full, width=ATLAS):
    gap = 2
    positions = []
    x = y = row_h = 0
    for tile in tiles:
        h, w = tile.shape
        if full and w+2*gap > width:
            raise ValueError("glyph wider than atlas")
        if full and not positions:
            x = y = gap
        if full and x + w + gap > width:
            x = gap
            y += row_h + gap
            row_h = 0
        positions.append((x, y))
        x += w + gap
        row_h = max(row_h, h)
    used_h = max(1, y + row_h + (gap if full else 0))
    h = 1 << (used_h-1).bit_length() if full else used_h
    w = width if full else max(1, x)
    atlas = np.zeros((h, w), np.uint8)
    for tile, (x, y) in zip(tiles, positions):
        th, tw = tile.shape
        atlas[y:y+th, x:x+tw] = tile
    return atlas, positions


def game_tile(atlas, g):
    h, w = atlas.shape
    x0, x1 = [round(g[k]*w) for k in ("uMin", "uMax")]
    y0, y1 = [h-round(g[k]*h) for k in ("vMax", "vMin")]
    return atlas[y0:y1, x0:x1]


def load_font_table(fontjson, game_assets=None):
    data = json.loads(Path(fontjson).read_text(encoding="utf-8-sig"))
    if game_assets:
        # Serialized arrays are authoritative; stale JSON must not omit ASCII again.
        runtime = runpy.run_path(str(HERE / "dump-font.py"))["extract_font"](game_assets)
        dumped_ids = {c["id"] for k in ("ascii", "other") for c in data[k]}
        runtime_ids = {c["id"] for k in ("ascii", "other") for c in runtime[k]}
        if dumped_ids != runtime_ids:
            print("font JSON differs from runtime asset; using asset ids (missing=%s, stale=%s)" %
                  (sorted(runtime_ids-dumped_ids), sorted(dumped_ids-runtime_ids)))
        data = runtime
    print("original runtime set: %d ASCII + %d other" % (len(data["ascii"]), len(data["other"])))
    return data


def load_original(args):
    if args.game_assets:
        import UnityPy
        env = UnityPy.load(str(args.game_assets))
        for obj in env.objects:
            if obj.type.name == "Texture2D":
                data = obj.read()
                if data.m_Name == "adobeHeitiStdR_0 distance map":
                    if data.m_TextureFormat != 1:
                        raise ValueError("expected original Alpha8 texture")
                    alpha = data.image.getchannel("A")
                    print("UnityPy: original %dx%d Alpha8" % alpha.size)
                    return np.asarray(alpha)
        raise ValueError("original Chinese atlas not found")
    if args.compare:
        image = Image.open(args.compare)
        return np.asarray(image.getchannel("A") if "A" in image.getbands() else image.convert("L"))
    return None


def fit_metrics(font, original, line, em, atlas):
    """Fit one translation to Heiti reference centers, preserving font shapes.

    Measure padding from original contours, independent of WenKai ink dimensions.
    """
    dx, dy, pads = [], [], []
    for ch in REFS:
        g = original.get(ord(ch))
        r = render_mask(font, ch, em)
        if g is None or r is None:
            continue
        _, left, top, (x0, x1, y0, y1) = r
        dx.append((g["xOffset"] + g["width"]/2)*line - (left + (x1-x0)/(2*SS)))
        dy.append((g["yOffset"] - g["height"]/2)*line - (top - (y1-y0)/(2*SS)))
        if atlas is not None:
            v = game_tile(atlas, g)
            ys, xs = np.where(v >= 128)
            if len(xs):
                pads.extend((xs.min(), ys.min(), v.shape[1]-xs.max()-1, v.shape[0]-ys.max()-1))
    if not dx:
        raise ValueError("no ideograph reference metrics to fit")
    pad = float(np.median(pads)) if pads else PAD
    if abs(pad-PAD) > 0.1:
        raise ValueError("unexpected original SDF padding: %s" % pad)
    shift = float(np.median(dx)), float(np.median(dy))
    print("fit %d refs: pad %.2f px, shared center shift x=%.4f y=%.4f px (sd %.2f/%.2f)" %
          (len(dx), pad, *shift, np.std(dx), np.std(dy)))
    return pad, shift


def check_font_coverage(path, chars):
    # fontTools is already vendored with the chosen fonts in this workspace.
    vendor = ROOT / "tools/zh-fonts/fonts/_vendor"
    if vendor.is_dir():
        sys.path.insert(0, str(vendor))
    from fontTools.ttLib import TTFont
    with TTFont(str(path)) as font:
        cmap = font.getBestCmap()
        missing = ["U+%04X" % ord(ch) for ch in chars if ord(ch) not in cmap]
    if missing:
        raise ValueError("font lacks requested glyphs: " + ", ".join(missing))


def tile_stats(v):
    inside = v >= 128
    a = v.astype(float)
    gy, gx = np.gradient(a)
    ramp = (a > 20) & (a < 235)
    slopes = np.concatenate((abs(gx[ramp & (abs(gy) < 2)]), abs(gy[ramp & (abs(gx) < 2)])))
    slopes = slopes[slopes > 10]
    return {"range": [int(v.min()), int(v.max())], "ink_pixels": int(inside.sum()),
            "max_inside_diameter_px": round(float(2*ndimage.distance_transform_edt(inside).max()), 3),
            "median_straight_ramp_levels_per_px": round(float(np.median(slopes)), 3) if len(slopes) else None}


def diagnostics(directory, atlas, original_atlas, original, records, line, font_path, pad, shift):
    directory.mkdir(parents=True, exist_ok=True)
    preview = Image.fromarray(atlas)
    preview.thumbnail((1024, 1024), Image.Resampling.LANCZOS)
    preview.save(directory / "atlas_preview.png")
    if original_atlas is None:
        return
    Image.fromarray(original_atlas).save(directory / "original_heiti.png")
    by_char = {r[0]: r for r in records}
    samples = [ch for ch in "Ag0!中口日月人我联，。□" if ord(ch) in original and ch in by_char]
    # Same pen origin and baseline in every panel.
    panel_w, panel_h, scale = 108, 100, 3
    header, label_w = 36, 105
    canvas = Image.new("RGB", (label_w+panel_w*scale*4, header+len(samples)*panel_h*scale), "#24272b")
    draw = ImageDraw.Draw(canvas)
    label_font = ImageFont.truetype(str(font_path), 27)
    for i, title in enumerate(("Heiti SDF", "WenKai SDF", "Heiti >= 0.5", "WenKai >= 0.5")):
        draw.text((label_w+i*panel_w*scale+8, 7), title, fill="white")
    stats = {"font": font_path.name, "em_px": original[ord("中")]["advance"]*line,
             "line_px": line, "pad_px": pad, "spread_px": SPREAD,
             "shift_px": list(shift), "samples": {}}

    def panel(v, qx, qy, threshold):
        a = np.zeros((panel_h, panel_w), np.uint8)
        x, y = round(20+qx), round(72-qy)
        h, w = v.shape
        if x < 0 or y < 0 or x+w > panel_w or y+h > panel_h:
            raise ValueError("comparison glyph outside panel")
        a[y:y+h, x:x+w] = (v >= 128)*255 if threshold else v
        im = Image.fromarray(a).convert("RGB")
        d = ImageDraw.Draw(im)
        d.line((0, 72, panel_w, 72), fill="#4f5660")
        d.line((20, 0, 20, panel_h), fill="#4f5660")
        return im.resize((panel_w*scale, panel_h*scale), Image.Resampling.NEAREST)

    for row, ch in enumerate(samples):
        g = original[ord(ch)]
        old = game_tile(original_atlas, g)
        _, new, qx, qy, advance = by_char[ch]
        stats["samples"][ch] = {"heiti": tile_stats(old), "wenkai": tile_stats(new)}
        yy = header+row*panel_h*scale
        draw.text((12, yy+90), ch, font=label_font, fill="white")
        draw.text((5, yy+130), "U+%04X" % ord(ch), fill="white")
        for col, (v, x, y, threshold) in enumerate(((old, g['xOffset']*line, g['yOffset']*line, False),
                                                 (new, qx, qy, False),
                                                 (old, g['xOffset']*line, g['yOffset']*line, True),
                                                 (new, qx, qy, True))):
            canvas.paste(panel(v, x, y, threshold), (label_w+col*panel_w*scale, yy))
    canvas.save(directory / "sdf_compare.png")
    (directory / "sdf_metrics.json").write_text(json.dumps(stats, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print("SDF comparison:", json.dumps(stats["samples"], ensure_ascii=True))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("fontjson", type=Path)
    ap.add_argument("--full", action="store_true", help="replace every original glyph plus required Chinese")
    ap.add_argument("--font", type=Path, default=ROOT / "tools/zh-fonts/fonts/LXGWWenKai-Light.ttf")
    ap.add_argument("--src", type=Path, default=ROOT / "src/OriCoop")
    ap.add_argument("--translations", type=Path, default=ROOT / "tools/zh-extract/translations.json")
    ap.add_argument("--zh-text", type=Path, default=ROOT / "src/assets/zh-text")
    ap.add_argument("--extra", default="")
    ap.add_argument("--out", type=Path, default=HERE)
    ap.add_argument("--preview-dir", type=Path, default=ROOT / "tools/zh-fonts/out")
    ap.add_argument("--game-assets", type=Path, help="read original atlas with UnityPy for comparison and pad check")
    ap.add_argument("--compare", type=Path, help="original atlas PNG as alternative to --game-assets")
    ap.add_argument("--wght", type=float, default=0, help="variable font weight axis, if applicable")
    ap.add_argument("--bold", type=float, default=0, help="stroke expansion in px per side; default 0")
    args = ap.parse_args()
    fj = load_font_table(args.fontjson, args.game_assets)
    line = fj["tail"][0]
    original = {g["id"]: g for key in ("ascii", "other") for g in fj[key]}
    em = original[ord("中")]["advance"] * line
    font = ImageFont.truetype(str(args.font), round(em*SS))
    if args.wght:
        font.set_variation_by_axes([args.wght])
    global BOLD
    BOLD = args.bold
    extra = wanted_chars(args.src, set(original), args.extra, args.translations, args.zh_text)
    chars = select_chars(original, extra, args.full)
    check_font_coverage(args.font, chars)
    original_atlas = load_original(args)
    pad, shift = fit_metrics(font, original, line, em, original_atlas)
    records = []
    for ch in chars:
        r = render_mask(font, ch, em)
        advance = original[ord(ch)]["advance"] if ord(ch) in original else font.getlength(ch)/SS/line
        if r is None:
            records.append((ch, np.zeros((1, 1), np.uint8), 0.0, 0.0, advance))
        else:
            a, left, top, box = r
            records.append((ch, sdf_tile(a, box, pad), left-pad+shift[0], top+pad+shift[1], advance))
    atlas, positions = pack_tiles([r[1] for r in records], args.full)
    args.out.mkdir(parents=True, exist_ok=True)
    Image.fromarray(atlas).save(args.out / "zh_glyphs.png")
    mode = "full" if args.full else "append"
    lines = ["# mode=%s width=%d height=%d count=%d" % (mode, atlas.shape[1], atlas.shape[0], len(records)),
             "# %s em=%.5f line=%.5f pad=%.2f spread=%.2f shift=%.5f,%.5f bold=%.3f" %
             (args.font.name, em, line, pad, SPREAD, *shift, BOLD)]
    for (ch, v, qx, qy, advance), (x, y) in zip(records, positions):
        h, w = v.shape
        blank = not np.any(v)
        lines.append("%d %d %d %d %d %.8f %.8f %.8f %.8f %.8f" %
                     (ord(ch), x, y, w, h, qx/line, qy/line,
                      0 if blank else w/line, 0 if blank else h/line, advance))
    with (args.out / "zh_glyphs.txt").open("w", encoding="utf-8", newline="\n") as output:
        output.write("\n".join(lines)+"\n")
    print("wrote %s atlas %dx%d: %d glyphs (%d original + %d extra)" %
          (mode, atlas.shape[1], atlas.shape[0], len(records), len(original) if args.full else 0, len(extra)))
    diagnostics(args.preview_dir, atlas, original_atlas, original, records, line, args.font, pad, shift)


if __name__ == "__main__":
    main()
