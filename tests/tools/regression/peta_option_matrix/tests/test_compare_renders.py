"""compare_renders.py の単体試験。

負例（negative control）が確実に FAIL することを機械的に証明する。「正解PDFのレンダと
EMF/SVGのレンダを機械判定する」用途で、全キャンバス差分率だけの判定を使わない設計（前景
recall/precision/IoU・シフト・bbox 判定）が意図どおり効くことをここで固定する。
"""

from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw

import compare_renders

DPI = 150
SIZE = (240, 240)
RECT_A = (60, 60, 180, 160)  # 正解に描く矩形
RECT_B = (30, 100, 90, 200)  # 「欠落」負例用の2つめの矩形


def make_image(rects: list[tuple[int, int, int, int]], size: tuple[int, int] = SIZE) -> Image.Image:
    img = Image.new("L", size, 255)
    draw = ImageDraw.Draw(img)
    for rect in rects:
        draw.rectangle(rect, fill=0)
    return img.convert("RGB")


def make_outline_image(rect: tuple[int, int, int, int], width: int, size: tuple[int, int] = SIZE) -> Image.Image:
    """輪郭のみ（塗りつぶしなし）の矩形を描く。線・寸法値のような疎な前景の合成用。"""
    img = Image.new("L", size, 255)
    draw = ImageDraw.Draw(img)
    draw.rectangle(rect, outline=0, width=width)
    return img.convert("RGB")


def make_supersampled(rect: tuple[int, int, int, int], phase_px: tuple[float, float], scale: int = 4) -> Image.Image:
    """rect をサブピクセル位相 phase_px（画素単位、小数可）だけずらして高解像度で描き縮小する。
    ダウンサンプルで縁にアンチエイリアスが乗った、1px未満のシフトを含む画像を作る。"""
    big = (SIZE[0] * scale, SIZE[1] * scale)
    img = Image.new("L", big, 255)
    draw = ImageDraw.Draw(img)
    ox, oy = phase_px[0] * scale, phase_px[1] * scale
    x0, y0, x1, y1 = rect
    draw.rectangle((x0 * scale + ox, y0 * scale + oy, x1 * scale + ox, y1 * scale + oy), fill=0)
    return img.resize(SIZE, Image.LANCZOS).convert("RGB")


def save(img: Image.Image, path: Path) -> Path:
    img.save(path)
    return path


def run(capsys, *cli_args: str) -> tuple[int, dict]:
    code = compare_renders.main(list(cli_args))
    captured = capsys.readouterr()
    data = json.loads(captured.out)
    return code, data


# --------------------------------------------------------------------------
# 正例
# --------------------------------------------------------------------------


def test_identical_images_pass(tmp_path, capsys):
    """同一画像同士は PASS する。"""
    a = save(make_image([RECT_A]), tmp_path / "a.png")
    b = save(make_image([RECT_A]), tmp_path / "b.png")
    code, data = run(capsys, str(a), str(b), "--dpi", str(DPI))
    assert code == 0
    assert data["pass"] is True
    assert data["overall"]["recall"] == 1.0
    assert data["overall"]["precision"] == 1.0
    assert data["shift"]["dx_px"] == 0
    assert data["shift"]["dy_px"] == 0


def test_subpixel_shift_with_aa_noise_pass(tmp_path, capsys):
    """1px未満のシフト＋軽微なAAノイズはPASSする（レンダの粒度差を過検出しない）。"""
    a = save(make_supersampled(RECT_A, phase_px=(0.0, 0.0)), tmp_path / "a.png")
    b = save(make_supersampled(RECT_A, phase_px=(0.4, -0.4)), tmp_path / "b.png")
    code, data = run(capsys, str(a), str(b), "--dpi", str(DPI))
    assert code == 0
    assert data["pass"] is True
    assert data["shift"]["magnitude_mm"] <= 0.2
    assert data["overall"]["recall"] >= 0.95
    assert data["overall"]["precision"] >= 0.95


# --------------------------------------------------------------------------
# 負例
# --------------------------------------------------------------------------


def test_blank_vs_shape_fails(tmp_path, capsys):
    """負例1: 空画像（全白） vs 図形入り → FAIL（recallが0近くまで落ちる）。"""
    correct = save(make_image([RECT_A]), tmp_path / "correct.png")
    blank = save(make_image([]), tmp_path / "blank.png")
    code, data = run(capsys, str(correct), str(blank), "--dpi", str(DPI))
    assert code == 1
    assert data["pass"] is False
    assert data["overall"]["recall"] < 0.95


def test_missing_shape_fails_via_recall(tmp_path, capsys):
    """負例2: 図形1つ欠落 → FAIL（recallで検出）。"""
    correct = save(make_image([RECT_A, RECT_B]), tmp_path / "correct.png")
    missing = save(make_image([RECT_A]), tmp_path / "missing.png")
    code, data = run(capsys, str(correct), str(missing), "--dpi", str(DPI))
    assert code == 1
    assert data["pass"] is False
    assert data["overall"]["recall"] < 0.95


def test_parallel_shift_0_3mm_fails_shift_threshold(tmp_path, capsys):
    """負例3: 0.3mm 平行移動（150dpiで約1.8px、整数画素シフトでは2px）→ FAIL（シフト閾値0.2mm超）。"""
    shift_px = round(0.3 * DPI / 25.4)
    assert shift_px == 2, "150dpiでの0.3mm換算が前提と食い違う"
    x0, y0, x1, y1 = RECT_A
    shifted_rect = (x0 + shift_px, y0 + shift_px, x1 + shift_px, y1 + shift_px)
    correct = save(make_image([RECT_A]), tmp_path / "correct.png")
    shifted = save(make_image([shifted_rect]), tmp_path / "shifted.png")
    code, data = run(capsys, str(correct), str(shifted), "--dpi", str(DPI))
    assert code == 1
    assert data["pass"] is False
    assert data["shift"]["magnitude_mm"] > 0.2
    assert data["shift"]["pass"] is False


def test_blank_vs_blank_is_undecidable(tmp_path, capsys):
    """負例5: 正解(a)が空白（前景ゼロ）→ たとえ比較対象(b)も空白でも判定不能として exit 2。
    ROIのrecall/precisionは分母ゼロで1.0になり見かけ上PASSしてしまうため、空白同士のPASSを
    比較そのものより手前で禁じる（実測: PDF切出し座標バグでこの「空白 vs 空白のPASS」が起きた）。"""
    blank_a = save(make_image([]), tmp_path / "blank_a.png")
    blank_b = save(make_image([]), tmp_path / "blank_b.png")
    code, data = run(capsys, str(blank_a), str(blank_b), "--dpi", str(DPI), "--roi", "5,5,50,50")
    assert code == 2
    assert "error" in data
    assert data["foregroundPxA"] == 0


def test_sparse_outline_aa_fringe_passes_via_relaxed_precision(tmp_path, capsys):
    """疎な前景（線の縁取りのみ、塗りつぶしなし）の正例: レンダラ差に相当する線幅のわずかな太さ
    違い（前景比5.7%・precision=0.918）は、既定のmin_precision=0.95では不合格になるが、疎な
    領域向けの緩和（min-precision-sparse既定0.85）でPASSする（実測: EMFのO窓・寸法値ケースで
    目視は完全一致なのにprecisionだけ僅差で不合格になった事象の再現）。"""
    rect = (60, 60, 180, 160)
    correct = save(make_outline_image(rect, width=8), tmp_path / "correct.png")
    thicker = save(make_outline_image(rect, width=9), tmp_path / "thicker.png")
    code, data = run(capsys, str(correct), str(thicker), "--dpi", str(DPI))
    assert code == 0
    assert data["pass"] is True
    assert data["overall"]["sparse"] is True
    assert data["overall"]["foregroundRatioA"] < 0.10
    assert 0.85 <= data["overall"]["precision"] < 0.95
    assert data["overall"]["minPrecisionApplied"] == 0.85


def test_sparse_but_genuinely_wrong_still_fails(tmp_path, capsys):
    """負例6: 疎な前景でも、precisionが緩和後の下限(0.85)より大きく不足するなら不合格のまま
    （疎判定が「なんでもPASSさせる無条件バイパス」でないことの証明）。"""
    rect = (60, 60, 180, 160)
    correct = save(make_outline_image(rect, width=2), tmp_path / "correct.png")
    much_thicker = save(make_outline_image(rect, width=4), tmp_path / "much_thicker.png")
    code, data = run(capsys, str(correct), str(much_thicker), "--dpi", str(DPI))
    assert code == 1
    assert data["pass"] is False
    assert data["overall"]["sparse"] is True
    assert data["overall"]["precision"] < 0.85


def test_dense_content_same_precision_gap_still_fails(tmp_path, capsys):
    """負例7: 疎な前景向けの緩和が密な前景へ漏れ出さないことの証明。塗りつぶし矩形（前景比21%、
    密）でsparseケースと同程度のprecision(約0.90)を作っても、疎判定にならず既定の
    min_precision=0.95がそのまま効いて不合格になる。"""
    correct = save(make_image([(60, 60, 180, 160)]), tmp_path / "correct.png")
    slightly_bigger = save(make_image([(57, 57, 183, 163)]), tmp_path / "bigger.png")
    code, data = run(capsys, str(correct), str(slightly_bigger), "--dpi", str(DPI))
    assert code == 1
    assert data["pass"] is False
    assert data["overall"]["sparse"] is False
    assert 0.85 <= data["overall"]["precision"] < 0.95


def test_expect_color_mismatch_fails(tmp_path, capsys):
    """負例4: 誤色（Bモードで期待 #FF8040 に対し実際は #FFFFFF）→ FAIL。"""
    img = Image.new("RGB", SIZE, (255, 255, 255))
    target = save(img, tmp_path / "blended.png")
    code, data = run(
        capsys,
        str(target),
        "--expect-color",
        "#FF8040",
        "--roi",
        "10,10,40,30",
        "--dpi",
        str(DPI),
    )
    assert code == 1
    assert data["mode"] == "B"
    assert data["pass"] is False
    assert data["roi"][0]["bad_px"] > 0


def test_expect_color_match_passes(tmp_path, capsys):
    """Bモードの対照: 期待どおりの色ならPASSする（負例4が色差そのものを検出している確認）。"""
    img = Image.new("RGB", SIZE, (0xFF, 0x80, 0x40))
    target = save(img, tmp_path / "blended_ok.png")
    code, data = run(
        capsys,
        str(target),
        "--expect-color",
        "#FF8040",
        "--roi",
        "10,10,40,30",
        "--dpi",
        str(DPI),
    )
    assert code == 0
    assert data["pass"] is True
    assert data["roi"][0]["bad_px"] == 0


# --------------------------------------------------------------------------
# 境界
# --------------------------------------------------------------------------


def test_size_mismatch_is_execution_error(tmp_path, capsys):
    """サイズ不一致 → 終了コード2（明示エラー、暗黙リサイズしない）。"""
    a = save(make_image([RECT_A], size=(240, 240)), tmp_path / "a.png")
    b = save(make_image([RECT_A], size=(200, 240)), tmp_path / "b.png")
    code, data = run(capsys, str(a), str(b), "--dpi", str(DPI))
    assert code == 2
    assert "error" in data
