#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""文字アウトライン化オプションの実機確認（Phase 3 節4）専用の全ページ比較。

compare_tiles.py はテストチャート格子・突き合わせレンダ向けにタイル分割し「要目視」
タイルのみ書き出す。本スクリプトは全ページを対象に、並置PNG（左右）と差分PNG
（階調反転）をそのまま1枚ずつ書き出す軽量版。検証専用であり src/ には関与しない。

使い方: render_diff.py a.png b.png out_dir
"""
import sys
import pathlib

from PIL import Image, ImageChops

a_path, b_path, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
out = pathlib.Path(out_dir)
out.mkdir(parents=True, exist_ok=True)

a = Image.open(a_path).convert("RGB")
b = Image.open(b_path).convert("RGB")
if a.size != b.size:
    print(f"サイズ調整: b {b.size} -> a {a.size}")
    b = b.resize(a.size, Image.LANCZOS)

w, h = a.size
side = Image.new("RGB", (w * 2 + 8, h), "white")
side.paste(a, (0, 0))
side.paste(b, (w + 8, 0))
side.save(out / "side_by_side.png")

diff = ImageChops.difference(a, b).convert("L")
hist = diff.histogram()
n = sum(hist)
bad = sum(hist[40:])
mean = sum(i * v for i, v in enumerate(hist)) / n
print(f"差分: 異画素率(閾値40)={bad/n:.5f} 平均差={mean:.3f} サイズ={w}x{h}")
Image.eval(diff, lambda v: min(255, v * 4)).save(out / "diff.png")
print(f"保存: {out / 'side_by_side.png'} , {out / 'diff.png'}")
