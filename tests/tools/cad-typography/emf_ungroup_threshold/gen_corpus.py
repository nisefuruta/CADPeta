#!/usr/bin/env python3
"""合成コーパス生成器（Phase 3 検証: Excel「図形に変換」閾値特定）。

CLI（cadpeta.exe）は PDF しか受け付けない（Program.cs 参照）ため、要素数を自由に
制御できる最小構成の PDF を素で生成する（外部ライブラリ不使用。標準の xref テーブルを
持つ PDF 1.4 として自前構築）。

生成する内容ストリームは「N 本の独立した stroke パス」を格子状に並べたもの。
各パスは 1 回の m（moveto）から始まり、--seg 個の l（lineto）を経て S（stroke）で終わる。
1 回の S = PdfIrAcquirer で 1 個の IrPath = EmfWriter で 1 個の GDI Polyline レコード
（EmfWriter.cs のコメントに実測記録あり: 直接描画の絶対座標 Polyline/PolylineTo が
「図形に変換」で分解実績のある構成）。ゆえに N は「図形に変換後の図形数」に直結する
制御変数として使える。

2 つの独立変数を用意する:
  --n    : パス（＝図形）の本数。閾値特定の主変数
  --seg  : 1 パスあたりの線分数（頂点数）。ファイルサイズ・GDI レコード複雑度を
           図形数と切り離して動かすための対照変数
  --len  : 1 線分の長さ（pt）。バウンディングボックス・ファイルサイズを図形数と
           切り離して動かすための対照変数

使い方:
  uv run python gen_corpus.py --n 5000 --out out.pdf
  uv run python gen_corpus.py --n 1000 --seg 20 --out out_seg20.pdf   # 同じ図形数でレコード数を増やす
  uv run python gen_corpus.py --n 1000 --len 200 --out out_long.pdf  # 同じ図形数でサイズを増やす
"""
import argparse
import math


def build_pdf(n: int, seg: int, seg_len: float, cell: float, line_width: float) -> bytes:
    cols = max(1, math.ceil(math.sqrt(n)))
    rows = max(1, math.ceil(n / cols))
    margin = 20.0
    width = margin * 2 + cols * cell
    height = margin * 2 + rows * cell
    # PDF ページの上限は実務上 14400pt（200inch）。それを超えたら呼び出し側の設計ミス
    if width > 14400 or height > 14400:
        raise SystemExit(
            f"ページが PDF の実務上限を超える: {width:.0f} x {height:.0f} pt "
            f"(n={n}, cell={cell})。cell を縮めるか n を分割すること")

    lines = [f"{line_width:.3f} w", "0 0 0 RG"]
    placed = 0
    for r in range(rows):
        for c in range(cols):
            if placed >= n:
                break
            x0 = margin + c * cell + cell * 0.1
            y0 = margin + r * cell + cell * 0.1
            lines.append(f"{x0:.3f} {y0:.3f} m")
            x, y = x0, y0
            # seg 本の線分を、セル内に収まるジグザグで敷く（seg=1 なら単純な1本線）
            for k in range(seg):
                x += seg_len
                y += seg_len * 0.3 if (k % 2 == 0) else -seg_len * 0.3
                lines.append(f"{x:.3f} {y:.3f} l")
            lines.append("S")
            placed += 1
        if placed >= n:
            break
    content = ("\n".join(lines) + "\n").encode("ascii")

    objects = []
    objects.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    objects.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objects.append(
        f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width:.3f} {height:.3f}] "
        f"/Resources << >> /Contents 4 0 R >>".encode("ascii"))
    objects.append(
        (f"<< /Length {len(content)} >>\nstream\n").encode("ascii") + content + b"endstream")

    out = bytearray()
    out += b"%PDF-1.4\n"
    # PDF は非ASCIIバイトを含むコメント行を1つ入れる慣習があるが必須ではないため省略
    offsets = [0]
    for i, obj in enumerate(objects, start=1):
        offsets.append(len(out))
        out += f"{i} 0 obj\n".encode("ascii") + obj + b"\nendobj\n"
    xref_offset = len(out)
    out += f"xref\n0 {len(objects) + 1}\n".encode("ascii")
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += f"{off:010d} 00000 n \n".encode("ascii")
    out += (
        f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\n"
        f"startxref\n{xref_offset}\n%%EOF\n").encode("ascii")
    return bytes(out)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--n", type=int, required=True, help="パス（図形）本数")
    ap.add_argument("--seg", type=int, default=1, help="1パスあたりの線分数（既定1）")
    ap.add_argument("--len", type=float, default=6.0, dest="seg_len", help="1線分の長さpt（既定6）")
    ap.add_argument("--cell", type=float, default=None, help="格子1マスの一辺pt（既定: seg*len*1.5+8 の自動算定）")
    ap.add_argument("--line-width", type=float, default=0.5, help="線幅pt（既定0.5）")
    ap.add_argument("--out", required=True, help="出力PDFパス")
    args = ap.parse_args()

    cell = args.cell if args.cell is not None else max(8.0, args.seg * args.seg_len * 1.5 + 8.0)
    pdf = build_pdf(args.n, args.seg, args.seg_len, cell, args.line_width)
    with open(args.out, "wb") as f:
        f.write(pdf)
    print(f"生成: {args.out} ({len(pdf):,} bytes) n={args.n} seg={args.seg} seg_len={args.seg_len} cell={cell:.1f}pt")


if __name__ == "__main__":
    main()
