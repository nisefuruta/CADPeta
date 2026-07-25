// プラグイン層の中立な既定値（4製品共通）。製品固有値（PDFデバイス名・ペーパー空間ビューポート
// 番号・プロット部数など CAD 依存の定数）はここに置かず、各殻が持つ。
// マジックナンバー禁止: 意味のある値は命名定数＋根拠コメント。
using CadPeta.Ir;

namespace CadPeta.Plugin.Shared;

public static class PluginDefaults
{
    /// <summary>コマンド名。製品名 CADPeta のコマンドは PETA。</summary>
    public const string CommandName = "PETA";

    /// <summary>診断モードのコマンド名。PETA と同じ出力に加え、変換層の各段の
    /// 前後を SVG ダンプする。既定コマンド PETA は診断オフ（通常運用で余分なファイルを出さない）。</summary>
    public const string DiagCommandName = "PETADIAG";

    public const string DataCommandName = "PETADATA";

    /// <summary>
    /// 未保存図面・書き込み不可時の退避出力先（%TEMP% 配下のサブフォルダ名）。
    /// 既定の出力先は図面と同じフォルダ。
    /// </summary>
    public const string FallbackSubdirName = "CadPeta";

    /// <summary>範囲プロンプトの「全体」キーワード（グローバル名。スクリプトは _All でも指定できる）。</summary>
    public const string KeywordAll = "All";

    /// <summary>範囲プロンプトの「余白」キーワード（グローバル名）。</summary>
    public const string KeywordMargin = "Margin";

    /// <summary>範囲プロンプトの「オブジェクト」キーワード（グローバル名）。選択入口へ入る。</summary>
    public const string KeywordObject = "Object";

    /// <summary>尺度プロンプトの「レイヤ」キーワード（グローバル名）。SVG のレイヤ別
    /// グループ化（既定オフ）を切り替える。</summary>
    public const string KeywordLayer = "Layer";

    /// <summary>「ファイル出力」キーワード（グローバル名）。オフでクリップボード登録のみにする。</summary>
    public const string KeywordFileOutput = "Fileoutput";

    /// <summary>尺度プロンプトの「SHX代替」キーワード（グローバル名）。SHX 文字を輪郭化
    /// （既定）せず代替書体の text で出力する近似（貼り付け先での文字編集を優先する場合）を
    /// 切り替える。</summary>
    public const string KeywordShxSubstitute = "Shx";

    /// <summary>尺度プロンプトの「文字輪郭」キーワード（グローバル名）。全 text の
    /// アウトライン化（環境非依存の見た目を優先。既定オフ=フォント保持）を切り替える。</summary>
    public const string KeywordTextOutline = "Textoutline";

    public const string KeywordGradientDpi = "Gradient";
    public const string KeywordEmfBackground = "Background";

    /// <summary>選択オブジェクト出力の貼付尺度 1:N の既定 N。1図面単位=1mm を等倍で貼る。</summary>
    public const double DefaultScaleDenominator = 1;

    /// <summary>クロップ余白の既定値（mm）。余白は明示指定時のみ付ける。</summary>
    public const double DefaultCropMarginMm = 0;

    /// <summary>PDFデバイスに元用紙と同寸法がない場合の既定はOffice貼付向けISO A4縦。</summary>
    public const double DefaultFallbackPaperWidthMm = 210;
    public const double DefaultFallbackPaperHeightMm = 297;

    /// <summary>代替用紙寸法を「幅mm,高さmm」で上書きする環境変数。</summary>
    public const string FallbackPaperSizeEnvironmentVariable = "CADPETA_FALLBACK_PAPER_MM";

    /// <summary>
    /// 窓の縮退判定（図面単位）。同一点の二度 pick を弾く数値誤差レベルの下限で、
    /// 意図的な微小窓は許容する（尺度は用紙合わせで補正される）。
    /// </summary>
    public const double MinWindowSizeDu = 1e-9;

    /// <summary>
    /// EMF透過合成下地色の既定（白）。RangeChoice の初期値と、EMF背景プロンプトの
    /// 再表示 &lt;#RRGGBB&gt; はここを単一の基準にする。
    /// </summary>
    public static readonly IrRgb DefaultEmfBackground = new(1, 1, 1);
}
