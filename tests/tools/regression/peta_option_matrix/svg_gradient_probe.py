# PETADATA が書き出した構造 SVG の埋込グラデーション PNG を実測する。
# peta_option_matrix.ps1 のグラデーション判定（枚数・画素寸法・非単色）の計測器。
# PowerShell 側での IHDR 手読みは実 PNG との不一致を起こしたため（実測）、判定は PIL に一本化する。
#
# 使い方: uv run --project tests/tools/regression/peta_option_matrix \
#             python tests/tools/regression/peta_option_matrix/svg_gradient_probe.py <svg>
# 出力: JSON {"count": N, "images": [{"w": px, "h": px, "unicolor": bool}, ...]}
import base64
import io
import json
import re
import sys

from PIL import Image


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) != 2:
        print("使い方: svg_gradient_probe.py <svg>", file=sys.stderr)
        return 2
    text = open(sys.argv[1], encoding="utf-8").read()
    images = []
    for b64 in re.findall(r'href="data:image/png;base64,([A-Za-z0-9+/=\s]+?)"', text):
        raw = base64.b64decode("".join(b64.split()))
        im = Image.open(io.BytesIO(raw))
        colors = im.convert("RGB").getcolors(maxcolors=16)
        images.append({
            "w": im.size[0],
            "h": im.size[1],
            "unicolor": colors is not None and len(colors) == 1,
        })
    print(json.dumps({"count": len(images), "images": images}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
