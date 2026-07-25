# -*- coding: utf-8 -*-
"""PdfPigProbe の JSON をテストチャートのセル対応表へ整形する。

使い方:
  uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/report_pdfpig.py <pdfpig.json>
"""
import json
import sys
from collections import Counter, defaultdict

from testchart_grid import PT2MM, N_ROW, N_COL, ROW_NAMES, classify

recs = json.load(open(sys.argv[1], encoding="utf-8"))

cells = defaultdict(list)
for o in recs:
    b = o.get("bbox_pt")
    if not b:
        o["cell"] = "不明"
    else:
        o["cell"] = classify([v * PT2MM for v in b])
    cells[str(o["cell"])].append(o)


def paint_kind(o):
    if o["kind"] != "path":
        return o["kind"]
    if o["clipping"]:
        return "clip"
    k = ""
    if o["filled"]:
        k += "fill"
    if o["stroked"]:
        k += "stroke"
    return k or "nopaint"


def summary(objs):
    parts = []
    kinds = Counter(paint_kind(o) for o in objs)
    for k, cnt in sorted(kinds.items()):
        sel = [o for o in objs if paint_kind(o) == k]
        extra = ""
        if k in ("fill", "stroke", "fillstroke", "clip"):
            nl = sum(o["lines"] for o in sel)
            nc = sum(o["curves"] for o in sel)
            extra = f"(L{nl}/C{nc})"
        if k == "letter":
            fonts = sorted({o.get("font") or "?" for o in sel})
            # 文字列復元: x昇順に連結
            txt = "".join(o["value"] for o in
                          sorted(sel, key=lambda x: x["bbox_pt"][0]))
            extra = f"({','.join(fonts)}:{txt!r})"
        if k == "image":
            extra = "(" + ",".join(f"{o['w_px']}x{o['h_px']}" for o in sel) + ")"
        if k == "annotation":
            extra = "(" + ",".join(f"{o['type']}:{o['content']!r}" for o in sel) + ")"
        parts.append(f"{k}x{cnt}{extra}")
    lws = sorted({round(o["lw_pt"] * PT2MM, 4) for o in objs
                  if o["kind"] == "path" and o.get("lw_pt") and o["stroked"]})
    if lws:
        parts.append(f"lw={lws}mm")
    return " ".join(parts)


print("---- グリッド対応表（PdfPig） ----")
for r in range(1, N_ROW + 1):
    print(f"\n[{r:2d}] {ROW_NAMES[r-1]}")
    for c in range(1, N_COL + 1):
        objs = cells.get(str((r, c)), [])
        if objs:
            print(f"  列{c}: {summary(objs)}")
for zone in ("見出し", "行ラベル/目盛", "枠外", "全面", "不明"):
    objs = cells.get(zone, [])
    if objs:
        print(f"\n[{zone}] {summary(objs)}")

# ベジェ制御点が座標変換済みかの検証: 円(3,1)の制御点は
# セル近傍（デバイス空間, mm換算で図面内）にあるはず
for o in recs:
    if o["kind"] == "path" and o.get("first_bezier_ctrl_pt") \
            and o["cell"] == [3, 1] or (isinstance(o.get("cell"), tuple)
                                        and o["cell"] == (3, 1)):
        if o.get("first_bezier_ctrl_pt"):
            x, y = o["first_bezier_ctrl_pt"]
            print(f"\n(3,1)円のベジェ第1制御点: ({x:.2f},{y:.2f})pt = "
                  f"({x*PT2MM:.2f},{y*PT2MM:.2f})mm（用紙座標なら図面内のはず）")
            break
