# -*- coding: utf-8 -*-
"""pikepdf による PDF メタ構造の回収（Phase 0 抽出検証・第1段）。

1. /Square 注釈（EPDFSHX=1 のSHX元テキスト）から文字列と mm 矩形を回収する
2. OCG（Optional Content Groups）＝AutoCADレイヤ情報の一覧と、
   コンテンツ内の /OC マーク付き内容の対応を確認する
3. 各フォントの /ToUnicode CMap の有無（テキスト復元可否の判定材料）

使い方: uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/annots_pikepdf.py <pdf>
"""
import sys

import pikepdf

PT2MM = 25.4 / 72.0
pdf = pikepdf.open(sys.argv[1])
page = pdf.pages[0]

print(f"=== {sys.argv[1]} ===")

# ---- 1. 注釈 ----
annots = page.get("/Annots", [])
print(f"\n-- 注釈: {len(annots)}件 --")
for a in annots:
    sub = str(a.get("/Subtype", "?"))
    rect = [float(v) for v in a.get("/Rect", [0, 0, 0, 0])]
    mm = [round(v * PT2MM, 2) for v in rect]
    contents = a.get("/Contents")
    text = str(contents) if contents is not None else None
    print(f"  {sub}  Rect(mm)= [{mm[0]}, {mm[1]}] - [{mm[2]}, {mm[3]}]  "
          f"Contents= {text!r}")

# ---- 2. OCG（レイヤ） ----
ocp = pdf.Root.get("/OCProperties")
print("\n-- OCG（レイヤ情報） --")
if ocp is None:
    print("  なし")
else:
    for ocg in ocp.get("/OCGs", []):
        print(f"  {ocg.get('/Name')}")
    # ページリソースの /Properties で oc名 → OCG の対応を見る
    props = page.Resources.get("/Properties")
    if props is not None:
        for name, obj in props.items():
            print(f"  {name} -> {obj.get('/Name')}")

# ---- 3. ToUnicode ----
print("\n-- フォントと ToUnicode --")
for name, fd in page.Resources.get("/Font", {}).items():
    has_tu = "/ToUnicode" in fd
    enc = fd.get("/Encoding")
    print(f"  {name}: BaseFont={fd.get('/BaseFont')} Encoding={enc} "
          f"ToUnicode={'あり' if has_tu else 'なし'}")
    if has_tu:
        # CMap から数エントリだけ試し読み
        data = fd.ToUnicode.read_bytes().decode("latin-1", "replace")
        n = data.count("beginbfchar") + data.count("beginbfrange")
        print(f"    CMapセクション数: {n}")
