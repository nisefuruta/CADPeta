"""画素の比較で、差のある画素を数える（compare_images.py と compare_pages.py が共有する）。

差の画像を輝度（"L"）へ変換してから数えると、1 チャンネルだけの小さな差（青の 1 階調など）が
整数への丸めで 0 になり、差があるのに一致と判定する。どれかのチャンネルが 0 でない画素を数える。
"""

from __future__ import annotations

from PIL import Image


def changed_pixels(diff: Image.Image) -> int:
    """ImageChops.difference の結果（RGB）で、どれかのチャンネルが 0 でない画素の数を返す。"""
    return sum(1 for pixel in diff.getdata() if any(pixel))
