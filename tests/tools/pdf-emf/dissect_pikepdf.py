# -*- coding: utf-8 -*-
"""pikepdf によるプロットPDFの解剖ツール（Phase 0 抽出検証・第1段）。

コンテンツストリームのオペレータ列をグラフィックス状態
（CTM・色・ExtGState・クリップ・テキスト状態）ごと追跡し、
描画オブジェクト1件ずつを mm 座標の記録に落とす。
テストチャート（gen_testchart.lsp の 19行×6列グリッド）への
対応付けと、セル別の対応表出力までを行う。

使い方:
  uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/dissect_pikepdf.py \
      tests/assets/testcharts/TESTCHART2-off.pdf [出力.json]
"""
import json
import sys

import pikepdf
from testchart_grid import PT2MM, N_ROW, N_COL, ROW_NAMES, classify


def mat_mul(a, b):
    """行列 a×b（PDF の 6要素アフィン、行ベクトル規約）"""
    return [
        a[0] * b[0] + a[1] * b[2],
        a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2],
        a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4],
        a[4] * b[1] + a[5] * b[3] + b[5],
    ]


def apply(m, x, y):
    return (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5])


class GState:
    def __init__(self):
        self.ctm = [1, 0, 0, 1, 0, 0]
        self.fill = None      # ("rgb", r,g,b) 等
        self.stroke = None
        self.ca = 1.0         # 塗り不透明度
        self.CA = 1.0         # 線不透明度
        self.smask = None     # ExtGState 由来ソフトマスク名
        self.blend = None
        self.lw = 1.0
        self.dash = None
        self.clip = None      # 有効クリップの bbox(pt) タプル

    def clone(self):
        g = GState()
        g.__dict__.update(self.__dict__)
        g.ctm = self.ctm[:]
        return g


def fnum(v):
    return float(v)


def color_str(c):
    if c is None:
        return None
    return "/".join(f"{v:.3f}" if isinstance(v, float) else str(v) for v in c)


class Dissector:
    def __init__(self, pdf):
        self.pdf = pdf
        self.objects = []   # 描画レコード
        self.warnings = []
        self.mc_stack = []  # マーク付き内容（/OC → レイヤ名）のスタック
        self.ocg_names = {} # /ocN → レイヤ名
        self.depth = 0      # Form XObject の入れ子深さ（0=ページ最上位）

    # -- パス構築の座標蓄積（デバイス座標 pt） --
    def _reset_path(self):
        self.pts = []
        self.segs = {"m": 0, "l": 0, "c": 0, "re": 0}
        self.pending_clip = None  # "W" or "W*"

    def record(self, kind, bbox, g, **extra):
        # 線幅・破線はユーザー空間値なので、描画時CTMのスケールでデバイス値へ
        m = g.ctm
        scale = abs(m[0] * m[3] - m[1] * m[2]) ** 0.5
        rec = {
            "kind": kind,
            "bbox_pt": [round(v, 3) for v in bbox],
            "fill": color_str(g.fill) if kind in ("fill", "fillstroke") else None,
            "stroke": color_str(g.stroke) if kind in ("stroke", "fillstroke") else None,
            "ca": g.ca,
            "CA": g.CA,
            "smask_gs": g.smask,
            "blend": g.blend,
            "lw_mm": round(g.lw * scale * PT2MM, 4),
            "dash_mm": [round(v * scale * PT2MM, 3) for v in g.dash] if g.dash else None,
            "clip_pt": [round(v, 3) for v in g.clip] if g.clip else None,
            "layer": self.mc_stack[-1] if self.mc_stack else None,
            "depth": self.depth,  # 0=ページ最上位。>0 は Form 展開由来（描画順照合ではプロファイル外）
        }
        if kind == "text":
            rec["fill"] = color_str(g.fill)  # テキストは塗り色で描かれる(Tr 0)
        rec.update(extra)
        self.objects.append(rec)

    def path_bbox(self):
        xs = [p[0] for p in self.pts]
        ys = [p[1] for p in self.pts]
        return (min(xs), min(ys), max(xs), max(ys))

    def run(self, source, resources, g, depth=0):
        """コンテンツストリーム1本を解釈する。source はページ or Form XObject"""
        stack = []
        self._reset_path()
        self.depth = depth
        # テキスト状態
        tm = None
        tlm = None
        font_name, font_size = None, None
        text_pts = []
        text_bytes = []

        try:
            instructions = pikepdf.parse_content_stream(source)
        except Exception as e:  # 解析不能ストリームは記録して続行
            self.warnings.append(f"parse失敗 depth={depth}: {e}")
            return

        for inst in instructions:
            if isinstance(inst, pikepdf.ContentStreamInlineImage):
                # インライン画像（BI..EI）。配置は現CTMの単位正方形
                x0, y0 = apply(g.ctm, 0, 0)
                x1, y1 = apply(g.ctm, 1, 1)
                bbox = (min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1))
                self.record("image", bbox, g, image={"inline": True})
                continue

            ops = inst.operands
            op = str(inst.operator)

            # ---- グラフィックス状態 ----
            if op == "q":
                stack.append(g.clone())
            elif op == "Q":
                if stack:
                    g = stack.pop()
            elif op == "cm":
                g.ctm = mat_mul([fnum(v) for v in ops], g.ctm)
            elif op == "gs":
                name = str(ops[0])
                try:
                    egs = resources["/ExtGState"][name]
                except Exception:
                    self.warnings.append(f"ExtGState {name} 未解決")
                    egs = None
                if egs is not None:
                    if "/ca" in egs:
                        g.ca = float(egs["/ca"])
                    if "/CA" in egs:
                        g.CA = float(egs["/CA"])
                    if "/BM" in egs:
                        g.blend = str(egs["/BM"])
                    if "/SMask" in egs:
                        sm = egs["/SMask"]
                        g.smask = None if str(sm) == "/None" else "SMask"
                    if "/LW" in egs:
                        g.lw = float(egs["/LW"])
            elif op == "w":
                g.lw = fnum(ops[0])
            elif op == "d":
                arr = [fnum(v) for v in ops[0]]
                g.dash = arr if arr else None

            # ---- 色 ----
            elif op == "rg":
                g.fill = ("rgb",) + tuple(round(fnum(v), 4) for v in ops)
            elif op == "RG":
                g.stroke = ("rgb",) + tuple(round(fnum(v), 4) for v in ops)
            elif op == "g":
                g.fill = ("gray", round(fnum(ops[0]), 4))
            elif op == "G":
                g.stroke = ("gray", round(fnum(ops[0]), 4))
            elif op == "k":
                g.fill = ("cmyk",) + tuple(round(fnum(v), 4) for v in ops)
            elif op == "K":
                g.stroke = ("cmyk",) + tuple(round(fnum(v), 4) for v in ops)
            elif op in ("cs", "CS"):
                pass  # 色空間選択のみ。値は sc/scn で来る
            elif op in ("sc", "scn"):
                g.fill = ("scn",) + tuple(round(fnum(v), 4) for v in ops if not isinstance(v, pikepdf.Name))
            elif op in ("SC", "SCN"):
                g.stroke = ("scn",) + tuple(round(fnum(v), 4) for v in ops if not isinstance(v, pikepdf.Name))

            # ---- パス構築 ----
            elif op == "m":
                self.pts.append(apply(g.ctm, fnum(ops[0]), fnum(ops[1])))
                self.segs["m"] += 1
            elif op == "l":
                self.pts.append(apply(g.ctm, fnum(ops[0]), fnum(ops[1])))
                self.segs["l"] += 1
            elif op == "c":
                n = [fnum(v) for v in ops]
                for i in (0, 2, 4):
                    self.pts.append(apply(g.ctm, n[i], n[i + 1]))
                self.segs["c"] += 1
            elif op in ("v", "y"):
                n = [fnum(v) for v in ops]
                for i in (0, 2):
                    self.pts.append(apply(g.ctm, n[i], n[i + 1]))
                self.segs["c"] += 1
            elif op == "re":
                x, y, w_, h_ = [fnum(v) for v in ops]
                self.pts.append(apply(g.ctm, x, y))
                self.pts.append(apply(g.ctm, x + w_, y + h_))
                self.segs["re"] += 1
            elif op == "h":
                pass

            # ---- クリップ予約 ----
            elif op in ("W", "W*"):
                self.pending_clip = op

            # ---- パス描画 ----
            elif op in ("f", "F", "f*", "B", "B*", "b", "b*", "S", "s", "n"):
                if self.pts:
                    bbox = self.path_bbox()
                    kind = {"S": "stroke", "s": "stroke", "n": "nopaint"}.get(op)
                    if kind is None:
                        kind = "fill" if op in ("f", "F", "f*") else "fillstroke"
                    if kind != "nopaint":
                        self.record(kind, bbox, g, segs=dict(self.segs),
                                    even_odd=op.endswith("*"))
                    if self.pending_clip:
                        # 新クリップは現クリップとの交差
                        c = g.clip
                        g.clip = bbox if c is None else (
                            max(c[0], bbox[0]), max(c[1], bbox[1]),
                            min(c[2], bbox[2]), min(c[3], bbox[3]))
                        self.record("clip", bbox, g, segs=dict(self.segs),
                                    even_odd=self.pending_clip == "W*")
                self._reset_path()

            # ---- テキスト ----
            elif op == "BT":
                tm = [1, 0, 0, 1, 0, 0]
                tlm = tm[:]
                text_pts, text_bytes = [], []
            elif op == "ET":
                if text_pts:
                    xs = [p[0] for p in text_pts]
                    ys = [p[1] for p in text_pts]
                    self.record("text", (min(xs), min(ys), max(xs), max(ys)), g,
                                font=font_name, size_pt=font_size,
                                text_matrix=[round(v, 4) for v in (tm or [])],
                                raw=b"".join(text_bytes)[:60].hex())
                tm = tlm = None
            elif op == "Tf":
                name = str(ops[0])
                font_size = fnum(ops[1])
                try:
                    fd = resources["/Font"][name]
                    font_name = str(fd.get("/BaseFont", name))
                    if "/DescendantFonts" in fd:
                        font_name += " [Type0]"
                except Exception:
                    font_name = name
            elif op == "Tm":
                tlm = [fnum(v) for v in ops]
                tm = tlm[:]
            elif op == "Td":
                tlm = mat_mul([1, 0, 0, 1, fnum(ops[0]), fnum(ops[1])], tlm)
                tm = tlm[:]
            elif op == "TD":
                tlm = mat_mul([1, 0, 0, 1, fnum(ops[0]), fnum(ops[1])], tlm)
                tm = tlm[:]
            elif op == "T*":
                tm = tlm[:]
            elif op in ("Tj", "'", '"', "TJ"):
                if tm is not None:
                    trm = mat_mul(tm, g.ctm)
                    text_pts.append(apply(trm, 0, 0))
                    # サイズ×行列でおおまかな上端も入れる
                    text_pts.append(apply(trm, 0, font_size or 1))
                    items = ops[-1] if op == "TJ" else ops[-1]
                    if op == "TJ":
                        for it in items:
                            if isinstance(it, pikepdf.String):
                                text_bytes.append(bytes(it))
                    elif isinstance(items, pikepdf.String):
                        text_bytes.append(bytes(items))

            # ---- XObject ----
            elif op == "Do":
                name = str(ops[0])
                try:
                    xo = resources["/XObject"][name]
                except Exception:
                    self.warnings.append(f"XObject {name} 未解決")
                    continue
                sub = str(xo.get("/Subtype", ""))
                if sub == "/Image":
                    x0, y0 = apply(g.ctm, 0, 0)
                    x1, y1 = apply(g.ctm, 1, 1)
                    bbox = (min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1))
                    self.record("image", bbox, g, image={
                        "name": name,
                        "w_px": int(xo.get("/Width", 0)),
                        "h_px": int(xo.get("/Height", 0)),
                        "bpc": int(xo.get("/BitsPerComponent", 0)),
                        "filter": str(xo.get("/Filter", "")),
                        "colorspace": str(xo.get("/ColorSpace", "")),
                        "has_smask": "/SMask" in xo,
                        "interpolate": bool(xo.get("/Interpolate", False)),
                    })
                elif sub == "/Form":
                    g2 = g.clone()
                    if "/Matrix" in xo:
                        g2.ctm = mat_mul([float(v) for v in xo["/Matrix"]], g2.ctm)
                    res2 = xo.get("/Resources", resources)
                    saved = (self.pts, self.segs, self.pending_clip)
                    self.run(xo, res2, g2, depth + 1)
                    self.pts, self.segs, self.pending_clip = saved
                    self.depth = depth  # 再帰から戻ったら現在深さへ復元

            # ---- マーク付き内容（/OC = レイヤ） ----
            elif op == "BDC":
                tag = None
                if len(ops) >= 2 and str(ops[0]) == "/OC":
                    name = str(ops[1])
                    if name not in self.ocg_names:
                        try:
                            props = resources["/Properties"][name]
                            self.ocg_names[name] = str(props["/Name"])
                        except Exception:
                            self.ocg_names[name] = name
                    tag = self.ocg_names[name]
                self.mc_stack.append(tag)
            elif op == "BMC":
                self.mc_stack.append(None)
            elif op == "EMC":
                if self.mc_stack:
                    self.mc_stack.pop()

            # ---- シェーディング ----
            elif op == "sh":
                c = g.clip or (0, 0, 0, 0)
                self.record("shading", c, g, name=str(ops[0]))


def to_mm(v):
    return v * PT2MM


def main():
    src = sys.argv[1]
    out_json = sys.argv[2] if len(sys.argv) > 2 else None

    pdf = pikepdf.open(src)
    page = pdf.pages[0]
    media = [float(v) for v in page.MediaBox]
    print(f"=== {src} ===")
    print(f"MediaBox: {media} pt = "
          f"{to_mm(media[2]-media[0]):.1f} x {to_mm(media[3]-media[1]):.1f} mm")

    d = Dissector(pdf)
    d.run(page, page.get("/Resources", pikepdf.Dictionary()), GState())

    # 外枠（400×280mm のストローク）を探して DWG 原点オフセットを決める
    off = (0.0, 0.0)
    for o in d.objects:
        if o["kind"] not in ("stroke", "fillstroke"):
            continue
        b = o["bbox_pt"]
        w, h = to_mm(b[2] - b[0]), to_mm(b[3] - b[1])
        if abs(w - 400) < 1.0 and abs(h - 280) < 1.0:
            off = (to_mm(b[0]), to_mm(b[1]))
            print(f"外枠検出: 原点オフセット=({off[0]:.3f}, {off[1]:.3f})mm "
                  f"実測 {w:.3f}x{h:.3f}mm")
            break

    # 分類とmm換算
    for o in d.objects:
        b = o["bbox_pt"]
        o["bbox_mm"] = [round(to_mm(v) - off[i % 2], 3) for i, v in enumerate(b)]
        o["cell"] = classify([to_mm(v) for v in b], off)

    # ---- セル別対応表 ----
    from collections import defaultdict
    cells = defaultdict(list)
    for o in d.objects:
        cells[str(o["cell"])].append(o)

    def cell_summary(objs):
        parts = []
        from collections import Counter
        kinds = Counter(o["kind"] for o in objs)
        for k in ("fill", "stroke", "fillstroke", "text", "image", "clip", "shading"):
            if kinds[k]:
                extra = ""
                if k in ("fill", "stroke", "fillstroke"):
                    nl = sum(o["segs"]["l"] + o["segs"]["re"] for o in objs
                             if o["kind"] == k and "segs" in o)
                    nc = sum(o["segs"]["c"] for o in objs
                             if o["kind"] == k and "segs" in o)
                    extra = f"(L{nl}/C{nc})"
                if k == "text":
                    fonts = {o.get("font") for o in objs if o["kind"] == "text"}
                    extra = f"({','.join(sorted(f or '?' for f in fonts))})"
                if k == "image":
                    extra = "(" + ",".join(
                        f"{o['image'].get('w_px', '?')}x{o['image'].get('h_px', '?')}"
                        f"{'+SMask' if o['image'].get('has_smask') else ''}"
                        for o in objs if o["kind"] == "image") + ")"
                parts.append(f"{k}x{kinds[k]}{extra}")
        alphas = {o["ca"] for o in objs if o["ca"] < 1.0} | \
                 {o["CA"] for o in objs if o["CA"] < 1.0}
        if alphas:
            parts.append(f"α={sorted(alphas)}")
        dashes = [o["dash_mm"] for o in objs if o.get("dash_mm")]
        if dashes:
            parts.append(f"dash={dashes[0]}mm")
        lws = sorted({o["lw_mm"] for o in objs if o["kind"] in ("stroke", "fillstroke")})
        if lws:
            parts.append(f"lw={lws}mm")
        return " ".join(parts)

    print("\n---- グリッド対応表（行×列 → PDF構造） ----")
    for r in range(1, N_ROW + 1):
        print(f"\n[{r:2d}] {ROW_NAMES[r-1]}")
        for c in range(1, N_COL + 1):
            objs = cells.get(str((r, c)), [])
            if objs:
                print(f"  列{c}: {cell_summary(objs)}")

    for zone in ("見出し", "行ラベル/目盛", "枠外", "全面"):
        objs = cells.get(zone, [])
        if objs:
            print(f"\n[{zone}] {cell_summary(objs)}")

    if d.warnings:
        print("\n---- 警告 ----")
        for w in d.warnings[:20]:
            print(" ", w)

    print(f"\n総オブジェクト数: {len(d.objects)}")

    if out_json:
        with open(out_json, "w", encoding="utf-8") as f:
            json.dump({"source": src, "offset_mm": off,
                       "objects": d.objects}, f, ensure_ascii=False, indent=1)
        print(f"JSON出力: {out_json}")


if __name__ == "__main__":
    main()
