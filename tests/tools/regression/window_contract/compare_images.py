"""2枚の画像を画素で比較する。

Office の「図形に変換」で見た目が変わるかの判定に使う。図形が動く不具合は、外接矩形の
集計では検出できない（枠の内側で動くため）。レンダを直接比べるのが確実な判定になる。

使い方:
    uv run --project tests/tools/regression/window_contract \
        python tests/tools/regression/window_contract/compare_images.py a.png b.png [--diff-out d.png]

終了コード: 0=一致、1=不一致、2=実行できない（寸法不一致など）。
標準出力に DIFF_PERCENT=<値> を出す（呼び出し側が機械で読む）。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageChops

# Windows の既定コードページのままだと日本語が化けて結果を読めない（この repo の実測則）
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description="2枚の画像を画素で比較する")
    ap.add_argument("a", type=Path)
    ap.add_argument("b", type=Path)
    ap.add_argument("--diff-out", type=Path, default=None)
    args = ap.parse_args()
    for p in (args.a, args.b):
        if not p.is_file():
            print(f"実行できない: ファイルがない {p}", file=sys.stderr)
            return 2

    ia = Image.open(args.a).convert("RGB")
    ib = Image.open(args.b).convert("RGB")
    if ia.size != ib.size:
        # 寸法が違えば見た目は当然違う。割合を出せないため、事実だけ報告する
        print(f"不一致: 寸法が違う {ia.size} vs {ib.size}")
        print("DIFF_PERCENT=100")
        return 1

    diff = ImageChops.difference(ia, ib)
    gray = diff.convert("L")
    total = ia.size[0] * ia.size[1]
    diff_px = sum(1 for v in gray.getdata() if v != 0)
    percent = diff_px / total * 100 if total else 0.0
    worst = max(hi for _, hi in diff.getextrema())
    print(f"寸法={ia.size} 最大チャンネル差={worst} 差分画素={diff_px}/{total} 差分範囲={diff.getbbox()}")
    print(f"DIFF_PERCENT={percent:.4f}")
    if args.diff_out is not None and diff.getbbox() is not None:
        diff.save(args.diff_out)
        print(f"差分画像: {args.diff_out}")
    return 0 if diff_px == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
