// PDF ドライバのページの丸め（PdfPageGeometry）と、窓のクロップの写像（PlotWindowMapper）を合わせた位置が、
// 実機の PDF に CAD が印字した窓の位置と合うことの試験。期待値は実測の値（2026-10-30、
// tests/tools/regression/peta_window_rotation_compat.ps1 -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' の
// クロップ前のページの、CAD が書いた窓のクリップの中心。docs/validation/architecture-decisions.md）。
using System;
using CadPeta.Geometry;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class PdfPageGeometryTests
{
    // 実測の窓の中心と比べる許容（mm）。ページの丸めを扱わない今までの計算は、AutoCAD で最大 0.29mm ずれた
    const double MeasuredTolMm = 0.01;
    // ARES 2027 の実測との許容（mm）。ARES の印字は丸めの規則で説明しきれない差を 0.033mm まで持つ（2026-10-30 の 12 件の最大）
    const double AresMeasuredTolMm = 0.04;
    // 余白のある A3 横（AutoCAD と ARES 2027 の PDF デバイスの ISO_A3_(420.00_x_297.00_MM)。左右 5mm・上下 17mm）
    const double PaperW = 420, PaperH = 297, MarginX = 5, MarginY = 17;
    const double WindowW = 100, WindowH = 70;

    static (double X, double Y) CropCenter(PdfPointSnapping snapping, int quarterTurnsCw)
    {
        var page = PdfPageGeometry.Place(PaperW, PaperH, MarginX, MarginY,
            PaperW - 2 * MarginX, PaperH - 2 * MarginY, snapping);
        var rect = PlotWindowMapper.MapCenteredWindow(WindowW, WindowH, page.PrintCenterXMm, page.PrintCenterYMm,
            page.PageWidthMm, page.PageHeightMm, quarterTurnsCw);
        return ((rect.X0 + rect.X1) / 2, (rect.Y0 + rect.Y1) / 2);
    }

    // AutoCAD の PDF ドライバは、ページの大きさと印刷可能領域の原点を 1pt の整数へ丸める。プロット回転は反時計回りで、
    // 90 度は時計回りの 3 回、270 度は 1 回にあたる
    [Theory]
    [InlineData(0, 209.933, 148.613)]
    [InlineData(3, 148.613, 210.226)]
    [InlineData(2, 210.226, 148.426)]
    [InlineData(1, 148.426, 209.933)]
    public void AutoCADの窓のクロップは実測の印字の位置に合う(int quarterTurnsCw, double measuredX, double measuredY)
    {
        var center = CropCenter(PdfPointSnapping.PageSizeAndPrintableOrigin, quarterTurnsCw);

        Assert.Equal(measuredX, center.X, MeasuredTolMm);
        Assert.Equal(measuredY, center.Y, MeasuredTolMm);
    }

    // ARES 2027 の PDF ドライバは、ページの大きさだけを 1pt の整数へ丸める（原点は丸めない）。180/270 度は 0/90 度へ正規化する
    [Theory]
    [InlineData(0, 210.016, 148.534)]
    [InlineData(3, 148.505, 210.164)]
    public void ARESの窓のクロップは実測の印字の位置に合う(int quarterTurnsCw, double measuredX, double measuredY)
    {
        var center = CropCenter(PdfPointSnapping.PageSize, quarterTurnsCw);

        Assert.Equal(measuredX, center.X, AresMeasuredTolMm);
        Assert.Equal(measuredY, center.Y, AresMeasuredTolMm);
    }

    // 丸めない製品（IJCAD・BricsCAD は実機で測っていない）は、用紙の公称の大きさと余白のまま
    [Fact]
    public void 丸めない製品は用紙の公称の大きさと余白のまま()
    {
        var page = PdfPageGeometry.Place(PaperW, PaperH, MarginX, MarginY,
            PaperW - 2 * MarginX, PaperH - 2 * MarginY, PdfPointSnapping.None);

        Assert.Equal((420.0, 297.0, 210.0, 148.5), (page.PageWidthMm, page.PageHeightMm, page.PrintCenterXMm, page.PrintCenterYMm));
    }

    // ページの大きさは 1pt の整数の近い方へ丸める（A4 の幅 595.28pt は 595pt へ切り下がり、A3 の幅 1190.55pt は 1191pt へ切り上がる）
    [Theory]
    [InlineData(210.0, 209.902778)]
    [InlineData(420.0, 420.158333)]
    public void ページの大きさは1ポイントの整数の近い方へ丸める(double paperWidthMm, double expectedPageWidthMm)
    {
        var page = PdfPageGeometry.Place(paperWidthMm, 297, 0, 0, paperWidthMm, 297, PdfPointSnapping.PageSize);

        Assert.Equal(expectedPageWidthMm, page.PageWidthMm, 5);
    }

    // 原点の丸めはページの丸めと別に確かめる（窓の位置の試験だけでは、両方の誤差が打ち消して合う場合を見分けられない）。
    // 余白 5.8mm（16.44pt）は、原点を丸めると 16pt（5.644mm）になり、丸めなければ 5.8mm のまま
    [Fact]
    public void ページと原点を丸める製品は印刷可能領域の原点も1ポイントの整数にする()
    {
        var page = PdfPageGeometry.Place(210, 297, 5.8, 5.8, 198.4, 261.4, PdfPointSnapping.PageSizeAndPrintableOrigin);

        Assert.Equal(16 * Units.MmPerPt + 198.4 / 2, page.PrintCenterXMm, 9);
        Assert.Equal(16 * Units.MmPerPt + 261.4 / 2, page.PrintCenterYMm, 9);
    }

    [Fact]
    public void ページだけを丸める製品は印刷可能領域の原点を丸めない()
    {
        var page = PdfPageGeometry.Place(210, 297, 5.8, 5.8, 198.4, 261.4, PdfPointSnapping.PageSize);

        Assert.Equal(5.8 + 198.4 / 2, page.PrintCenterXMm, 9);
        Assert.Equal(5.8 + 261.4 / 2, page.PrintCenterYMm, 9);
    }

    // ちょうど .5pt のときは 0 から遠い方へ丸める（既定の銀行型の丸めなら 594.5pt は 594pt になる）
    [Fact]
    public void ちょうど半ポイントのページは大きい方へ丸める()
    {
        double widthMm = 594.5 * Units.MmPerPt;

        var page = PdfPageGeometry.Place(widthMm, 297, 0, 0, widthMm, 297, PdfPointSnapping.PageSize);

        Assert.Equal(595 * Units.MmPerPt, page.PageWidthMm, 9);
    }
}