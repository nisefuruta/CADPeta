// 境界ポート（プラグイン層の SSOT が所有する interface）。CAD 固有の対話・プロット・
// 用紙実寸取得を、中立型（double・タプル・不透明ハンドル）だけで表現する。各製品の薄い殻が
// CAD SDK で実装する。ここに CAD 型を持ち込まない（参照方向テストが機械検査する）。
using System;
using System.Collections.Generic;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>プロンプトに渡すキーワード（グローバル名・ローカル名・表示）。</summary>
public readonly struct PromptKeyword
{
    public PromptKeyword(string global, string local, string display)
    {
        Global = global; Local = local; Display = display;
    }

    /// <summary>スクリプト・比較で使う言語非依存の正式名。返却時もこれを返す。</summary>
    public string Global { get; }

    /// <summary>対話入力の短縮名（例 "A"）。</summary>
    public string Local { get; }

    /// <summary>コマンドラインの表示（例 "全体(A)"）。</summary>
    public string Display { get; }
}

/// <summary>対話プロンプトの結果種別。</summary>
public enum PromptOutcome
{
    /// <summary>点・数値・窓など具体値が入力された。</summary>
    Value,
    /// <summary>キーワードが選ばれた（<see cref="PromptResult{T}.Keyword"/> がグローバル名）。</summary>
    Keyword,
    /// <summary>既定選択（Enter・空入力）。</summary>
    None,
    /// <summary>利用者キャンセル（ESC 等）。</summary>
    Cancelled,
}

/// <summary>プロンプト結果（値・キーワード・既定・キャンセルのいずれか）。</summary>
public readonly struct PromptResult<T>
{
    PromptResult(PromptOutcome outcome, T value, string? keyword)
    {
        Outcome = outcome; Value = value; Keyword = keyword;
    }

    public PromptOutcome Outcome { get; }
    public T Value { get; }

    /// <summary>キーワード選択時のグローバル名。</summary>
    public string? Keyword { get; }

    public static PromptResult<T> OfValue(T value) => new(PromptOutcome.Value, value, null);
    public static PromptResult<T> OfKeyword(string global) => new(PromptOutcome.Keyword, default!, global);
    public static PromptResult<T> OfNone() => new(PromptOutcome.None, default!, null);
    public static PromptResult<T> OfCancelled() => new(PromptOutcome.Cancelled, default!, null);
}

/// <summary>選択取得の結果種別。</summary>
public enum SelectionOutcome
{
    /// <summary>1つ以上のオブジェクトが選択された（<see cref="SelectionResult.Handle"/> が不透明ハンドル）。</summary>
    Selected,
    /// <summary>選択が空だった（全体へフォールバック、または中止の判断は呼び出し側）。</summary>
    Empty,
    /// <summary>利用者キャンセル。</summary>
    Cancelled,
}

/// <summary>後選択プロンプトの結果。ハンドルは印刷ポートまたはPETADATA取得器が解決する不透明値。</summary>
public readonly struct SelectionResult
{
    SelectionResult(SelectionOutcome outcome, object? handle)
    {
        Outcome = outcome; Handle = handle;
    }

    public SelectionOutcome Outcome { get; }
    public object? Handle { get; }

    public static SelectionResult OfSelected(object handle) => new(SelectionOutcome.Selected, handle);
    public static SelectionResult OfEmpty() => new(SelectionOutcome.Empty, null);
    public static SelectionResult OfCancelled() => new(SelectionOutcome.Cancelled, null);
}

public sealed record UnsupportedSelectionItem(string Kind, string Identifier, string Reason);
public sealed record UnsupportedSelectionSummary(int SelectedCount,
    IReadOnlyList<UnsupportedSelectionItem> Unsupported);

/// <summary>
/// 窓プロット指定（図面単位の軸平行矩形。ペーパー空間では用紙単位）。
/// どの座標系の値かは製品ごとの契約で決まり、規則と根拠は <see cref="WindowPickMapper"/> が持つ
/// （AutoCAD／ARES／BricsCAD／IJCAD は実機で DCS と確定）。
/// </summary>
public readonly struct PlotWindow
{
    public PlotWindow(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public override string ToString() =>
        FormattableString.Invariant($"({MinX:F3},{MinY:F3})-({MaxX:F3},{MaxY:F3})");
}

/// <summary>
/// 対話ポート: 範囲プロンプト・出力先問い合わせが必要とする対話プリミティブと図面属性を、
/// 中立型でだけ提供する。窓の2点 pick はペーパー空間切替をこの実装の内側で完結し、中立矩形を返す。
/// 座標系の写像そのものは <see cref="WindowPickMapper"/> が唯一の実装を持ち、ポートは SDK の値を
/// 中立型へ移して渡すだけにする。行列・TILEMODE/CVPORT を外へ出さない。
/// </summary>
public interface IEditorPort
{
    /// <summary>図面が保存済みか（未保存なら出力先が定まらない）。</summary>
    bool IsDrawingSaved { get; }

    /// <summary>保存済み図面のフルパス（未保存時は空でも可。呼び出し側は IsDrawingSaved で判断）。</summary>
    string DrawingPath { get; }

    /// <summary>成果物名の語幹（図面名の拡張子なし）。</summary>
    string DrawingStem { get; }

    /// <summary>現在レイアウト名（成果物名・予約に使う）。</summary>
    string CurrentLayoutName { get; }

    /// <summary>コマンド起動時に選択済みのオブジェクト（先選択）。無ければ null。</summary>
    object? GetPickfirstSelection();

    /// <summary>後選択プロンプト（オブジェクト選択）。</summary>
    SelectionResult PromptSelection();

    /// <summary>文字列入力（出力先フォルダ問い合わせ）。キャンセルは null。</summary>
    string? PromptString(string message);

    /// <summary>ダイアログを開ける（画面のある）CAD か。AutoCAD の accoreconsole は false。</summary>
    bool HasScreen { get; }

    /// <summary>
    /// ファイル名のコメントのダイアログ（<see cref="HasScreen"/> のときだけ呼ぶ）。OK なら入力したコメントを <paramref name="form"/> に入れて true、
    /// キャンセルなら false を返す。使えないコメントでは OK を押せない。
    /// </summary>
    bool ShowCommentDialog(CommentForm form);

    /// <summary>
    /// 数値入力（貼付尺度・余白）。キーワードは SDK の戻り表記にかかわらず、
    /// <see cref="PromptKeyword.Global"/> の値へ正規化して返す。
    /// </summary>
    PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
                                      double defaultValue, bool useDefault,
                                      bool allowNone, bool allowZero, bool allowNegative);

    /// <summary>
    /// キーワードだけの入力（-PETAOPT）。Enter は None、Esc は Cancelled、キーワードは <see cref="PromptKeyword.Global"/> の値で返す。
    /// </summary>
    PromptResult<string> PromptKeywords(string message, IReadOnlyList<PromptKeyword> keywords);

    /// <summary>コマンドラインへ1行出す（オプション切替の確認表示など）。</summary>
    void WriteLine(string message);

    /// <summary>
    /// 窓 pick のためにペーパー空間へ切り替える文脈を開く。切替が要らなければ何もしない
    /// disposable を返す。Dispose で元のビューポート操作状態へ戻す。
    /// </summary>
    IDisposable BeginWindowPickScope(System.IO.TextWriter verbose);

    /// <summary>
    /// 最初のコーナー（キーワード付き）を促し、点なら第2コーナーまで pick して中立矩形を返す。
    /// キーワードは SDK の戻り表記にかかわらず <see cref="PromptKeyword.Global"/> の値へ
    /// 正規化して返す。既定（全体）・キャンセルはその旨を返す（矩形は未設定）。
    /// 縮退窓（同一点二度 pick 等）の弾き直しは呼び出し側が担う。
    /// </summary>
    PromptResult<PlotWindow> PromptWindow(string firstMessage,
                                          IReadOnlyList<PromptKeyword> keywords,
                                          string secondMessage);
}

/// <summary>
/// 発行される PDF のページの大きさ・印刷可能領域（mm）とプロット設定の中立ビュー。IPlotPort が検証済みプロット設定から算出して返す。
/// 座標系は用紙座標（左下原点・Y上向き）。窓クロップ矩形の算出に PlotOrchestrator が使う。
/// </summary>
public readonly struct PaperMetrics
{
    public PaperMetrics(double pageWidthMm, double pageHeightMm,
                        double printableWidthMm, double printableHeightMm,
                        double printCenterXMm, double printCenterYMm,
                        double scaleMmPerUnit, int quarterTurnsCw, bool isModel)
    {
        PageWidthMm = pageWidthMm; PageHeightMm = pageHeightMm;
        PrintableWidthMm = printableWidthMm; PrintableHeightMm = printableHeightMm;
        PrintCenterXMm = printCenterXMm; PrintCenterYMm = printCenterYMm;
        ScaleMmPerUnit = scaleMmPerUnit; QuarterTurnsCw = quarterTurnsCw; IsModel = isModel;
    }

    /// <summary>発行される PDF のページの幅（PDF ドライバの 1pt の丸めを含む。PdfPageGeometry）。</summary>
    public double PageWidthMm { get; }
    /// <summary>発行される PDF のページの高さ（PageWidthMm と同じく丸めを含む）。</summary>
    public double PageHeightMm { get; }
    /// <summary>印刷可能領域の幅（用紙の公称の大きさから余白を除いた値。フィットの尺度と収まりの判定に使う）。</summary>
    public double PrintableWidthMm { get; }
    public double PrintableHeightMm { get; }
    /// <summary>窓を置く印刷可能領域の中心（用紙座標。PDF ドライバが印刷可能領域の原点を丸める製品では、丸めた原点から測る）。</summary>
    public double PrintCenterXMm { get; }
    public double PrintCenterYMm { get; }

    /// <summary>用紙mm/図面単位の明示尺度。用紙合わせ（フィット）と非対応単位は NaN。</summary>
    public double ScaleMmPerUnit { get; }

    /// <summary>用紙→表示ページの時計回り四半回転数（0〜3）。</summary>
    public int QuarterTurnsCw { get; }

    /// <summary>モデル空間のプロットか（尺度・領域の既定判断に使う）。</summary>
    public bool IsModel { get; }

    /// <summary>表示フレーム（窓と同じ向き）で見た印刷可能領域の幅。プロット回転が奇数四半のとき
    /// 用紙は表示フレーム上で縦横が入れ替わる。窓の収まり判定とフィット尺度はこの向きで行う
    /// （accoreconsole 実機で確定: 回転90の窓フィットは印刷可能領域の縦横を入れ替えた
    /// min(printH/winW, printW/winH) で印字される。用紙全体基準ではない。2026-07-17、
    /// 余白付き/フルブリードA4×回転0/90×窓縦横比の5ケースと実図面で一致）。</summary>
    public double DisplayPrintableWidthMm =>
        QuarterTurnsCw % 2 == 0 ? PrintableWidthMm : PrintableHeightMm;

    /// <summary>表示フレームで見た印刷可能領域の高さ。<see cref="DisplayPrintableWidthMm"/> と対。</summary>
    public double DisplayPrintableHeightMm =>
        QuarterTurnsCw % 2 == 0 ? PrintableHeightMm : PrintableWidthMm;
}

/// <summary>
/// プロットポート: 現在レイアウトのページ設定を保ったままデバイスだけを製品固有の
/// ベクタ PDF ドライバへ上書きしてサイレント発行する。プロット種別・尺度採否・窓クロップ矩形の
/// 判断は PlotOrchestrator（Shared 側）にあり、ここには「用紙実寸取得」「窓設定」「発行」等の
/// 中立オペレーションだけを持たせる。プロット設定オブジェクトは CAD 型なので実装の内側に閉じ、
/// Shared へは中立値（mm）だけ返す。
/// </summary>
public interface IPlotPort
{
    /// <summary>
    /// プロットの一括処理を開始する（進行中プロットの検査・BACKGROUNDPLOT 退避・ページ設定の
    /// 複製・デバイス上書き・用紙復元・レイアウト情報のログ）。Dispose で退避した状態を戻す。
    /// </summary>
    /// <exception cref="InvalidOperationException">他のプロットが進行中・デバイス設定不能。</exception>
    IDisposable BeginSession(System.IO.TextWriter log, System.IO.TextWriter verbose);

    /// <summary>窓プロットの領域・種別・中央合わせを設定する。</summary>
    void SetWindow(PlotWindow window);

    /// <summary>窓プロットの尺度を確定する（フィット or ページ設定の尺度維持）。</summary>
    void SetWindowScale(bool useFitScale);

    /// <summary>全体プロットの種別を設定する（モデル=図形範囲・用紙合わせ・中央、ペーパー=レイアウト）。</summary>
    void SetFullPlotType();

    /// <summary>プロット設定を検証する（デバイス上書きに伴う近似用紙整合を含む）。</summary>
    /// <exception cref="InvalidOperationException">検証失敗。</exception>
    void Validate(System.IO.TextWriter log);

    /// <summary>検証済みプロット設定から用紙実寸（mm）と尺度・回転を中立値で得る。</summary>
    PaperMetrics GetPaperMetrics(System.IO.TextWriter log);

    /// <summary>PDF を発行する（PlotEngine 実行）。発行後に出力ファイルの実在を確認する。</summary>
    /// <exception cref="InvalidOperationException">プロット実行の各段で失敗。</exception>
    /// <exception cref="System.IO.IOException">正常終了したが PDF 未生成。</exception>
    void Publish(string outputPdfPath, System.IO.TextWriter log, System.IO.TextWriter verbose);
}

/// <summary>
/// 選択絞り込みセッション: 選択集合の対象件数・除外内訳と、側DBまたはViewport設定を保持した
/// 元レイアウトを対象とする範囲プロットポートを提供する。範囲の種別・尺度採否・クロップ矩形の
/// 判断は通常経路と同じ PlotOrchestrator が <see cref="Plotter"/> に対して行い、
/// 絞り込みはプロットされるペーパー空間図形を選択対象だけに限定する。
/// </summary>
public interface ISelectionPlotSession : IDisposable
{
    int SelectedCount { get; }
    int SupportedCount { get; }
    IReadOnlyList<UnsupportedSelectionItem> Unsupported { get; }

    /// <summary>絞り込み済みの側DBまたは元レイアウトを対象とする範囲プロットポート。</summary>
    IPlotPort Plotter { get; }
}

/// <summary>選択集合を限定し、範囲プロット可能なポートとして提供する製品境界。</summary>
public interface ISelectionPlotPort
{
    ISelectionPlotSession Begin(object selectionHandle,
        System.IO.TextWriter log, System.IO.TextWriter verbose);
}
