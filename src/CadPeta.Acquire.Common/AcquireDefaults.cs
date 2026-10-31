// 取得アダプタ（CAD製品API非依存の共有計算）の既定値（マジックナンバー禁止: 意味のある値は命名定数＋根拠コメント）。
namespace CadPeta.Acquire.Common;

public static class AcquireDefaults
{
    public const double GradientDefaultDpi = 150.0;
    public const long GradientMaxPixelsPerImage = 16_000_000;
    public const long GradientMaxPixelsPerAcquire = 64_000_000;

    /// <summary>
    /// 線幅0（AutoCAD の「0.00mm」線の太さ＝デバイス最細線）の固定置換幅。
    /// SVG に最細線の概念がないため固定幅へ置換する。PDF印刷経路（PdfIrAcquirer）もこの値を使い、
    /// 両系統の出力品質を一致させる。
    /// </summary>
    public const double HairlineMm = 0.05;

    /// <summary>
    /// 線種のドット（長さ0の実線区間）の置換長（図面単位）。SVG の stroke-dasharray は
    /// 長さ0＋端形状「平」の区間を描画しないため、視認できる最小長へ置換する。
    /// 値は一般的な細線の線幅級（プロットでドットが線幅相当の点として現れる実態に合わせる）。
    /// </summary>
    public const double DotDashLengthDu = 0.15;

    /// <summary>
    /// スプライン近似の許容誤差（mm）。ベジェへ厳密変換できない類型（有理・3次以外）を
    /// 折れ線近似する際の弦との最大距離。IR のコンテンツ量子 0.03pt≒0.0106mm と同水準。
    /// </summary>
    public const double SplineToleranceMm = 0.01;

    /// <summary>折れ線近似の再帰分割の上限深さ。許容誤差が数値的に収束しない縮退曲線の保険
    /// （2^12=4096 分割あれば実用曲線は十分に収束する）。</summary>
    public const int MaxSubdivisionDepth = 12;

    /// <summary>
    /// ACI 7（White/Black）の写像先。ACI 7 は背景色に応じて白黒が反転する表示専用の色で、
    /// 白地（Office 文書・用紙）への貼付では黒として扱う（プロットの標準挙動と同じ）。
    /// </summary>
    public static readonly Ir.IrRgb Aci7OnWhite = new(0, 0, 0);

    /// <summary>
    /// MTEXT 背景マスクで「図面の背景色を使う」が指定されたときの塗り色。
    /// 貼付先（Office 文書）の地は白のため白で塗る（下の図形を隠すマスクの意味を保つ）。
    /// </summary>
    public static readonly Ir.IrRgb DrawingBackgroundAsWhite = new(1, 1, 1);

    /// <summary>SHX（ストロークフォント）の欧文代替書体。輪郭化が未対応の間の近似描画に使う。</summary>
    public const string ShxSubstituteLatin = "Arial";

    /// <summary>SHX＋ビッグフォント（和文）の代替書体。</summary>
    public const string ShxSubstituteCjk = "MS Gothic";

    /// <summary>
    /// 複雑線種の記号（シェイプ・文字）の配置位置を曲線距離で走査する際の許容誤差（図面単位）。
    /// 浮動小数の丸めで区間終端ちょうどの記号を取りこぼさないための猶予で、区間終端へ
    /// クランプして配置する（区間長に対して無視できる大きさ）。
    /// </summary>
    public const double LinetypeSymbolDistanceEpsilonDu = 1e-6;
}
