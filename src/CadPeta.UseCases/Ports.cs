// 境界ポート（ユースケース層が所有する interface）。
// 取得アダプタ（構造SVG書き出しのDB直接取得とPDF印刷経路のPDF解析）と
// 出力アダプタ（EMF等）はこれを実装し、合成ルートが結線する。
using System.Collections.Generic;
using CadPeta.Ir;

namespace CadPeta.UseCases;

/// <summary>取得ポート: 入力（PDFパス等）から SVG IR を得る。</summary>
public interface IIrAcquirer
{
    IrDocument Acquire(string sourcePath, IrDiagnostics diag);
}

/// <summary>出力ポート: SVG IR をファイルへ書き出す。</summary>
public interface IIrOutput
{
    void Write(IrDocument ir, string outputPath, IrDiagnostics diag);
}

/// <summary>
/// グリフ輪郭ポート: 文字ランをグリフ輪郭のセグメント列へ展開する。
/// 出力はテキスト空間（原点=ベースライン左端、X右・Y下向き、単位mm、幅係数は未適用）。
/// 輪郭を取れない場合は null を返し、呼び出し側が代替（フォント保持等）へフォールバックして
/// その事実を診断ログへ記録する。配置（幅係数→回転→平行移動）は GlyphPlacement で行う。
/// </summary>
public interface IGlyphOutliner
{
    List<IrSegment>? Outline(GlyphRun run);
}

/// <summary>
/// グリフ輪郭ポートへ渡す文字ラン。TrueType（フォントファミリ指定）と
/// SHX（フォントファイルパス指定）のどちらかで作る。
/// </summary>
public sealed record GlyphRun
{
    public string Text { get; init; } = "";

    /// <summary>TrueType のフォントファミリ名。SHX ランでは null。</summary>
    public string? FontFamily { get; init; }

    /// <summary>SHX フォントファイルの絶対パス。TrueType ランでは null。</summary>
    public string? ShxPath { get; init; }

    /// <summary>ビッグフォント（2バイト文字用 SHX）の絶対パス。併用しない場合は null。</summary>
    public string? BigFontPath { get; init; }

    /// <summary>字高（mm）。TrueType では em サイズ、SHX ではキャップハイト（CAD の文字高さ）。</summary>
    public double FontSizeMm { get; init; }

    /// <summary>幅係数。輪郭の座標には適用せず、配置時に GlyphPlacement が適用する。</summary>
    public double WidthFactor { get; init; } = 1.0;

    /// <summary>
    /// 配置済み座標系（回転前）でのグリフX位置列（mm）。null なら自然な字送りを使う。
    /// IrText.GlyphXsMm と同じ規約。
    /// </summary>
    public double[]? GlyphXsMm { get; init; }

    public bool IsShx => ShxPath != null;

    public static GlyphRun TrueType(string text, string fontFamily, double fontSizeMm,
                                    double widthFactor, double[]? glyphXsMm) =>
        new()
        {
            Text = text,
            FontFamily = fontFamily,
            FontSizeMm = fontSizeMm,
            WidthFactor = widthFactor,
            GlyphXsMm = glyphXsMm,
        };

    public static GlyphRun Shx(string text, string shxPath, string? bigFontPath, double capHeightMm,
                               double widthFactor, double[]? glyphXsMm) =>
        new()
        {
            Text = text,
            ShxPath = shxPath,
            BigFontPath = bigFontPath,
            FontSizeMm = capHeightMm,
            WidthFactor = widthFactor,
            GlyphXsMm = glyphXsMm,
        };
}
