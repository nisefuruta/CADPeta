// PETA/PETADIAG の本体フロー（4製品共通の単一実装）: 範囲（全体・窓）＋選択絞り込み → PDF印刷 →
// IR → 変換層 → SVG＋EMF ファイル出力＋診断ログ → クリップボード登録 → 結果サマリ。
// 絞り込みはプロット対象を側DB（選択オブジェクトだけの複製）へ差し替えるだけで、範囲・尺度・
// クロップの判断は PlotOrchestrator の単一実装を全経路で共有する。
// CAD 固有処理は IEditorPort・IPlotPort と取得デリゲートに委ね、ここには CAD 型が現れない。
using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using CadPeta.Ir;
using CadPeta.Transforms;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// PETA 本体を組み立てる合成入力。CAD 依存の対話・プロット・取得・出力は殻がここへ差し込む。
/// </summary>
public sealed class PetaEnvironment
{
    /// <summary>対話ポート（範囲プロンプト・出力先問い合わせ）。</summary>
    public IEditorPort Editor { get; init; } = null!;

    /// <summary>現在レイアウトをPDF印刷するポート。</summary>
    public IPlotPort Plotter { get; init; } = null!;

    /// <summary>選択絞り込み時にプロット対象を側DBへ差し替えるポート。</summary>
    public ISelectionPlotPort SelectionPlotter { get; init; } = null!;

    /// <summary>発行 PDF を IR 化する（PdfIrAcquirer）。</summary>
    public IIrAcquirer PdfAcquirer { get; init; } = null!;

    /// <summary>EMF 出力（EmfWriter）。</summary>
    public Func<IrRgb, IIrOutput> EmfWriterFactory { get; init; } = null!;

    /// <summary>EMF をクリップボードへ登録する。競合時は InvalidOperationException を投げる。</summary>
    public Action<string> SetClipboardFromFile { get; init; } = null!;

    /// <summary>文字アウトライン化に使う TrueType グリフ輪郭ポート（GDI 実装）の生成。</summary>
    public Func<IGlyphOutliner> TrueTypeOutlinerFactory { get; init; } = null!;

}

public static class PetaRunner
{
    // EMF 出力が透過を下地色と合成して不透明化したときの診断カテゴリの接頭辞。
    // 本プロジェクトは Out.Emf を参照しない（EMF 実装は殻が差し込む）ため文字列で持ち、
    // EmfWriter.OpacityFlattenedDiagPrefix との一致は単体テストで固定する
    internal const string EmfOpacityFlattenedDiagPrefix = "透過の不透明化";

    public static void Run(PetaEnvironment env, TextWriter log, bool diagnostic)
    {
        TextWriter verbose = diagnostic ? log : TextWriter.Null;
        var ed = env.Editor;

        // ---------- 範囲指定（Enter=全体・2点窓。先選択/オブジェクト(O)は範囲内の絞り込み） ----------
        var choice = RangePrompt.TryGetChoice(ed, log, verbose);
        if (choice == null)
            return; // 利用者の明示キャンセル: 何も出力しない
        IIrOutput emfWriter = env.EmfWriterFactory(choice.EmfBackground);

        bool effectiveFileOutput = choice.FileOutput || diagnostic;
        if (diagnostic && !choice.FileOutput)
            log.WriteLine(Msg.Text("runner.diag_forces_file_output"));
        Action<string> warn = message => log.WriteLine(message);
        var now = DateTime.Now;
        var scope = effectiveFileOutput
            ? ArtifactScope.ReserveInDir(OutputLocation.Resolve(ed, log), OutputLocation.Stem(ed),
                                         ed.CurrentLayoutName, now, warn)
            : ArtifactScope.CreateTemp(OutputLocation.Stem(ed), ed.CurrentLayoutName, now, warn);
        var outDir = scope.Dir;
        var baseName = scope.BaseName;
        var svgPath = Path.Combine(outDir, baseName + ".svg");
        var emfPath = Path.Combine(outDir, baseName + ".emf");

        // 成果物の生成〜クリップボード登録を try で囲み、途中失敗でも finally で scope を必ず
        // 後始末する（ファイル出力オフの一時フォルダ削除・予約ロック解放）。結果サマリは
        // 後始末が済んだ後に PetaOutcome.Report で出す
        long emfBytes = 0;
        int nodeCount = 0;
        int flattenedOpacity = 0;
        string? clipboardError = null;
        string? recoveredEmfPath = null;
        bool effectiveGroupByLayer = choice.GroupByLayer;
        try
        {
            var diag = new IrDiagnostics();

            // ---------- サイレントプロット（絞り込み時はプロット対象を側DBへ差し替える） ----------
            var selection = choice.IsSelection
                ? SelectionPlotOrchestrator.Begin(env.SelectionPlotter, ed,
                    choice.SelectionHandle!, log, verbose)
                : null;
            if (choice.IsSelection && selection == null)
                return; // 対象なし・除外の不同意（理由は SelectionPlotOrchestrator が報告済み）

            var pdfPath = Path.Combine(outDir, baseName + ".pdf");
            var sw = Stopwatch.StartNew();
            // 選択セッションはPDF発行にだけ必要である。発行本体と復元が両方失敗した場合も
            // CleanupRunnerで両例外を保持し、取得・変換中まで元DBの一時変更を持ち越さない。
            var cropRect = selection != null
                ? CleanupRunner.RunWithScope(selection, () => PlotOrchestrator.Plot(
                    selection.Plotter, choice.Window, pdfPath, log, verbose))
                : PlotOrchestrator.Plot(env.Plotter, choice.Window, pdfPath, log, verbose);
            ArtifactFile.Validate(pdfPath, ArtifactKind.Pdf);
            verbose.WriteLine(Msg.Format("runner.pdf_published",
                pdfPath, new FileInfo(pdfPath).Length, sw.ElapsedMilliseconds));

            IrDocument ir = env.PdfAcquirer.Acquire(pdfPath, diag);
            if (choice.GroupByLayer && ir.Nodes.Count > 0
                && !ir.Nodes.Any(n => !string.IsNullOrWhiteSpace(n.DataLayer)))
            {
                // 発行 PDF ドライバ・版によっては OCG（レイヤ）情報を持たない場合がある。
                // 既存の他フォールバック（近似用紙・Wblock 複製切替）と同じ方針で、
                // コマンド全体を失敗させずレイヤ別出力を諦めて続行する
                log.WriteLine(Msg.Text("runner.no_ocg"));
                diag.Add("レイヤ別グループ化: OCGレイヤ情報欠如のため無効化", $"pdf={pdfPath}");
                effectiveGroupByLayer = false;
            }
            // 背景除去=既定で有効。窓指定時のみクロップ（発行 PDF 上の窓の載る矩形＋余白、
            // CLI --crop と同じ変換層）。レイヤ・文字輪郭は範囲・絞り込みに依らず反映する
            var convertOptions = new ConvertOptions
            {
                CropRectMm = cropRect,
                CropMarginMm = choice.MarginMm,
                TextOutline = choice.TextOutline,
                TextOutliner = choice.TextOutline ? env.TrueTypeOutlinerFactory() : null,
                GroupByLayer = effectiveGroupByLayer,
            };
            if (!diagnostic)
            {
                ConvertPipeline.Apply(ir, convertOptions, diag);
            }
            else
            {
                // 診断モード: 各段の前後を SVG へダンプ。出力先は本成果物と同じ outDir
                log.WriteLine(Msg.Format("runner.diag_stage_dump", outDir));
                ConvertPipeline.ApplyWithDiagnostics(ir, convertOptions, diag,
                    DiagnosticsReport.MakeStageSink(outDir, baseName, log));
            }
            if (choice.IsSelection && AbortIfEmptySelectionIr(ir, diag, log))
            {
                // 中止経路でも診断モードなら diag.json は書く（変換各段の SVG ダンプは
                // 中止判定より前の段階で既に書き出し済み）。SVG/EMF 本出力とクリップボード
                // 登録だけを行わない
                if (diagnostic)
                {
                    log.WriteLine(Msg.Text("runner.diag_empty_ir"));
                    DiagnosticsReport.WriteDiagJson(log, svgPath, diag);
                }
                return;
            }
            if (ir.Nodes.Count == 0)
                log.WriteLine(Msg.Text("runner.empty_ir_warning"));

            // 通常出力は出所メタデータ（data-*）を除去する（Write 既定）。保持は診断ダンプのみ
            if (effectiveFileOutput)
            {
                ArtifactFile.WriteAtomically(svgPath, ArtifactKind.Svg,
                    temp => File.WriteAllText(temp, SvgWriter.Write(ir), TextEncodings.Utf8NoBom));
                verbose.WriteLine(Msg.Format("runner.svg_written", svgPath));
            }

            var swEmf = Stopwatch.StartNew();
            ArtifactFile.WriteAtomically(emfPath, ArtifactKind.Emf,
                temp => emfWriter.Write(ir, temp, diag));
            emfBytes = new FileInfo(emfPath).Length; // 一時 EMF 削除前にサマリ用の値を確保
            nodeCount = ir.Nodes.Count;
            verbose.WriteLine(Msg.Format("runner.emf_written", emfPath, emfBytes, swEmf.ElapsedMilliseconds));

            flattenedOpacity = diag.Entries.Count(
                e => e.Type.StartsWith(EmfOpacityFlattenedDiagPrefix, StringComparison.Ordinal));

            // ---------- 診断ログ（件数と対象。Cli と同形式の diag.json） ----------
            if (diagnostic)
                DiagnosticsReport.WriteDiagJson(log, svgPath, diag);

            // ---------- クリップボード登録（全ファイル成果物を書き終えてから最後に登録する） ----------
            // 登録失敗（分単位のクリップボード占有＝ウィンドウなし保持者はリトライで救えないと実測）は
            // ここで止めず、後始末後に EMF の代替導線を案内する。InvalidOperationException 以外
            // （引数・ファイル系＝直前に自書きした EMF の異常）は内部欠陥のためコマンド境界の全量報告へ通す
            try
            {
                env.SetClipboardFromFile(emfPath);
            }
            catch (InvalidOperationException ex)
            {
                clipboardError = ex.Message;
                recoveredEmfPath = PetaOutcome.RecoverIfNeeded(scope, emfPath, effectiveFileOutput, warn);
            }
        }
        finally
        {
            scope.Dispose(); // ファイル出力オフの一時後始末・予約ロック解放。削除失敗は警告のみ
        }

        // 後始末後に結果を報告する（登録失敗の案内＋通常モードの結果サマリ）
        PetaOutcome.Report(Msg.Current, log, diagnostic, effectiveFileOutput, emfPath, clipboardError,
            recoveredEmfPath, emfBytes, nodeCount, effectiveGroupByLayer, choice.ShxSubstitute,
            choice.TextOutline, choice.MarginMm);

        // 透過を検出した実行では、貼付（EMF）が透過を持てず SVG だけが透過表現を保つことを
        // 通常モードでも案内する（診断ログは PETADIAG 限定のため）。結果サマリの後に置き、
        // 通常の返答を注記が押し流さないようにする
        if (flattenedOpacity > 0)
            log.WriteLine(Msg.Text("runner.emf_opacity_flattened"));
    }

    internal static bool AbortIfEmptySelectionIr(IrDocument ir, IrDiagnostics diagnostics, TextWriter log)
    {
        if (ir.Nodes.Count != 0) return false;
        log.WriteLine(Msg.Text("runner.empty_selection_ir"));
        DiagnosticsReport.WriteSummaryLines(log, diagnostics);
        if (diagnostics.Entries.Any(e => e.Type == BackgroundRemover.DiagCategory))
            log.WriteLine(Msg.Text("runner.background_removed_all"));
        return true;
    }
}
