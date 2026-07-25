# -*- coding: utf-8 -*-
"""2枚のレンダの全体平行移動量を推定する（±5px 総当たり、輝度SAD最小）。"""
import sys

from PIL import Image, ImageChops

a = Image.open(sys.argv[1]).convert("L")
b = Image.open(sys.argv[2]).convert("L")
if b.size != a.size:
    print(f"resize {b.size} -> {a.size}")
    b = b.resize(a.size, Image.LANCZOS)
w, h = a.size
best = None
for dy in range(-5, 6):
    for dx in range(-5, 6):
        box_a = (max(0, dx), max(0, dy), w + min(0, dx), h + min(0, dy))
        box_b = (max(0, -dx), max(0, -dy), w + min(0, -dx), h + min(0, -dy))
        d = ImageChops.difference(a.crop(box_a), b.crop(box_b))
        s = sum(i * v for i, v in enumerate(d.histogram())) / (d.size[0] * d.size[1])
        if best is None or s < best[0]:
            best = (s, dx, dy)
print(f"最良シフト: dx={best[1]} dy={best[2]} (平均差 {best[0]:.3f})")
