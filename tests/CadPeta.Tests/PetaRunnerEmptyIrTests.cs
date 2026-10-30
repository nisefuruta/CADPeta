using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Ir;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class PetaRunnerEmptyIrTests
{
    static readonly byte[] MinimalPdf = MinimalArtifactBytes.Pdf();

    static byte[] MinimalEmf() => MinimalArtifactBytes.Emf();

    sealed class Editor : IEditorPort
    {
        readonly string _drawingPath;
        public Editor(string drawingPath) => _drawingPath = drawingPath;
        public bool IsDrawingSaved => true;
        public string DrawingPath => _drawingPath;
        public string DrawingStem => "empty";
        public string CurrentLayoutName => "Model";
        public object? GetPickfirstSelection() => null;
        public SelectionResult PromptSelection() => SelectionResult.OfEmpty();
        public string? PromptString(string message) => null;
        public PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
            double defaultValue, bool useDefault, bool allowNone, bool allowZero, bool allowNegative) =>
            PromptResult<double>.OfNone();
        public void WriteLine(string message) { }
        public IDisposable BeginWindowPickScope(TextWriter verbose) => new Scope();
        public PromptResult<PlotWindow> PromptWindow(string firstMessage,
            IReadOnlyList<PromptKeyword> keywords, string secondMessage) =>
            PromptResult<PlotWindow>.OfNone();
        sealed class Scope : IDisposable { public void Dispose() { } }
    }

    sealed class Plotter : IPlotPort
    {
        public IDisposable BeginSession(TextWriter log, TextWriter verbose) => new Scope();
        public void SetWindow(PlotWindow window) { }
        public void SetWindowScale(bool useFitScale) { }
        public void SetFullPlotType() { }
        public void Validate(TextWriter log) { }
        public PaperMetrics GetPaperMetrics(TextWriter log) =>
            new(100, 100, 90, 90, 50, 50, 1, 0, true);
        public void Publish(string outputPdfPath, TextWriter log, TextWriter verbose) =>
            File.WriteAllBytes(outputPdfPath, MinimalPdf);
        sealed class Scope : IDisposable { public void Dispose() { } }
    }

    sealed class EmptyAcquirer : IIrAcquirer
    {
        public IrDocument Acquire(string sourcePath, IrDiagnostics diag) => new();
    }

    sealed class EmfOutput : IIrOutput
    {
        public bool Called { get; private set; }
        public void Write(IrDocument ir, string outputPath, IrDiagnostics diag)
        { Called = true; File.WriteAllBytes(outputPath, MinimalEmf()); }
    }

    sealed class UnusedSelectionPlotter : ISelectionPlotPort
    {
        public ISelectionPlotSession Begin(object selectionHandle,
            TextWriter log, TextWriter verbose) => throw new InvalidOperationException("選択絞り込みは呼ばれない");
    }

    sealed class UnusedOutliner : IGlyphOutliner
    {
        public List<IrSegment>? Outline(GlyphRun run) => null;
    }

    // 先選択（GetPickfirstSelection）で絞り込みへ入り、範囲プロンプトの1回目で
    // レイヤ別グループ化(L)をオンにし、2回目の Enter で全体を確定するエディタ。
    // OCG欠如フォールバック・診断モード中止の両テストで共用する
    sealed class SelectionEditor : IEditorPort
    {
        readonly string _drawingPath;
        int _windowCalls;
        public SelectionEditor(string drawingPath) => _drawingPath = drawingPath;
        public bool IsDrawingSaved => true;
        public string DrawingPath => _drawingPath;
        public string DrawingStem => "sel";
        public string CurrentLayoutName => "Model";
        public object? GetPickfirstSelection() => new object();
        public SelectionResult PromptSelection() => SelectionResult.OfEmpty();
        public string? PromptString(string message) => null;
        public PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
            double defaultValue, bool useDefault, bool allowNone, bool allowZero, bool allowNegative) =>
            PromptResult<double>.OfNone();
        public void WriteLine(string message) { }
        public IDisposable BeginWindowPickScope(TextWriter verbose) => new Scope();
        public PromptResult<PlotWindow> PromptWindow(string firstMessage,
            IReadOnlyList<PromptKeyword> keywords, string secondMessage)
        {
            _windowCalls++;
            return _windowCalls == 1
                ? PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordLayer)
                : PromptResult<PlotWindow>.OfNone();
        }
        sealed class Scope : IDisposable { public void Dispose() { } }
    }

    // 選択絞り込みポートのフェイク（除外なし。範囲プロットは Plotter フェイクが pdf を
    // 実ファイルとして書き出す）。OCG（レイヤ）情報の有無は IIrAcquirer 側（発行 PDF の
    // 解析結果）で表現するため、このポート自体は常に成功させる
    sealed class NoOcgSelectionPlotPort : ISelectionPlotPort
    {
        public ISelectionPlotSession Begin(object selectionHandle,
            TextWriter log, TextWriter verbose) => new Session();

        sealed class Session : ISelectionPlotSession
        {
            public int SelectedCount => 1;
            public int SupportedCount => 1;
            public IReadOnlyList<UnsupportedSelectionItem> Unsupported => Array.Empty<UnsupportedSelectionItem>();
            public IPlotPort Plotter { get; } = new Plotter();
            public void Dispose() { }
        }
    }

    sealed class TrackingSelectionPlotPort : ISelectionPlotPort
    {
        public Session Value { get; } = new();
        public ISelectionPlotSession Begin(object selectionHandle,
            TextWriter log, TextWriter verbose) => Value;

        public sealed class Session : ISelectionPlotSession
        {
            public int SelectedCount => 1;
            public int SupportedCount => 1;
            public IReadOnlyList<UnsupportedSelectionItem> Unsupported =>
                Array.Empty<UnsupportedSelectionItem>();
            public IPlotPort Plotter { get; } = new Plotter();
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }
    }

    sealed class DisposalCheckingAcquirer : IIrAcquirer
    {
        readonly Func<bool> _isDisposed;
        public DisposalCheckingAcquirer(Func<bool> isDisposed) => _isDisposed = isDisposed;
        public IrDocument Acquire(string sourcePath, IrDiagnostics diag)
        {
            Assert.True(_isDisposed(), "PDF取得より前に選択セッションが破棄されていない");
            return new SingleNodeAcquirer().Acquire(sourcePath, diag);
        }
    }

    // OCG（data-layer）を持たない発行PDFの取得結果を模する。ページ全面は覆わない小矩形の
    // 描画要素を1つ持ち、背景除去では消えず、内容クロップでも空にならない
    sealed class SingleNodeAcquirer : IIrAcquirer
    {
        public IrDocument Acquire(string sourcePath, IrDiagnostics diag)
        {
            var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
            ir.Nodes.Add(new IrPath
            {
                Segments = new List<IrSegment>
                {
                    new IrMove(10, 10), new IrLine(20, 10), new IrLine(20, 20),
                    new IrLine(10, 20), new IrClose(),
                },
                Fill = new IrRgb(0, 0, 0),
            });
            return ir;
        }
    }

    // EMF 出力が透過を下地色合成で不透明化した事実を診断へ記録するフェイク
    sealed class FlatteningEmfOutput : IIrOutput
    {
        public void Write(IrDocument ir, string outputPath, IrDiagnostics diag)
        {
            diag.Add(PetaRunner.EmfOpacityFlattenedDiagPrefix + "（合成下地色=#FFFFFF）",
                     "path seq=0 fill ca=0.4");
            File.WriteAllBytes(outputPath, MinimalEmf());
        }
    }

    [Fact]
    public void 透過の不透明化を検出したらSVGのみ透過表現が残る旨を案内する()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var log = new StringWriter();
        PetaRunner.Run(new PetaEnvironment
        {
            Editor = new Editor(Path.Combine(dir, "empty.dwg")),
            Plotter = new Plotter(),
            SelectionPlotter = new UnusedSelectionPlotter(),
            PdfAcquirer = new SingleNodeAcquirer(),
            EmfWriterFactory = _ => new FlatteningEmfOutput(),
            SetClipboardFromFile = _ => { },
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
        }, log, diagnostic: false);

        string text = log.ToString();
        Assert.Contains("透過図形検出。透過表現はSVGファイルを利用のこと", text);
        // 注記は結果サマリ（summary.line の「... bytes / n図形 → ...」）の後に出す。
        // 通常の返答を注記が押し流さないための順序を固定する
        Assert.Contains("bytes /", text);
        Assert.True(text.LastIndexOf("透過図形検出", StringComparison.Ordinal)
                    > text.LastIndexOf("bytes /", StringComparison.Ordinal),
                    "透過注記が結果サマリより前に出ている:\n" + text);
    }

    [Fact]
    public void 透過がなければ案内を出さない()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var log = new StringWriter();
        PetaRunner.Run(new PetaEnvironment
        {
            Editor = new Editor(Path.Combine(dir, "empty.dwg")),
            Plotter = new Plotter(),
            SelectionPlotter = new UnusedSelectionPlotter(),
            PdfAcquirer = new SingleNodeAcquirer(),
            EmfWriterFactory = _ => new EmfOutput(),
            SetClipboardFromFile = _ => { },
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
        }, log, diagnostic: false);

        Assert.DoesNotContain("透過のある図形", log.ToString());
    }

    [Fact]
    public void 案内の判定接頭辞はEmfWriterの診断カテゴリと一致する()
    {
        // PetaRunner は Out.Emf を参照しないため接頭辞を文字列で持つ。実装間の食い違いを
        // ここで機械的に固定する（片方だけ変えると通知が黙って出なくなるため）
        Assert.Equal(CadPeta.Out.Emf.EmfWriter.OpacityFlattenedDiagPrefix,
                     PetaRunner.EmfOpacityFlattenedDiagPrefix);
    }

    [Fact]
    public void 空の選択IRは理由を記録して中止する()
    {
        var ir = new IrDocument();
        var diag = new IrDiagnostics();
        diag.Add("非対応", "HATCH 1");
        var log = new StringWriter();
        Assert.True(PetaRunner.AbortIfEmptySelectionIr(ir, diag, log));
        Assert.Contains("クリップボード更新を中止", log.ToString());
        Assert.Contains("非対応: 1件", log.ToString());
    }

    [Fact]
    public void 描画要素があれば処理を続ける()
    {
        var ir = new IrDocument();
        ir.Nodes.Add(new IrPath());
        Assert.False(PetaRunner.AbortIfEmptySelectionIr(ir, new IrDiagnostics(), TextWriter.Null));
    }

    [Fact]
    public void 非選択経路の空IRは警告してEMF出力とクリップボード登録を続ける()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var emf = new EmfOutput();
        bool clipboardCalled = false;
        var log = new StringWriter();
        PetaRunner.Run(new PetaEnvironment
        {
            Editor = new Editor(Path.Combine(dir, "empty.dwg")),
            Plotter = new Plotter(),
            SelectionPlotter = new UnusedSelectionPlotter(),
            PdfAcquirer = new EmptyAcquirer(),
            EmfWriterFactory = _ => emf,
            SetClipboardFromFile = _ => clipboardCalled = true,
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
        }, log, diagnostic: false);

        Assert.Contains("警告: IR が空", log.ToString());
        Assert.True(emf.Called);
        Assert.True(clipboardCalled);
    }

    [Fact]
    public void OCGレイヤ情報がない発行PDFはレイヤ別出力を諦めて続行する()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var emf = new EmfOutput();
        bool clipboardCalled = false;
        var log = new StringWriter();
        PetaRunner.Run(new PetaEnvironment
        {
            Editor = new SelectionEditor(Path.Combine(dir, "sel.dwg")),
            Plotter = new Plotter(),
            SelectionPlotter = new NoOcgSelectionPlotPort(),
            PdfAcquirer = new SingleNodeAcquirer(),
            EmfWriterFactory = _ => emf,
            SetClipboardFromFile = _ => clipboardCalled = true,
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
        }, log, diagnostic: false);

        string output = log.ToString();
        // 例外にせず、明示ログ＋続行（他のフォールバックと同じ方針）
        Assert.Contains("レイヤ情報(OCG)がないためレイヤ別グループ化を行わずに出力する", output);
        // 実効値が false へ落ちているため、結果サマリは「レイヤ別グループ化」を名乗らない
        Assert.DoesNotContain("オプション:", output);
        Assert.True(emf.Called);
        Assert.True(clipboardCalled);
    }

    [Fact]
    public void 選択セッションはPDF発行直後かつ取得前に破棄する()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var selection = new TrackingSelectionPlotPort();
        PetaRunner.Run(new PetaEnvironment
        {
            Editor = new SelectionEditor(Path.Combine(dir, "sel.dwg")),
            Plotter = new Plotter(),
            SelectionPlotter = selection,
            PdfAcquirer = new DisposalCheckingAcquirer(() => selection.Value.Disposed),
            EmfWriterFactory = _ => new EmfOutput(),
            SetClipboardFromFile = _ => { },
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
        }, TextWriter.Null, diagnostic: false);

        Assert.True(selection.Value.Disposed);
    }

    [Fact]
    public void 診断モードの絞り込みが空IRで中止してもdiag_jsonを書き出す()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        var emf = new EmfOutput();
        var log = new StringWriter();
        PetaRunner.Run(new PetaEnvironment
        {
            Editor = new SelectionEditor(Path.Combine(dir, "diag.dwg")),
            Plotter = new Plotter(),
            SelectionPlotter = new NoOcgSelectionPlotPort(),
            PdfAcquirer = new EmptyAcquirer(),
            EmfWriterFactory = _ => emf,
            SetClipboardFromFile = _ => { },
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
        }, log, diagnostic: true);

        Assert.False(emf.Called); // 中止したので SVG/EMF/クリップボードは行わない
        var diagFiles = Directory.GetFiles(dir, "*.diag.json", SearchOption.AllDirectories);
        Assert.Single(diagFiles);
        Assert.Contains("診断ログ出力", log.ToString());
    }
}
