"""画素の比較が、どのチャンネルの 1 階調の差も不一致と判定することを確かめる。

compare_images.py を実際に起動して終了コードを確かめ、compare_pages.py と共有する数え方
（pixel_diff.changed_pixels）も、同じ色・各チャンネルだけの 1 階調差の入力で確かめる。

使い方:
    uv run --project tests/tools/regression/window_contract \
        python tests/tools/regression/window_contract/pixel_diff_selftest.py
"""

from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageChops

HERE = Path(__file__).resolve().parent
BASE_COLOR = (100, 150, 200)
# 1 チャンネルだけを 1 階調変えた色。輝度へ変換すると 0 に丸められる差を含む（青の 1 階調は 0.114 で 0 になる）
SINGLE_CHANNEL_CHANGES = [(101, 150, 200), (100, 151, 200), (100, 150, 201)]


def make(color: tuple[int, int, int]) -> Image.Image:
    image = Image.new("RGB", (20, 10), BASE_COLOR)
    image.putpixel((5, 5), color)
    return image


def run_compare_images(a: Image.Image, b: Image.Image) -> int:
    with tempfile.TemporaryDirectory() as tmp:
        pa, pb = Path(tmp) / "a.png", Path(tmp) / "b.png"
        a.save(pa)
        b.save(pb)
        result = subprocess.run([sys.executable, str(HERE / "compare_images.py"), str(pa), str(pb)],
                                capture_output=True, text=True, encoding="utf-8")
        return result.returncode


failures: list[str] = []
if run_compare_images(make(BASE_COLOR), make(BASE_COLOR)) != 0:
    failures.append("compare_images.py: 同じ画像を一致と判定しない")
for color in SINGLE_CHANNEL_CHANGES:
    if run_compare_images(make(BASE_COLOR), make(color)) != 1:
        failures.append(f"compare_images.py: 1 画素の色 {BASE_COLOR}→{color} の差を不一致と判定しない")

from pixel_diff import changed_pixels  # noqa: E402  compare_pages.py と共有する数え方

if changed_pixels(ImageChops.difference(make(BASE_COLOR), make(BASE_COLOR))) != 0:
    failures.append("changed_pixels: 同じ画像の差の画素が 0 でない")
for color in SINGLE_CHANNEL_CHANGES:
    count = changed_pixels(ImageChops.difference(make(BASE_COLOR), make(color)))
    if count != 1:
        failures.append(f"changed_pixels: 1 画素の色 {BASE_COLOR}→{color} の差の画素が {count}（期待 1）")

if failures:
    raise SystemExit("\n".join(failures))
print("pixel_diff 自己試験 OK: 同じ色は一致、各チャンネルの 1 階調差は不一致")
