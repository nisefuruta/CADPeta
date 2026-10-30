using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using CadPeta.Acquire.Pdf;
using CadPeta.Ir;
using CadPeta.Out.Emf;
using CadPeta.Out.Pdf;
using CadPeta.Out.Png;
using CadPeta.UseCases;

namespace CadPeta.App;

public static class PdfPetaPipeline
{
    public static PdfPetaResult Run(PdfPetaRequest request, TextWriter log,
        PdfPetaRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var errors = request.ValidateInvariants();
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        cancellationToken.ThrowIfCancellationRequested();
        // CLI・GUI の表示は言語切替の対象外（常に既定 ja）だが、共有層（Acquire.Pdf・Out.Emf）は
        // カタログ経由で文言を組み立てるため、差し替え lang ファイルの欠陥は CAD 殻と同様に
        // ここで実行ログへ報告する（黙って組み込み文言へ切り替えない）
        using var langAnomalies = BeginCatalogAnomalyScope(MessageCatalogs.JapaneseSlot, log);
        var diag = new IrDiagnostics();
        var ir = new PdfIrAcquirer(log).Acquire(request.InputPdfPath, diag);
        cancellationToken.ThrowIfCancellationRequested();
        int nodesBefore = ir.Nodes.Count;
        var convert = new ConvertOptions
        {
            RemoveBackground = !request.KeepBackground, CropRectMm = request.CropRectMm,
            CropMarginMm = request.CropMarginMm, ColorMode = request.ColorMode,
            ColorReplacements = request.ColorReplacements, TextOutline = request.TextOutline,
            TextOutliner = request.TextOutline ? new GdiGlyphOutliner() : null,
            GroupByLayer = request.GroupByLayer,
        };
        if (options?.DiagnosticStageSink is { } sink)
            ConvertPipeline.ApplyWithDiagnostics(ir, convert, diag, sink, cancellationToken);
        else ConvertPipeline.Apply(ir, convert, diag, cancellationToken);

        if (!request.KeepBackground)
            log.WriteLine($"背景除去: {diag.Entries.Count(e => e.Type.StartsWith("背景矩形の除去"))}件");
        if (request.CropRectMm is { } c)
            log.WriteLine($"クロップ: ({c.X0},{c.Y0})-({c.X1},{c.Y1})mm 余白{request.CropMarginMm}mm → width={ir.WidthMm:F3}mm height={ir.HeightMm:F3}mm 間引き {nodesBefore - ir.Nodes.Count}/{nodesBefore}ノード");
        if (request.ColorMode != CadPeta.Transforms.ColorMode.None || request.ColorReplacements.Count > 0)
            log.WriteLine($"色変換: {(request.ColorMode == CadPeta.Transforms.ColorMode.Monochrome ? "モノクロ化（純白は維持） " : request.ColorMode == CadPeta.Transforms.ColorMode.Grayscale ? "グレースケール化 " : "")}置換規則{request.ColorReplacements.Count}件（変換した色と件数は診断ログに記録）");
        if (request.GroupByLayer)
            log.WriteLine("レイヤ別グループ化: " + (diag.Entries.LastOrDefault(e => e.Type.StartsWith("レイヤ別グループ化（z順"))?.Target ?? "（対象なし）"));

        string? clipboardPath = null, clipboardError = null;
        foreach (var output in request.Outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (output.Kind)
            {
                case PdfPetaOutputKind.Svg:
                    ArtifactFile.WriteAtomically(output.Path, ArtifactKind.Svg,
                        temp => File.WriteAllText(temp, SvgWriter.Write(ir), TextEncodings.Utf8NoBom));
                    log.WriteLine($"SVG出力: {output.Path}"); break;
                case PdfPetaOutputKind.Emf:
                case PdfPetaOutputKind.Clipboard:
                    WriteTimed("EMF", output.Path, () => ArtifactFile.WriteAtomically(output.Path,
                        ArtifactKind.Emf, temp => new EmfWriter(request.EmfBackground, log).Write(ir, temp, diag)), log);
                    if (output.Kind == PdfPetaOutputKind.Clipboard)
                        clipboardPath = output.Path;
                    break;
                case PdfPetaOutputKind.Pdf:
                    WriteTimed("PDF", output.Path, () => ArtifactFile.WriteAtomically(output.Path,
                        ArtifactKind.Pdf, temp => new PdfWriter(new GdiGlyphOutliner(), log).Write(ir, temp, diag)), log); break;
                case PdfPetaOutputKind.Png:
                    WriteTimed("PNG", output.Path, () => ArtifactFile.WriteAtomically(output.Path,
                        ArtifactKind.Png, temp => new PngWriter(output.PngDpi ?? PngWriter.DefaultDpi, log).Write(ir, temp, diag)), log); break;
            }
        }
        // クリップボード登録は全ファイル出力の完了後に行う。登録時の読み戻し検証ログを
        // ファイル出力ログより後に確定し、1回の実行結果を生成順に読めるようにする。
        if (clipboardPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { (options?.ClipboardSetter ?? SetClipboardSta)(clipboardPath, log); }
            catch (InvalidOperationException ex) { clipboardError = ex.ToString(); }
        }
        var summary = diag.Summarize().Select(x => new PdfPetaDiagnostic(x.Type, x.Count, x.Targets)).ToList();
        return new PdfPetaResult(summary, clipboardPath, clipboardError);
    }

    /// <summary>言語カタログの異常検知を log へ結線する。差し替え lang ファイルの読込不能・
    /// キー欠落はここで報告し、表示時フォールバック（読込時に検出できない書式不正）は発生の
    /// 都度報告する。返り値の Dispose が通知先の解除（直前の状態への復元）。lang ファイルの
    /// 不在は報告しない（言語切替を提供しない CLI・GUI では単一ファイル配布を含め常態のため）。</summary>
    internal static IDisposable BeginCatalogAnomalyScope(MessageCatalogSlot slot, TextWriter log)
    {
        var scope = MessageCatalog.BeginFormatFallbackScope((catalogName, key, error) =>
            log.WriteLine(slot.Catalog.Format("lang.format_fallback", catalogName, key, error)));
        try
        {
            if (slot.FileError != null)
                log.WriteLine(slot.Catalog.Format("lang.file_invalid", slot.FilePath, slot.FileError));
            if (slot.MissingKeys.Count > 0)
                log.WriteLine(slot.Catalog.Format("lang.missing_keys",
                    slot.Catalog.Name, slot.MissingKeys.Count, string.Join(", ", slot.MissingKeys)));
            return scope;
        }
        catch
        {
            // 初期報告に失敗すると呼び出し側はスコープを受け取れず解除できないため、
            // 直前の状態へ戻してから再送出する
            scope.Dispose();
            throw;
        }
    }

    static void WriteTimed(string kind, string path, Action write, TextWriter log)
    {
        var sw = Stopwatch.StartNew(); write(); sw.Stop();
        log.WriteLine($"{kind}出力: {path} ({new FileInfo(path).Length:N0} bytes, {sw.ElapsedMilliseconds}ms)");
    }

    static void SetClipboardSta(string path, TextWriter log)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { EmfClipboard.SetFromFile(path, log); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
