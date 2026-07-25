"""PETA E2E 総点検「三点検証」(独立オラクル・可視再現・目視) 向けの小さな前処理ユーティリティ。

peta_option_g8_verify.ps1 から呼ばれる。判定（PASS/FAIL）そのものは一切ここでは行わない。
判定は compare_renders.py（同ディレクトリ）に集約し、ここでは以下だけを行う:

  - 正解 PDF（accoreconsole -PLOT の出力）を、PETA 側の成果物と同一の内容ボックス・
    同一 DPI・同一画素寸法の白背景 PNG へ正規化する（用紙窓のオフセット切出し／
    Fit+Center の逆算切出し／M5 の白余白合成／画素寸法の強制一致）。
  - B（EMF合成下地色）ケースの期待 RGB を「元色×0.5＋下地×0.5」で算出する（元色は
    正解 PDF の対象 ROI 平均色から、白下地でのブレンドを逆算して求める）。
  - 目視判定用の差分画像・コンタクトシートを組み立てる。

各サブコマンドは標準出力へ JSON を1つだけ出す（呼び出し側 PowerShell が機械で読む）。
終了コード: 0=成功、1=入力不整合（寸法不一致など）、2=引数エラー。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

import numpy as np
import pypdfium2 as pdfium
from PIL import Image, ImageDraw

from compare_renders import foreground_mask, load_image, parse_color, parse_roi, roi_to_px  # 既存の画素処理を流用する

MM_PER_INCH = 25.4
CSS_DPI = 96.0  # Chromium が SVG の mm を px へ解決する既定の解像度


def _print(result: dict) -> None:
    print(json.dumps(result, ensure_ascii=False, indent=2))


def _parse_point(text: str) -> tuple[float, float]:
    parts = text.split(",")
    if len(parts) < 2:
        raise argparse.ArgumentTypeError(f"点として読めない: {text!r}")
    return float(parts[0]), float(parts[1])


def _parse_window(text: str) -> tuple[float, float, float, float]:
    """"x0,y0,x1,y1" 形式の窓を読む（--window 引数用。--roi と違い1点目<2点目を強制しない）。"""
    parts = [p.strip() for p in text.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError(f"--window は x0,y0,x1,y1 の4値: {text!r}")
    return tuple(float(p) for p in parts)  # type: ignore[return-value]


def _parse_mm_pair(text: str) -> tuple[float, float]:
    parts = text.split(",")
    if len(parts) != 2:
        raise argparse.ArgumentTypeError(f"W,H の2値が必要: {text!r}")
    return float(parts[0]), float(parts[1])


# --------------------------------------------------------------------------
# レンダ・切出し
# --------------------------------------------------------------------------


def render_paper_window(pdf_path: Path, dpi: float, window_mm: tuple[float, float, float, float],
                         off_x: float, off_y: float, page: int = 0) -> Image.Image:
    """全ブリード用紙へ1:1でプロットした正解PDFから、図面mm窓を切り出す。

    render_crop.py (tests/tools/pdf-emf) と同じ換算式（図面原点の用紙内オフセット既定値
    10.0, 8.5mm は同スクリプトの実測値を踏襲）。
    """
    x0, y0, x1, y1 = window_mm
    doc = pdfium.PdfDocument(str(pdf_path))
    try:
        pg = doc[page]
        page_w_pt, page_h_pt = pg.get_size()
        scale = dpi / 72.0
        img = pg.render(scale=scale).to_pil().convert("RGB")
    finally:
        doc.close()
    mm2pt = 72.0 / MM_PER_INCH
    px0 = (x0 + off_x) * mm2pt * scale
    px1 = (x1 + off_x) * mm2pt * scale
    py0 = (page_h_pt - (y1 + off_y) * mm2pt) * scale
    py1 = (page_h_pt - (y0 + off_y) * mm2pt) * scale
    return img.crop((round(px0), round(py0), round(px1), round(py1)))


def render_fit_centered(pdf_path: Path, dpi: float, content_w_mm: float, content_h_mm: float,
                         page: int = 0) -> tuple[Image.Image, dict]:
    """「用紙合わせ(Fit)・中央(Center)」でプロットした正解PDFから、既知の内容ボックス
    (content_w_mm x content_h_mm) に相当する領域だけを、指定DPIの画素へ正しい縮尺で切り出す。

    Fit は用紙寸法に対する一様倍率、Center は用紙中央合わせなので、
    倍率 = min(用紙幅/内容幅, 用紙高/内容高)、配置後の余白は上下左右で等しい。
    """
    doc = pdfium.PdfDocument(str(pdf_path))
    try:
        pg = doc[page]
        page_w_pt, page_h_pt = pg.get_size()
        page_w_mm = page_w_pt / 72.0 * MM_PER_INCH
        page_h_mm = page_h_pt / 72.0 * MM_PER_INCH
        fit_scale = min(page_w_mm / content_w_mm, page_h_mm / content_h_mm)
        equiv_dpi = dpi * fit_scale
        img = pg.render(scale=equiv_dpi / 72.0).to_pil().convert("RGB")
    finally:
        doc.close()
    placed_w_px = content_w_mm * fit_scale * dpi / MM_PER_INCH
    placed_h_px = content_h_mm * fit_scale * dpi / MM_PER_INCH
    off_x_px = (img.width - placed_w_px) / 2.0
    off_y_px = (img.height - placed_h_px) / 2.0
    box = (round(off_x_px), round(off_y_px), round(off_x_px + placed_w_px), round(off_y_px + placed_h_px))
    meta = {
        "fitScale": fit_scale,
        "pageWidthMm": page_w_mm,
        "pageHeightMm": page_h_mm,
        "contentWidthMm": content_w_mm,
        "contentHeightMm": content_h_mm,
    }
    return img.crop(box), meta


def pad_white_mm(img: Image.Image, dpi: float, pad_mm: float) -> Image.Image:
    """画像の四辺へ pad_mm 分の白を足す（M5 ケース: 100x70 の正解へ5mmずつ足して110x80と比べる）。"""
    px = round(pad_mm * dpi / MM_PER_INCH)
    w, h = img.size
    canvas = Image.new("RGB", (w + 2 * px, h + 2 * px), (255, 255, 255))
    canvas.paste(img, (px, px))
    return canvas


def normalize_canvas(img: Image.Image, target_w: int, target_h: int) -> tuple[Image.Image, int]:
    """レンダラ(PDFium/GDI/Chromium)ごとの丸め差を吸収し、画素寸法を強制的に一致させる。

    判定ロジックではない（ここで内容を比較・加工しない。白背景へ左上合わせで載せるだけ）。
    差が大きい（3px超）ときは戻り値の diff で呼び出し側が気づけるようにする。
    """
    w, h = img.size
    diff = max(abs(w - target_w), abs(h - target_h))
    if (w, h) == (target_w, target_h):
        return img, diff
    canvas = Image.new("RGB", (target_w, target_h), (255, 255, 255))
    paste_w, paste_h = min(w, target_w), min(h, target_h)
    canvas.paste(img.crop((0, 0, paste_w, paste_h)), (0, 0))
    return canvas, diff


# --------------------------------------------------------------------------
# B（EMF合成下地色）ケース: 期待RGBの算出
# --------------------------------------------------------------------------


def roi_mean_rgb(img: Image.Image, dpi: float, roi_mm: tuple[float, float, float, float]) -> tuple[float, float, float]:
    px0, py0, px1, py1 = roi_to_px(roi_mm, dpi, (img.height, img.width))
    if px1 <= px0 or py1 <= py0:
        raise ValueError(f"ROI が画像範囲に入らない: {roi_mm} → px=({px0},{py0},{px1},{py1})")
    arr = np.asarray(img.crop((px0, py0, px1, py1)), dtype=np.float64).reshape(-1, 3)
    mean = arr.mean(axis=0)
    return float(mean[0]), float(mean[1]), float(mean[2])


def derive_original_from_white_blend(white_blended: tuple[float, float, float]) -> tuple[float, float, float]:
    """正解PDFは全ブリード用紙(白)へネイティブにプロットしたものなので、半透明の実体は
    「元色×0.5＋白×0.5」で既に合成された値になっている。ここから元色を逆算する。"""
    return tuple(max(0.0, min(255.0, 2.0 * c - 255.0)) for c in white_blended)  # type: ignore[return-value]


def expected_blend(original: tuple[float, float, float], base: tuple[int, int, int]) -> tuple[int, int, int]:
    return tuple(int(round(o * 0.5 + b * 0.5)) for o, b in zip(original, base))  # type: ignore[return-value]


# --------------------------------------------------------------------------
# 目視判定材料: 差分画像・コンタクトシート
# --------------------------------------------------------------------------


def save_diff(img_a: Image.Image, img_b: Image.Image, out_path: Path) -> None:
    a = np.asarray(img_a.convert("RGB"), dtype=np.int16)
    b = np.asarray(img_b.convert("RGB"), dtype=np.int16)
    if a.shape != b.shape:
        raise ValueError(f"寸法が違うため差分を作れない a={a.shape} b={b.shape}")
    diff = np.abs(a - b).max(axis=-1)
    gray = np.clip(255 - diff, 0, 255).astype(np.uint8)
    Image.fromarray(gray, mode="L").convert("RGB").save(out_path)


def build_contact_sheet(items: list[tuple[str, str | None]], out_path: Path, title: str | None, thumb_h: int = 360) -> None:
    """label=path のペア列を横並びに配置した確認用PNGを作る。path が None/MISSING は
    理由をプレースホルダへ書く（判定者が目視で埋めるチェックリストと対にして使う）。"""
    pad = 12
    caption_h = 20
    title_h = 28 if title else 0
    thumbs: list[tuple[str, Image.Image]] = []
    for label, path in items:
        if path and not path.startswith("MISSING:") and Path(path).is_file():
            im = Image.open(path).convert("RGB")
            ratio = thumb_h / im.height
            im = im.resize((max(1, round(im.width * ratio)), thumb_h))
        else:
            reason = path[len("MISSING:"):] if path and path.startswith("MISSING:") else "N/A"
            im = Image.new("RGB", (round(thumb_h * 0.75), thumb_h), (232, 232, 232))
            d = ImageDraw.Draw(im)
            d.rectangle((0, 0, im.width - 1, im.height - 1), outline=(160, 160, 160))
            d.text((8, 8), reason, fill=(170, 0, 0))
        thumbs.append((label, im))

    total_w = sum(im.width for _, im in thumbs) + pad * (len(thumbs) + 1)
    total_h = title_h + caption_h + thumb_h + pad * 2
    sheet = Image.new("RGB", (max(total_w, 1), total_h), (255, 255, 255))
    d = ImageDraw.Draw(sheet)
    if title:
        d.text((pad, pad), title, fill=(0, 0, 0))
    x = pad
    y_caption = pad + title_h
    y_img = y_caption + caption_h
    for label, im in thumbs:
        d.text((x, y_caption), label, fill=(0, 0, 0))
        sheet.paste(im, (x, y_img))
        x += im.width + pad
    sheet.save(out_path)


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def cmd_paper_window(args: argparse.Namespace) -> int:
    img = render_paper_window(args.pdf, args.dpi, args.window, args.off_x, args.off_y, args.page)
    img.save(args.out)
    _print({"path": str(args.out), "width": img.width, "height": img.height})
    return 0


def cmd_fit_centered(args: argparse.Namespace) -> int:
    img, meta = render_fit_centered(args.pdf, args.dpi, args.content_mm[0], args.content_mm[1], args.page)
    img.save(args.out)
    meta.update({"path": str(args.out), "width": img.width, "height": img.height})
    _print(meta)
    return 0


def cmd_pad(args: argparse.Namespace) -> int:
    img = load_image(args.input, args.dpi, args.page)
    out = pad_white_mm(img, args.dpi, args.pad_mm)
    out.save(args.out)
    _print({"path": str(args.out), "width": out.width, "height": out.height})
    return 0


def cmd_render(args: argparse.Namespace) -> int:
    img = load_image(args.input, args.dpi, args.page)
    diff = None
    if args.target:
        img, diff = normalize_canvas(img, args.target[0], args.target[1])
    img.save(args.out)
    result = {"path": str(args.out), "width": img.width, "height": img.height}
    if diff is not None:
        result["normalizeDiffPx"] = diff
    _print(result)
    return 0


def cmd_png_check(args: argparse.Namespace) -> int:
    """PNG の完全性検査。ハングしたブラウザを停止した後の screenshot は途中書きであり得るため、
    「ファイルが在る」ことを完了扱いにせず、全画素のデコード成功と期待画素寸法の一致を確認する
    （途中書きの PNG は IEND 欠落や途中チャンク破損でデコードが失敗する）。"""
    from PIL import Image

    try:
        im = Image.open(args.input)
        im.load()  # 全画素デコード（遅延読みのままだと破損を検出しない）
    except Exception as ex:  # 破損の型は複数あるため理由を返して非0終了する
        _print({"ok": False, "reason": f"デコード失敗: {ex}"})
        return 1
    dw = abs(im.size[0] - args.width)
    dh = abs(im.size[1] - args.height)
    if dw > args.tolerance or dh > args.tolerance:
        _print({"ok": False, "reason": f"画素寸法不一致: 実 {im.size[0]}x{im.size[1]} 期待 {args.width}x{args.height}±{args.tolerance}"})
        return 1
    _print({"ok": True, "width": im.size[0], "height": im.size[1]})
    return 0


def cmd_page_size(args: argparse.Namespace) -> int:
    """PDF ページ寸法を mm で返す（モデル全体ケースの「PETA SVG 寸法 == オラクル用紙寸法」照合用）。"""
    import pypdfium2 as pdfium

    pdf = pdfium.PdfDocument(str(args.input))
    page = pdf[args.page]
    w_pt, h_pt = page.get_size()
    _print({"wMm": w_pt * 25.4 / 72.0, "hMm": h_pt * 25.4 / 72.0})
    return 0


def cmd_resize(args: argparse.Namespace) -> int:
    """既存PNGを指定画素寸法へ縮尺リサイズする（normalizeとは別物: normalizeは同一縮尺内の丸め差を
    白パディング/切り詰めだけで吸収するが、こちらは実際に画素を再配分する縮尺変換）。

    Model窓/Model全体のオラクルは Fit+Center（1:1でない）でプロットするため、切出し直後の画像は
    「用紙に収まるよう縮小された実寸」になっている。PETA側(EMF/SVG/PDF)は常に実寸(1:1)で出力される
    ため、オラクル側だけ目標画素寸法へ拡大リサイズしないと、内容が左上に小さく写り込んだ画像同士を
    比較してしまう（実測で判明した欠陥。normalizeのパディングでは直せない）。"""
    img = Image.open(args.input).convert("RGB")
    out = img.resize((args.width, args.height), Image.LANCZOS)
    out.save(args.out)
    _print({"path": str(args.out), "width": out.width, "height": out.height, "srcSizePx": [img.width, img.height]})
    return 0


def cmd_normalize(args: argparse.Namespace) -> int:
    img = Image.open(args.input).convert("RGB")
    out, diff = normalize_canvas(img, args.width, args.height)
    out.save(args.out)
    _print({"path": str(args.out), "width": out.width, "height": out.height, "normalizeDiffPx": diff})
    return 0


def cmd_fg_count(args: argparse.Namespace) -> int:
    """切出しPNGの前景画素数を数える（目視相当の非空確認。判定はしない）。

    peta_option_g8_verify.ps1 が、オラクル切出し・PETA PDF切出しの直後にこれを呼び、クロップ
    座標が誤って空白画像を作っていないかを比較へ進む前に確認する（欠陥2の再発防止）。"""
    img = load_image(args.input, args.dpi, args.page)
    mask = foreground_mask(img, args.threshold)
    count = int(mask.sum())
    _print({"path": str(args.input), "foregroundPx": count, "sizePx": [img.width, img.height], "threshold": args.threshold})
    return 0


def cmd_roi_color(args: argparse.Namespace) -> int:
    img = load_image(args.input, args.dpi, args.page)
    r, g, b = roi_mean_rgb(img, args.dpi, args.roi)
    _print({"meanRgb": [r, g, b], "hex": "#%02X%02X%02X" % (round(r), round(g), round(b))})
    return 0


def cmd_expected_b_color(args: argparse.Namespace) -> int:
    img = load_image(args.input, args.dpi, args.page)
    white_blended = roi_mean_rgb(img, args.dpi, args.roi)
    original = derive_original_from_white_blend(white_blended)
    expected = expected_blend(original, args.base)
    _print({
        "oracleWhiteBlendRgb": list(white_blended),
        "derivedOriginalRgb": list(original),
        "baseRgb": list(args.base),
        "expectedRgb": list(expected),
        "expectedHex": "#%02X%02X%02X" % expected,
    })
    return 0


def cmd_diff(args: argparse.Namespace) -> int:
    img_a = Image.open(args.a).convert("RGB")
    img_b = Image.open(args.b).convert("RGB")
    try:
        save_diff(img_a, img_b, args.out)
    except ValueError as e:
        print(f"実行できない: {e}", file=sys.stderr)
        return 1
    _print({"path": str(args.out)})
    return 0


def cmd_contact_sheet(args: argparse.Namespace) -> int:
    items: list[tuple[str, str | None]] = []
    for raw in args.item:
        if "=" not in raw:
            raise argparse.ArgumentTypeError(f"--item は label=path 形式: {raw!r}")
        label, path = raw.split("=", 1)
        items.append((label, path))
    build_contact_sheet(items, args.out, args.title)
    _print({"path": str(args.out), "items": len(items)})
    return 0


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("paper-window", help="全ブリード用紙1:1プロットの正解PDFから図面mm窓を切り出す")
    p.add_argument("--pdf", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--window", type=_parse_window, required=True, help="x0,y0,x1,y1 (mm, 図面座標)")
    p.add_argument("--off-x", type=float, default=10.0)
    p.add_argument("--off-y", type=float, default=8.5)
    p.set_defaults(func=cmd_paper_window)

    p = sub.add_parser("fit-centered", help="Fit+Centerでプロットした正解PDFから既知の内容ボックスを切り出す")
    p.add_argument("--pdf", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--content-mm", type=_parse_mm_pair, required=True, help="内容ボックスの W,H (mm)")
    p.set_defaults(func=cmd_fit_centered)

    p = sub.add_parser("pad", help="四辺へ白を足す(M5ケース用)")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--pad-mm", type=float, required=True)
    p.set_defaults(func=cmd_pad)

    p = sub.add_parser("render", help="PDF/PNGを指定DPIのPNGへ変換し、必要なら画素寸法を正規化する")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--target", type=lambda s: tuple(int(x) for x in s.split(",")), default=None, help="W,H(px)")
    p.set_defaults(func=cmd_render)

    p = sub.add_parser("png-check", help="PNGの完全性検査（全画素デコード+期待画素寸法）")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--width", type=int, required=True)
    p.add_argument("--height", type=int, required=True)
    p.add_argument("--tolerance", type=int, default=2)
    p.set_defaults(func=cmd_png_check)

    p = sub.add_parser("page-size", help="PDFページ寸法をmmで返す")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--page", type=int, default=0)
    p.set_defaults(func=cmd_page_size)

    p = sub.add_parser("resize", help="既存PNGを指定画素寸法へ縮尺リサイズする(Model系オラクルのFit+Center逆変換用)")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--width", type=int, required=True)
    p.add_argument("--height", type=int, required=True)
    p.set_defaults(func=cmd_resize)

    p = sub.add_parser("normalize", help="既存PNGの画素寸法を強制的に一致させる")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--width", type=int, required=True)
    p.add_argument("--height", type=int, required=True)
    p.set_defaults(func=cmd_normalize)

    p = sub.add_parser("fg-count", help="前景画素数を数える（クロップが非空か確認する目視相当チェック）")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--threshold", type=int, default=250)
    p.set_defaults(func=cmd_fg_count)

    p = sub.add_parser("roi-color", help="ROI内の平均RGBを出す")
    p.add_argument("--in", dest="input", type=Path, required=True)
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--roi", type=parse_roi, required=True)
    p.set_defaults(func=cmd_roi_color)

    p = sub.add_parser("expected-b-color", help="B(EMF合成下地色)ケースの期待RGBを算出する")
    p.add_argument("--in", dest="input", type=Path, required=True, help="正解PDF由来のPNG(窓局所座標)")
    p.add_argument("--dpi", type=float, default=150)
    p.add_argument("--page", type=int, default=0)
    p.add_argument("--roi", type=parse_roi, required=True)
    p.add_argument("--base", type=parse_color, required=True, help="下地色 #RRGGBB")
    p.set_defaults(func=cmd_expected_b_color)

    p = sub.add_parser("diff", help="差分可視化PNGを作る")
    p.add_argument("--a", type=Path, required=True)
    p.add_argument("--b", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.set_defaults(func=cmd_diff)

    p = sub.add_parser("contact-sheet", help="判定材料のコンタクトシートを作る")
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--title", default=None)
    p.add_argument("--item", action="append", required=True, help="label=path (path が MISSING:理由 ならプレースホルダを描く)")
    p.set_defaults(func=cmd_contact_sheet)

    return ap


def main(argv: list[str] | None = None) -> int:
    ap = build_parser()
    args = ap.parse_args(argv)
    try:
        return args.func(args)
    except (ValueError, FileNotFoundError) as e:
        print(f"実行できない: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
