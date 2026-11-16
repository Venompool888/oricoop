# Chinese distance-field atlas: full WenKai replacement or append mode

The game ships `adobeHeitiStdR_0 distance map` in `resources.assets`: a
2048×2048 Alpha8 SDF atlas with **997 runtime characters: 94 ASCII + 903 other**.
`adobeHeitiStdR.json` contains its
dumped `BitmapFontChar` table and line metrics (`dump-font.py`, UnityPy).

The checked-in `zh_glyphs.png` / `zh_glyphs.txt` are a **full LXGW WenKai Light
(霞鹜文楷) replacement**: 1089 glyphs, packed into a 2048×4096 atlas. Full mode
re-renders every original entry, including any Latin, digits, punctuation or
blank entries in the supplied table. ASCII IDs 33–126 (`!` through `~`) live in
the separately indexed `asciiChars` array. The 903 `otherChars` entries are
ideographs, CJK punctuation and `□`. The old JSON incorrectly had `ascii: []`:
that partial dump caused the first full bake to omit all 94 ASCII entries and
runtime validation to abort at ID 33. IDs are integer Unicode codepoints in both
arrays; no key conversion or deduplication error was involved.

The additional character set is deduplicated from decoded regular/verbatim C#
string literals below `src/OriCoop`, `zh` strings in
`tools/zh-extract/translations.json`, and every `src/assets/zh-text/*.txt` file.
Comments and char literals are excluded; Unicode escapes in strings are decoded.
These sources are only read. Font cmap coverage is checked before baking: missing
glyphs fail generation instead of silently rendering tofu. The current build has
997 originals plus 92 extras, all supported by the selected font.

`tools/zh-fonts/out/runtime_coverage.json` records the asset path ID (16907),
the serialized ASCII IDs and the 94 IDs absent from the previous JSON and atlas.
`dump-font.py` now writes both arrays with full float precision and kerning data:

```powershell
& .\tools\zh-extract\venv\Scripts\python.exe -B .\src\assets\zh-glyphs\dump-font.py
```

## Regenerate

From `C:\OriCoop` in PowerShell (Pillow, numpy, scipy, UnityPy, fontTools; the latter
is already vendored in `tools/zh-fonts/fonts/_vendor`):

```powershell
& .\tools\zh-extract\venv\Scripts\python.exe -B -u `
  .\src\assets\zh-glyphs\make-zh-glyphs.py `
  .\src\assets\zh-glyphs\adobeHeitiStdR.json --full `
  --font .\tools\zh-fonts\fonts\LXGWWenKai-Light.ttf `
  --game-assets .\game\oriDE_Data\resources.assets
```

Light is the default. Pass `--font .\tools\zh-fonts\fonts\LXGWWenKai-Regular.ttf`
to try Regular. `--wght` remains available for variable fonts; `--bold` defaults
to zero. `--src`, `--translations`, `--zh-text`, `--extra`, `--out`, and
`--preview-dir` can override inputs/outputs. Reading game assets does not launch
or modify the game. With `--game-assets`, generation reads both font arrays from
the serialized asset as the authoritative set, including when JSON is stale;
it reports missing/stale IDs. Without that option it uses the corrected JSON.
`--compare <original.png>` accepts an extracted top-down
Alpha8/RGBA or grayscale atlas as an alternative to `--game-assets`.

Both output filenames and resource names remain unchanged; **no build.ps1 change
is needed**. The first table comment declares `mode=full`, dimensions and count.
Each following data row has ten whitespace-separated fields:

```text
id x y w h xOffset yOffset width height advance
```

`x,y,w,h` are atlas pixels with y measured from the PNG's top; the other fields
use game font units. Runtime checks dimensions, count, duplicate IDs, bounds,
and finite metrics. Coverage mismatches use the old-glyph fallback below.

## SDF and layout conventions

- 66 px em, eightfold supersampling, three-pixel ink padding, and signed Euclidean
  distance encoded as `clip(128 + distance_px * 127.5 / 3, 0, 255)`. Positive
  distance is inside ink. Grayscale red is copied into the runtime Alpha8 channel;
  the contour is at 0.5 (`>=128`). Full packing is 2048 wide, with two-pixel gaps
  and power-of-two height. UV y is inverted once from PNG to Unity conventions.
- The original line unit is 83 atlas pixels. Width/height and left/top offsets
  are divided by 83; top is positive above the baseline. Original `advance`
  values are preserved individually, including punctuation and any Latin entries.
  New characters use the selected font's unkerned advance at 66 px. Whitespace
  can have an empty quad while retaining its advance.
- Keep `pixelLineHeight`, `baseline`, `baselineToBottom`, `spaceAdvance` and old
  kerning arrays unchanged. Use one shared translation, fitted to the median
  center difference between WenKai ink and the original Heiti reference boxes
  (29 available references). For Light it is x=+0.06250, y=+1.75000 px.
  This is the baseline assumption: align the original glyph-box centers globally,
  retaining WenKai's natural bearings and proportions rather than stretching it.
- UnityPy extraction confirms the original three-pixel padding. Straight sampled
  SDF ramps for `中口日月我联` are 42.5 levels/px in both fonts. For `中`, Heiti is
  0–255 with 1068 thresholded ink pixels, Light is 0–219 with 610: the lower peak
  and thinner strokes are deliberate Light outlines, with no artificial bolding.
  `sdf_metrics.json` records ranges, areas, ramp slopes and a maximum-inside-diameter
  thickness proxy (pixel-grid measurement, not a typographic stem width).

Diagnostics go to `tools/zh-fonts/out/`: `atlas_preview.png` is downscaled,
`sdf_compare.png` shows original/new raw SDF and thresholded glyphs side by side,
with identical pen origins and baseline guides. `original_heiti.png` is the
read-only UnityPy extraction saved as grayscale. The generated atlas is also
grayscale PNG; it becomes Alpha8 in Unity, without PNG alpha ambiguity.

## Runtime modes and verification

`FontGlyphs.cs` patches the Chinese style's font on MessageBox Awake. In full mode
it creates the embedded Alpha8 texture directly, rewrites existing ASCII/other
character objects, appends new IDs in sorted `otherChars` order, retargets all
loaded materials using `_TxtTexture` / `_MainTex`, handles same-font duplicate
assets, and refreshes text boxes. Complete coverage needs no old-pixel readback.
If original IDs are absent from the full table, one warning lists all missing IDs.
Before changing live font data, runtime reads the old atlas and copies each missing
glyph's original UV rectangle into free rows of the replacement. It keeps that
glyph's metrics and kerning, fixes all UVs against the final texture dimensions,
and installs the generated WenKai glyphs normally. The height grows to a power of
two if those rows do not fit; the GPU texture-size limit is checked before upload.
Thus an incomplete table preserves a few old-style glyphs instead of rejecting
the entire replacement. A coverage info log reports ASCII/other/embedded counts.

Omit `--full` to generate **append mode**. Headerless legacy tables are also
append mode: runtime retains the original atlas, adds missing glyph rows, rescales
original v coordinates and appends character entries. Use a separate `--out`
directory when comparing modes so the embedded full atlas is not overwritten.

```powershell
& .\tools\zh-extract\venv\Scripts\python.exe -B .\src\assets\zh-glyphs\test_full_atlas.py
```

`tools/zh-fonts/FontGlyphsTests.cs` is a standalone managed regression harness for
header compatibility, full rewrite of ASCII/other entries, UV inversion, metrics,
kerning retention, copied-pixel orientation, atlas growth, the original 94-ASCII
omission, and incomplete-table fallback. With the generated table and font JSON
as arguments it also rewrites all 997 serialized runtime entries and appends the
92 extras. Python tests compare JSON IDs/metrics directly with both asset arrays
and verify generation recovers the ASCII set from a deliberately stale JSON.
The managed test does not call
Unity native APIs. Compile the plugin using the same C# 5 / game .NET 3.5
references/resources as `src/build.ps1`, with `/out:` pointed to a temporary
folder; do not run the normal installing build while a test is running.
No live game test has been performed for this atlas replacement.

This game build's `GameSettings.Language` setter turns Chinese into English;
`ORICOOP_FORCE_ZH=1` makes the existing test hook report Chinese when a future
game test is explicitly authorized.
