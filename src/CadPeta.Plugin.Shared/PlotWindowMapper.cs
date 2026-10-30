// 窓プロットの配置計算: 利用者指定の窓（プロット領域）が
// 発行 PDF ページ上のどこへ載るかを算出し、変換層クロップ（CropTransform）へ渡す
// 矩形を求める純粋数学。CAD 型を署名に持たない（単体テスト対象、実機値との
// 突き合わせ済み）。
//
// 前提とする配置規則（PlotOrchestrator が窓プロット時に強制する設定と対応）:
// - 窓は印刷可能領域へ尺度 s で配置され、中央合わせ（SetPlotCentered=true）
// - 用紙座標は左下原点・Y上向き（プロット座標系）、IR 座標はページ左上原点・Y下向き
// - プロット回転（PlotRotation）は「用紙→表示ページ」の四半回転として写す
using System;
using CadPeta.Geometry;

namespace CadPeta.Plugin.Shared;

public static class PlotWindowMapper
{
    /// <summary>
    /// 窓を印刷可能領域いっぱいへ収める等倍率（ScaleToFit 相当）。
    /// 単位は「用紙mm / 窓の図面単位」。窓は表示フレームの矩形なので、印刷可能領域も
    /// 表示フレームの寸法（PaperMetrics.DisplayPrintable*、回転で縦横が入れ替わった値）を渡す。
    /// </summary>
    public static double FitScale(double windowWidthDu, double windowHeightDu,
                                  double printableWidthMm, double printableHeightMm)
    {
        if (windowWidthDu <= 0 || windowHeightDu <= 0)
            throw new ArgumentException(
                $"窓の寸法が正でない: {windowWidthDu}×{windowHeightDu}（図面単位）");
        if (printableWidthMm <= 0 || printableHeightMm <= 0)
            throw new ArgumentException(
                $"印刷可能領域の寸法が正でない: {printableWidthMm}×{printableHeightMm}mm");
        return Math.Min(printableWidthMm / windowWidthDu, printableHeightMm / windowHeightDu);
    }

    /// <summary>
    /// 印刷可能領域の中央へ配置した内容矩形（窓×尺度）を、表示ページの IR mm 座標
    /// （左上原点・Y下向き）で返す。
    /// プロット回転はプロット像ごと用紙上で回り、表示ページ（PDF の表示座標）では
    /// 像が正立へ戻るため、内容寸法は表示フレームで窓の縦横のまま変わらない。
    /// 回すのは中心点とページ寸法だけである（accoreconsole 実機の rot73=1 で実測確定:
    /// 窓 100×70mm はページ 297×420 上でも 100×70 のまま）。
    /// </summary>
    /// <param name="contentWidthMm">窓幅×尺度（表示フレームの mm）。</param>
    /// <param name="contentHeightMm">窓高×尺度（表示フレームの mm）。</param>
    /// <param name="printCenterXMm">印刷可能領域の中心X（回転前の用紙座標、左下原点）。</param>
    /// <param name="printCenterYMm">印刷可能領域の中心Y（同上、Y上向き）。</param>
    /// <param name="paperWidthMm">回転前の用紙幅。</param>
    /// <param name="paperHeightMm">回転前の用紙高。</param>
    /// <param name="quarterTurnsCw">用紙→表示ページの時計回り四半回転数（0〜3）。</param>
    public static RectMm MapCenteredWindow(
        double contentWidthMm, double contentHeightMm,
        double printCenterXMm, double printCenterYMm,
        double paperWidthMm, double paperHeightMm,
        int quarterTurnsCw)
    {
        if (contentWidthMm <= 0 || contentHeightMm <= 0)
            throw new ArgumentException(
                $"内容矩形の寸法が正でない: {contentWidthMm}×{contentHeightMm}mm");
        if (paperWidthMm <= 0 || paperHeightMm <= 0)
            throw new ArgumentException($"用紙寸法が正でない: {paperWidthMm}×{paperHeightMm}mm");

        // 用紙座標（左下原点・Y上向き）→ ページ座標（左上原点・Y下向き）
        double cx = printCenterXMm;
        double cy = paperHeightMm - printCenterYMm;
        double pageW = paperWidthMm, pageH = paperHeightMm;

        // 時計回り四半回転を1回ずつ適用する（左上原点・Y下向きの座標で
        // (x,y) → (pageH - y, x)、ページ寸法は縦横が入れ替わる。内容寸法は表示フレームのため不変）
        int turns = ((quarterTurnsCw % 4) + 4) % 4;
        for (int i = 0; i < turns; i++)
        {
            (cx, cy) = (pageH - cy, cx);
            (pageW, pageH) = (pageH, pageW);
        }

        return new RectMm(cx - contentWidthMm / 2, cy - contentHeightMm / 2,
                          cx + contentWidthMm / 2, cy + contentHeightMm / 2);
    }
}
