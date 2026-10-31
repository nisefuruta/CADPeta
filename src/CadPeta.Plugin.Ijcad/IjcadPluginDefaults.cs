using CadPeta.Plugin.Shared;

namespace CadPeta.Plugin.Ijcad;

internal static class IjcadPluginDefaults
{
    public const string PdfDeviceName = "DWG To PDF.pc3";
    public const int PlotCopyCount = 1;

    /// <summary>PDF デバイスが 1pt の整数へ丸めて書く値。IJCAD の PDF デバイスでは測っていないので、丸めない前提のまま。</summary>
    public const PdfPointSnapping PdfPointSnapping = Shared.PdfPointSnapping.None;

    /// <summary>
    /// CVPORT がこの値のとき真のペーパー空間がアクティブ（IJCAD 2026 実測 2026-07-17:
    /// レイアウトのペーパー空間で CVPORT=1、MSPACE でビューポートへ入ると 2、PSPACE で 1 へ戻る。
    /// AutoCAD と同じ割当て）。
    /// </summary>
    public const int PaperSpaceViewportNumber = 1;

    /// <summary>
    /// 窓の座標契約。**IJCAD 2026 実機で DCS と確定**（2026-07-17、
    /// tests/tools/regression/window_contract の window_contract_compat.ps1 ケース A/G/J/M）。
    /// IJCAD 自身の -PLOT に同じ2点を与えたときページ設定へ格納される窓（DXF 48/49/140/141）が
    /// UCS→DCS 値と厳密一致し、DCS 候補の PDF だけがオラクル PDF と同内容
    /// （残差は描線の丸め差 0.63% 以下・平行移動なし。ケース J は差分7画素）。
    /// ViewTableRecord の Target・ViewDirection は AutoCAD 同様 WCS（実測: WCS 目標点 (60,50) に
    /// 対しシステム変数 TARGET は UCS 表現 (6.6,113.8)）。
    /// </summary>
    public const WindowPickContract WindowPickContract = Shared.WindowPickContract.Dcs;
}
