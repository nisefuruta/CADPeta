// 取得意図の中立DTO（範囲プロンプトの確定結果）。範囲（全体・窓）と選択絞り込みは独立で、
// 併用できる。製品固有の選択集合は不透明ハンドルで持ち、選択絞り込みポートまたは
// PETADATA取得器が解決する。
using CadPeta.Acquire.Common;
using CadPeta.Ir;

namespace CadPeta.Plugin.Shared;

public sealed class RangeChoice
{
    /// <summary>印刷窓（null=全体出力）。選択絞り込みとは独立に効く。</summary>
    public PlotWindow? Window;

    /// <summary>クロップ・外接矩形へ付ける余白（mm）。</summary>
    public double MarginMm = PluginDefaults.DefaultCropMarginMm;

    /// <summary>出力対象を絞る選択オブジェクト（不透明ハンドル。null=絞り込みなし）。</summary>
    public object? SelectionHandle;

    /// <summary>構造SVG書き出し（PETADATA）の尺度 1:N の N。PETA の印刷経路では使わない
    /// （尺度は範囲プロットの規則＝ページ設定尺度または用紙合わせに従う）。</summary>
    public double ScaleDenominator = PluginDefaults.DefaultScaleDenominator;

    /// <summary>SVG出力でレイヤ別グループ化を適用するか（既定オフ）。</summary>
    public bool GroupByLayer;

    /// <summary>SHX文字を代替書体のtextで出力するか（既定オフ=輪郭化）。</summary>
    public bool ShxSubstitute;

    /// <summary>全 text をアウトライン化するか（既定オフ=フォント保持）。</summary>
    public bool TextOutline;

    /// <summary>成果物をファイルへ出力するか（オフ=クリップボード登録のみ）。</summary>
    public bool FileOutput = true;

    public double GradientDpi = AcquireDefaults.GradientDefaultDpi;
    public IrRgb EmfBackground = PluginDefaults.DefaultEmfBackground;

    /// <summary>選択オブジェクトへの絞り込みが指定されているか。</summary>
    public bool IsSelection => SelectionHandle != null;
}
