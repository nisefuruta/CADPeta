// PETA/PETADIAG の本体フロー（4製品共通の単一実装）: 範囲（全体・窓）＋選択絞り込み → PDF印刷 →
// IR → 変換層 → 一時フォルダに成果物＋診断ログ → 設定の形式を出力先へ写す → クリップボード登録 → 結果サマリ。
// PDF 印刷の後の取得・変換・書き出しは背景のスレッドで進め、その間にファイル名のコメントと出力先を聞く。
// 絞り込みはプロット対象を側DB（選択オブジェクトだけの複製）へ差し替えるだけで、範囲・尺度・
// クロップの判断は PlotOrchestrator の単一実装を全経路で共有する。
// CAD 固有処理は IEditorPort・IPlotPort と取得デリゲートに委ね、ここには CAD 型が現れない。
using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CadPeta.Acquire.Common;
using CadPeta.Geometry;
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

    /// <summary>発行 PDF を IR 化する（PdfIrAcquirer）。引数はメッセージの出力先（log、verbose）。背景のスレッドで使う。</summary>
    public Func<TextWriter, TextWriter, IIrAcquirer> PdfAcquirerFactory { get; init; } = null!;

    /// <summary>EMF 出力（EmfWriter）。引数は下地色とメッセージの出力先（log、verbose）。背景のスレッドで使う。</summary>
    public Func<IrRgb, TextWriter, TextWriter, IIrOutput> EmfWriterFactory { get; init; } = null!;

    /// <summary>PNG 出力（PngWriter）。引数は解像度（DPI）とメッセージの出力先。背景のスレッドで使う。</summary>
    public Func<double, TextWriter, IIrOutput> PngWriterFactory { get; init; } = null!;

    /// <summary>利用者の設定（出力する形式、出力先、ファイル名の形、PNG の解像度）。</summary>
    public SettingsHolder Settings { get; init; } = null!;

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

        // ---------- 設定（直したことなどは、ファイルを読み直したときだけ表示する） ----------
        var snapshot = env.Settings.Get();
        foreach (string message in snapshot.Messages)
            log.WriteLine(message);
        var settings = snapshot.Settings;

        // ---------- 範囲指定（Enter=全体・2点窓。先選択/オブジェクト(O)は範囲内の絞り込み） ----------
        var choice = RangePrompt.TryGetChoice(ed, log, verbose);
        if (choice == null)
            return; // 利用者の明示キャンセル: 何も出力しない

        // ファイルを出すのは、出力する形式があるときと診断のとき
        bool writesFiles = diagnostic || !settings.Formats.IsEmpty;
        Action<string> warn = message => log.WriteLine(message);
        var now = DateTime.Now;
        // 成果物は一時フォルダで作り、最後に設定の形式だけを出力先へ写す（PDF の発行とクリップボードの EMF は、
        // 形式がオフでも要る）。一時の名前は今までの既定の名前にし、出力先へ写さずに回復した EMF の名前にも使う
        var scope = ArtifactScope.CreateTemp(FileNameTemplate.DefaultName(ed.DrawingStem, ed.CurrentLayoutName, now), warn);
        var emfPath = scope.PathOf(ArtifactKind.Emf);

        // 成果物の生成〜クリップボード登録を try で囲み、途中失敗でも finally で一時フォルダを必ず後始末する。
        // 結果サマリは後始末が済んだ後に PetaOutcome.Report で出す
        string? clipboardError = null;
        string availableEmf = emfPath;
        var publish = new PublishResult(Array.Empty<string>(), Array.Empty<string>());
        Produced produced;
        try
        {
            // ---------- サイレントプロット（AutoCAD の API を使うので、このスレッドで行う） ----------
            var selection = choice.IsSelection
                ? SelectionPlotOrchestrator.Begin(env.SelectionPlotter, ed,
                    choice.SelectionHandle!, log, verbose)
                : null;
            if (choice.IsSelection && selection == null)
                return; // 対象なし・除外の不同意（理由は SelectionPlotOrchestrator が報告済み）

            var pdfPath = scope.PathOf(ArtifactKind.Pdf);
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

            // ---------- 取得・変換・一時フォルダへの書き出しは背景で進め、その間にコメントと出力先を聞く ----------
            // 背景の処理は AutoCAD の API を使わない。メッセージは CAD の画面へ直接書かずにためておき、このスレッドで表示する
            var background = new StringWriter();
            var work = Task.Run(() => Produce(env, choice, settings, diagnostic, scope, pdfPath, cropRect,
                background, diagnostic ? background : TextWriter.Null));
            string? comment = "";
            string? outputDir = null;
            Exception? promptFailure = null;
            try
            {
                if (writesFiles && settings.FileName.UsesComment)
                    comment = CommentPrompt.TryGet(ed, log, settings.FileName,
                        new FileNameFields(ed.DrawingStem, ed.CurrentLayoutName, now, "")); // Esc は null（何も出力せずに中止する）
                if (writesFiles && comment != null)
                    outputDir = OutputLocation.ResolveFor(ed, log, settings);
            }
            catch (Exception ex)
            {
                // 背景の処理が一時フォルダを使い終わるまで待ってから投げ直す（後始末の前に終わらせる）
                promptFailure = ex;
            }
            // 背景の処理のメッセージは、入力や背景の処理が失敗したときも表示する（失敗の前に何が起きたかが分かるように）
            try { produced = WaitBoth(work, promptFailure); }
            finally { log.Write(background.ToString()); }
            if (comment == null)
                return;

            var fields = new FileNameFields(ed.DrawingStem, ed.CurrentLayoutName, now, comment);
            if (produced.EmptySelection)
            {
                // 中止経路でも診断モードなら diag.json と変換各段の SVG ダンプは出力先へ写す。
                // 成果物の出力とクリップボード登録だけを行わない
                if (diagnostic)
                {
                    publish = ArtifactPublisher.Publish(scope, settings.Formats, diagnostics: true,
                        settings.FileName, fields, outputDir!, warn);
                    PetaOutcome.ReportPublished(Msg.Current, log, publish);
                }
                return;
            }

            // ---------- 出力先へ写す（設定の形式だけ。診断は全部） ----------
            // 書けなかった PNG は写さず、写せなかったファイルと同じく失敗の一覧に入れて結果に表示する
            if (writesFiles)
                publish = ArtifactPublisher.Publish(scope,
                    produced.PngFailure == null ? settings.Formats : settings.Formats.With(ArtifactKind.Png, false), diagnostic,
                    settings.FileName, fields, outputDir!, warn);
            if (produced.PngFailure is { } pngFailure)
                publish = publish with { Failures = publish.Failures.Concat(new[] { pngFailure }).ToList() };

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
                availableEmf = PetaOutcome.AvailableEmf(scope, emfPath, publish.PathOf(ArtifactKind.Emf), warn);
            }
        }
        finally
        {
            scope.Dispose(); // 一時フォルダの後始末。削除失敗は警告のみ
        }

        // 後始末後に結果を報告する（写せなかったファイル、登録失敗の案内、結果サマリ）
        PetaOutcome.Report(Msg.Current, log, diagnostic, publish, clipboardError, availableEmf,
            produced.EmfBytes, produced.NodeCount, produced.GroupByLayer, choice.ShxSubstitute,
            choice.TextOutline, choice.MarginMm);

        // 透過を検出した実行では、貼付（EMF）が透過を持てず SVG だけが透過表現を保つことを
        // 通常モードでも案内する（診断ログは PETADIAG 限定のため）。結果サマリの後に置き、
        // 通常の返答を注記が押し流さないようにする
        if (produced.FlattenedOpacity > 0)
            log.WriteLine(Msg.Text("runner.emf_opacity_flattened"));
        // PNG の解像度を、画像の大きさの上限のため下げたこと
        if (produced.PngDpi is { } pngDpi)
            log.WriteLine(Msg.Format("runner.png_dpi_reduced", pngDpi, settings.PngDpi));
    }

    // 背景の処理の結果（結果の要約に使う値）。EmptySelection は、絞り込みの図から描画要素を取得できず成果物を作らなかったこと。
    // PngDpi は、PNG の解像度を下げて書いたときの解像度（下げなければ null）。PngFailure は、PNG を書けなかった理由（書ければ null）
    sealed record Produced(long EmfBytes, int NodeCount, int FlattenedOpacity, bool GroupByLayer, bool EmptySelection,
                           double? PngDpi = null, string? PngFailure = null);

    // 背景の処理を待つ。入力の失敗と背景の処理の失敗は、片方ならそのまま、両方なら合わせて投げる
    static Produced WaitBoth(Task<Produced> work, Exception? promptFailure)
    {
        Produced result;
        try
        {
            result = work.GetAwaiter().GetResult();
        }
        catch (Exception workFailure) when (promptFailure != null)
        {
            throw new AggregateException(promptFailure, workFailure);
        }
        if (promptFailure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(promptFailure).Throw();
        return result;
    }

    // PDF の取得、変換、一時フォルダへの書き出し（SVG・EMF・PNG・診断のファイル）。AutoCAD の API を使わないので背景のスレッドで行う。
    // メッセージは log と verbose（コマンドの側で表示する）にだけ書く
    static Produced Produce(PetaEnvironment env, RangeChoice choice, CadPetaSettings settings, bool diagnostic,
                            ArtifactScope scope, string pdfPath, RectMm? cropRect, TextWriter log, TextWriter verbose)
    {
        var diag = new IrDiagnostics();
        var svgPath = scope.PathOf(ArtifactKind.Svg);
        var emfPath = scope.PathOf(ArtifactKind.Emf);
        bool groupByLayer = choice.GroupByLayer;

        IrDocument ir = env.PdfAcquirerFactory(log, verbose).Acquire(pdfPath, diag);
        if (choice.GroupByLayer && ir.Nodes.Count > 0
            && !ir.Nodes.Any(n => !string.IsNullOrWhiteSpace(n.DataLayer)))
        {
            // 発行 PDF ドライバ・版によっては OCG（レイヤ）情報を持たない場合がある。
            // 既存の他フォールバック（近似用紙・Wblock 複製切替）と同じ方針で、
            // コマンド全体を失敗させずレイヤ別出力を諦めて続行する
            log.WriteLine(Msg.Text("runner.no_ocg"));
            diag.Add("レイヤ別グループ化: OCGレイヤ情報欠如のため無効化", $"pdf={pdfPath}");
            groupByLayer = false;
        }
        // 背景除去=既定で有効。窓指定時のみクロップ（発行 PDF 上の窓の載る矩形＋余白、
        // CLI --crop と同じ変換層）。レイヤ・文字輪郭は範囲・絞り込みに依らず反映する
        var convertOptions = new ConvertOptions
        {
            CropRectMm = cropRect,
            CropMarginMm = choice.MarginMm,
            TextOutline = choice.TextOutline,
            TextOutliner = choice.TextOutline ? env.TrueTypeOutlinerFactory() : null,
            GroupByLayer = groupByLayer,
        };
        if (!diagnostic)
        {
            ConvertPipeline.Apply(ir, convertOptions, diag);
        }
        else
        {
            // 診断モード: 各段の前後を SVG へダンプする。ダンプも一時フォルダに書き、最後に出力先へ写す
            log.WriteLine(Msg.Text("runner.diag_stage_dump"));
            ConvertPipeline.ApplyWithDiagnostics(ir, convertOptions, diag,
                DiagnosticsReport.MakeStageSink(scope.Dir, scope.BaseName, log));
        }
        if (choice.IsSelection && AbortIfEmptySelectionIr(ir, diag, log))
        {
            if (diagnostic)
            {
                log.WriteLine(Msg.Text("runner.diag_empty_ir"));
                DiagnosticsReport.WriteDiagJson(log, svgPath, diag);
            }
            return new Produced(0, 0, 0, groupByLayer, EmptySelection: true);
        }
        if (ir.Nodes.Count == 0)
            log.WriteLine(Msg.Text("runner.empty_ir_warning"));

        // 通常出力は出所メタデータ（data-*）を除去する（Write 既定）。保持は診断ダンプのみ
        if (diagnostic || settings.Formats.Contains(ArtifactKind.Svg))
        {
            ArtifactFile.WriteAtomically(svgPath, ArtifactKind.Svg,
                temp => File.WriteAllText(temp, SvgWriter.Write(ir), TextEncodings.Utf8NoBom));
            verbose.WriteLine(Msg.Format("runner.svg_written", svgPath));
        }

        var swEmf = Stopwatch.StartNew();
        ArtifactFile.WriteAtomically(emfPath, ArtifactKind.Emf,
            temp => env.EmfWriterFactory(choice.EmfBackground, log, verbose).Write(ir, temp, diag));
        long emfBytes = new FileInfo(emfPath).Length; // 一時 EMF 削除前にサマリ用の値を確保
        verbose.WriteLine(Msg.Format("runner.emf_written", emfPath, emfBytes, swEmf.ElapsedMilliseconds));

        // PNG は画像を確保できないなどで失敗することがある。その失敗は PNG だけの失敗として返し、ほかの形式とクリップボードは続ける
        string? pngFailure = null;
        if (diagnostic || settings.Formats.Contains(ArtifactKind.Png))
        {
            var pngPath = scope.PathOf(ArtifactKind.Png);
            try
            {
                ArtifactFile.WriteAtomically(pngPath, ArtifactKind.Png,
                    temp => env.PngWriterFactory(settings.PngDpi, verbose).Write(ir, temp, diag));
            }
            catch (Exception ex) when (IsPngFailure(ex))
            {
                pngFailure = Msg.Format("runner.png_failed", FileFailures.OneLine(ex.Message));
            }
        }

        int flattenedOpacity = diag.Entries.Count(
            e => e.Type.StartsWith(EmfOpacityFlattenedDiagPrefix, StringComparison.Ordinal));

        // ---------- 診断ログ（件数と対象。Cli と同形式の diag.json） ----------
        if (diagnostic)
            DiagnosticsReport.WriteDiagJson(log, svgPath, diag);
        double? reducedPngDpi = diag.Entries.Where(e => e.Type == PngSize.DpiReducedDiag).Select(e => e.Value).FirstOrDefault();
        return new Produced(emfBytes, ir.Nodes.Count, flattenedOpacity, groupByLayer, EmptySelection: false, reducedPngDpi, pngFailure);
    }

    // PNG の書き出しの失敗のうち、PNG だけを失敗にするもの: 成果物のファイルの失敗（ArtifactFile.IsWriteFailure）と、
    // GDI+ が画像を確保・描画できない失敗（ArgumentException、OutOfMemoryException、ExternalException）
    static bool IsPngFailure(Exception ex) =>
        ArtifactFile.IsWriteFailure(ex)
        || ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException;
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
