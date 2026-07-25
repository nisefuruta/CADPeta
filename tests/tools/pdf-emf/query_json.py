# -*- coding: utf-8 -*-
"""dissect_pikepdf.py が出した JSON への即席クエリ（検証補助）"""
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
objs = data["objects"]
mode = sys.argv[2]

if mode == "yband":  # 指定 y帯(dwg mm)のオブジェクト
    y0, y1 = float(sys.argv[3]), float(sys.argv[4])
    for o in objs:
        b = o["bbox_mm"]
        cy = (b[1] + b[3]) / 2
        if y0 <= cy <= y1:
            print(o["cell"], o["kind"], "bbox=", b, "segs=", o.get("segs"),
                  "fill=", o["fill"], "stroke=", o["stroke"], "ca=", o["ca"])

elif mode == "images":
    for i, o in enumerate(objs):
        if o["kind"] == "image":
            print(i, o["cell"], "bbox_mm=", o["bbox_mm"], o["image"],
                  "ca=", o["ca"], "smask_gs=", o["smask_gs"], "blend=", o["blend"])

elif mode == "cell":  # 指定セルの全オブジェクト（描画順）
    r, c = int(sys.argv[3]), int(sys.argv[4])
    for i, o in enumerate(objs):
        if o["cell"] == [r, c] or o["cell"] == (r, c) or str(o["cell"]) == str((r, c)):
            print(i, o["kind"], "bbox=", o["bbox_mm"], "segs=", o.get("segs"),
                  "fill=", o["fill"], "stroke=", o["stroke"], "ca=", o["ca"],
                  "lw=", o.get("lw_mm"), "clip=", o.get("clip_pt"),
                  "font=", o.get("font"), "Tm=", o.get("text_matrix"),
                  "raw=", o.get("raw"))

elif mode == "texts":  # 全テキストの font / Tm / raw
    for i, o in enumerate(objs):
        if o["kind"] == "text":
            print(i, o["cell"], o.get("font"), "size=", o.get("size_pt"),
                  "Tm=", o.get("text_matrix"), "ca=", o["ca"],
                  "raw=", (o.get("raw") or "")[:32])

elif mode == "seq":  # 描画順で通し番号範囲を表示
    a, b = int(sys.argv[3]), int(sys.argv[4])
    for i in range(a, min(b, len(objs))):
        o = objs[i]
        print(i, o["cell"], o["kind"], "fill=", o["fill"], "stroke=", o["stroke"],
              "ca=", o["ca"], "font=", o.get("font"), "bbox=", o["bbox_mm"])
