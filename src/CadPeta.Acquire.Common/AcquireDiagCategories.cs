namespace CadPeta.Acquire.Common;

/// <summary>取得アダプタと表示層で共有する機械判定用の診断カテゴリ。</summary>
public static class AcquireDiagCategories
{
    public const string GradientRasterized = "構造SVG書き出し: グラデーションハッチのラスタ化";
    public const string GradientShapeFallback = "構造SVG書き出し: 実測式未確定形状の代表色縮退";
    public const string GradientBudgetFallback = "構造SVG書き出し: ラスタ予算不足による代表色縮退";
    public const string GradientInvalidFallback = "構造SVG書き出し: グラデーション定義不正による縮退";
    public const string GradientDpiReduced = "構造SVG書き出し: グラデーションラスタDPIの自動低減";
    public const string SelectionOtherSpace = "構造SVG書き出し: 現在の空間直属でない選択オブジェクトの除外";
}
