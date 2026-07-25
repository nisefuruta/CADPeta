using CadPeta.Plugin.Shared;

namespace CadPeta.Plugin.Bricscad;

internal static class BricscadPluginDefaults
{
    public const string PdfDeviceName = "Print As PDF.pc3";
    public const int PlotCopyCount = 1;

    /// <summary>
    /// CVPORT がこの値のとき真のペーパー空間がアクティブ（BricsCAD V26 実測 2026-07-16:
    /// レイアウトのペーパー空間で CVPORT=1、MSPACE でビューポートへ入ると 2、PSPACE で 1 へ戻る。
    /// AutoCAD と同じ割当て）。
    /// </summary>
    public const int PaperSpaceViewportNumber = 1;

    /// <summary>
    /// 窓の座標契約。**BricsCAD V26 実機で DCS と確定**（2026-07-16、
    /// tests/tools/regression/window_contract の window_contract_compat.ps1 ケース A/G/J/M）。
    /// BricsCAD 自身の -PLOT に同じ2点を与えたときページ設定へ格納される窓（DXF 48/49/140/141）が
    /// UCS→DCS 値と厳密一致し、DCS 候補の PDF がオラクル PDF と全ケース画素完全一致
    /// （生値・UCS→WCS は別領域）。ViewTableRecord の Target・ViewDirection は AutoCAD 同様 WCS
    /// （実測: WCS 目標点 (60,50) に対しシステム変数 TARGET は UCS 表現 (6.6,113.8)）。
    /// 注意: 座標入力は定常オブジェクトスナップに吸着し得るため、計測時は OSMODE=0 が要る
    /// （吸着で -PLOT 格納値が入力とずれた実測がある）。
    /// </summary>
    public const WindowPickContract WindowPickContract = Shared.WindowPickContract.Dcs;
}
