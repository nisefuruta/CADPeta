using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using CadPeta.Acquire.Common;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

public sealed class SvgExportEnvironment
{
    public IEditorPort Editor { get; init; } = null!;
    public Func<object, RangeChoice, IrDiagnostics, IrDocument> AcquireSelection { get; init; } = null!;
    public Func<IGlyphOutliner> TrueTypeOutlinerFactory { get; init; } = null!;
}

public static class SvgExportRunner
{
    /// <summary>
    /// PETADATA本体。diagnostic=true（PETADATADIAGが結線する）は、PETADIAGと同じ趣旨で
    /// 変換層の各段の前後をSVGダンプし、diag.jsonを出力フォルダへ書く。既定はfalse
    /// （既存のPETADATA呼び出し元は無改修で通る）。
    /// </summary>
    public static void Run(SvgExportEnvironment env, TextWriter log, TextWriter verbose, bool diagnostic = false)
    {
        var choice = RangePrompt.TryGetDataChoice(env.Editor, log, verbose);
        if (choice == null) return;
        var outputDir = OutputLocation.TryResolveExport(env.Editor, log);
        if (outputDir == null) { log.WriteLine(Msg.Text("data.aborted")); return; }
        Action<string> warn = message => log.WriteLine(message);
        using var scope = ArtifactScope.ReserveInDir(outputDir,
            OutputLocation.Stem(env.Editor), env.Editor.CurrentLayoutName + "_data", DateTime.Now, warn);
        string svgPath = Path.Combine(scope.Dir, scope.BaseName + ".svg");
        var diag = new IrDiagnostics();
        var sw = Stopwatch.StartNew();
            var ir = env.AcquireSelection(choice.SelectionHandle!, choice, diag);
            if (ir.Nodes.Count == 0)
            {
                log.WriteLine(Msg.Text("data.empty_ir"));
                WriteDiagnostics(diag, log);
                if (diagnostic)
                    DiagnosticsReport.WriteDiagJson(log, svgPath, diag);
                return;
            }
            var convertOptions = new ConvertOptions
            {
                RemoveBackground = false,
                TextOutline = choice.TextOutline,
                TextOutliner = choice.TextOutline ? env.TrueTypeOutlinerFactory() : null,
                GroupByLayer = choice.GroupByLayer,
            };
            if (!diagnostic)
            {
                ConvertPipeline.Apply(ir, convertOptions, diag);
            }
            else
            {
                // 診断モード（PETADATADIAG）: PETADIAGと同じ趣旨で各段の前後をSVGダンプする
                log.WriteLine(Msg.Format("runner.diag_stage_dump", scope.Dir));
                ConvertPipeline.ApplyWithDiagnostics(ir, convertOptions, diag,
                    DiagnosticsReport.MakeStageSink(scope.Dir, scope.BaseName, log));
            }
            ArtifactFile.WriteAtomically(svgPath, ArtifactKind.Svg,
                temp => File.WriteAllText(temp, SvgWriter.Write(ir), TextEncodings.Utf8NoBom));
            verbose.WriteLine(Msg.Format("data.svg_written", svgPath, ir.Nodes.Count, sw.ElapsedMilliseconds));
            log.WriteLine(Msg.Format("data.done", svgPath, ir.Nodes.Count));
            WriteGradientSummary(diag, log);
            WriteDiagnostics(diag, log);
            if (diagnostic)
                DiagnosticsReport.WriteDiagJson(log, svgPath, diag);
    }

    static void WriteDiagnostics(IrDiagnostics diagnostics, TextWriter log) =>
        DiagnosticsReport.WriteSummaryLines(log, diagnostics);

    internal static void WriteGradientSummary(IrDiagnostics diagnostics, TextWriter log)
    {
        string[] fallbackTypes =
        {
            AcquireDiagCategories.GradientShapeFallback,
            AcquireDiagCategories.GradientBudgetFallback,
            AcquireDiagCategories.GradientInvalidFallback,
        };
        var rasterized = diagnostics.Entries.Where(entry => entry.Type == AcquireDiagCategories.GradientRasterized).ToList();
        int fallbackCount = diagnostics.Entries.Count(entry => fallbackTypes.Contains(entry.Type));
        if (rasterized.Count == 0 && fallbackCount == 0) return;

        var actualDpis = rasterized.Where(entry => entry.Value.HasValue)
                                   .Select(entry => entry.Value!.Value).ToList();
        string rasterText = rasterized.Count == 0 ? Msg.Text("data.gradient.zero")
            : actualDpis.Count == 0 ? Msg.Format("data.gradient.count", rasterized.Count)
            : Msg.Format("data.gradient.count_dpi", rasterized.Count, actualDpis.Min(), actualDpis.Max());
        log.WriteLine(Msg.Format("data.gradient.summary", rasterText, fallbackCount));
    }
}
