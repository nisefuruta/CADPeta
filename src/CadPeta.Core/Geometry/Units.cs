// 単位換算の共通定数（内核）。インチ⇔mm換算をこの1箇所へ集約し、
// 各アダプタ・プラグインでの複製（マジックナンバー化）を防ぐ。
namespace CadPeta.Geometry;

public static class Units
{
    /// <summary>1インチ=25.4mm（国際インチの定義値）。</summary>
    public const double MmPerInch = 25.4;

    /// <summary>1インチ=72pt（PDF のデフォルトユーザー空間の単位。PDF32000-1 §7.9.5）。</summary>
    public const double PtPerInch = 72.0;

    /// <summary>PDF の 1pt の mm（25.4/72）。</summary>
    public const double MmPerPt = MmPerInch / PtPerInch;

    /// <summary>1mm の PDF の pt（72/25.4）。</summary>
    public const double PtPerMm = PtPerInch / MmPerInch;
}
