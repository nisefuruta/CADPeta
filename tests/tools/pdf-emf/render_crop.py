# -*- coding: utf-8 -*-
"""pypdfium2 でPDFをレンダリングし、mm指定の矩形を切り出して PNG 保存する。
使い方: render_crop.py in.pdf out.png dpi [x0 y0 x1 y1(mm, 図面左下原点) [offx offy]]
矩形省略時は全ページ。オフセット既定値(10, 8.5)mm は全ブリードA3プロットの実測値。
用紙左下原点のmmで指定する場合は offx offy に 0 0 を与える"""
import sys

import pypdfium2 as pdfium

OFF_X, OFF_Y = 10.0, 8.5  # 図面原点の用紙内オフセット(mm)。引数7-8で上書き可
MM2PT = 72.0 / 25.4

src, out, dpi = sys.argv[1], sys.argv[2], float(sys.argv[3])
pdf = pdfium.PdfDocument(src)
page = pdf[0]
scale = dpi / 72.0
bitmap = page.render(scale=scale)
img = bitmap.to_pil()
print(f"page render: {img.size}px @{dpi}dpi")

if len(sys.argv) > 4:
    x0, y0, x1, y1 = [float(v) for v in sys.argv[4:8]]
    if len(sys.argv) > 9:
        OFF_X, OFF_Y = float(sys.argv[8]), float(sys.argv[9])
    W, H = page.get_size()  # pt
    # 図面mm → 用紙pt → 画像px（画像は上原点）
    px0 = (x0 + OFF_X) * MM2PT * scale
    px1 = (x1 + OFF_X) * MM2PT * scale
    py0 = (H - (y1 + OFF_Y) * MM2PT) * scale
    py1 = (H - (y0 + OFF_Y) * MM2PT) * scale
    img = img.crop((int(px0), int(py0), int(px1), int(py1)))
    print(f"crop: {img.size}px")

img.save(out)
print(f"保存: {out}")
