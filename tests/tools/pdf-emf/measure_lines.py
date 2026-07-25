# -*- coding: utf-8 -*-
"""hairline.emf のレンダPNGから、各行の水平線の描画太さ(px)を計測する。

使い方: measure_lines.py <png> <dpi>
チャート仕様（HairlineChart）: 水平線は x=5〜105mm、y=8,18,28,38,48,58mm。
x=50mm の縦断面で、暗い画素(輝度<128)の連続数を行ごとに数える。
"""
import sys

from PIL import Image

png, dpi = sys.argv[1], float(sys.argv[2])
img = Image.open(png).convert("L")
px_mm = dpi / 25.4
x = int(round(50 * px_mm))
rows_mm = [8, 18, 28, 38, 48, 58]
labels = ["w=0(cosmetic)", "w=1(0.01mm)", "w=5(0.05mm)",
          "w=13(0.13mm)", "w=17(0.169mm)", "w=35(0.35mm)"]

print(f"{png} dpi={dpi} px/mm={px_mm:.3f} 断面x={x}px")
for y_mm, label in zip(rows_mm, labels):
    yc = int(round(y_mm * px_mm))
    y0, y1 = max(0, yc - 12), min(img.height, yc + 12)
    col = [img.getpixel((x, y)) for y in range(y0, y1)]
    dark = [i for i, v in enumerate(col) if v < 128]
    if not dark:
        print(f"  {label:16s} 検出なし")
        continue
    width = dark[-1] - dark[0] + 1
    mmw = width / px_mm
    print(f"  {label:16s} {width}px ({mmw:.3f}mm相当)")
