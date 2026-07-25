# -*- coding: utf-8 -*-
"""PDFBox（正解データ生成器）と PdfPig の抽出結果を突き合わせる（第3段の照合本体）。

比較は2層:
  A) オブジェクト単位（描画順に1:1対応）: パスの bbox・塗り/線・色・線幅(CTM補正済み)・
     透過 ca/CA・レイヤ・クリップ、文字の Unicode・フォント・基線原点・色・サイズ・透過・レイヤ
  B) セル単位（テストチャート19行×6列）: 要素数と属性の一致表

使い方:
  uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/compare_pdfbox.py \
      <pdfbox.json> <pdfpig.json> [--cells]
"""
import json
import sys
from collections import Counter, defaultdict

from testchart_grid import PT2MM, N_ROW, N_COL, ROW_NAMES, classify

TOL_PT = 0.01      # 座標一致許容（float32 由来の丸め: 1191pt で eps≒1.4e-4pt）
TOL_COLOR = 2e-3   # 色成分（8bit 1/255 の半分以下）
TOL_ALPHA = 1e-4   # 透過
TOL_LW = 1e-3      # 線幅 pt


def load(path):
    return json.load(open(path, encoding="utf-8"))


def norm_font(name):
    if not name:
        return None
    # サブセット接頭辞 "ABCDEF+" を落とす
    if len(name) > 7 and name[6] == "+" and name[:6].isalpha():
        return name[7:]
    return name


def close(a, b, tol):
    if a is None and b is None:
        return True
    if a is None or b is None:
        return False
    return abs(a - b) <= tol


def close_arr(a, b, tol):
    if a is None and b is None:
        return True
    if a is None or b is None or len(a) != len(b):
        return False
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def main():
    box = load(sys.argv[1])
    pig = load(sys.argv[2])
    show_cells = "--cells" in sys.argv

    box_paths = [r for r in box if r["kind"] in ("path", "shading")]
    pig_paths = [r for r in pig if r["kind"] == "path"]
    box_letters = [r for r in box if r["kind"] == "letter"]
    pig_letters = [r for r in pig if r["kind"] == "letter"]
    box_images = [r for r in box if r["kind"] == "image"]
    pig_images = [r for r in pig if r["kind"] == "image"]
    box_annots = [r for r in box if r["kind"] == "annotation"]
    pig_annots = [r for r in pig if r["kind"] == "annotation"]

    print(f"件数: paths {len(box_paths)}/{len(pig_paths)}  "
          f"letters {len(box_letters)}/{len(pig_letters)}  "
          f"images {len(box_images)}/{len(pig_images)}  "
          f"annots {len(box_annots)}/{len(pig_annots)}  (PDFBox/PdfPig)")

    # ---------- A) パスのオブジェクト単位照合 ----------
    fails = Counter()
    examples = defaultdict(list)
    n = min(len(box_paths), len(pig_paths))
    if len(box_paths) != len(pig_paths):
        fails["件数不一致"] += 1

    seg_diff = Counter()
    for i in range(n):
        b, p = box_paths[i], pig_paths[i]

        def bad(key, detail=""):
            fails[key] += 1
            if len(examples[key]) < 3:
                examples[key].append(f"#{i} {detail}")

        b_clip = b.get("paint") == "clip"
        if b_clip != p["clipping"]:
            bad("クリップフラグ", f"box={b.get('paint')} pig={p['clipping']}")
        if not b_clip:
            if b.get("filled") != p["filled"] or b.get("stroked") != p["stroked"]:
                bad("塗り/線フラグ", f"box={b.get('paint')} pig=f{p['filled']}/s{p['stroked']}")
            # PdfPig は色オペレータ発行前の既定色を null で返す（PDF 既定＝黒）。
            # PDFBox は初期状態の黒を明示的に返すため、null vs 黒 は同値として別枠集計
            if p["filled"] and not close_arr(b.get("fill"), p.get("fill"), TOL_COLOR):
                if p.get("fill") is None and b.get("fill") == [0.0, 0.0, 0.0]:
                    fails["塗り色: PdfPig null＝既定黒(同値)"] += 1
                else:
                    bad("塗り色", f"box={b.get('fill')} pig={p.get('fill')}")
            if p["stroked"] and not close_arr(b.get("stroke"), p.get("stroke"), TOL_COLOR):
                if p.get("stroke") is None and b.get("stroke") == [0.0, 0.0, 0.0]:
                    fails["線色: PdfPig null＝既定黒(同値)"] += 1
                else:
                    bad("線色", f"box={b.get('stroke')} pig={p.get('stroke')}")
            if p["stroked"] and not close(b.get("lw_dev_pt"), p.get("lw_dev_pt"), TOL_LW):
                bad("線幅(CTM補正)", f"box={b.get('lw_dev_pt')} pig={p.get('lw_dev_pt')}")
        if not close_arr(b.get("bbox_pt"), p.get("bbox_pt"), TOL_PT):
            bad("bbox", f"box={b.get('bbox_pt')} pig={p.get('bbox_pt')}")
        if not close(b.get("ca"), p.get("ca"), TOL_ALPHA):
            bad("ca(塗り透過)", f"box={b.get('ca')} pig={p.get('ca')}")
        if not close(b.get("CA"), p.get("CA"), TOL_ALPHA):
            bad("CA(線透過)", f"box={b.get('CA')} pig={p.get('CA')}")
        if (b.get("layer") or None) != (p.get("layer") or None):
            bad("レイヤ", f"box={b.get('layer')} pig={p.get('layer')}")
        # セグメント数は数え方規約差が出やすいので別枠で集計
        if (b.get("lines"), b.get("curves")) != (p.get("lines"), p.get("curves")):
            seg_diff[(b.get("lines"), b.get("curves"), p.get("lines"), p.get("curves"))] += 1

    print(f"\n--- パス {n}件のオブジェクト単位照合 ---")
    if not fails:
        print("全項目一致（bbox/塗り線フラグ/色/線幅CTM補正/ca/CA/レイヤ/クリップ）")
    for k, v in fails.most_common():
        print(f"不一致 {k}: {v}件")
        for e in examples[k]:
            print(f"    例 {e}")
    if seg_diff:
        total = sum(seg_diff.values())
        print(f"セグメント数の規約差: {total}件 "
              f"(box lines/curves vs pig lines/curves の組: "
              f"{dict(list(seg_diff.items())[:5])})")
    else:
        print("セグメント数も完全一致")

    # ---------- A) 文字のオブジェクト単位照合 ----------
    fails = Counter()
    examples = defaultdict(list)
    n = min(len(box_letters), len(pig_letters))
    for i in range(n):
        b, p = box_letters[i], pig_letters[i]

        def bad(key, detail=""):
            fails[key] += 1
            if len(examples[key]) < 3:
                examples[key].append(f"#{i} {b.get('value')!r} {detail}")

        if b.get("value") != p.get("value"):
            bad("Unicode", f"box={b.get('value')!r} pig={p.get('value')!r}")
        if norm_font(b.get("font")) != norm_font(p.get("font")):
            bad("フォント", f"box={b.get('font')} pig={p.get('font')}")
        if not close_arr(b.get("origin_pt"), p.get("origin_pt"), TOL_PT):
            bad("基線原点", f"box={b.get('origin_pt')} pig={p.get('origin_pt')}")
        if not close(b.get("size_pt"), p.get("size_pt"), 0.05):
            bad("サイズ", f"box={b.get('size_pt')} pig={p.get('size_pt')}")
        if not close_arr(b.get("color"), p.get("color"), TOL_COLOR):
            bad("色", f"box={b.get('color')} pig={p.get('color')}")
        if not close(b.get("ca"), p.get("ca"), TOL_ALPHA):
            bad("ca(透過)", f"box={b.get('ca')} pig={p.get('ca')}")
        if (b.get("layer") or None) != (p.get("layer") or None):
            bad("レイヤ", f"box={b.get('layer')} pig={p.get('layer')}")

    print(f"\n--- 文字 {n}件のオブジェクト単位照合 ---")
    if not fails:
        print("全項目一致（Unicode/フォント/基線原点/サイズ/色/ca/レイヤ)")
    for k, v in fails.most_common():
        print(f"不一致 {k}: {v}件")
        for e in examples[k]:
            print(f"    例 {e}")

    # ---------- 画像・注釈 ----------
    for i in range(min(len(box_images), len(pig_images))):
        b, p = box_images[i], pig_images[i]
        ok = (b["w_px"] == p["w_px"] and b["h_px"] == p["h_px"]
              and close_arr(b["bbox_pt"], p["bbox_pt"], TOL_PT)
              and close(b.get("ca"), p.get("ca"), TOL_ALPHA)
              and (b.get("layer") or None) == (p.get("layer") or None))
        print(f"\n画像#{i}: {'一致' if ok else '不一致'} "
              f"({b['w_px']}x{b['h_px']}px, SMask={b.get('has_smask')}, "
              f"ca={b.get('ca')}, layer={b.get('layer')})")
    for i in range(min(len(box_annots), len(pig_annots))):
        b, p = box_annots[i], pig_annots[i]
        ok = (b.get("content") == p.get("content")
              and close_arr(b.get("bbox_pt"),
                            # PdfPig /Rect は非正規のことがある → 正規化して比較
                            [min(p["bbox_pt"][0], p["bbox_pt"][2]),
                             min(p["bbox_pt"][1], p["bbox_pt"][3]),
                             max(p["bbox_pt"][0], p["bbox_pt"][2]),
                             max(p["bbox_pt"][1], p["bbox_pt"][3])], TOL_PT))
        print(f"注釈#{i}: {'一致' if ok else '不一致'} {b.get('content')!r}")

    # ---------- 補完3点のサマリ ----------
    print("\n--- 補完3点（透過α・レイヤ・線幅CTM補正）の分布 ---")
    for name, recs_b, recs_p in (("パス", box_paths, pig_paths),
                                 ("文字", box_letters, pig_letters)):
        a_b = Counter(round(r.get("ca") or 1.0, 4) for r in recs_b)
        a_p = Counter(round(r.get("ca") or 1.0, 4) for r in recs_p)
        l_b = Counter(r.get("layer") for r in recs_b)
        l_p = Counter(r.get("layer") for r in recs_p)
        print(f"{name} ca分布   PDFBox={dict(a_b)}")
        print(f"{name} ca分布   PdfPig={dict(a_p)}  {'一致' if a_b == a_p else '不一致'}")
        print(f"{name} レイヤ分布 PDFBox={dict(l_b)}")
        print(f"{name} レイヤ分布 PdfPig={dict(l_p)}  {'一致' if l_b == l_p else '不一致'}")
    lw_b = Counter(round((r.get("lw_dev_pt") or 0) * PT2MM, 4)
                   for r in box_paths if r.get("stroked"))
    lw_p = Counter(round((r.get("lw_dev_pt") or 0) * PT2MM, 4)
                   for r in pig_paths if r.get("stroked"))
    print(f"線幅(mm)分布 PDFBox={dict(sorted(lw_b.items()))}")
    print(f"線幅(mm)分布 PdfPig={dict(sorted(lw_p.items()))}  "
          f"{'一致' if lw_b == lw_p else '不一致'}")

    # ---------- B) セル単位一致表 ----------
    if not show_cells:
        return

    def cell_of(r):
        bb = r.get("bbox_pt") or (r.get("origin_pt") and r["origin_pt"] * 2)
        if not bb:
            return "不明"
        return classify([v * PT2MM for v in bb])

    def cell_sig(recs):
        cells = defaultdict(lambda: Counter())
        texts = defaultdict(list)
        for r in recs:
            c = str(cell_of(r))
            if r["kind"] in ("path", "shading"):
                if r.get("paint") == "clip" or r.get("clipping"):
                    k = "clip"
                else:
                    k = ("fill" if r.get("filled") else "") + \
                        ("stroke" if r.get("stroked") else "")
                cells[c][k] += 1
            else:
                cells[c][r["kind"]] += 1
                if r["kind"] == "letter":
                    texts[c].append((r.get("origin_pt") or [0])[0], )
        return cells

    sig_b = cell_sig(box)
    sig_p = cell_sig(pig)
    print("\n--- セル単位一致表（19行×6列） ---")
    all_ok = True
    for r in range(1, N_ROW + 1):
        row_res = []
        for c in range(1, N_COL + 1):
            key = str((r, c))
            a, b = sig_b.get(key, Counter()), sig_p.get(key, Counter())
            row_res.append("○" if a == b else f"×{dict(a)}≠{dict(b)}")
            if a != b:
                all_ok = False
        print(f"[{r:2d}] {ROW_NAMES[r-1]:<24} {' '.join(row_res)}")
    for zone in ("見出し", "行ラベル/目盛", "枠外", "全面"):
        a, b = sig_b.get(zone, Counter()), sig_p.get(zone, Counter())
        mark = "○" if a == b else f"×{dict(a)}≠{dict(b)}"
        print(f"[{zone}] {mark}")
        if a != b:
            all_ok = False
    print("セル表:", "全セル一致" if all_ok else "不一致あり（上記×）")


if __name__ == "__main__":
    main()
