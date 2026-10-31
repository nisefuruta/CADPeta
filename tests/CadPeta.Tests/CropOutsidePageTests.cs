// PDF のページが用紙より狭く丸められる（余白の無い A4 は 595pt = 209.903mm）と、窓のクロップの範囲が
// ページの外へ少しはみ出し、原点が負になる。そのクロップを各形式へ止まらずに書き出せることの試験。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Out.Emf;
using CadPeta.Out.Pdf;
using CadPeta.Out.Png;
using CadPeta.Transforms;
using CadPeta.UseCases;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public sealed class CropOutsidePageTests : IDisposable
{
    readonly TempFolder _temp = new();

    // PDF の座標は小数 4 桁で書くので、期待値との差をこの値（pt）まで許す
    const double PdfCoordinateTolPt = 0.001;

    public void Dispose() => _temp.Dispose();

    static IrDocument CroppedOutsidePage()
    {
        var ir = new IrDocument { WidthMm = 209.903, HeightMm = 297.039 };
        ir.Nodes.Add(Line(10, 10, 200, 280));
        CropTransform.Apply(ir, new RectMm(-0.05, -0.04, 209.95, 297.04), 0, new IrDiagnostics());
        return ir;
    }

    [Fact]
    public void ページの外へはみ出したクロップは負の原点のまま範囲にする()
    {
        var ir = CroppedOutsidePage();

        Assert.Equal((-0.05, -0.04, 210.0, 297.08), (ir.ViewMinXMm, ir.ViewMinYMm, Math.Round(ir.WidthMm, 9), Math.Round(ir.HeightMm, 9)));
    }

    [Fact]
    public void ページの外へはみ出したクロップはSVGの負の原点のviewBoxになる()
    {
        string svg = SvgWriter.Write(CroppedOutsidePage());

        Assert.Contains("viewBox=\"-0.05 -0.04 210 297.08\"", svg);
    }

    // 書き出した EMF の範囲（ヘッダの枠）が、はみ出したクロップの大きさ（210×297.08mm）になる
    [Fact]
    public void ページの外へはみ出したクロップをEMFへ書き出せる()
    {
        string path = _temp.File("outside.emf");

        new EmfWriter().Write(CroppedOutsidePage(), path, new IrDiagnostics());

        using var metafile = new Metafile(path);
        var header = metafile.GetMetafileHeader();
        Assert.Equal(210.0, header.Bounds.Width / header.DpiX * Units.MmPerInch, 0);
        Assert.Equal(297.08, header.Bounds.Height / header.DpiY * Units.MmPerInch, 0);
    }

    // PDF のページ（MediaBox）は、はみ出したクロップの大きさ（210×297.08mm）になる。PDF の座標は小数 4 桁で書く
    [Fact]
    public void ページの外へはみ出したクロップをPDFへ書き出せる()
    {
        string path = _temp.File("outside.pdf");

        new PdfWriter(new GdiGlyphOutliner()).Write(CroppedOutsidePage(), path, new IrDiagnostics());

        string pdf = File.ReadAllText(path, System.Text.Encoding.GetEncoding("iso-8859-1"));
        var mediaBox = Regex.Match(pdf, @"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]");
        Assert.Equal(210.0 * Units.PtPerMm, double.Parse(mediaBox.Groups[1].Value, CultureInfo.InvariantCulture), PdfCoordinateTolPt);
        Assert.Equal(297.08 * Units.PtPerMm, double.Parse(mediaBox.Groups[2].Value, CultureInfo.InvariantCulture), PdfCoordinateTolPt);
    }

    // PDF のページの座標変換（最初の cm）は、負の原点 (-0.05, -0.04)mm がページの左上の角に来るように図をずらし、
    // ページ全体のクリップも負の原点から始まる
    [Fact]
    public void ページの外へはみ出したクロップはPDFで負の原点の分だけ図をずらす()
    {
        string path = _temp.File("outside-origin.pdf");

        new PdfWriter(new GdiGlyphOutliner()).Write(CroppedOutsidePage(), path, new IrDiagnostics());

        string pdf = File.ReadAllText(path, System.Text.Encoding.GetEncoding("iso-8859-1"));
        var cm = Regex.Match(pdf, @"q\n[-\d.]+ 0 0 [-\d.]+ (?<e>[-\d.]+) (?<f>[-\d.]+) cm\n(?<clip>[^\n]*) re W n");
        Assert.True(cm.Success, "ページの座標変換とクリップが見つからない");
        Assert.Equal(0.05 * Units.PtPerMm, double.Parse(cm.Groups["e"].Value, CultureInfo.InvariantCulture), PdfCoordinateTolPt);
        Assert.Equal((297.08 - 0.04) * Units.PtPerMm, double.Parse(cm.Groups["f"].Value, CultureInfo.InvariantCulture), PdfCoordinateTolPt);
        Assert.Equal("-0.05 -0.04 210 297.08", cm.Groups["clip"].Value);
    }

    // PNG は線の始点と中点に描いた画素がある（負の原点でも内容を描く）
    [Fact]
    public void ページの外へはみ出したクロップをPNGへ書き出せる()
    {
        string path = _temp.File("outside.png");
        const double Dpi = 96;

        new PngWriter(Dpi).Write(CroppedOutsidePage(), path, new IrDiagnostics());

        using var bitmap = new Bitmap(path);
        Assert.True(IsDrawn(bitmap, 10, 10, Dpi), "線の始点 (10, 10)mm が描かれていない");
        Assert.True(IsDrawn(bitmap, 105, 145, Dpi), "線の中点 (105, 145)mm が描かれていない");
    }

    // ページ座標（mm）の点の周り 3×3 画素に、白でない画素があるか（クロップの原点 -0.05, -0.04mm から数える）
    static bool IsDrawn(Bitmap bitmap, double xMm, double yMm, double dpi)
    {
        int cx = (int)Math.Round((xMm + 0.05) * dpi / Units.MmPerInch), cy = (int)Math.Round((yMm + 0.04) * dpi / Units.MmPerInch);
        return Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => bitmap.GetPixel(cx + dx, cy + dy)))
            .Any(c => c.R < 128);
    }
}