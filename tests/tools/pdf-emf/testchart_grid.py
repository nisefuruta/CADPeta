# -*- coding: utf-8 -*-
"""テストチャート（gen_testchart.lsp）のグリッド定義と分類（共通モジュール）"""

PT2MM = 25.4 / 72.0

COL0_X, COL_W, N_COL = 48.0, 58.0, 6
ROW0_Y, ROW_H, N_ROW = 258.0, 12.0, 19
ROW_NAMES = [
    "線分 LINE", "ポリライン(円弧・幅0.6)", "円 CIRCLE", "円弧 ARC",
    "楕円 ELLIPSE", "楕円弧 ELLIPSE-A", "スプライン SPLINE",
    "文字 TrueType (Arial)", "文字 TrueType 日本語", "文字 TT 変形(幅0.8/斜15)",
    "文字 SHX (romans)", "文字 SHX+BigFont",
    "ハッチ SOLID", "ハッチ ANSI31", "ハッチ GRAVEL",
    "ワイプアウト", "ブロック参照 (ByBlock)", "入れ子ブロック", "寸法 DIMLINEAR",
]

# 対話プロット（A3フルブリード・窓=外枠）実測の図面原点オフセット
OFF_X, OFF_Y = 10.0, 8.5


def classify(bbox_mm, off=(OFF_X, OFF_Y)):
    """図面mm座標系でのグリッド分類。bbox_mm=(x0,y0,x1,y1)。
    返り値 (row, col) タプルまたは領域名文字列"""
    w = bbox_mm[2] - bbox_mm[0]
    h = bbox_mm[3] - bbox_mm[1]
    if w > 350 and h > 250:
        return "全面"  # 外枠・プロット窓クリップ等
    cx = (bbox_mm[0] + bbox_mm[2]) / 2 - off[0]
    cy = (bbox_mm[1] + bbox_mm[3]) / 2 - off[1]
    if cy >= ROW0_Y:
        return "見出し"
    if COL0_X <= cx < COL0_X + N_COL * COL_W:
        r = int((ROW0_Y - cy) // ROW_H) + 1
        c = int((cx - COL0_X) // COL_W) + 1
        if 1 <= r <= N_ROW:
            return (r, c)
    if cx < COL0_X:
        return "行ラベル/目盛" if cy > 12 else "枠外"
    return "枠外"
