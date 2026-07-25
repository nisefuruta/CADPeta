"""2枚の画像の間の平行移動量を測る。

「分解すると図形がずれる」の判定に使う。差分画素の割合だけでは、全体が微小に平行移動したのか、
描画の粒度（アンチエイリアス）の差なのかを区別できない。片方をずらしながら差分が最小になる
位置を探し、ずれ量を画素と mm で出す。

使い方:
    uv run --project tests/tools/regression/window_contract \
        python .../measure_shift.py a.png b.png --dpi 200 [--search 8]
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")


def diff_count(a: Image.Image, b: Image.Image) -> int:
    # histogram は画素走査をPillowのC実装で行う。Pythonで1画素ずつ数えると、
    # 1500×1050画素×289シフトで約4.5億回のループになり、1比較に数分を要する。
    histogram = ImageChops.difference(a, b).convert("L").histogram()
    return sum(histogram[1:])


def foreground(image: Image.Image) -> Image.Image:
    """白以外を前景255へ二値化する。"""
    return image.point(lambda value: 255 if value < 250 else 0)


def foreground_count(image: Image.Image) -> int:
    return image.histogram()[255]


def translate(image: Image.Image, dx: int, dy: int, fill: int) -> Image.Image:
    """反対辺へ回り込ませずに画像を平行移動する。"""
    width, height = image.size
    result = Image.new(image.mode, image.size, fill)
    source_left = max(0, -dx)
    source_top = max(0, -dy)
    source_right = min(width, width - dx)
    source_bottom = min(height, height - dy)
    if source_left < source_right and source_top < source_bottom:
        result.paste(
            image.crop((source_left, source_top, source_right, source_bottom)),
            (max(0, dx), max(0, dy)),
        )
    return result


def unmatched_percent(
    source: Image.Image,
    target: Image.Image,
    radius: int,
    valid_box: tuple[int, int, int, int] | None = None,
) -> float:
    """source前景のうちtarget前景からradius画素より遠い割合。"""
    expanded = target.filter(ImageFilter.MaxFilter(radius * 2 + 1))
    unmatched = ImageChops.subtract(source, expanded)
    if valid_box is not None:
        source = source.crop(valid_box)
        unmatched = unmatched.crop(valid_box)
    source_count = foreground_count(source)
    return 0.0 if source_count == 0 else foreground_count(unmatched) / source_count * 100


def main() -> int:
    ap = argparse.ArgumentParser(description="2枚の画像の平行移動量を測る")
    ap.add_argument("a", type=Path)
    ap.add_argument("b", type=Path)
    ap.add_argument("--dpi", type=int, default=200)
    ap.add_argument("--search", type=int, default=8, help="探索する平行移動の範囲（画素）")
    args = ap.parse_args()

    ia = Image.open(args.a).convert("L")
    ib = Image.open(args.b).convert("L")
    if ia.size != ib.size:
        print(f"実行できない: 寸法が違う {ia.size} vs {ib.size}", file=sys.stderr)
        return 2

    best = None
    for dy in range(-args.search, args.search + 1):
        for dx in range(-args.search, args.search + 1):
            shifted = translate(ib, dx, dy, 255)
            n = diff_count(ia, shifted)
            if best is None or n < best[0]:
                best = (n, dx, dy)
    n0 = diff_count(ia, ib)
    n, dx, dy = best
    fa = foreground(ia)
    fb = foreground(translate(ib, dx, dy, 255))
    # 回転経路ではCADの線ラスタ化位置が最大3px（0.2mm）変わり得る。前景同士がこの
    # 距離内に存在するかを双方向で測り、線幅・アンチエイリアス差を内容欠落と区別する。
    valid_box = (max(0, dx), max(0, dy), ia.width + min(0, dx), ia.height + min(0, dy))
    missed_a = unmatched_percent(fa, fb, 3, valid_box)
    missed_b = unmatched_percent(fb, fa, 3, valid_box)
    mm = 25.4 / args.dpi
    total = ia.size[0] * ia.size[1]
    print(f"ずらさない場合の差分画素: {n0} ({n0/total*100:.4f}%)")
    print(f"差分が最小になる平行移動: dx={dx} dy={dy} 画素 = ({dx*mm:.3f}, {dy*mm:.3f}) mm")
    print(f"その時の差分画素: {n} ({n/total*100:.4f}%)")
    print(f"BEST_DX={dx} BEST_DY={dy}")
    print(f"UNMATCHED_A_PERCENT={missed_a:.4f} UNMATCHED_B_PERCENT={missed_b:.4f}")
    if dx == 0 and dy == 0:
        print("判定: 平行移動なし（差はレンダの粒度に由来する）")
    else:
        print("判定: 平行移動あり（図形が動いている）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
