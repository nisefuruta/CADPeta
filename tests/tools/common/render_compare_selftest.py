"""render_compare.blurred_diff_metrics の約束を確かめる。

使い方:
    uv run --project tests/tools/pdf-emf python tests/tools/common/render_compare_selftest.py
"""

from PIL import Image, ImageDraw

from render_compare import blurred_diff_metrics

white = Image.new("RGB", (60, 40), (255, 255, 255))
frac, mean, _ = blurred_diff_metrics(white, white.copy())
if frac != 0 or mean != 0:
    raise SystemExit(f"同じ画像の差が 0 でない: frac={frac} mean={mean}")

marked = white.copy()
ImageDraw.Draw(marked).rectangle((10, 10, 40, 30), fill=(0, 0, 0))
frac, mean, _ = blurred_diff_metrics(white, marked)
if frac <= 0 or mean <= 0:
    raise SystemExit(f"黒い矩形の有無の差を見つけない: frac={frac} mean={mean}")

empty = Image.new("RGB", (0, 0))
try:
    blurred_diff_metrics(empty, empty)
except ValueError:
    pass
else:
    raise SystemExit("画素の無い画像を比べて結果を返した（一致と区別できない）")

print("render_compare 自己試験 OK: 同じ画像は差 0、違う画像は差あり、画素の無い画像は例外")