# -*- coding: utf-8 -*-
"""変形TTF検証用プローブ: 指定 Tm 平行移動値を持つテキストの前後オペレータと、
ページの /Font リソース全辞書（FontDescriptor 含む）を表示する"""
import sys

import pikepdf

pdf = pikepdf.open(sys.argv[1])
page = pdf.pages[0]
tx, ty = float(sys.argv[2]), float(sys.argv[3])

inst = list(pikepdf.parse_content_stream(page))
for i, ins in enumerate(inst):
    if isinstance(ins, pikepdf.ContentStreamInlineImage):
        continue
    if str(ins.operator) == "Tm":
        ops = [float(v) for v in ins.operands]
        if abs(ops[4] - tx) < 0.5 and abs(ops[5] - ty) < 0.5:
            print(f"--- Tm({tx},{ty}) 前後のオペレータ列 (i={i}) ---")
            for j in range(max(0, i - 12), min(len(inst), i + 6)):
                x = inst[j]
                if isinstance(x, pikepdf.ContentStreamInlineImage):
                    print(f"  [{j}] <inline image>")
                else:
                    print(f"  [{j}] {' '.join(str(o) for o in x.operands)} "
                          f"{x.operator}")
            break

print("\n--- /Font リソース ---")
fonts = page.Resources.get("/Font", {})
for name, fd in fonts.items():
    print(f"\n{name}: Subtype={fd.get('/Subtype')} BaseFont={fd.get('/BaseFont')}")
    if "/DescendantFonts" in fd:
        df = fd.DescendantFonts[0]
        print(f"  Descendant: {df.get('/Subtype')} CIDToGIDMap={df.get('/CIDToGIDMap')}")
        desc = df.get("/FontDescriptor")
        if desc is not None:
            for k in ("/Flags", "/ItalicAngle", "/StemV", "/FontBBox", "/FontFile2"):
                if k in desc:
                    v = "あり" if k == "/FontFile2" else desc[k]
                    print(f"  FontDescriptor {k} = {v}")
