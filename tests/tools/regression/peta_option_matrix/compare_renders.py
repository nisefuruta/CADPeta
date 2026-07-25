"""レンダラ横断（PDFium／GDI／Chromium）の画像比較器。

PETA の E2E 総点検で、正解 PDF のレンダと EMF/SVG のレンダを機械判定する。全キャンバス
差分率だけの判定は「全ケースが同じ誤りを共有しても通る」ため使わず、前景 bbox・ROI ごとの
前景 recall / precision / IoU で判定する（負例は tests/test_compare_renders.py で確実に
FAIL することを証明済み）。

2 つの動作モードを持つ。

  Aモード（既定）: 画像 2 枚（正解 a・比較対象 b）の構造比較。
      前景マスクを作り、±探索窓で非循環（パディング）シフトを総当たりして最良位置を求め、
      その位置での bbox 一致・全体 recall / precision / IoU・ROI ごとの recall / precision /
      IoU を判定する。

  Bモード（--expect-color）: 画像 1 枚（対象）の ROI 色判定。
      半透明合成などの解析的判定用に、ROI 内の全画素（または前景画素のみ）が期待 RGB に
      チャネル許容誤差内で一致するかを判定する。

使い方:
    uv run --project tests/tools/regression/peta_option_matrix \
        python tests/tools/regression/peta_option_matrix/compare_renders.py \
        correct.pdf candidate.png --dpi 150 --roi 0,0,100,50

    uv run --project tests/tools/regression/peta_option_matrix \
        python tests/tools/regression/peta_option_matrix/compare_renders.py \
        --expect-color '#FF8040' --color-tolerance 8 --roi 10,10,40,30 blended.png

終了コード: 0=PASS、1=FAIL、2=実行できない（引数・寸法の不整合など）。
標準出力へ全指標を JSON で出す（呼び出し側が機械で読む）。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# Windows の既定コードページのままだと日本語が化けて結果を読めない（この repo の実測則）
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

import numpy as np
import pypdfium2 as pdfium
from PIL import Image

MM_PER_INCH = 25.4


# --------------------------------------------------------------------------
# 画像読み込み・前景マスク
# --------------------------------------------------------------------------


def load_image(path: Path, dpi: int, page: int) -> Image.Image:
    """PNG/PDF を RGB 画像として読み込む。PDF は固定 DPI でレンダする。

    アルファを持つ画像は白背景へ合成する（透過は「白紙に印刷した見え方」で比較するため）。
    """
    if path.suffix.lower() == ".pdf":
        doc = pdfium.PdfDocument(str(path))
        try:
            scale = dpi / 72.0  # PDF の既定ユーザ空間は 1/72 インチ
            img = doc[page].render(scale=scale).to_pil()
        finally:
            doc.close()
    else:
        img = Image.open(path)
        img.load()

    if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
        img = img.convert("RGBA")
        bg = Image.new("RGB", img.size, (255, 255, 255))
        bg.paste(img, mask=img.split()[-1])
        img = bg
    else:
        img = img.convert("RGB")
    return img


def foreground_mask(img: Image.Image, threshold: int) -> np.ndarray:
    """背景（白）から閾値で前景マスクを作る（グレースケール値 < threshold を前景とする）。"""
    gray = np.asarray(img.convert("L"), dtype=np.uint8)
    return gray < threshold


def dilate(mask: np.ndarray, radius: int) -> np.ndarray:
    """正方形構造要素（辺 2*radius+1）で二値膨張する。アンチエイリアス差の吸収用。"""
    if radius <= 0:
        return mask
    h, w = mask.shape
    out = mask.copy()
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            if dx == 0 and dy == 0:
                continue
            out |= shift_array(mask, dx, dy)
    return out


def shift_array(mask: np.ndarray, dx: int, dy: int) -> np.ndarray:
    """非循環（パディング）シフト。範囲外へ出た画素は失われ、新たに入る領域は False で埋める。"""
    h, w = mask.shape
    out = np.zeros_like(mask)
    src_x0, src_x1 = max(0, -dx), w - max(0, dx)
    src_y0, src_y1 = max(0, -dy), h - max(0, dy)
    dst_x0, dst_x1 = max(0, dx), w - max(0, -dx)
    dst_y0, dst_y1 = max(0, dy), h - max(0, -dy)
    if src_x0 < src_x1 and src_y0 < src_y1:
        out[dst_y0:dst_y1, dst_x0:dst_x1] = mask[src_y0:src_y1, src_x0:src_x1]
    return out


# --------------------------------------------------------------------------
# シフト探索・指標
# --------------------------------------------------------------------------


def search_best_shift(mask_a: np.ndarray, mask_b: np.ndarray, search: int) -> tuple[int, int, float]:
    """mask_b を dx,dy シフトしたときに mask_a との IoU が最大になる位置を総当たりで探す。"""
    best_dx, best_dy, best_iou = 0, 0, -1.0
    for dy in range(-search, search + 1):
        for dx in range(-search, search + 1):
            shifted = shift_array(mask_b, dx, dy)
            iou = compute_iou(mask_a, shifted)
            if iou > best_iou:
                best_dx, best_dy, best_iou = dx, dy, iou
    return best_dx, best_dy, best_iou


def compute_iou(a: np.ndarray, b: np.ndarray) -> float:
    inter = int(np.count_nonzero(a & b))
    union = int(np.count_nonzero(a | b))
    return 1.0 if union == 0 else inter / union


def compute_recall_precision(a: np.ndarray, b: np.ndarray) -> tuple[float, float]:
    """a=正解の前景, b=比較対象の前景。recall=a に対する再現率、precision=b に対する適合率。"""
    inter = int(np.count_nonzero(a & b))
    a_count = int(np.count_nonzero(a))
    b_count = int(np.count_nonzero(b))
    recall = 1.0 if a_count == 0 else inter / a_count
    precision = 1.0 if b_count == 0 else inter / b_count
    return recall, precision


def judge_region(
    sub_a_raw: np.ndarray, sub_a: np.ndarray, sub_b: np.ndarray, args: argparse.Namespace
) -> dict:
    """recall/precision/iou を計算し、疎な前景（線・文字などの輪郭のみで塗りつぶしがない領域）向けの
    precision 緩和つきで pass 判定する。

    背景（実測で判明した事実）: 前景が輪郭主体で疎な領域（例: 単線の矩形、寸法値の文字）は、
    レンダラ間のアンチエイリアス縁の太さの差が前景画素数に対して大きな割合を占め、内容が目視で
    完全一致していても precision だけが数%〜十数%不足して不合格になる（面積主体の密な前景では
    同じ絶対的な縁幅でも比率としては無視できる）。dilate を上げて縁の許容を広げる対策は密な前景の
    誤検出感度も一緒に下げてしまうため、疎密で precision の下限を変える方が的確。
    sparse 判定は膨張前(sub_a_raw)の前景率で行う（膨張後は疎な内容ほど相対的に大きく膨らみ、
    密な内容との差が縮んで判定に使いにくいため）。recall/iouの下限は疎密で変えない
    （疎な領域でも欠落・過剰な余剰内容はrecall/iouで検出できる実測が取れている）。"""
    recall, precision = compute_recall_precision(sub_a, sub_b)
    iou = compute_iou(sub_a, sub_b)
    fg_ratio_a = float(np.count_nonzero(sub_a_raw)) / sub_a_raw.size if sub_a_raw.size else 0.0
    sparse = fg_ratio_a < args.sparse_fg_ratio
    min_precision = min(args.min_precision, args.min_precision_sparse) if sparse else args.min_precision
    is_pass = recall >= args.min_recall and precision >= min_precision and iou >= args.min_iou
    return {
        "recall": recall,
        "precision": precision,
        "iou": iou,
        "sparse": sparse,
        "foregroundRatioA": fg_ratio_a,
        "minPrecisionApplied": min_precision,
        "pass": is_pass,
    }


def bbox_of(mask: np.ndarray) -> tuple[int, int, int, int] | None:
    """前景の外接矩形を (x0,y0,x1,y1) で返す（x1,y1 は排他的）。前景なしは None。"""
    if not mask.any():
        return None
    ys, xs = np.nonzero(mask)
    return int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1


def roi_to_px(roi_mm: tuple[float, float, float, float], dpi: int, shape: tuple[int, int]) -> tuple[int, int, int, int]:
    """ROI(mm, 画像左上原点)を画素矩形へ変換し画像範囲へクランプする。"""
    h, w = shape
    px_per_mm = dpi / MM_PER_INCH
    x0, y0, x1, y1 = roi_mm
    px0 = max(0, min(w, round(x0 * px_per_mm)))
    py0 = max(0, min(h, round(y0 * px_per_mm)))
    px1 = max(0, min(w, round(x1 * px_per_mm)))
    py1 = max(0, min(h, round(y1 * px_per_mm)))
    return px0, py0, px1, py1


def parse_roi(text: str) -> tuple[float, float, float, float]:
    parts = text.split(",")
    if len(parts) != 4:
        raise argparse.ArgumentTypeError(f"--roi は x0,y0,x1,y1(mm) の4値: {text!r}")
    try:
        x0, y0, x1, y1 = (float(p) for p in parts)
    except ValueError as e:
        raise argparse.ArgumentTypeError(f"--roi の数値変換に失敗: {text!r} ({e})") from e
    if x1 <= x0 or y1 <= y0:
        raise argparse.ArgumentTypeError(f"--roi は x1>x0 かつ y1>y0 が必要: {text!r}")
    return x0, y0, x1, y1


def parse_color(text: str) -> tuple[int, int, int]:
    s = text.strip().lstrip("#")
    if len(s) != 6:
        raise argparse.ArgumentTypeError(f"--expect-color は #RRGGBB 形式: {text!r}")
    try:
        return int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16)
    except ValueError as e:
        raise argparse.ArgumentTypeError(f"--expect-color の16進変換に失敗: {text!r} ({e})") from e


# --------------------------------------------------------------------------
# Aモード: 構造比較
# --------------------------------------------------------------------------


def run_mode_a(args: argparse.Namespace) -> int:
    path_a, path_b = args.images
    img_a = load_image(path_a, args.dpi, args.page_a)
    img_b = load_image(path_b, args.dpi, args.page_b)
    if img_a.size != img_b.size:
        result = {
            "mode": "A",
            "error": f"寸法が違う a={img_a.size} b={img_b.size}（同寸で描画する契約）",
        }
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 2

    w, h = img_a.size
    px_per_mm = args.dpi / MM_PER_INCH
    mm_per_px = MM_PER_INCH / args.dpi

    mask_a_raw = foreground_mask(img_a, args.threshold)
    mask_b_raw = foreground_mask(img_b, args.threshold)
    mask_a = dilate(mask_a_raw, args.dilate)
    mask_b_raw = dilate(mask_b_raw, args.dilate)

    # 正解(a)側が空白（前景ゼロ近傍）だと、ROI判定は「空白 vs 空白」で recall/precision が
    # 分母ゼロにより自動的に1.0となり、クロップ座標の誤りなどで両方とも空白でも見かけ上PASSして
    # しまう（実測: 切出し座標バグでこれが起きた）。判定不能として exit 2 で止め、空白同士のPASSを許さない
    fg_a_count = int(np.count_nonzero(mask_a))
    if fg_a_count < args.min_foreground_px:
        result = {
            "mode": "A",
            "error": (
                f"正解(a)の前景画素が少なすぎて判定不能（{fg_a_count}px < 閾値{args.min_foreground_px}px）。"
                "空白同士のPASSを許さない（クロップ座標の誤りを疑うこと）"
            ),
            "foregroundPxA": fg_a_count,
            "minForegroundPx": args.min_foreground_px,
        }
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 2

    dx, dy, _shift_iou = search_best_shift(mask_a, mask_b_raw, args.search)
    mask_b = shift_array(mask_b_raw, dx, dy)

    shift_mm = (dx * mm_per_px, dy * mm_per_px)
    shift_magnitude_mm = float(np.hypot(*shift_mm))
    shift_pass = shift_magnitude_mm <= args.max_shift_mm

    bbox_a = bbox_of(mask_a)
    bbox_b = bbox_of(mask_b)
    if bbox_a is None or bbox_b is None:
        edge_diff_mm = None
        max_edge_diff_mm = None
        bbox_pass = bbox_a is None and bbox_b is None
    else:
        edges = [abs(bbox_a[i] - bbox_b[i]) * mm_per_px for i in range(4)]
        edge_diff_mm = {
            "left": edges[0],
            "top": edges[1],
            "right": edges[2],
            "bottom": edges[3],
        }
        max_edge_diff_mm = max(edges)
        bbox_pass = max_edge_diff_mm <= args.max_bbox_mm

    overall_judge = judge_region(mask_a_raw, mask_a, mask_b, args)
    recall, precision, iou = overall_judge["recall"], overall_judge["precision"], overall_judge["iou"]
    overall_pass = overall_judge["pass"]

    roi_results = []
    roi_all_pass = True
    for roi_mm in args.roi or []:
        px0, py0, px1, py1 = roi_to_px(roi_mm, args.dpi, (h, w))
        sub_a_raw = mask_a_raw[py0:py1, px0:px1]  # 疎密判定は非シフトのa基準（aは動かしていない）
        sub_a = mask_a[py0:py1, px0:px1]
        sub_b = mask_b[py0:py1, px0:px1]
        r_judge = judge_region(sub_a_raw, sub_a, sub_b, args)
        roi_all_pass = roi_all_pass and r_judge["pass"]
        roi_results.append(
            {
                "rect_mm": list(roi_mm),
                "rect_px": [px0, py0, px1, py1],
                "recall": r_judge["recall"],
                "precision": r_judge["precision"],
                "iou": r_judge["iou"],
                "sparse": r_judge["sparse"],
                "foregroundRatioA": r_judge["foregroundRatioA"],
                "minPrecisionApplied": r_judge["minPrecisionApplied"],
                "pass": r_judge["pass"],
            }
        )

    overall = shift_pass and bbox_pass and overall_pass and roi_all_pass

    result = {
        "mode": "A",
        "images": {"a": str(path_a), "b": str(path_b)},
        "dpi": args.dpi,
        "size_px": [w, h],
        "threshold": args.threshold,
        "dilate": args.dilate,
        "shift": {
            "search_window_px": args.search,
            "dx_px": dx,
            "dy_px": dy,
            "dx_mm": shift_mm[0],
            "dy_mm": shift_mm[1],
            "magnitude_mm": shift_magnitude_mm,
            "threshold_mm": args.max_shift_mm,
            "pass": shift_pass,
        },
        "bbox": {
            "a_px": list(bbox_a) if bbox_a else None,
            "b_shifted_px": list(bbox_b) if bbox_b else None,
            "edge_diff_mm": edge_diff_mm,
            "max_diff_mm": max_edge_diff_mm,
            "threshold_mm": args.max_bbox_mm,
            "pass": bbox_pass,
        },
        "overall": {
            "recall": recall,
            "precision": precision,
            "iou": iou,
            "min_recall": args.min_recall,
            "min_precision": args.min_precision,
            "min_iou": args.min_iou,
            "sparse": overall_judge["sparse"],
            "foregroundRatioA": overall_judge["foregroundRatioA"],
            "minPrecisionApplied": overall_judge["minPrecisionApplied"],
            "pass": overall_pass,
        },
        "roi": roi_results,
        "pass": overall,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if overall else 1


# --------------------------------------------------------------------------
# Bモード: ROI 色判定
# --------------------------------------------------------------------------


def run_mode_b(args: argparse.Namespace) -> int:
    (path,) = args.images
    img = load_image(path, args.dpi, args.page_a)
    arr = np.asarray(img, dtype=np.int16)  # (h, w, 3)
    h, w = arr.shape[0], arr.shape[1]
    expect = np.array(args.expect_color, dtype=np.int16)

    fg_mask = foreground_mask(img, args.threshold) if args.expect_color_fg_only else None

    roi_results = []
    all_pass = True
    for roi_mm in args.roi:
        px0, py0, px1, py1 = roi_to_px(roi_mm, args.dpi, (h, w))
        region = arr[py0:py1, px0:px1, :]
        diff = np.abs(region - expect)
        within = np.all(diff <= args.color_tolerance, axis=-1)

        if args.expect_color_fg_only:
            fg_region = fg_mask[py0:py1, px0:px1]
            checked = fg_region
        else:
            checked = np.ones(region.shape[:2], dtype=bool)

        checked_count = int(np.count_nonzero(checked))
        bad_count = int(np.count_nonzero(checked & ~within))
        max_channel_diff = int(diff[checked].max()) if checked_count else 0
        roi_pass = bad_count == 0
        all_pass = all_pass and roi_pass
        roi_results.append(
            {
                "rect_mm": list(roi_mm),
                "rect_px": [px0, py0, px1, py1],
                "checked_px": checked_count,
                "bad_px": bad_count,
                "max_channel_diff": max_channel_diff,
                "pass": roi_pass,
            }
        )

    result = {
        "mode": "B",
        "image": str(path),
        "dpi": args.dpi,
        "size_px": [w, h],
        "expect_color": "#%02X%02X%02X" % tuple(args.expect_color),
        "color_tolerance": args.color_tolerance,
        "fg_only": args.expect_color_fg_only,
        "roi": roi_results,
        "pass": all_pass,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if all_pass else 1


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(
        description="レンダラ横断（PDFium/GDI/Chromium）の画像比較器。"
        "Aモード=2画像の構造比較（既定）、Bモード(--expect-color)=1画像のROI色判定。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument(
        "images",
        nargs="+",
        type=Path,
        help="比較する画像/PDF。Aモードは2つ(正解 比較対象)、Bモードは1つ(対象)",
    )
    ap.add_argument("--dpi", type=int, default=150, help="PDF描画DPI（既定150）")
    ap.add_argument("--page-a", type=int, default=0, help="images[0]がPDFのときのページ番号（既定0）")
    ap.add_argument("--page-b", type=int, default=0, help="images[1]がPDFのときのページ番号（既定0、Aモードのみ）")
    ap.add_argument("--threshold", type=int, default=250, help="前景判定の閾値。グレースケール値がこれ未満を前景とする（既定250）")
    ap.add_argument("--search", type=int, default=8, help="シフト探索窓（画素、既定8）")
    ap.add_argument("--dilate", type=int, default=1, help="前景マスクの膨張半径（画素、既定1。0で無効）")
    ap.add_argument("--max-shift-mm", type=float, default=0.2, help="シフト量の許容上限mm（既定0.2）")
    ap.add_argument("--max-bbox-mm", type=float, default=0.5, help="前景bbox各辺のずれ許容上限mm（既定0.5）")
    ap.add_argument("--min-iou", type=float, default=0.90, help="前景IoUの合格下限（既定0.90）")
    ap.add_argument(
        "--min-foreground-px", type=int, default=50,
        help="正解(a)側の前景画素数の下限（既定50）。未満なら空白同士のPASSを許さず exit 2（判定不能）とする",
    )
    ap.add_argument("--min-recall", type=float, default=0.95, help="前景recallの合格下限（既定0.95）")
    ap.add_argument("--min-precision", type=float, default=0.95, help="前景precisionの合格下限（既定0.95）")
    ap.add_argument(
        "--sparse-fg-ratio", type=float, default=0.10,
        help="正解(a)側の膨張前・前景率がこれ未満の領域を「疎（輪郭主体）」とみなす（既定0.10）。"
        "疎な領域は --min-precision-sparse を使う（レンダラ間のアンチエイリアス縁差が相対的に大きく出るため）",
    )
    ap.add_argument(
        "--min-precision-sparse", type=float, default=0.85,
        help="疎な領域でのprecision合格下限（既定0.85）。--min-precisionより緩いがrecall/iouの下限は変えない",
    )
    ap.add_argument(
        "--roi",
        action="append",
        type=parse_roi,
        default=None,
        help="判定対象ROI(mm、画像左上原点) x0,y0,x1,y1。複数指定可。Bモードでは必須",
    )
    ap.add_argument("--expect-color", type=parse_color, default=None, help="Bモードを有効化: 期待RGB(#RRGGBB)")
    ap.add_argument("--color-tolerance", type=int, default=10, help="Bモードのチャネルごと許容誤差（既定10）")
    ap.add_argument(
        "--expect-color-fg-only",
        action="store_true",
        help="Bモード: ROI全画素でなく前景画素のみ判定する",
    )
    return ap


def main(argv: list[str] | None = None) -> int:
    ap = build_parser()
    args = ap.parse_args(argv)

    for p in args.images:
        if not p.is_file():
            print(f"実行できない: ファイルがない {p}", file=sys.stderr)
            return 2

    if args.expect_color is not None:
        if len(args.images) != 1:
            print("実行できない: Bモード(--expect-color)は画像を1つだけ渡す", file=sys.stderr)
            return 2
        if not args.roi:
            print("実行できない: Bモードは --roi が1つ以上必要", file=sys.stderr)
            return 2
        return run_mode_b(args)

    if len(args.images) != 2:
        print("実行できない: Aモードは画像を2つ(正解 比較対象)渡す", file=sys.stderr)
        return 2
    return run_mode_a(args)


if __name__ == "__main__":
    sys.exit(main())
