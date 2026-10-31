// 全体・窓のサイレントプロット判断。旧 SilentPlotter の中核をここへ集約し、
// CAD API 呼び出しは IPlotPort 実装へ委ねる。プロット種別・尺度採否・窓クロップ矩形の判断が
// 1か所（Shared）に存在し、製品差はポート実装だけに封じ込められる。
using CadPeta.Acquire.Common;
using CadPeta.Geometry;

namespace CadPeta.Plugin.Shared;

public static class PlotOrchestrator
{
    /// <summary>
    /// 窓プロットの用紙合わせ判定の数値誤差吸収（mm）。ちょうど収まる寸法が
    /// 浮動小数の丸めで僅かに超過へ倒れても誤判定しないための単一箇所。
    /// IR のコンテンツ量子 0.03pt≒0.0106mm より小さい差は寸法差として意味を持たない。
    /// </summary>
    internal const double FitToleranceMm = 0.01;

    /// <summary>
    /// 現在レイアウトを <paramref name="outputPdfPath"/> へサイレントプロットする。
    /// <paramref name="window"/> があれば窓プロットにし、発行 PDF 上の窓の載る矩形
    /// （IR mm 座標＝変換層クロップへ渡す値）を返す。窓なしは従来どおり全体で null を返す。
    /// </summary>
    public static RectMm? Plot(IPlotPort port, PlotWindow? window, string outputPdfPath,
                               System.IO.TextWriter log, System.IO.TextWriter verbose)
    {
        var session = port.BeginSession(log, verbose);
        return CleanupRunner.RunWithScope(session, () =>
        {
            RectMm? cropRect = null;
            if (window is { } win)
            {
                // 窓プロット: 窓を先に設定してから種別・尺度・検証・クロップ算出へ進む
                port.SetWindow(win);
                var metrics = port.GetPaperMetrics(log);
                bool useFit = DecideWindowFit(win, metrics, log);
                port.SetWindowScale(useFit);
                port.Validate(log);
                // クロップ矩形は検証後の最終設定（用紙名・回転・尺度）から算出する
                cropRect = ComputeWindowCropRect(win, port.GetPaperMetrics(log), verbose);
            }
            else
            {
                port.SetFullPlotType();
                port.Validate(log);
            }
            port.Publish(outputPdfPath, log, verbose);
            return cropRect;
        });
    }

    /// <summary>
    /// 窓プロットで用紙合わせ（フィット）にするか、ページ設定の尺度を維持するかを決める。
    /// モデル空間は用紙合わせ。ペーパー空間はページ設定の尺度を維持し（通常 1:1 → 窓が
    /// 用紙mm実寸で出て実寸を保証する）、窓が印刷可能領域へ収まらない場合だけ用紙合わせへ
    /// 切り替えて理由をログする。
    /// </summary>
    static bool DecideWindowFit(PlotWindow win, PaperMetrics m, System.IO.TextWriter log)
    {
        if (m.IsModel)
            return true; // モデルタブは常に用紙合わせ（全体プロットのモデル既定と同一）
        double s = m.ScaleMmPerUnit;
        if (double.IsNaN(s))
            return true; // 尺度計算が成立しない用紙単位・用紙合わせ設定は用紙合わせで扱う
        // 窓は表示フレームの矩形なので、収まり判定は回転を映した印刷可能領域と比べる
        if (win.Width * s <= m.DisplayPrintableWidthMm + FitToleranceMm &&
            win.Height * s <= m.DisplayPrintableHeightMm + FitToleranceMm)
            return false; // ページ設定の尺度のまま収まる
        log.WriteLine(Msg.Format("plot.window_fit_switch",
            win.Width * s, win.Height * s, m.DisplayPrintableWidthMm, m.DisplayPrintableHeightMm));
        return true;
    }

    /// <summary>
    /// 検証後のプロット設定から、発行 PDF 上で窓の載る矩形（IR mm 座標＝ページ左上原点・Y下向き）を算出する。
    /// </summary>
    static RectMm ComputeWindowCropRect(PlotWindow win, PaperMetrics m, System.IO.TextWriter verbose)
    {
        double s = m.ScaleMmPerUnit;
        if (double.IsNaN(s))
            // フィット尺度は表示フレームで計算する。回転前の印刷可能領域で計算すると、
            // 回転90/270の窓プロットが実機より小さいクロップ矩形になり、窓の周辺が欠落する
            s = PlotWindowMapper.FitScale(win.Width, win.Height,
                                          m.DisplayPrintableWidthMm, m.DisplayPrintableHeightMm);
        var rect = PlotWindowMapper.MapCenteredWindow(
            win.Width * s, win.Height * s, m.PrintCenterXMm, m.PrintCenterYMm,
            m.PageWidthMm, m.PageHeightMm, m.QuarterTurnsCw);
        verbose.WriteLine(Msg.Format("plot.window_verbose",
            win, s, m.QuarterTurnsCw * 90, m.PageWidthMm, m.PageHeightMm,
            m.PrintableWidthMm, m.PrintableHeightMm, m.PrintCenterXMm, m.PrintCenterYMm,
            rect.X0, rect.Y0, rect.X1, rect.Y1));
        return rect;
    }
}
