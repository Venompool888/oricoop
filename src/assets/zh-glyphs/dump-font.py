"""Read both serialized BitmapFont arrays; importing has no side effects."""
import argparse
import json
from pathlib import Path
import struct

import UnityPy


class Reader:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def read(self, kind):
        value = struct.unpack_from("<" + kind, self.data, self.pos)[0]
        self.pos += struct.calcsize(kind)
        return value

    def string(self):
        size = self.read("i")
        if size < 0 or self.pos+size > len(self.data):
            raise ValueError("bad asset name length")
        value = self.data[self.pos:self.pos+size].decode("utf-8")
        self.pos = (self.pos+size+3) & ~3
        return value


def chars(reader):
    count = reader.read("i")
    if count < 0 or count > 65536:
        raise ValueError("bad character count")
    result = []
    for _ in range(count):
        c = {"id": reader.read("i")}
        for key in ("uMin", "uMax", "vMin", "vMax", "xOffset", "yOffset", "width", "height", "advance"):
            c[key] = reader.read("f")
        for key, kind in (("kerningIds", "i"), ("kernings", "f")):
            n = reader.read("i")
            if n < 0 or reader.pos+n*4 > len(reader.data):
                raise ValueError("bad kerning count")
            c[key] = [reader.read(kind) for _ in range(n)]
        result.append(c)
    return result


def extract_font(assets, font_name="adobeHeitiStdR"):
    for obj in UnityPy.load(str(assets)).objects:
        if obj.type.name != "MonoBehaviour":
            continue
        reader = Reader(obj.get_raw_data())
        try:
            reader.read("i"); reader.read("q"); reader.pos += 4
            reader.read("i"); reader.read("q"); name = reader.string()
        except (struct.error, UnicodeError, ValueError):
            continue
        if name != font_name:
            continue
        ascii_chars, other_chars = chars(reader), chars(reader)
        tail = [reader.read("f") for _ in range(5)]
        ids = [c["id"] for c in ascii_chars+other_chars]
        if len(ids) != len(set(ids)):
            raise ValueError("duplicate serialized font ids")
        if [c["id"] for c in ascii_chars] != list(range(33, 127)):
            raise ValueError("unexpected indexed ASCII layout")
        return {"ascii": ascii_chars, "other": other_chars, "tail": tail,
                "source": {"asset": Path(assets).name, "path_id": obj.path_id, "font": name}}
    raise ValueError("font asset not found: " + font_name)


def main():
    root = Path(__file__).resolve().parents[3]
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--assets", type=Path, default=root / "game/oriDE_Data/resources.assets")
    ap.add_argument("--out", type=Path, default=Path(__file__).with_name("adobeHeitiStdR.json"))
    ap.add_argument("--report", type=Path, help="compare current JSON/table ids before replacing JSON")
    args = ap.parse_args()
    data = extract_font(args.assets)
    if args.report:
        old = json.loads(args.out.read_text(encoding="utf-8"))
        old_ids = {c["id"] for k in ("ascii", "other") for c in old[k]}
        runtime_ids = {c["id"] for k in ("ascii", "other") for c in data[k]}
        embedded = args.out.with_name("zh_glyphs.txt")
        embedded_ids = {int(row.split()[0]) for row in embedded.read_text(encoding="utf-8").splitlines()
                        if row.strip() and not row.startswith("#")}
        report = {"source": data["source"], "runtime_ascii_ids": [c["id"] for c in data["ascii"]],
                  "runtime_other_count": len(data["other"]), "runtime_total": len(runtime_ids),
                  "previous_json_missing_ids": sorted(runtime_ids-old_ids),
                  "previous_embedded_missing_ids": sorted(runtime_ids-embedded_ids),
                  "unexpected_json_ids": sorted(old_ids-runtime_ids)}
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    with args.out.open("w", encoding="utf-8", newline="\n") as output:
        json.dump(data, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print("%s: %d ASCII + %d other = %d runtime glyphs" %
          (data["source"]["font"], len(data["ascii"]), len(data["other"]), len(data["ascii"])+len(data["other"])))


if __name__ == "__main__":
    main()
