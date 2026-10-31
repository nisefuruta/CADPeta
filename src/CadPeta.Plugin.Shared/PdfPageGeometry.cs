// 発行した PDF のページの大きさと、印刷可能領域の中心。PDF ドライバは、ページの大きさや印刷可能領域の原点を
// PDF の 1 ポイント（1/72 インチ）の整数へ丸めて書くことがあり、窓のクロップは用紙の公称の値ではなく丸めた値で
// 計算しないと、丸めの差だけずれる（回転 180 度ではページが丸めた大きさの中心で回るので、差が反対の側で足し合わされる）。
// どこを丸めるかは製品の PDF ドライバの実測で決め、各製品の *PluginDefaults.PdfPointSnapping に置く。
using System;
using CadPeta.Geometry;

namespace CadPeta.Plugin.Shared;

/// <summary>PDF ドライバが 1pt の整数へ丸めて書く値。</summary>
public enum PdfPointSnapping
{
    /// <summary>丸めない（実機で確かめていない製品）。</summary>
    None,
    /// <summary>ページの大きさだけを丸める。</summary>
    PageSize,
    /// <summary>ページの大きさと、印刷可能領域の原点（左下の余白）を丸める。</summary>
    PageSizeAndPrintableOrigin,
}

public static class PdfPageGeometry
{
    /// <summary>
    /// 用紙の公称の大きさと余白（用紙座標: 左下原点・Y上向き、mm）から、発行される PDF のページの大きさと、
    /// 印刷可能領域の中心を返す。窓は印刷可能領域の公称の大きさの中央へ置かれ、その原点だけが丸められる。
    /// </summary>
    public static (double PageWidthMm, double PageHeightMm, double PrintCenterXMm, double PrintCenterYMm) Place(
        double paperWidthMm, double paperHeightMm, double marginLeftMm, double marginBottomMm,
        double printableWidthMm, double printableHeightMm, PdfPointSnapping snapping)
    {
        bool snapPage = snapping != PdfPointSnapping.None;
        bool snapOrigin = snapping == PdfPointSnapping.PageSizeAndPrintableOrigin;
        double pageW = snapPage ? ToWholePoints(paperWidthMm) : paperWidthMm;
        double pageH = snapPage ? ToWholePoints(paperHeightMm) : paperHeightMm;
        double originX = snapOrigin ? ToWholePoints(marginLeftMm) : marginLeftMm;
        double originY = snapOrigin ? ToWholePoints(marginBottomMm) : marginBottomMm;
        return (pageW, pageH, originX + printableWidthMm / 2, originY + printableHeightMm / 2);
    }

    static double ToWholePoints(double mm) => Math.Round(mm * Units.PtPerMm, MidpointRounding.AwayFromZero) * Units.MmPerPt;
}
