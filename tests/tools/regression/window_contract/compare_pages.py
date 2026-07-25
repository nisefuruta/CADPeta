"""PDF のページ内容を画素で比較する。

窓プロットの座標契約の判定に使う。PDF のバイト一致は日時・ID 差があるため判定に使えず、
ファイルサイズ一致は内容一致の証明にならない。同一レンダラ・固定 DPI で描画して画素で比べる。

使い方:
    uv run --project tests/tools/regression/window_contract \
        python tests/tools/regression/window_contract/compare_pages.py a.pdf b.pdf [--dpi 150]

終了コード: 0=一致、1=不一致、2=実行できない（引数・ページ数・寸法の不整合）。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Windows の既定コードページのままだと日本語が化けて結果を読めない（この repo の実測則）
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

import pypdfium2 as pdfium
from PIL import Image, ImageChops


def render(path: Path, dpi: int) -> list[Image.Image]:
    """PDF の全ページを固定 DPI で描画する。"""
    doc = pdfium.PdfDocument(str(path))
    try:
        scale = dpi / 72.0  # PDF の既定ユーザ空間は 1/72 インチ
        return [doc[i].render(scale=scale).to_pil().convert("RGB") for i in range(len(doc))]
    finally:
        doc.close()


def compare(a: Path, b: Path, dpi: int, diff_out: Path | None) -> int:
    pages_a, pages_b = render(a, dpi), render(b, dpi)
    if len(pages_a) != len(pages_b):
        print(f"不一致: ページ数が違う {a.name}={len(pages_a)} {b.name}={len(pages_b)}")
        return 2
    worst = 0
    total_diff_px = 0
    total_px = 0
    for i, (pa, pb) in enumerate(zip(pages_a, pages_b)):
        if pa.size != pb.size:
            print(f"不一致: ページ {i+1} の寸法が違う {pa.size} vs {pb.size}")
            return 2
        diff = ImageChops.difference(pa, pb)
        bbox = diff.getbbox()
        # 画素ごとの最大チャンネル差と、差のある画素数
        extrema = diff.getextrema()  # ((minR,maxR),(minG,maxG),(minB,maxB))
        page_worst = max(hi for _, hi in extrema)
        worst = max(worst, page_worst)
        gray = diff.convert("L")
        diff_px = sum(1 for v in gray.getdata() if v != 0)
        total_diff_px += diff_px
        total_px += pa.size[0] * pa.size[1]
        print(
            f"ページ {i+1}: 寸法={pa.size} 最大チャンネル差={page_worst} "
            f"差分画素={diff_px} ({diff_px / (pa.size[0]*pa.size[1]) * 100:.4f}%) "
            f"差分範囲={bbox}"
        )
        if diff_out is not None and bbox is not None:
            out = diff_out if len(pages_a) == 1 else diff_out.with_stem(f"{diff_out.stem}_p{i+1}")
            diff.save(out)
            print(f"  差分画像: {out}")
    ratio = total_diff_px / total_px * 100 if total_px else 0.0
    print(f"総計: 最大チャンネル差={worst} 差分画素={total_diff_px} ({ratio:.4f}%) dpi={dpi}")
    return 0 if total_diff_px == 0 else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="PDF のページ内容を画素で比較する")
    ap.add_argument("a", type=Path)
    ap.add_argument("b", type=Path)
    ap.add_argument("--dpi", type=int, default=150, help="描画 DPI（既定 150）")
    ap.add_argument("--diff-out", type=Path, default=None, help="差分画像の出力先")
    args = ap.parse_args()
    for p in (args.a, args.b):
        if not p.is_file():
            print(f"実行できない: ファイルがない {p}", file=sys.stderr)
            return 2
    return compare(args.a, args.b, args.dpi, args.diff_out)


if __name__ == "__main__":
    sys.exit(main())
