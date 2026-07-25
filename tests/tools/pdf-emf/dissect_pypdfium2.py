# -*- coding: utf-8 -*-
"""pypdfium2（PDFium）によるプロットPDFの抽出（Phase 0 抽出検証・第2段）。

ページオブジェクト列挙 API から パス（セグメント種別・制御点）・色RGBA・
線幅・破線・クリップ・テキスト（フォント・Unicode）・画像メタデータを取り、
pikepdf 解剖（dissect_pikepdf.py）と同じセル対応表形式で出力して突き合わせる。

使い方:
  uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/dissect_pypdfium2.py \
      tests/assets/testcharts/TESTCHART2-off.pdf [出力.json]
"""
import ctypes
import json
import sys
from collections import Counter, defaultdict

import pypdfium2 as pdfium
import pypdfium2.raw as C

from testchart_grid import PT2MM, N_ROW, N_COL, ROW_NAMES, classify

OBJ_TYPES = {
    C.FPDF_PAGEOBJ_TEXT: "text",
    C.FPDF_PAGEOBJ_PATH: "path",
    C.FPDF_PAGEOBJ_IMAGE: "image",
    C.FPDF_PAGEOBJ_SHADING: "shading",
    C.FPDF_PAGEOBJ_FORM: "form",
}
SEG_TYPES = {
    C.FPDF_SEGMENT_MOVETO: "m",
    C.FPDF_SEGMENT_LINETO: "l",
    C.FPDF_SEGMENT_BEZIERTO: "c",
}


def utf16_buffer(fn, *args, size_hint=256):
    """UTF-16(c_ushort配列) を返す PDFium 関数の呼び出しヘルパ"""
    buf = (ctypes.c_ushort * size_hint)()
    n = fn(*args, buf, size_hint)
    if n <= 0:
        return None
    return bytes(buf)[: (n - 1) * 2].decode("utf-16-le", "replace")


def get_matrix(obj):
    m = C.FS_MATRIX()
    ok = C.FPDFPageObj_GetMatrix(obj.raw, m)
    return [m.a, m.b, m.c, m.d, m.e, m.f] if ok else None


def path_detail(obj):
    n = C.FPDFPath_CountSegments(obj.raw)
    segs = Counter()
    first_bezier = None
    for i in range(n):
        seg = C.FPDFPath_GetPathSegment(obj.raw, i)
        st = SEG_TYPES.get(C.FPDFPathSegment_GetType(seg), "?")
        segs[st] += 1
        if st == "c" and first_bezier is None:
            x, y = ctypes.c_float(), ctypes.c_float()
            C.FPDFPathSegment_GetPoint(seg, x, y)
            first_bezier = [x.value, y.value]  # 制御点が取れる証跡（オブジェクト空間）
    fillmode = ctypes.c_int()
    stroke = C.FPDF_BOOL()
    C.FPDFPath_GetDrawMode(obj.raw, fillmode, stroke)
    kind = []
    if fillmode.value != C.FPDF_FILLMODE_NONE:
        kind.append("fill")
    if stroke.value:
        kind.append("stroke")
    return dict(segs), first_bezier, "".join(kind) or "nopaint", fillmode.value


def get_color(obj, which):
    r, g, b, a = (ctypes.c_uint(), ctypes.c_uint(), ctypes.c_uint(),
                  ctypes.c_uint())
    fn = (C.FPDFPageObj_GetFillColor if which == "fill"
          else C.FPDFPageObj_GetStrokeColor)
    if fn(obj.raw, r, g, b, a):
        return [r.value, g.value, b.value, a.value]
    return None


def clip_info(obj):
    clip = C.FPDFPageObj_GetClipPath(obj.raw)
    if not clip:
        return None
    np_ = C.FPDFClipPath_CountPaths(clip)
    if np_ <= 0:
        return None
    segs = C.FPDFClipPath_CountPathSegments(clip, 0)
    return {"paths": np_, "segs0": segs}


def marks(obj):
    out = []
    for i in range(C.FPDFPageObj_CountMarks(obj.raw)):
        mark = C.FPDFPageObj_GetMark(obj.raw, i)
        out_len = ctypes.c_ulong()
        buf = (ctypes.c_ushort * 64)()
        if C.FPDFPageObjMark_GetName(mark, buf, 128, out_len):
            out.append(bytes(buf)[: max(0, out_len.value - 2)]
                       .decode("utf-16-le", "replace"))
    return out or None


def main():
    src = sys.argv[1]
    out_json = sys.argv[2] if len(sys.argv) > 2 else None

    pdf = pdfium.PdfDocument(src)
    page = pdf[0]
    textpage = page.get_textpage()
    W, H = page.get_size()
    print(f"=== {src} (pypdfium2) ===")
    print(f"page: {W:.1f} x {H:.1f} pt = {W*PT2MM:.1f} x {H*PT2MM:.1f} mm")

    records = []
    n_form = 0
    for obj in page.get_objects(max_depth=8):
        t = OBJ_TYPES.get(obj.type, str(obj.type))
        if t == "form":
            n_form += 1
            continue
        fl, fb, fr, ft = (ctypes.c_float(), ctypes.c_float(),
                          ctypes.c_float(), ctypes.c_float())
        if C.FPDFPageObj_GetBounds(obj.raw, fl, fb, fr, ft):
            l, b, r, tp = fl.value, fb.value, fr.value, ft.value
        else:
            l = b = r = tp = 0.0
        rec = {
            "kind": t,
            "bbox_mm": [round(v * PT2MM, 3) for v in (l, b, r, tp)],
            "matrix": get_matrix(obj),
            "marks": marks(obj),
            "clip": clip_info(obj),
        }
        if t == "path":
            segs, first_c, paint, fillmode = path_detail(obj)
            lw = ctypes.c_float()
            C.FPDFPageObj_GetStrokeWidth(obj.raw, lw)
            m = rec["matrix"] or [1, 0, 0, 1, 0, 0]
            scale = abs(m[0] * m[3] - m[1] * m[2]) ** 0.5
            nd = C.FPDFPageObj_GetDashCount(obj.raw)
            dash = None
            if nd > 0:
                arr = (ctypes.c_float * nd)()
                C.FPDFPageObj_GetDashArray(obj.raw, arr, nd)
                dash = [round(v * scale * PT2MM, 3) for v in arr]
            rec.update({
                "paint": paint,
                "even_odd": fillmode == C.FPDF_FILLMODE_ALTERNATE,
                "segs": segs,
                "first_bezier_ctrl": first_c,
                "fill_rgba": get_color(obj, "fill") if "fill" in paint else None,
                "stroke_rgba": get_color(obj, "stroke") if "stroke" in paint else None,
                "lw_mm": round(lw.value * scale * PT2MM, 4),
                "dash_mm": dash,
            })
        elif t == "text":
            size = ctypes.c_float()
            C.FPDFTextObj_GetFontSize(obj.raw, size)
            font = C.FPDFTextObj_GetFont(obj.raw)
            fname = None
            if font:
                buf = ctypes.create_string_buffer(256)
                n = C.FPDFFont_GetBaseFontName(font, buf, len(buf))
                if n > 0:
                    fname = buf.value.decode("utf-8", "replace")
            text = utf16_buffer(C.FPDFTextObj_GetText, obj.raw, textpage)
            if text:
                text = text.rstrip("\x00")
            rec.update({
                "font": fname,
                "size_pt": round(size.value, 2),
                "text": text,
                "fill_rgba": get_color(obj, "fill"),
            })
        elif t == "image":
            meta = C.FPDF_IMAGEOBJ_METADATA()
            ok = C.FPDFImageObj_GetImageMetadata(obj.raw, page.raw, meta)
            filters = []
            for i in range(C.FPDFImageObj_GetImageFilterCount(obj.raw)):
                buf = ctypes.create_string_buffer(64)
                n = C.FPDFImageObj_GetImageFilter(obj.raw, i, buf, len(buf))
                if n > 0:
                    filters.append(buf.value.decode("ascii", "replace"))
            rec["image"] = {
                "w_px": meta.width if ok else None,
                "h_px": meta.height if ok else None,
                "bpp": meta.bits_per_pixel if ok else None,
                "colorspace": meta.colorspace if ok else None,
                "filters": filters,
            }
        records.append(rec)

    # セル分類（オフセットは共通モジュールの実測値）
    for rec in records:
        rec["cell"] = classify(rec["bbox_mm"])

    # ---- セル別対応表 ----
    cells = defaultdict(list)
    for rec in records:
        cells[str(rec["cell"])].append(rec)

    def summary(objs):
        parts = []
        kinds = Counter(
            (o["paint"] if o["kind"] == "path" else o["kind"]) for o in objs)
        for k, cnt in sorted(kinds.items()):
            extra = ""
            sel = [o for o in objs
                   if (o["paint"] if o["kind"] == "path" else o["kind"]) == k]
            if k in ("fill", "stroke", "fillstroke"):
                nl = sum(o["segs"].get("l", 0) for o in sel)
                nc = sum(o["segs"].get("c", 0) for o in sel)
                extra = f"(L{nl}/C{nc})"
            if k == "text":
                extra = "(" + ",".join(sorted({o.get("font") or "?" for o in sel})) \
                    + ")"
            if k == "image":
                extra = "(" + ",".join(
                    f"{o['image']['w_px']}x{o['image']['h_px']}"
                    f"[{'+'.join(o['image']['filters'])}]" for o in sel) + ")"
            parts.append(f"{k}x{cnt}{extra}")
        alphas = sorted({o["fill_rgba"][3] for o in objs if o.get("fill_rgba")} |
                        {o["stroke_rgba"][3] for o in objs if o.get("stroke_rgba")})
        if alphas and alphas != [255]:
            parts.append(f"A={alphas}")
        lws = sorted({o["lw_mm"] for o in objs if o.get("lw_mm")})
        if lws:
            parts.append(f"lw={lws}mm")
        dashes = [o["dash_mm"] for o in objs if o.get("dash_mm")]
        if dashes:
            parts.append(f"dash={dashes[0]}mm")
        texts = sorted({o["text"] for o in objs if o.get("text")})
        if texts:
            parts.append(f"text={texts}")
        return " ".join(parts)

    print(f"\nform objects (再帰済): {n_form}")
    print("\n---- グリッド対応表（pypdfium2） ----")
    for r in range(1, N_ROW + 1):
        print(f"\n[{r:2d}] {ROW_NAMES[r-1]}")
        for c in range(1, N_COL + 1):
            objs = cells.get(str((r, c)), [])
            if objs:
                print(f"  列{c}: {summary(objs)}")
    for zone in ("見出し", "行ラベル/目盛", "枠外", "全面"):
        objs = cells.get(zone, [])
        if objs:
            print(f"\n[{zone}] {summary(objs)}")

    print(f"\n総オブジェクト数: {len(records)}")

    if out_json:
        with open(out_json, "w", encoding="utf-8") as f:
            json.dump({"source": src, "objects": records},
                      f, ensure_ascii=False, indent=1)
        print(f"JSON出力: {out_json}")


if __name__ == "__main__":
    main()
