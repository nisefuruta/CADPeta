# -*- coding: utf-8 -*-
"""ページの指定と色置換（許容差・スポイト）の試験に使う PDF を決定的に作る（pikepdf）。

- pages_fixture.pdf : 3 ページ。ページごとに用紙の大きさと線の色が違う
                      （1: 100×80mm 赤、2: 120×90mm 緑、3: 150×100mm 青）。どのページを変換したかを大きさと色で見分ける
- near_colors_fixture.pdf : 200×100mm の 1 ページ。上から順に
    - 近い色の塗り 4 つ（左から #FF0000、#FE0101、#F80000、#E00000。#FF0000 との各成分の差は 0、1、7、31）
    - 重なった塗り 2 つ（下 #0000FF、上 #00FF00）
    - 細い線（#FF00FF、0.1mm）
  座標は mm で、下の表の値と試験（CadPeta.App.Tests の PdfPetaPickMapTests）が同じ値を使う
- layers_fixture.pdf : 100×60mm の 1 ページ。レイヤ A の青（10,10 から 50×40）、レイヤ B の緑（40,10 から 50×40）、
                       レイヤ A の赤（45,20 から 10×20）の順に描く。重なりの中心 (50,30) は、そのままなら赤、
                       レイヤ別にグループ化すると緑が上になる（スポイトがプレビューと同じ図形の色を返すかの試験）

使い方: uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/gen_page_color_fixtures.py [出力dir]
既定出力: <repo>/tests/assets/pdf-fixtures/
"""
import os
import sys

import pikepdf
from pikepdf import Name

PT_PER_MM = 72 / 25.4


def pt(mm):
    return mm * PT_PER_MM


def rgb(hex6):
    return " ".join(f"{int(hex6[i:i + 2], 16) / 255:.6f}" for i in (0, 2, 4))


def add_page(pdf, width_mm, height_mm, content, resources=None):
    stream = pikepdf.Stream(pdf, content.strip().encode("ascii"))
    page = pdf.make_indirect(pikepdf.Dictionary(
        Type=Name.Page,
        MediaBox=[0, 0, round(pt(width_mm), 4), round(pt(height_mm), 4)],
        Contents=stream,
        Resources=resources if resources is not None else pikepdf.Dictionary(),
    ))
    pdf.pages.append(pikepdf.Page(page))


def rect_fill(hex6, x_mm, y_mm, w_mm, h_mm, page_h_mm):
    # 引数は IR と同じ左上原点・下向きの mm。PDF は左下原点・上向きなので y を反転する
    y_pdf = page_h_mm - y_mm - h_mm
    return f"{rgb(hex6)} rg {pt(x_mm):.4f} {pt(y_pdf):.4f} {pt(w_mm):.4f} {pt(h_mm):.4f} re f\n"


def line(hex6, width_mm, x0, y0, x1, y1, page_h_mm):
    return (f"{rgb(hex6)} RG {pt(width_mm):.4f} w "
            f"{pt(x0):.4f} {pt(page_h_mm - y0):.4f} m {pt(x1):.4f} {pt(page_h_mm - y1):.4f} l S\n")


PAGES = [(100, 80, "FF0000"), (120, 90, "00FF00"), (150, 100, "0000FF")]

NEAR_W, NEAR_H = 200, 100
NEAR_BANDS = ["FF0000", "FE0101", "F80000", "E00000"]  # x = 10 + 45*i、y = 10、40×30mm
OVERLAP_BOTTOM = ("0000FF", 20, 50, 60, 30)
OVERLAP_TOP = ("00FF00", 50, 55, 60, 20)
THIN_LINE = ("FF00FF", 0.1, 10, 95, 190, 95)


def gen_pages(path):
    pdf = pikepdf.new()
    for w, h, color in PAGES:
        add_page(pdf, w, h, line(color, 1.0, 10, 10, w - 10, h - 10, h))
    save(pdf, path)


def gen_near_colors(path):
    pdf = pikepdf.new()
    content = "".join(rect_fill(c, 10 + 45 * i, 10, 40, 30, NEAR_H) for i, c in enumerate(NEAR_BANDS))
    content += rect_fill(*OVERLAP_BOTTOM, NEAR_H)
    content += rect_fill(*OVERLAP_TOP, NEAR_H)
    content += line(*THIN_LINE, NEAR_H)
    add_page(pdf, NEAR_W, NEAR_H, content)
    save(pdf, path)


LAYERS_W, LAYERS_H = 100, 60
LAYER_SHAPES = [("L1", ("0000FF", 10, 10, 50, 40)), ("L2", ("00FF00", 40, 10, 50, 40)), ("L1", ("FF0000", 45, 20, 10, 20))]


def gen_layers(path):
    pdf = pikepdf.new()
    content = "".join(f"/OC /{layer} BDC {rect_fill(*shape, LAYERS_H)}EMC\n" for layer, shape in LAYER_SHAPES)
    props = pikepdf.Dictionary(
        L1=pikepdf.Dictionary(Type=Name.OCG, Name="LayerA"),
        L2=pikepdf.Dictionary(Type=Name.OCG, Name="LayerB"),
    )
    add_page(pdf, LAYERS_W, LAYERS_H, content, pikepdf.Dictionary(Properties=props))
    save(pdf, path)


def save(pdf, path):
    pdf.save(path, compress_streams=False, deterministic_id=True)
    print("生成:", path)


def main() -> int:
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(repo, "tests", "assets", "pdf-fixtures")
    gen_pages(os.path.join(out_dir, "pages_fixture.pdf"))
    gen_near_colors(os.path.join(out_dir, "near_colors_fixture.pdf"))
    gen_layers(os.path.join(out_dir, "layers_fixture.pdf"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
