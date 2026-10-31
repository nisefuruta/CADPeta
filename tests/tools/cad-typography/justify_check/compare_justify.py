# -*- coding: utf-8 -*-
"""構造SVG書き出し（AutoCAD DB 直接取得）とPDF印刷経路（プロットPDF→CLI変換）の SVG を Chrome ヘッドレスで
同一 DPI レンダし、Justify 種別（行）ごとに位置ずれを実測する。

処理の流れ（tests/tools/cad-typography/glyph_render_check/compare_glyphs.py の方式を踏襲）:
  1. 両 SVG を Chrome ヘッドレスで PNG 化する
  2. 両 PNG から見当枠（最外郭の黒矩形、CAD 10,10-410,287 の LWPOLYLINE）を検出し、
     構造SVG書き出し側をPDF印刷経路側へアフィン（平行移動＋軸別スケール）で位置合わせする
  3. anchors.tsv の行定義から各行の帯（Justify 種別ごとの ink 想定域）を切り出し、
     (a) ぼかし差分の異画素率・平均差（compare_glyphs.py と同一の指標）
     (b) 黒インク（R,G,B すべて<128。赤十字マーカー・緑ラベルを除外）の bbox を
         mm へ換算して両系の左右上下端のずれを数値比較する
  4. 並置 PNG・差分 PNG・行別ストリップ PNG と results.json を保存する

使い方:
  uv run python compare_justify.py <構造SVG書き出しのSVG> <PDF印刷経路のSVG> <anchors.tsv> <出力先> [dpi=150]
"""
import json
import pathlib
import sys
import re

import numpy as np
from PIL import Image
# 描画・見当枠・ぼかした差の指標は、ほかの比較の試験と共有する（tests/tools/common/render_compare.py）
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "common"))
from render_compare import DARK_THRESHOLD, render_svg_via_chrome, detect_frame_bbox, blurred_diff_metrics  # noqa: E402

FRAC_FLAG = 0.01  # 行の異画素率がこれを超えたら要目視（文字は線が疎なため glyph 検証と同値）
MEAN_FLAG = 3.0   # 行の平均差がこれを超えたら要目視
SHIFT_FLAG_MM = 0.4  # ink bbox 端のずれ判定閾値（phase2 構造SVG書き出し検証の実寸精度 ±0.4mm と同値）


# 見当枠の CAD 座標（build_justify_chart.lsp と共有する定数）
FRAME_X0, FRAME_Y0, FRAME_X1, FRAME_Y1 = 10.0, 10.0, 410.0, 287.0

# Justify 種別ごとの帯（アンカーY からの ink 想定域、mm）。
# 文字高さ 8mm・descender 約2.4mm・Align は導出高さ 14.5mm を踏まえた余裕込みの値
BAND_BY_TYPE = {
    # ベースライン系（アンカー=ベースライン）
    "Left":   (-4.0, 10.0), "Center": (-4.0, 10.0), "Right": (-4.0, 10.0),
    "Align":  (-4.0, 17.0), "Fit":    (-4.0, 10.0),
    "SHX-Left": (-4.0, 10.0), "SHX-Center": (-4.0, 10.0),
    "JP-Center(TT)": (-4.0, 10.0),
    # Middle/M?（アンカー=中央）
    "Middle": (-7.0, 7.0), "ML": (-7.0, 7.0), "MC": (-7.0, 7.0), "MR": (-7.0, 7.0),
    # Top系（アンカー=上端）
    "TL": (-12.0, 1.5), "TC": (-12.0, 1.5), "TR": (-12.0, 1.5), "SHX-TR": (-12.0, 1.5),
    # Bottom系（アンカー=下端）
    "BL": (-1.5, 12.0), "BC": (-1.5, 12.0), "BR": (-1.5, 12.0),
    # 回転ケース（右側領域。ink は斜めに大きく動くため広帯で切る）
    "Center-rot30": (-18.0, 18.0), "TR-rot30": (-38.0, 2.0), "Align-diag": (-3.0, 45.0),
}
# 行の計測X範囲（mm）。通常行は本体列、回転ケースは右側領域。
# SHX-TR と JP-Center(TT) は縦帯が重なるため X で分離する（チャート側で JP の
# アンカーX を 160 に置いている）
XRANGE_DEFAULT = (15.0, 220.0)
XRANGE_ROT = (220.0, 408.0)
XRANGE_BY_TYPE = {
    "SHX-TR": (15.0, 110.0),
    "JP-Center(TT)": (110.0, 220.0),
    "Center-rot30": XRANGE_ROT, "TR-rot30": XRANGE_ROT, "Align-diag": XRANGE_ROT,
}


def ink_bbox_mm(img: Image.Image, box_px, px_to_mm) -> dict | None:
    """box_px 内の黒インク（R,G,B すべて<128）bbox を mm で返す。無ければ None。"""
    arr = np.asarray(img.crop(box_px))
    dark = (arr[:, :, 0] < DARK_THRESHOLD) & (arr[:, :, 1] < DARK_THRESHOLD) & (arr[:, :, 2] < DARK_THRESHOLD)
    ys, xs = np.nonzero(dark)
    if len(xs) == 0:
        return None
    x0, y0 = px_to_mm(box_px[0] + xs.min(), box_px[1] + ys.min())
    x1, y1 = px_to_mm(box_px[0] + xs.max() + 1, box_px[1] + ys.max() + 1)
    # 画像Y下向き → CAD Y上向き（y0 が上端＝CADでは大きい方）
    return {"left": x0, "right": x1, "top": y0, "bottom": y1}


def main():
    svg_a, svg_b, anchors_path, out_dir_s = sys.argv[1:5]
    dpi = float(sys.argv[5]) if len(sys.argv) > 5 else 150.0
    out = pathlib.Path(out_dir_s)
    out.mkdir(parents=True, exist_ok=True)

    print(f"PDF印刷経路のSVGを {dpi}dpi でレンダ(Chrome): {svg_b}")
    img_b = render_svg_via_chrome(svg_b, dpi, str(out / "b.png"))
    print(f"構造SVG書き出しのSVGを {dpi}dpi でレンダ(Chrome): {svg_a}")
    img_a_raw = render_svg_via_chrome(svg_a, dpi, str(out / "a_raw.png"))

    print("見当枠を検出中...")
    b_bbox = detect_frame_bbox(img_b)
    a_bbox = detect_frame_bbox(img_a_raw)
    print(f"  PDF印刷経路のbbox(px) = {b_bbox}")
    print(f"  構造SVG書き出しのbbox(px) = {a_bbox}")

    bx0, by0, bx1, by1 = b_bbox
    ax0, ay0, ax1, ay1 = a_bbox
    scale_x = (bx1 - bx0) / (ax1 - ax0)
    scale_y = (by1 - by0) / (ay1 - ay0)
    off_x = bx0 - ax0 * scale_x
    off_y = by0 - ay0 * scale_y
    print(f"  affine: scale=({scale_x:.5f},{scale_y:.5f}) offset=({off_x:.2f},{off_y:.2f})")

    # CAD(mm) ←→ PDF印刷経路の画像画素
    b_scale_x = (bx1 - bx0) / (FRAME_X1 - FRAME_X0)
    b_scale_y = (by1 - by0) / (FRAME_Y1 - FRAME_Y0)

    def cad_to_px(cad_x: float, cad_y: float) -> tuple[float, float]:
        return (bx0 + (cad_x - FRAME_X0) * b_scale_x,
                by0 + (FRAME_Y1 - cad_y) * b_scale_y)

    def px_to_mm(px: float, py: float) -> tuple[float, float]:
        return (FRAME_X0 + (px - bx0) / b_scale_x,
                FRAME_Y1 - (py - by0) / b_scale_y)

    def build_matrix(dx: float, dy: float):
        return (1.0 / scale_x, 0.0, -(off_x + dx) / scale_x,
                0.0, 1.0 / scale_y, -(off_y + dy) / scale_y)

    def crop_matrix(mat, cx0: float, cy0: float):
        a, b, c, d, e, f = mat
        return (a, b, a * cx0 + b * cy0 + c, d, e, d * cx0 + e * cy0 + f)

    # サブピクセル位置合わせ（本体列の文字領域で探索）
    sx0, sy0 = cad_to_px(15.0, 285.0)
    sx1, sy1 = cad_to_px(220.0, 20.0)
    search_box = (int(sx0), int(sy0), int(sx1), int(sy1))
    search_size = (search_box[2] - search_box[0], search_box[3] - search_box[1])
    b_search = img_b.crop(search_box)
    best_dx, best_dy, best_frac = 0.0, 0.0, None
    for dx in np.arange(-3.0, 3.01, 0.5):
        for dy in np.arange(-3.0, 3.01, 0.5):
            mat = crop_matrix(build_matrix(dx, dy), search_box[0], search_box[1])
            a_search = img_a_raw.transform(search_size, Image.AFFINE, mat,
                                           resample=Image.BILINEAR, fillcolor=(255, 255, 255))
            frac, _, _ = blurred_diff_metrics(b_search, a_search)
            if best_frac is None or frac < best_frac:
                best_frac, best_dx, best_dy = frac, dx, dy
    print(f"  サブピクセル微調整: dx={best_dx:.2f} dy={best_dy:.2f} px（探索領域frac_bad={best_frac:.5f}）")

    img_a = img_a_raw.transform(img_b.size, Image.AFFINE, build_matrix(best_dx, best_dy),
                                resample=Image.BICUBIC, fillcolor=(255, 255, 255))
    img_a.save(out / "a_aligned.png")

    frac_all, mean_all, diff_all = blurred_diff_metrics(img_b, img_a)
    print(f"\n全ページ: frac_bad={frac_all:.5f} mean={mean_all:.3f}")

    W, H = img_b.size
    side = Image.new("RGB", (W, H * 2 + 8), "white")
    side.paste(img_b, (0, 0))
    side.paste(img_a, (0, H + 8))
    side.save(out / "side_by_side.png")
    Image.eval(diff_all, lambda v: 255 - v).convert("RGB").save(out / "diff_full.png")

    # 行定義（anchors.tsv: row type anchorX1 anchorY1 anchorX2 anchorY2 rowY）
    rows = []
    with open(anchors_path, "r", encoding="utf-8") as fp:
        fp.readline()
        for line in fp:
            line = line.rstrip("\n")
            if not line:
                continue
            r, t, x1, y1, x2, y2, rowy = line.split("\t")
            rows.append({"row": int(r), "type": t, "anchorX1": float(x1), "anchorY1": float(y1),
                         "anchorX2": float(x2) if x2 else None, "anchorY2": float(y2) if y2 else None,
                         "rowY": float(rowy)})

    results = []
    print(f"\n{'行':>3} {'Justify':<14} {'異画素率':>9} {'平均差':>7} "
          f"{'dxL(mm)':>8} {'dxR(mm)':>8} {'dyT(mm)':>8} {'dyB(mm)':>8}  判定")
    for r in rows:
        t = r["type"]
        band = BAND_BY_TYPE[t]
        xr = XRANGE_BY_TYPE.get(t, XRANGE_DEFAULT)
        y0mm = r["rowY"] + band[0]
        y1mm = r["rowY"] + band[1]
        pxa, pya = cad_to_px(xr[0], y1mm)
        pxb, pyb = cad_to_px(xr[1], y0mm)
        box = (max(0, int(pxa)), max(0, int(pya)), min(W, int(pxb)), min(H, int(pyb)))

        b_c = img_b.crop(box)
        a_c = img_a.crop(box)
        frac, mean, d = blurred_diff_metrics(b_c, a_c)

        bb = ink_bbox_mm(img_b, box, px_to_mm)
        ba = ink_bbox_mm(img_a, box, px_to_mm)
        if bb and ba:
            dxl = ba["left"] - bb["left"]
            dxr = ba["right"] - bb["right"]
            dyt = ba["top"] - bb["top"]        # CAD mm（上端。正=構造SVG書き出しが上へ）
            dyb = ba["bottom"] - bb["bottom"]
        else:
            dxl = dxr = dyt = dyb = float("nan")

        # 判定: アンカー側の端（左端・ベースライン近傍）を主指標に、両端 0.4mm 以内をずれなしとする
        shift_ok = (bb and ba and abs(dxl) <= SHIFT_FLAG_MM and abs(dyt) <= SHIFT_FLAG_MM
                    and abs(dyb) <= SHIFT_FLAG_MM)
        pix_ok = frac <= FRAC_FLAG and mean <= MEAN_FLAG
        verdict = "ずれなし" if shift_ok else "要確認"
        flag = not (shift_ok and pix_ok)
        results.append({"row": r["row"], "type": t, "frac_bad": round(frac, 5),
                        "mean": round(mean, 3),
                        "dx_left_mm": round(dxl, 3), "dx_right_mm": round(dxr, 3),
                        "dy_top_mm": round(dyt, 3), "dy_bottom_mm": round(dyb, 3),
                        "ink_b": bb, "ink_a": ba, "verdict": verdict, "flag": flag})
        mark = " <== 要目視" if flag else ""
        print(f"{r['row']:>3} {t:<14} {frac:>9.5f} {mean:>7.3f} "
              f"{dxl:>8.3f} {dxr:>8.3f} {dyt:>8.3f} {dyb:>8.3f}  {verdict}{mark}")

        if flag:
            w, h = b_c.size
            if w > 0 and h > 0:
                strip = Image.new("RGB", (w, h * 3 + 8), "white")
                strip.paste(b_c, (0, 0))
                strip.paste(a_c, (0, h + 4))
                strip.paste(Image.eval(d, lambda v: 255 - v).convert("RGB"), (0, 2 * h + 8))
                strip.save(out / f"row_{r['row']:02d}_{re.sub(r'[^A-Za-z0-9-]', '_', t)}.png")

    (out / "results.json").write_text(
        json.dumps({"page": {"frac_bad": frac_all, "mean": mean_all},
                    "frame_b_px": b_bbox, "frame_a_px": a_bbox,
                    "affine": {"scale_x": scale_x, "scale_y": scale_y,
                               "off_x": off_x, "off_y": off_y,
                               "subpx_dx": best_dx, "subpx_dy": best_dy},
                    "rows": results},
                   ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n保存: {out / 'results.json'}, side_by_side.png, diff_full.png, row_*.png（要目視行のみ）")


if __name__ == "__main__":
    main()
