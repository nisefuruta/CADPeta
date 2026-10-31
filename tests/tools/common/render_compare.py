"""SVG・PDF の描画を画素で比べる試験が共有する処理。

compare_glyphs.py・compare_justify.py（cad-typography）と compare_irmap.py・compare_tiles.py（pdf-emf）が使う。
各試験は uv の別のプロジェクトなので、依存は Pillow だけにし、numpy は見当枠を探す関数の中で読み込む
（pdf-emf のプロジェクトは numpy を持たない）。要目視とする閾値は試験ごとに違うので、各試験に置く。

使い方（各試験のスクリプト）:
    sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[<n>] / "common"))
    from render_compare import blurred_diff_metrics
"""

from __future__ import annotations

import pathlib
import re
import subprocess

from PIL import Image, ImageChops, ImageFilter

BLUR = 1.5             # アンチエイリアスのずれを許すための前処理のぼかしの半径(px)
DIFF_TH = 40           # ぼかした後に「異なる画素」とみなす階調の差
DARK_THRESHOLD = 128   # 見当枠の検出: これ未満のグレースケールの値を「黒」とみなす
FRAME_RUN_RATIO = 0.5  # 見当枠の検出: 行・列の最大の連続した黒の画素数が、幅・高さのこの比率を超えたら枠の辺とみなす

from chrome_path import CHROME  # noqa: E402  同じフォルダ（呼び出し側が sys.path に入れる）
CSS_PX_PER_INCH = 96.0
MM_PER_INCH = 25.4


def render_svg_via_chrome(svg_path: str, dpi: float, out_png: str) -> Image.Image:
    """SVG の width/height(mm) の大きさで、Chrome のヘッドレスで dpi の PNG にして読み込む。"""
    text = pathlib.Path(svg_path).read_text(encoding="utf-8")
    m = re.search(r'width="([\d.]+)mm"\s+height="([\d.]+)mm"', text)
    if not m:
        raise ValueError(f"SVG の width/height(mm) が読み取れない: {svg_path}")
    w_mm, h_mm = float(m.group(1)), float(m.group(2))
    css_px_per_mm = CSS_PX_PER_INCH / MM_PER_INCH
    css_w = round(w_mm * css_px_per_mm)
    css_h = round(h_mm * css_px_per_mm)
    subprocess.run([
        CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
        "--default-background-color=FFFFFFFF",
        f"--force-device-scale-factor={dpi / CSS_PX_PER_INCH}",
        f"--window-size={css_w},{css_h}",
        f"--screenshot={out_png}",
        pathlib.Path(svg_path).resolve().as_uri(),
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return Image.open(out_png).convert("RGB")


def max_run_lengths(dark, axis: int):
    """dark（真偽の 2 次元配列）の最大の連続した True の長さを、axis と垂直な各インデックスごとに返す。
    axis=1: 各行の X 方向（横の辺の検出に使う）。axis=0: 各列の Y 方向（縦の辺の検出に使う）。"""
    import numpy as np

    h, w = dark.shape
    count = h if axis == 1 else w
    run = np.zeros(count, dtype=np.int32)
    best = np.zeros(count, dtype=np.int32)
    for k in range(w if axis == 1 else h):
        line = dark[:, k] if axis == 1 else dark[k, :]
        run = np.where(line, run + 1, 0)
        np.maximum(best, run, out=best)
    return best


def detect_frame_bbox(img: Image.Image) -> tuple[float, float, float, float]:
    """見当枠（最も外側の黒い矩形）の画素の外接矩形（x0, y0, x1, y1）を返す。見つからなければ例外。"""
    import numpy as np

    dark = np.asarray(img.convert("L")) < DARK_THRESHOLD
    h, w = dark.shape
    horiz_rows = np.where(max_run_lengths(dark, axis=1) > FRAME_RUN_RATIO * w)[0]
    vert_cols = np.where(max_run_lengths(dark, axis=0) > FRAME_RUN_RATIO * h)[0]
    if len(horiz_rows) == 0 or len(vert_cols) == 0:
        raise ValueError("見当枠が検出できない（水平/垂直の長い黒ランが見つからない）")
    return float(vert_cols.min()), float(horiz_rows.min()), float(vert_cols.max()), float(horiz_rows.max())


def blurred_diff_metrics(a: Image.Image, b: Image.Image) -> tuple[float, float, Image.Image]:
    """2 枚をぼかしてから差をとり、（異なる画素の割合、平均の差、差の画像）を返す。
    画素の無い画像は、比べた結果にならない（一致と区別できない）ので例外にする。"""
    diff = ImageChops.difference(a.filter(ImageFilter.GaussianBlur(BLUR)),
                                 b.filter(ImageFilter.GaussianBlur(BLUR))).convert("L")
    hist = diff.histogram()
    n = sum(hist)
    if n == 0:
        raise ValueError(f"画素の無い画像は比べられない: {a.size} と {b.size}")
    bad = sum(hist[DIFF_TH:])
    mean = sum(i * v for i, v in enumerate(hist)) / n
    return bad / n, mean, diff
