# -*- coding: utf-8 -*-
"""SHX 解釈器（CadPeta.Glyphs.Shx）の輪郭化結果と AutoCAD 実機レンダ（プロットPDF）を
機械比較する。

処理の流れ:
  1. 正解PDF（AutoCAD -PLOT 出力）と プローブSVG（ShxGlyphProbe 出力）を同一 DPI で PNG 化する
  2. 両PNGから見当枠（最外郭の黒矩形）の bbox を検出し、プローブ側を正解側へアフィン
     （平行移動＋軸別スケール）で位置合わせする
  3. 全ページ・行ごと・文字セルごと（cells.tsv の座標を見当枠経由で画素へ換算）に
     被覆率を比較し、低被覆行・低被覆セルを列挙する
  4. 並置比較PNG（上=正解、下=プローブ）と重ね合わせ差分PNGを保存する

使い方:
  uv run python compare_glyphs.py <正解PDF> <プローブSVG> <cells.tsv> <出力先ディレクトリ> [dpi=150]
"""
import json
import pathlib
import subprocess
import sys
import re

import numpy as np
import pypdfium2 as pdfium
from PIL import Image, ImageChops, ImageFilter

BLUR = 1.5          # AAずれ許容のための前処理ぼかし半径(px)（compare_tiles.py と同一方式）
DIFF_TH = 40         # 「異なる画素」とみなす階調差（ぼかし後）
FRAC_FLAG = 0.01      # 行/セルの異画素率がこれを超えたら要目視（文字は線が疎なので compare_tiles.py より緩める）
MEAN_FLAG = 3.0       # 行/セルの平均差がこれを超えたら要目視

DARK_THRESHOLD = 128   # 見当枠検出: これ未満のグレースケール値を「黒」とみなす
FRAME_RUN_RATIO = 0.5  # 見当枠検出: 行/列の最大連続黒画素数がこの比率を超えたら枠の辺とみなす

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

# 見当枠のCAD座標（build_glyph_chart.lsp・ShxGlyphProbe/Program.cs と共有する定数）
FRAME_X0, FRAME_Y0, FRAME_X1, FRAME_Y1 = 10.0, 10.0, 410.0, 287.0
PAPER_W_MM, PAPER_H_MM = 420.0, 297.0


def render_pdf(pdf_path: str, dpi: float) -> Image.Image:
    pdf = pdfium.PdfDocument(pdf_path)
    page = pdf[0]
    bitmap = page.render(scale=dpi / 72.0)
    return bitmap.to_pil().convert("RGB")


def render_svg_via_chrome(svg_path: str, dpi: float, out_png: str) -> Image.Image:
    text = pathlib.Path(svg_path).read_text(encoding="utf-8")
    m = re.search(r'width="([\d.]+)mm"\s+height="([\d.]+)mm"', text)
    if not m:
        raise ValueError(f"SVG の width/height(mm) が読み取れない: {svg_path}")
    w_mm, h_mm = float(m.group(1)), float(m.group(2))

    css_px_per_mm = 96.0 / 25.4
    scale_factor = dpi / 96.0
    css_w = round(w_mm * css_px_per_mm)
    css_h = round(h_mm * css_px_per_mm)

    svg_uri = pathlib.Path(svg_path).resolve().as_uri()
    subprocess.run([
        CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
        "--default-background-color=FFFFFFFF",
        f"--force-device-scale-factor={scale_factor}",
        f"--window-size={css_w},{css_h}",
        f"--screenshot={out_png}",
        svg_uri,
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return Image.open(out_png).convert("RGB")


def max_run_lengths(dark: np.ndarray, axis: int) -> np.ndarray:
    """dark[i, axis方向] の最大連続True run長を、axisと垂直な各インデックスごとに返す。
    axis=1: 各行(row)についてX方向の最大連続run（横辺=水平エッジの検出に使う）
    axis=0: 各列(col)についてY方向の最大連続run（縦辺=垂直エッジの検出に使う）"""
    if axis == 1:
        h, w = dark.shape
        run = np.zeros(h, dtype=np.int32)
        best = np.zeros(h, dtype=np.int32)
        for j in range(w):
            col = dark[:, j]
            run = np.where(col, run + 1, 0)
            np.maximum(best, run, out=best)
        return best
    else:
        h, w = dark.shape
        run = np.zeros(w, dtype=np.int32)
        best = np.zeros(w, dtype=np.int32)
        for i in range(h):
            row = dark[i, :]
            run = np.where(row, run + 1, 0)
            np.maximum(best, run, out=best)
        return best


def detect_frame_bbox(img: Image.Image) -> tuple[float, float, float, float]:
    """見当枠（最外郭の黒矩形）の画素bboxを検出する（x0,y0,x1,y1）。"""
    gray = np.asarray(img.convert("L"))
    dark = gray < DARK_THRESHOLD
    h, w = dark.shape

    row_runs = max_run_lengths(dark, axis=1)  # 各行の最大連続横ラン
    col_runs = max_run_lengths(dark, axis=0)  # 各列の最大連続縦ラン

    horiz_rows = np.where(row_runs > FRAME_RUN_RATIO * w)[0]
    vert_cols = np.where(col_runs > FRAME_RUN_RATIO * h)[0]
    if len(horiz_rows) == 0 or len(vert_cols) == 0:
        raise ValueError("見当枠が検出できない（水平/垂直の長い黒ランが見つからない）")

    return float(vert_cols.min()), float(horiz_rows.min()), float(vert_cols.max()), float(horiz_rows.max())


def metrics(a: Image.Image, b: Image.Image):
    ab = a.filter(ImageFilter.GaussianBlur(BLUR))
    bb = b.filter(ImageFilter.GaussianBlur(BLUR))
    d = ImageChops.difference(ab, bb).convert("L")
    hist = d.histogram()
    n = sum(hist)
    if n == 0:
        return 0.0, 0.0, d
    bad = sum(hist[DIFF_TH:])
    mean = sum(i * v for i, v in enumerate(hist)) / n
    return bad / n, mean, d


def main():
    pdf_path, svg_path, cells_path, out_dir_s = sys.argv[1:5]
    dpi = float(sys.argv[5]) if len(sys.argv) > 5 else 150.0
    out = pathlib.Path(out_dir_s)
    out.mkdir(parents=True, exist_ok=True)

    print(f"正解PDFを{dpi}dpiでレンダ: {pdf_path}")
    gt = render_pdf(pdf_path, dpi)
    gt.save(out / "gt.png")

    print(f"プローブSVGを{dpi}dpiでレンダ(Chrome): {svg_path}")
    probe_raw = render_svg_via_chrome(svg_path, dpi, str(out / "probe_raw.png"))

    print("見当枠を検出中...")
    gt_bbox = detect_frame_bbox(gt)
    probe_bbox = detect_frame_bbox(probe_raw)
    print(f"  正解側 bbox(px)  = {gt_bbox}")
    print(f"  プローブ側 bbox(px) = {probe_bbox}")

    gx0, gy0, gx1, gy1 = gt_bbox
    px0, py0, px1, py1 = probe_bbox
    scale_x = (gx1 - gx0) / (px1 - px0)
    scale_y = (gy1 - gy0) / (py1 - py0)
    off_x = gx0 - px0 * scale_x
    off_y = gy0 - py0 * scale_y
    print(f"  affine: scale=({scale_x:.5f},{scale_y:.5f}) offset=({off_x:.2f},{off_y:.2f})")

    # CAD(mm) → 正解画像の画素 への変換（検出した見当枠bboxを基準にする）
    gt_scale_x = (gx1 - gx0) / (FRAME_X1 - FRAME_X0)
    gt_scale_y = (gy1 - gy0) / (FRAME_Y1 - FRAME_Y0)

    def cad_to_px(cad_x: float, cad_y: float) -> tuple[float, float]:
        px = gx0 + (cad_x - FRAME_X0) * gt_scale_x
        py = gy0 + (FRAME_Y1 - cad_y) * gt_scale_y
        return px, py

    # 見当枠bboxの検出は画素単位（±1px）のため、そのままだと辺の位置に
    # 1px弱のずれが残り、AA縁で「本物の差」ではないノイズが乗る。
    # 文字が並ぶ領域（見当枠内側、build_glyph_chart.lsp のレイアウト定数と対応）を
    # フル解像度のまま切り出し、平行移動オフセットを微調整（サブピクセル探索）してから
    # 本番の変換に適用する（縮小画像で探索すると細い線が潰れて偽の最小値に落ちるため使わない）。
    def build_matrix(dx: float, dy: float):
        a, b, c = 1.0 / scale_x, 0.0, -(off_x + dx) / scale_x
        d, e, f = 0.0, 1.0 / scale_y, -(off_y + dy) / scale_y
        return (a, b, c, d, e, f)

    def crop_matrix(mat, crop_x0: float, crop_y0: float):
        # 宛先を crop 領域相対の座標系にする（宛先(X,Y)の元のgt座標は(X+crop_x0, Y+crop_y0)）。
        a, b, c, d, e, f = mat
        return (a, b, a * crop_x0 + b * crop_y0 + c, d, e, d * crop_x0 + e * crop_y0 + f)

    content_x0, content_y0 = cad_to_px(15.0, 283.0)
    content_x1, content_y1 = cad_to_px(405.0, 90.0)
    search_box = (int(content_x0), int(content_y0), int(content_x1), int(content_y1))
    search_size = (search_box[2] - search_box[0], search_box[3] - search_box[1])
    gt_search = gt.crop(search_box)

    best_dx, best_dy, best_frac = 0.0, 0.0, None
    for dx in np.arange(-3.0, 3.01, 0.5):
        for dy in np.arange(-3.0, 3.01, 0.5):
            mat = crop_matrix(build_matrix(dx, dy), search_box[0], search_box[1])
            probe_search = probe_raw.transform(search_size, Image.AFFINE, mat,
                                                resample=Image.BILINEAR, fillcolor=(255, 255, 255))
            frac, _, _ = metrics(gt_search, probe_search)
            if best_frac is None or frac < best_frac:
                best_frac, best_dx, best_dy = frac, dx, dy
    print(f"  サブピクセル微調整: dx={best_dx:.2f} dy={best_dy:.2f} px（探索領域frac_bad={best_frac:.5f}）")

    probe = probe_raw.transform(gt.size, Image.AFFINE, build_matrix(best_dx, best_dy),
                                 resample=Image.BICUBIC, fillcolor=(255, 255, 255))
    probe.save(out / "probe_aligned.png")

    # 全ページ
    frac_all, mean_all, diff_all = metrics(gt, probe)
    print(f"\n全ページ: frac_bad={frac_all:.5f} mean={mean_all:.3f}")

    W, H = gt.size
    side = Image.new("RGB", (W, H * 2 + 8), "white")
    side.paste(gt, (0, 0))
    side.paste(probe, (0, H + 8))
    side.save(out / "side_by_side.png")
    Image.eval(diff_all, lambda v: 255 - v).convert("RGB").save(out / "diff_full.png")

    # 配置セルの読み込み（cells.tsv: row, charIndex, char, x0Mm, x1Mm, yCadMm, heightMm）
    cells: list[dict] = []
    with open(cells_path, "r", encoding="utf-8") as fp:
        header = fp.readline()
        for line in fp:
            line = line.rstrip("\n")
            if not line:
                continue
            row, ci, ch, x0, x1, ycad, hmm = line.split("\t")
            cells.append({
                "row": int(row), "charIndex": int(ci), "char": ch,
                "x0Mm": float(x0), "x1Mm": float(x1),
                "yCadMm": float(ycad), "heightMm": float(hmm),
            })

    rows_idx = sorted(set(c["row"] for c in cells))
    row_results = []
    cell_results = []

    for r in rows_idx:
        row_cells = [c for c in cells if c["row"] == r]
        x0mm = min(c["x0Mm"] for c in row_cells) - 2.0
        x1mm = max(c["x1Mm"] for c in row_cells) + 2.0
        ycad = row_cells[0]["yCadMm"]
        hmm = row_cells[0]["heightMm"]
        # 行帯: ベースライン下(降下部)4mm～キャップハイト上(オーバーシュート)3mmを含める
        y0mm = ycad - 4.0
        y1mm = ycad + hmm + 3.0

        pxa, pya = cad_to_px(x0mm, y1mm)  # 上端
        pxb, pyb = cad_to_px(x1mm, y0mm)  # 下端
        box = (int(pxa), int(pya), int(pxb), int(pyb))
        box = (max(0, box[0]), max(0, box[1]), min(W, box[2]), min(H, box[3]))

        gt_c = gt.crop(box)
        pr_c = probe.crop(box)
        frac, mean, d = metrics(gt_c, pr_c)
        flag = frac > FRAC_FLAG or mean > MEAN_FLAG
        row_results.append({"row": r, "text": "".join(c["char"] for c in row_cells),
                             "box": box, "frac_bad": round(frac, 5), "mean": round(mean, 3),
                             "flag": flag})
        if flag:
            w, h = gt_c.size
            if w > 0 and h > 0:
                strip = Image.new("RGB", (w, h * 3 + 8), "white")
                strip.paste(gt_c, (0, 0))
                strip.paste(pr_c, (0, h + 4))
                strip.paste(Image.eval(d, lambda v: 255 - v).convert("RGB"), (0, 2 * h + 8))
                strip.save(out / f"row_{r}.png")

            # 低被覆行は文字セル単位でも見る
            for c in row_cells:
                cy0 = c["yCadMm"] - 4.0
                cy1 = c["yCadMm"] + c["heightMm"] + 3.0
                cxa, cya = cad_to_px(c["x0Mm"] - 0.5, cy1)
                cxb, cyb = cad_to_px(c["x1Mm"] + 0.5, cy0)
                cbox = (max(0, int(cxa)), max(0, int(cya)), min(W, int(cxb)), min(H, int(cyb)))
                if cbox[2] <= cbox[0] or cbox[3] <= cbox[1]:
                    continue
                gt_cc = gt.crop(cbox)
                pr_cc = probe.crop(cbox)
                cfrac, cmean, _ = metrics(gt_cc, pr_cc)
                cell_results.append({"row": r, "charIndex": c["charIndex"], "char": c["char"],
                                      "frac_bad": round(cfrac, 5), "mean": round(cmean, 3),
                                      "flag": cfrac > FRAC_FLAG or cmean > MEAN_FLAG})

    flagged_rows = [x for x in row_results if x["flag"]]
    print(f"\n行判定: {len(row_results)}行中 要目視 {len(flagged_rows)}")
    print(f"{'行':>4} {'異画素率':>9} {'平均差':>7}  文字列")
    for x in row_results:
        mark = " <== 要目視" if x["flag"] else ""
        print(f"{x['row']:>4} {x['frac_bad']:>9.5f} {x['mean']:>7.3f}{mark}  {x['text']}")

    flagged_cells = sorted([c for c in cell_results if c["flag"]],
                            key=lambda c: -c["frac_bad"])
    if flagged_cells:
        print(f"\n低被覆セル（上位{min(20, len(flagged_cells))}件）:")
        for c in flagged_cells[:20]:
            print(f"  row={c['row']} char={c['charIndex']} '{c['char']}' "
                  f"frac_bad={c['frac_bad']:.5f} mean={c['mean']:.3f}")

    (out / "results.json").write_text(
        json.dumps({"page": {"frac_bad": frac_all, "mean": mean_all},
                    "frame_gt_px": gt_bbox, "frame_probe_px": probe_bbox,
                    "affine": {"scale_x": scale_x, "scale_y": scale_y, "off_x": off_x, "off_y": off_y},
                    "rows": row_results, "cells": cell_results},
                   ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n保存: {out / 'results.json'}, side_by_side.png, diff_full.png, row_*.png（要目視行のみ）")


if __name__ == "__main__":
    main()
