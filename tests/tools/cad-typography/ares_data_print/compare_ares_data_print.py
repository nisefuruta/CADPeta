#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ARESの構造SVG書き出し／PDF印刷結果の突き合わせレンダ。

tests/tools/data-export/compare_data_print.ps1・linetype_symbol_check/compare_data_print.ps1と同じ
「CAD(0,0) を同一ピクセルへ位置合わせしてChromeヘッドレスでPNG化→render_diff.pyで
異画素率を出す」手法を踏襲する。相違点は、ARES の PDF プロットが「用紙に合わせて
自動縮小（fit）」されており、構造SVG（1:1実寸）と印刷結果（プロット紙面mm）の間に
既存ハーネスにはなかった一様スケール係数 S が入る点——本スクリプトはこれを
（境界矩形／既知テキスト間隔の実測から）明示的な引数として受け取り、印刷側の描画
密度を s/S へ変えることで補正する。

使い方: compare_ares_data_print.py <caseName> <dataSvg> <printSvg> <outDir>
        --data-origin X Y --print-origin X Y --print-scale S --canvas W H --anchor X Y [--dpi N]
"""
import argparse
import re
import subprocess
import pathlib
import sys

# Chrome の場所は、ほかの比較の試験と共有する（tests/tools/common/chrome_path.py）
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "common"))
from chrome_path import CHROME  # noqa: E402


def read_viewbox(svg_path):
    with open(svg_path, encoding="utf-8") as f:
        head = f.readline()
    m = re.search(r'viewBox="([-\d.]+) ([-\d.]+) ([\d.]+) ([\d.]+)"', head)
    return [float(x) for x in m.groups()]  # minx, miny, w, h


def make_wrapper(svg_path, origin_x, origin_y, scale_factor, anchor_x, anchor_y, dpi, html_path):
    vminx, vminy, w, h = read_viewbox(svg_path)
    s = dpi / 25.4 / scale_factor
    left_px = (anchor_x - (origin_x - vminx) / scale_factor) * (dpi / 25.4)
    top_px = (anchor_y - (origin_y - vminy) / scale_factor) * (dpi / 25.4)
    img_w_px = w * s
    img_h_px = h * s
    uri = pathlib.Path(svg_path).resolve().as_uri()
    html = f'''<!doctype html><html><head><meta charset="utf-8"><style>
html,body{{margin:0;padding:0;background:#fff}}
img{{position:absolute;left:{left_px}px;top:{top_px}px;width:{img_w_px}px;height:{img_h_px}px}}
</style></head><body><img src="{uri}"></body></html>
'''
    pathlib.Path(html_path).write_text(html, encoding="utf-8")
    print(f"{html_path}: viewBox=({vminx},{vminy},{w},{h}) scaleFactor={scale_factor} "
          f"left={left_px:.2f}px top={top_px:.2f}px imgSize=({img_w_px:.1f}x{img_h_px:.1f})px")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("case")
    ap.add_argument("data_svg")
    ap.add_argument("print_svg")
    ap.add_argument("out_dir")
    ap.add_argument("--data-origin", nargs=2, type=float, required=True)
    ap.add_argument("--print-origin", nargs=2, type=float, required=True)
    ap.add_argument("--print-scale", type=float, required=True,
                     help="印刷側の物理縮尺（paper_mm / real_mm）")
    ap.add_argument("--canvas", nargs=2, type=float, required=True, help="実寸mmでの出力キャンバス幅高さ")
    ap.add_argument("--anchor", nargs=2, type=float, required=True, help="CAD(0,0)を置く実寸mm位置")
    ap.add_argument("--dpi", type=float, default=150.0)
    args = ap.parse_args()

    out = pathlib.Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    dpi = args.dpi
    s = dpi / 25.4
    vw = round(args.canvas[0] * s)
    vh = round(args.canvas[1] * s)

    make_wrapper(args.data_svg, args.data_origin[0], args.data_origin[1], 1.0,
                 args.anchor[0], args.anchor[1], dpi, out / "data.html")
    make_wrapper(args.print_svg, args.print_origin[0], args.print_origin[1], args.print_scale,
                 args.anchor[0], args.anchor[1], dpi, out / "print.html")

    for side in ("data", "print"):
        subprocess.run([
            CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
            "--default-background-color=FFFFFFFF",
            f"--window-size={vw},{vh}",
            f"--screenshot={out / (side + '.png')}",
            str(out / f"{side}.html"),
        ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
    print(f"レンダ完了: {out}/data.png, {out}/print.png ({vw}x{vh}px, {dpi}dpi)")


if __name__ == "__main__":
    main()
