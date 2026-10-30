using CadPeta.Plugin.Shared;

namespace CadPeta.Plugin.Ares;

internal static class AresPluginDefaults
{
    public const string PdfDeviceName = "PDF";
    public const int PlotCopyCount = 1;

    /// <summary>
    /// CVPORT がこの値のとき真のペーパー空間がアクティブ（ARES 2024 実測 2026-07-17:
    /// レイアウトのペーパー空間で CVPORT=1、MSPACE でビューポートへ入ると 2、PSPACE で 1 へ戻る。
    /// AutoCAD・IJCAD・BricsCAD と同じ割当て）。
    /// </summary>
    public const int PaperSpaceViewportNumber = 1;

    /// <summary>
    /// 窓の座標契約。**ARES 2024・2027 の実機で DCS と確定**（2026-07-17、
    /// tests/tools/regression/window_contract の window_contract_compat.ps1 ケース A/G/J/M）。
    /// ARES 自身の -PLOT に同じ2点を与えたときページ設定へ格納される窓（DXF 48/49/140/141）が、
    /// 公式行列で計算した UCS→DCS 値と両版とも厳密一致し、DCS 候補の PDF がオラクル PDF と
    /// 画素完全一致（0 差分。生値・UCS→WCS は別領域）。
    /// 「ARES 2027 で DCS を送ると空になった」観測（2026-07-13）は契約の反証ではなく、
    /// 2027 の GetCurrentView().Target がビュー中心を畳み込んだ汚染値を返すことによる
    /// 別領域化だった（CadEditorPort.ReadPickFrame が ARES では目標点をシステム変数 TARGET から
    /// 読むことで両版とも成立する）。
    /// </summary>
    public const WindowPickContract WindowPickContract = Shared.WindowPickContract.Dcs;
}
