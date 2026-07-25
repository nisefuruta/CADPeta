# -*- coding: utf-8 -*-
"""描画順（z順）の機械照合ゲート。

正解  = dissect_pikepdf.py の JSON（生 content stream を描画順に解剖したオブジェクト列）
候補  = IrMapProbe --dump-order の JSON（修正後 acquirer が組む ir.Nodes の kind 列）

両者の「kind 列」を run-length 圧縮して完全一致を判定する。一致で終了コード0、
不一致またはプロファイル外で非ゼロ終了する（サイレント合格にしない）。結果は JSON へも書く。

分類写像（dissect kind → 正準 kind）:
  fill / stroke / fillstroke → path,  image → image,  text → text
  clip / shading / nopaint は両側から除外（acquirer はクリップ確定パスをノード化せず ClipDef へ
  送るため）。

run-length 圧縮の理由（粒度差の吸収）:
  dissect の text は BT..ET ブロック単位で1件、acquirer は TextSequence（表示op）単位で複数ノード。
  パスの分割単位も抽出器で異なり得る。圧縮は「画像/文字がパス群の前後どちらに来るか」という
  交錯順序だけを比較する。中核回帰（画像が末尾へ）は圧縮後も保存される
  （旧 path,image ≠ 真 path,image,path）。

Form XObject は本体 PdfIrAcquirer / IrMapProbe が depth-first に再帰走査して真の描画順を持つため、
depth>0（Form 展開由来）のレコードも照合対象に含める（Form 内の path/text/image の z順を検証する）。

プロファイル外（照合不能として非ゼロ終了する）条件:
  1. shading（sh）が存在する（acquirer にノード対応なし）
  2. 塗り＋クリップ兼用パス（同一 bbox/segs の paint レコード直後に clip レコード。acquirer は
     IsClipping パスをノード化せず paint 側が欠落し得る）

使い方:
  uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/compare_draworder.py \
      <dissect.json> <order.json> [result.json]
"""
import json
import sys

# dissect kind → 正準 kind（None は照合列から除外）
KIND_MAP = {
    "fill": "path",
    "stroke": "path",
    "fillstroke": "path",
    "image": "image",
    "text": "text",
    "clip": None,
    "shading": None,
    "nopaint": None,
}


def collapse(seq):
    """連続する同一要素を1つに畳む（run-length 圧縮）。"""
    out = []
    for k in seq:
        if not out or out[-1] != k:
            out.append(k)
    return out


def profile_violations(objs):
    """プロファイル外条件を列挙する（空なら照合可）。

    Form 展開（depth>0）は本体・Probe とも depth-first 再帰で扱うため照合対象に含める
    （プロファイル外にしない）。
    """
    v = []
    if any(o["kind"] == "shading" for o in objs):
        n = sum(1 for o in objs if o["kind"] == "shading")
        v.append(f"shading が {n} 件")
    # 塗り＋クリップ兼用: paint レコードの直後に、同一 bbox/segs の clip レコード
    for a, b in zip(objs, objs[1:]):
        if (a["kind"] in ("fill", "stroke", "fillstroke") and b["kind"] == "clip"
                and a.get("bbox_pt") == b.get("bbox_pt")
                and a.get("segs") == b.get("segs")):
            v.append("塗り＋クリップ兼用パス（paint 直後に同一 bbox/segs の clip）")
            break
    return v


def main():
    if len(sys.argv) < 3:
        print("使い方: compare_draworder.py <dissect.json> <order.json> [result.json]",
              file=sys.stderr)
        return 2

    dissect = json.load(open(sys.argv[1], encoding="utf-8"))
    order = json.load(open(sys.argv[2], encoding="utf-8"))
    result_path = sys.argv[3] if len(sys.argv) > 3 else None

    objs = dissect["objects"]
    violations = profile_violations(objs)

    ref_seq = [KIND_MAP[o["kind"]] for o in objs if KIND_MAP.get(o["kind"]) is not None]
    cand_seq = list(order["kinds"])
    unknown = sorted(set(cand_seq) - {"path", "text", "image"})

    ref_c = collapse(ref_seq)
    cand_c = collapse(cand_seq)
    match = ref_c == cand_c and not unknown

    verdict = "match" if match else ("out-of-profile" if violations else "mismatch")
    # プロファイル外は「照合を保証できない」ため、たとえ列が一致していても失敗扱いにする
    # （コーパス変質を見逃さないため）。
    ok = match and not violations and not unknown

    out = {
        "dissect_source": dissect.get("source"),
        "order_source": order.get("source"),
        "verdict": verdict,
        "ok": ok,
        "profile_violations": violations,
        "unknown_candidate_kinds": unknown,
        "ref_collapsed": ref_c,
        "cand_collapsed": cand_c,
        "ref_raw_len": len(ref_seq),
        "cand_raw_len": len(cand_seq),
    }
    if result_path:
        with open(result_path, "w", encoding="utf-8") as f:
            json.dump(out, f, ensure_ascii=False, indent=1)

    print(f"verdict: {verdict}  ok={ok}")
    if violations:
        print("プロファイル外:")
        for x in violations:
            print("  -", x)
    if unknown:
        print(f"未知の候補kind: {unknown}")
    if not match:
        print(f"ref (collapsed, {len(ref_c)}): {ref_c}")
        print(f"cand(collapsed, {len(cand_c)}): {cand_c}")
    else:
        print(f"collapsed列一致（{len(ref_c)}要素）: {ref_c}")
    if result_path:
        print(f"結果JSON: {result_path}")

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
