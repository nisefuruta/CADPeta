# -*- coding: utf-8 -*-
"""cli_option_matrix.ps1 の keepBg 実効プローブ用 fixture を決定的に生成する（pikepdf）。

TESTCHART2 系の PDF には背景除去の対象になる矩形が無い（実測: 変換ログ「背景除去: 0件」）ため、
--keep-bg の実効（既定=除去1件・keep=除去0件・描画数+1）は本 fixture で確認する。
内容は 200×150mm ページに、全面白矩形（背景）→黒線→赤矩形。

使い方: uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/gen_bg_fixture.py [出力パス]
既定出力: <repo>/tests/assets/pdf-fixtures/bg_rect_fixture.pdf
"""
import os
import sys

import pikepdf

W = 200 / 25.4 * 72
H = 150 / 25.4 * 72
CONTENT = f"""
1 1 1 rg 0 0 {W:.2f} {H:.2f} re f
0 0 0 RG 2 w 30 30 m {W - 30:.2f} {H - 30:.2f} l S
1 0 0 rg 60 60 120 60 re f
""".strip()


def main() -> int:
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        repo, "tests", "assets", "pdf-fixtures", "bg_rect_fixture.pdf")
    pdf = pikepdf.new()
    page = pdf.add_blank_page(page_size=(W, H))
    page.Contents = pdf.make_stream(CONTENT.encode("ascii"))
    pdf.save(out, compress_streams=False)
    print(f"saved {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
