# -*- coding: utf-8 -*-
"""IR写像SVGのブラウザレンダと pypdfium2 正解レンダをセル別に画素比較する。

使い方: compare_irmap.py ref.png svg.png page_w_mm page_h_mm out_dir
- 両画像は同一図面の全ページレンダ（サイズ差は svg 側を ref へリサイズして吸収）
- テストチャート19行×6列（testchart_grid.py）ごとに差分指標を出し、
  閾値超過セルの並置画像（ref|svg|diff）を out_dir へ保存する
- 結果は out_dir/cells.json と標準出力の表
"""
import json
import pathlib
import sys

from PIL import Image
# 描画・見当枠・ぼかした差の指標は、ほかの比較の試験と共有する（tests/tools/common/render_compare.py）
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "common"))
from render_compare import blurred_diff_metrics  # noqa: E402

from testchart_grid import COL0_X, COL_W, N_COL, OFF_X, OFF_Y, ROW0_Y, ROW_H, N_ROW, ROW_NAMES

FRAC_FLAG = 0.002   # 異画素率がこれを超えたら要目視
MEAN_FLAG = 2.0     # 平均差がこれを超えたら要目視

ref_path, svg_path, w_mm, h_mm, out_dir = (
    sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4]), sys.argv[5])
out = pathlib.Path(out_dir)
out.mkdir(parents=True, exist_ok=True)

ref = Image.open(ref_path).convert("RGB")
svg = Image.open(svg_path).convert("RGB")
if svg.size != ref.size:
    print(f"サイズ調整: svg {svg.size} -> ref {ref.size}")
    svg = svg.resize(ref.size, Image.LANCZOS)

ppm = ref.size[0] / w_mm  # px/mm
print(f"render: {ref.size}px, {ppm:.3f} px/mm")


def cell_box(r, c):
    """セル(r,c)の画像px矩形。図面mm→用紙mm→px（画像は上原点）"""
    x0 = (OFF_X + COL0_X + (c - 1) * COL_W) * ppm
    x1 = x0 + COL_W * ppm
    top_mm = OFF_Y + ROW0_Y - (r - 1) * ROW_H   # 用紙mm（下原点）
    y0 = (h_mm - top_mm) * ppm
    y1 = y0 + ROW_H * ppm
    return (int(x0), int(y0), int(x1), int(y1))


results = []
for r in range(1, N_ROW + 1):
    for c in range(1, N_COL + 1):
        box = cell_box(r, c)
        ca, cb = ref.crop(box), svg.crop(box)
        frac, mean, d = blurred_diff_metrics(ca, cb)
        flag = frac > FRAC_FLAG or mean > MEAN_FLAG
        results.append({"row": r, "col": c, "name": ROW_NAMES[r - 1],
                        "frac_bad": round(frac, 5), "mean": round(mean, 3),
                        "flag": flag})
        if flag:
            w, h = ca.size
            strip = Image.new("RGB", (w, h * 3 + 8), "white")
            strip.paste(ca, (0, 0))
            strip.paste(cb, (0, h + 4))
            strip.paste(Image.eval(d, lambda v: 255 - v).convert("RGB"), (0, 2 * h + 8))
            strip = strip.resize((w * 2, (h * 3 + 8) * 2), Image.NEAREST)
            strip.save(out / f"cell_r{r:02d}c{c}.png")

# 全ページの粗い指標（グリッド外＝見出し・ラベル含む）
frac_all, mean_all, _ = blurred_diff_metrics(ref, svg)

flagged = [x for x in results if x["flag"]]
print(f"\n全ページ: frac_bad={frac_all:.5f} mean={mean_all:.3f}")
print(f"セル判定: {len(results)}セル中 要目視 {len(flagged)}")
print(f"{'セル':>7} {'行名':<24} {'異画素率':>9} {'平均差':>7}")
for x in results:
    mark = " <== 要目視" if x["flag"] else ""
    print(f"r{x['row']:02d}c{x['col']}  {x['name']:<24} {x['frac_bad']:>9.5f} {x['mean']:>7.3f}{mark}")

(out / "cells.json").write_text(
    json.dumps({"page": {"frac_bad": frac_all, "mean": mean_all}, "cells": results},
               ensure_ascii=False, indent=1), encoding="utf-8")
print(f"\n保存: {out / 'cells.json'} と要目視セルの並置PNG")
