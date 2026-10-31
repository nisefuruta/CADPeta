// AutoCAD 固有の既定値（中立な既定は CadPeta.Plugin.Shared.PluginDefaults にある）。
using CadPeta.Plugin.Shared;

namespace CadPeta.Plugin.Acad;

internal static class AcadPluginDefaults
{
    /// <summary>
    /// 窓の座標契約。AutoCAD 2026 実機で DCS と確定した
    /// （CAD 自身の -PLOT が格納する窓が、pick 2点の DCS の min/max と厳密一致。
    /// 対照との画素完全一致でも裏付け。再現は tests/tools/regression/window_contract）。
    /// </summary>
    public const WindowPickContract WindowPickContract = Shared.WindowPickContract.Dcs;

    /// <summary>
    /// サイレントプロットに使う PDF デバイス。テストチャート・実物図面のコーパスを
    /// このドライバでプロットして IR 写像・EMF 接続を検証済みであり、出力 PDF の構造が既知。
    /// </summary>
    public const string PdfDeviceName = "AutoCAD PDF (High Quality Print).pc3";

    /// <summary>プロット部数。本ツールはファイル発行が目的のため常に1部。</summary>
    public const int PlotCopyCount = 1;

    /// <summary>
    /// PDF デバイスが 1pt の整数へ丸めて書く値。AutoCAD PDF (High Quality Print).pc3 は、ページの大きさ（A3 は 1191×842pt）と
    /// 印刷可能領域の原点（余白 5mm・17mm を 14・48pt）を丸める（AutoCAD 2026 の実測、2026-10-30。窓の位置が 0.01mm 以内で合う）。
    /// </summary>
    public const PdfPointSnapping PdfPointSnapping = Shared.PdfPointSnapping.PageSizeAndPrintableOrigin;

    /// <summary>CVPORT がこの値のときペーパー空間がアクティブ（AutoCAD の固定割当て）。</summary>
    public const short PaperSpaceViewportNumber = 1;
}
