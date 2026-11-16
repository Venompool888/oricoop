"""Generator regression tests; run with tools/zh-extract/venv/Scripts/python.exe -B."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np
from PIL import ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
spec = importlib.util.spec_from_file_location("glyphs", HERE / "make-zh-glyphs.py")
glyphs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(glyphs)


class FullAtlasTests(unittest.TestCase):
    def test_checked_in_table_covers_serialized_runtime_arrays(self):
        data = json.loads((HERE / "adobeHeitiStdR.json").read_text(encoding="utf-8"))
        self.assertEqual([c["id"] for c in data["ascii"]], list(range(33, 127)))
        runtime = glyphs.load_font_table(HERE / "adobeHeitiStdR.json",
                                       ROOT / "game/oriDE_Data/resources.assets")
        self.assertEqual(len(runtime["ascii"]), 94)
        self.assertEqual(len(runtime["other"]), 903)
        self.assertEqual({c["id"] for k in ("ascii", "other") for c in data[k]},
                         {c["id"] for k in ("ascii", "other") for c in runtime[k]})
        self.assertEqual(data["ascii"], runtime["ascii"])
        self.assertEqual(data["other"], runtime["other"])
        with tempfile.TemporaryDirectory(dir=ROOT / "tools/zh-fonts/out") as td:
            stale = Path(td) / "stale.json"
            stale.write_text(json.dumps(dict(data, ascii=[])), encoding="utf-8")
            recovered = glyphs.load_font_table(stale, ROOT / "game/oriDE_Data/resources.assets")
            self.assertEqual(recovered["ascii"], runtime["ascii"])

    def test_sources_decode_literals_and_only_collect_chinese_translations(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tools/zh-fonts/out") as td:
            root = Path(td)
            (root / "strings.cs").write_text(
                '// "假"\nvar a = "中\\u6587\\x5B57"; var b = @"联""机"; '
                '/* "忽" */ var c = \'略\';', encoding="utf-8")
            (root / "translations.json").write_text(json.dumps([
                {"en": "英", "zh": "翻译"}, {"nested": {"zh": "译文"}}]), encoding="utf-8")
            (root / "extra.txt").write_text("补充", encoding="utf-8")
            actual = glyphs.wanted_chars(root, {ord("中")}, "额",
                                        root / "translations.json", root)
            self.assertEqual(set(actual), set("文字联机翻译补充额"))

    def test_full_character_set_contains_every_original_entry_including_ascii(self):
        table = {ord(c): {} for c in "A0 中。"}
        self.assertEqual(glyphs.select_chars(table, ["机", "中"], True), sorted(set("A0 中。机")))
        self.assertEqual(glyphs.select_chars(table, ["机"], False), ["机"])

    def test_pack_wraps_and_rounds_height_to_power_of_two(self):
        tiles = [np.ones((7, 12), np.uint8) for _ in range(5)]
        atlas, positions = glyphs.pack_tiles(tiles, full=True, width=32)
        self.assertEqual(atlas.shape, (32, 32))
        self.assertEqual(len(set(positions)), 5)
        for tile, (x, y) in zip(tiles, positions):
            np.testing.assert_array_equal(atlas[y:y+7, x:x+12], tile)

    def test_space_has_no_ink_and_latin_has_baseline_metrics(self):
        font = ImageFont.truetype(str(ROOT / "tools/zh-fonts/fonts/LXGWWenKai-Light.ttf"), 66 * glyphs.SS)
        self.assertIsNone(glyphs.render_mask(font, " ", 66))
        mask, left, top, box = glyphs.render_mask(font, "g", 66)
        x0, x1, y0, y1 = box
        self.assertGreater(top, 0)
        self.assertLess(top - (y1-y0) / glyphs.SS, 0)  # descender below baseline
        tile = glyphs.sdf_tile(mask, box, 3)
        self.assertGreater(tile.max(), 128)
        self.assertEqual(tile.min(), 0)


if __name__ == "__main__":
    unittest.main()
