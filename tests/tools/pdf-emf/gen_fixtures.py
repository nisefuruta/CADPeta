# -*- coding: utf-8 -*-
"""描画順テスト用の固定 PDF fixture を決定的に生成する（pikepdf）。

生成物は tests/assets/pdf-fixtures/ にコミットし、CadPeta.Tests の
PdfDrawOrderTests から本体 PdfIrAcquirer で読ませて描画順を検証する。

- interleave_ptx.pdf       : path(塗り) → text(BT/Tj/ET) → image(Do) → path(線) の型交錯
- subtype_form_noimg.pdf   : image_A(Do) → 空 Form(Do) → image_B(Do)。Form Do が画像状態を
                             汚さない（Subtype 判別で除外）ことの検証用。3枚とも別レイヤ(/OC)で
                             属性対応も見る（インライン画像は本体 PdfImageDecoder が省略キー
                             〔W/H/CS/BPC〕を非対応のため使わない）
- subtype_form_withimg.pdf : Form(path→text→image) → page path。Form 内の一般描画を含む再帰順序の検証用
- ccitt_g4_indexed1bit.pdf : CCITT G4（K=-1）1bit Indexed 画像。OLE 貼付（Excel 表など）の
                             プロット結果に現れる形の再現。実機 PDF は /DecodeParms の /Rows へ
                             /Columns と同じ値を書き /Height と食い違う癖があるため、同じ
                             食い違いを固定する（復号が /Rows でなくデータ末尾まで読む回帰）

画像は描画順 fixture が DeviceRGB 8bpc 無圧縮（PdfImageDecoder が確実に復元できる形式）。
compress_streams=False で保存し、フィルタ・予測子を付けない（CCITT fixture の画像本体のみ
G4 符号のまま書く）。

使い方: uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/gen_fixtures.py [出力dir]
既定出力: <repo>/tests/assets/pdf-fixtures/
"""
import os
import sys

import pikepdf
from pikepdf import Name


def rgb_image(pdf, w, h, rgb):
    """全画素が単色の DeviceRGB 8bpc 画像 XObject（無圧縮）。"""
    data = bytes(rgb) * (w * h)
    img = pikepdf.Stream(pdf, data)
    img.Type = Name.XObject
    img.Subtype = Name.Image
    img.Width = w
    img.Height = h
    img.ColorSpace = Name.DeviceRGB
    img.BitsPerComponent = 8
    return img


def form_xobject(pdf, content_bytes, resources=None):
    """/Subtype /Form の XObject。BBox は 100x100 固定。"""
    f = pikepdf.Stream(pdf, content_bytes)
    f.Type = Name.XObject
    f.Subtype = Name.Form
    f.BBox = [0, 0, 100, 100]
    if resources is not None:
        f.Resources = resources
    return f


def add_page(pdf, content_bytes, resources):
    cs = pikepdf.Stream(pdf, content_bytes)
    page = pdf.make_indirect(pikepdf.Dictionary(
        Type=Name.Page,
        MediaBox=[0, 0, 300, 300],
        Contents=cs,
        Resources=resources,
    ))
    pdf.pages.append(pikepdf.Page(page))


def save(pdf, path):
    # 無圧縮で保存（フィルタ・予測子を付けない）。PdfPig と PdfImageDecoder が素直に読める。
    pdf.save(path, compress_streams=False,
             stream_decode_level=pikepdf.StreamDecodeLevel.none)
    print("生成:", path)


def gen_interleave(path):
    """path(塗り) → text → image(Do) → path(線)。"""
    pdf = pikepdf.Pdf.new()
    img = rgb_image(pdf, 2, 2, (20, 200, 120))
    font = pikepdf.Dictionary(Type=Name.Font, Subtype=Name.Type1,
                              BaseFont=Name.Helvetica, Encoding=Name.WinAnsiEncoding)
    content = (
        b"1 0 0 rg 50 200 100 40 re f\n"                 # 赤塗り矩形（path）
        b"BT /F1 14 Tf 0 0 0 rg 50 150 Td (Hi) Tj ET\n"  # 文字（text）
        b"q 40 0 0 40 150 90 cm /Im0 Do Q\n"             # 画像（image）
        b"0 0 1 RG 2 w 50 70 m 200 70 l S\n"             # 青線（path）
    )
    res = pikepdf.Dictionary(
        Font=pikepdf.Dictionary(F1=font),
        XObject=pikepdf.Dictionary(Im0=img),
    )
    add_page(pdf, content, res)
    save(pdf, path)


def gen_form_noimg(path):
    """image_A(Do) → 空 Form(Do) → image_B(Do)。全て別レイヤ(/OC)で属性対応も見る。"""
    pdf = pikepdf.Pdf.new()
    img_a = rgb_image(pdf, 2, 2, (200, 30, 30))       # A=赤
    img_b = rgb_image(pdf, 2, 2, (30, 200, 30))       # B=緑
    form = form_xobject(pdf, b"")                      # 画像も描画も持たない空 Form
    content = (
        b"/OC /Oc1 BDC q 30 0 0 30 40 240 cm /ImA Do Q EMC\n"      # 画像A（レイヤ Oc1）
        b"/OC /Oc2 BDC q /Fm0 Do Q EMC\n"                          # 空 Form（レイヤ Oc2）
        b"/OC /Oc3 BDC q 30 0 0 30 40 40 cm /ImB Do Q EMC\n"       # 画像B（レイヤ Oc3）
    )
    props = pikepdf.Dictionary(
        Oc1=pikepdf.Dictionary(Type=Name.OCG, Name="LayerA"),
        Oc2=pikepdf.Dictionary(Type=Name.OCG, Name="LayerForm"),
        Oc3=pikepdf.Dictionary(Type=Name.OCG, Name="LayerB"),
    )
    res = pikepdf.Dictionary(
        XObject=pikepdf.Dictionary(ImA=img_a, ImB=img_b, Fm0=form),
        Properties=props,
    )
    add_page(pdf, content, res)
    save(pdf, path)


def gen_form_withimg(path):
    """Form(path→text→image) → ページ直下 path。Form の一般描画を再帰追跡する検体。"""
    pdf = pikepdf.Pdf.new()
    img_inner = rgb_image(pdf, 2, 2, (30, 30, 200))   # Form 内部画像=青
    font = pikepdf.Dictionary(Type=Name.Font, Subtype=Name.Type1,
                              BaseFont=Name.Helvetica, Encoding=Name.WinAnsiEncoding)
    form_res = pikepdf.Dictionary(
        XObject=pikepdf.Dictionary(ImInner=img_inner),
        Font=pikepdf.Dictionary(F1=font),
    )
    form = form_xobject(pdf, (
        b"1 0 0 rg 5 70 40 20 re f\n"
        b"BT /F1 10 Tf 0 0 0 rg 5 50 Td (Form) Tj ET\n"
        b"q 30 0 0 30 50 10 cm /ImInner Do Q\n"
    ), form_res)
    content = (
        b"q 1 0 0 1 40 150 cm /Fm0 Do Q\n"
        b"0 0 1 RG 2 w 40 40 m 200 40 l S\n"
    )
    res = pikepdf.Dictionary(XObject=pikepdf.Dictionary(Fm0=form))
    add_page(pdf, content, res)
    save(pdf, path)


def gen_ccitt_g4_indexed1bit(path):
    """CCITT G4（K=-1）1bit Indexed 画像。左上 1/4 と右下隅 1 画素が黒、他は白。

    G4 符号は Pillow の TIFF group4 出力から Strip をそのまま取り出す
    （TIFF Compression=4 の Strip データは PDF の /K -1 と同一の MMR 符号列）。
    """
    import io

    from PIL import Image

    w, h = 24, 40  # h > w にして /Rows(=Columns=w) と /Height の食い違いを作る
    # Pillow は mode "1" の TIFF を photometric=1（BlackIsZero）で書き、G4 の白黒割り当てが
    # PDF の規定（BlackIs1 省略時: 復号値 0=黒）と逆になる。意図した黒が PDF 側で黒になるよう
    # 画素値を反転して持つ（0=白として扱い、黒にしたい画素へ 255 を置く）
    img = Image.new("1", (w, h), 0)
    for y in range(h // 2):
        for x in range(w // 2):
            img.putpixel((x, y), 255)
    img.putpixel((w - 1, h - 1), 255)
    buf = io.BytesIO()
    img.save(buf, format="TIFF", compression="group4")
    t = Image.open(buf)
    (offset,) = t.tag_v2[273]   # StripOffsets
    (count,) = t.tag_v2[279]    # StripByteCounts
    ccitt = buf.getvalue()[offset:offset + count]

    pdf = pikepdf.Pdf.new()
    imgx = pikepdf.Stream(pdf, b"")
    imgx.write(ccitt, filter=Name.CCITTFaxDecode,
               decode_parms=pikepdf.Dictionary(K=-1, Columns=w, Rows=w))
    imgx.Type = Name.XObject
    imgx.Subtype = Name.Image
    imgx.Width = w
    imgx.Height = h
    imgx.BitsPerComponent = 1
    imgx.ColorSpace = pikepdf.Array([
        Name.Indexed, Name.DeviceRGB, 1,
        pikepdf.String(b"\x00\x00\x00\xff\xff\xff"),  # index0=黒, index1=白
    ])
    content = b"q 48 0 0 80 20 200 cm /Im0 Do Q\n"
    res = pikepdf.Dictionary(XObject=pikepdf.Dictionary(Im0=imgx))
    add_page(pdf, content, res)
    save(pdf, path)


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else None
    if out_dir is None:
        here = os.path.dirname(os.path.abspath(__file__))
        repo = os.path.abspath(os.path.join(here, "..", "..", ".."))
        out_dir = os.path.join(repo, "tests", "assets", "pdf-fixtures")
    os.makedirs(out_dir, exist_ok=True)
    gen_interleave(os.path.join(out_dir, "interleave_ptx.pdf"))
    gen_form_noimg(os.path.join(out_dir, "subtype_form_noimg.pdf"))
    gen_form_withimg(os.path.join(out_dir, "subtype_form_withimg.pdf"))
    gen_ccitt_g4_indexed1bit(os.path.join(out_dir, "ccitt_g4_indexed1bit.pdf"))


if __name__ == "__main__":
    main()
