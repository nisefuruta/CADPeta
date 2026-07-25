# -*- coding: utf-8 -*-
"""IR写像SVGのブラウザレンダと pypdfium2 正解レンダを均等タイルで画素比較する。

compare_irmap.py（テストチャート19行×6列格子専用）の任意図面向け一般化。
指標・閾値・並置画像の形式は compare_irmap.py と同一に保ち、判定の連続性を確保する。

使い方: compare_tiles.py ref.png svg.png out_dir [nx ny]
- 両画像は同一図面の全ページレンダ（サイズ差は svg 側を ref へリサイズして吸収）
- ページを nx×ny（既定 8×6）の均等タイルへ分割し、タイルごとに差分指標を出して
  閾値超過タイルの並置画像（ref|svg|diff）を out_dir へ保存する
- 結果は out_dir/tiles.json と標準出力の表
"""
import json
import pathlib
import sys

from PIL import Image, ImageChops, ImageFilter

BLUR = 1.5          # AAずれ許容のための前処理ぼかし半径(px)
DIFF_TH = 40        # 「異なる画素」とみなす階調差（ぼかし後）
FRAC_FLAG = 0.002   # 異画素率がこれを超えたら要目視
MEAN_FLAG = 2.0     # 平均差がこれを超えたら要目視

ref_path, svg_path, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
nx = int(sys.argv[4]) if len(sys.argv) > 4 else 8
ny = int(sys.argv[5]) if len(sys.argv) > 5 else 6
out = pathlib.Path(out_dir)
out.mkdir(parents=True, exist_ok=True)

ref = Image.open(ref_path).convert("RGB")
svg = Image.open(svg_path).convert("RGB")
if svg.size != ref.size:
    print(f"サイズ調整: svg {svg.size} -> ref {ref.size}")
    svg = svg.resize(ref.size, Image.LANCZOS)

W, H = ref.size
print(f"render: {W}x{H}px, タイル {nx}x{ny}")


def metrics(a, b):
    ab = a.filter(ImageFilter.GaussianBlur(BLUR))
    bb = b.filter(ImageFilter.GaussianBlur(BLUR))
    d = ImageChops.difference(ab, bb).convert("L")
    hist = d.histogram()
    n = sum(hist)
    bad = sum(hist[DIFF_TH:])
    mean = sum(i * v for i, v in enumerate(hist)) / n
    return bad / n, mean, d


results = []
for ty in range(ny):
    for tx in range(nx):
        box = (W * tx // nx, H * ty // ny, W * (tx + 1) // nx, H * (ty + 1) // ny)
        ca, cb = ref.crop(box), svg.crop(box)
        frac, mean, d = metrics(ca, cb)
        flag = frac > FRAC_FLAG or mean > MEAN_FLAG
        results.append({"tx": tx, "ty": ty, "box": box,
                        "frac_bad": round(frac, 5), "mean": round(mean, 3),
                        "flag": flag})
        if flag:
            w, h = ca.size
            strip = Image.new("RGB", (w, h * 3 + 8), "white")
            strip.paste(ca, (0, 0))
            strip.paste(cb, (0, h + 4))
            strip.paste(Image.eval(d, lambda v: 255 - v).convert("RGB"), (0, 2 * h + 8))
            strip = strip.resize((w * 2, (h * 3 + 8) * 2), Image.NEAREST)
            strip.save(out / f"tile_x{tx}y{ty}.png")

# 全ページの粗い指標
frac_all, mean_all, _ = metrics(ref, svg)

flagged = [x for x in results if x["flag"]]
print(f"\n全ページ: frac_bad={frac_all:.5f} mean={mean_all:.3f}")
print(f"タイル判定: {len(results)}タイル中 要目視 {len(flagged)}")
print(f"{'タイル':>8} {'異画素率':>9} {'平均差':>7}")
for x in results:
    mark = " <== 要目視" if x["flag"] else ""
    print(f"x{x['tx']}y{x['ty']:<5} {x['frac_bad']:>9.5f} {x['mean']:>7.3f}{mark}")

(out / "tiles.json").write_text(
    json.dumps({"page": {"frac_bad": frac_all, "mean": mean_all},
                "grid": [nx, ny], "tiles": results},
               ensure_ascii=False, indent=1), encoding="utf-8")
print(f"\n保存: {out / 'tiles.json'} と要目視タイルの並置PNG")
