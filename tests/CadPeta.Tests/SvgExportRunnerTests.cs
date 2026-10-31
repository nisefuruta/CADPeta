using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Acquire.Common;
using CadPeta.Ir;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class SvgExportRunnerTests
{
    [Fact]
    public void PETADATAのグラデーション集計は書き出し診断だけを数える()
    {
        var diagnostics = new IrDiagnostics();
        diagnostics.Add(AcquireDiagCategories.GradientRasterized, "実=144dpi 10x10px", 144);
        diagnostics.Add(AcquireDiagCategories.GradientRasterized, "実=288dpi 20x20px", 288);
        diagnostics.Add(AcquireDiagCategories.GradientBudgetFallback, "HATCH 1");
        diagnostics.Add("別機能: グラデーションハッチのラスタ化", "実=999dpi");
        var log = new StringWriter();

        SvgExportRunner.WriteGradientSummary(diagnostics, log);

        Assert.Equal(
            "[CadPeta] PETADATA グラデーション: 2件（実DPI 144～288）をラスタ化、1件を代表色へ縮退" +
            Environment.NewLine,
            log.ToString());
    }

    [Fact]
    public void 数値のない旧形式診断は件数だけを表示する()
    {
        var diagnostics = new IrDiagnostics();
        diagnostics.Add(AcquireDiagCategories.GradientRasterized, "旧形式");
        var log = new StringWriter();
        SvgExportRunner.WriteGradientSummary(diagnostics, log);
        Assert.Contains("1件をラスタ化", log.ToString());
        Assert.DoesNotContain("実DPI", log.ToString());
    }

    [Fact]
    public void 対象診断がなければPETADATA集計を出さない()
    {
        var diagnostics = new IrDiagnostics();
        diagnostics.Add("別機能", "対象");
        var log = new StringWriter();

        SvgExportRunner.WriteGradientSummary(diagnostics, log);

        Assert.Equal(string.Empty, log.ToString());
    }

    sealed class DataExportEditor : IEditorPort
    {
        readonly string _drawingPath;
        public DataExportEditor(string drawingPath) => _drawingPath = drawingPath;
        public bool IsDrawingSaved => true;
        public string DrawingPath => _drawingPath;
        public string DrawingStem => "data";
        public string CurrentLayoutName => "Model";
        public object? GetPickfirstSelection() => new object(); // 先選択あり→選択プロンプトを出さない
        public SelectionResult PromptSelection() => SelectionResult.OfEmpty();
        public bool HasScreen => false;
        public bool ShowCommentDialog(CommentForm form) => throw new InvalidOperationException("コメントのダイアログは開かない");
        public string? PromptString(string message) => null;
        public PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
            double defaultValue, bool useDefault, bool allowNone, bool allowZero, bool allowNegative) =>
            PromptResult<double>.OfNone(); // 尺度・オプションは既定のまま確定
        public PromptResult<string> PromptKeywords(string message, IReadOnlyList<PromptKeyword> keywords) =>
            throw new InvalidOperationException("キーワードの入力は呼ばれない");
        public void WriteLine(string message) { }
        public IDisposable BeginWindowPickScope(TextWriter verbose) => new Scope();
        public PromptResult<PlotWindow> PromptWindow(string firstMessage,
            IReadOnlyList<PromptKeyword> keywords, string secondMessage) =>
            PromptResult<PlotWindow>.OfNone();
        sealed class Scope : IDisposable { public void Dispose() { } }
    }

    sealed class NoopOutliner : IGlyphOutliner
    {
        public List<IrSegment>? Outline(GlyphRun run) => null;
    }

    [Fact]
    public void 診断モードは変換各段のSVGダンプとdiag_jsonを書き出す()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var env = new SvgExportEnvironment
        {
            Editor = new DataExportEditor(Path.Combine(dir, "data.dwg")),
            AcquireSelection = (handle, choice, diag) =>
            {
                var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
                ir.Nodes.Add(new IrPath
                {
                    Segments = new List<IrSegment>
                    {
                        new IrMove(0, 0), new IrLine(10, 0), new IrLine(10, 10), new IrClose(),
                    },
                    Fill = new IrRgb(0, 0, 0),
                });
                return ir;
            },
            TrueTypeOutlinerFactory = () => new NoopOutliner(),
        };
        var log = new StringWriter();

        SvgExportRunner.Run(env, log, TextWriter.Null, diagnostic: true);

        var stageFiles = Directory.GetFiles(dir, "*.stage00.acquired.svg", SearchOption.AllDirectories);
        Assert.Single(stageFiles);
        var diagFiles = Directory.GetFiles(dir, "*.diag.json", SearchOption.AllDirectories);
        Assert.Single(diagFiles);
        Assert.Contains("PETADATA 完了", log.ToString());
        Assert.Contains("診断ログ出力", log.ToString());
    }

    [Fact]
    public void 診断オフの既定呼び出しはdiag_jsonを書き出さない()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var env = new SvgExportEnvironment
        {
            Editor = new DataExportEditor(Path.Combine(dir, "data.dwg")),
            AcquireSelection = (handle, choice, diag) =>
            {
                var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
                ir.Nodes.Add(new IrPath
                {
                    Segments = new List<IrSegment>
                    {
                        new IrMove(0, 0), new IrLine(10, 0), new IrLine(10, 10), new IrClose(),
                    },
                    Fill = new IrRgb(0, 0, 0),
                });
                return ir;
            },
            TrueTypeOutlinerFactory = () => new NoopOutliner(),
        };
        var log = new StringWriter();

        // 診断引数を省略した既存呼び出し（4製品のPetaCommand.cs）と同じ形。既定はfalseで無回帰
        SvgExportRunner.Run(env, log, TextWriter.Null);

        Assert.Empty(Directory.GetFiles(dir, "*.diag.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(dir, "*.stage*.svg", SearchOption.AllDirectories));
    }
}
