using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CadPeta.Ir;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class PetaRunnerEmptyIrTests
{
    static readonly byte[] MinimalPdf = MinimalArtifactBytes.Pdf();

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
        { Called = true; File.WriteAllBytes(outputPath, MinimalArtifactBytes.Emf()); }
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
            File.WriteAllBytes(outputPath, MinimalArtifactBytes.Emf());
        }
    }

    sealed class PngOutput : IIrOutput
    {
        public void Write(IrDocument ir, string outputPath, IrDiagnostics diag) =>
            File.WriteAllBytes(outputPath, MinimalArtifactBytes.Png());
    }

    // GDI+ が画像を確保できなかったときと同じ例外を投げる PNG 出力
    sealed class FailingPngOutput : IIrOutput
    {
        public void Write(IrDocument ir, string outputPath, IrDiagnostics diag) => throw new ArgumentException("Parameter is not valid.");
    }

    // 画像の大きさの上限のため解像度を 123 DPI に下げたことを診断に記録する PNG 出力
    sealed class DpiReducingPngOutput : IIrOutput
    {
        public void Write(IrDocument ir, string outputPath, IrDiagnostics diag)
        {
            diag.Add(PngSize.DpiReducedDiag, "dpi=123", 123);
            File.WriteAllBytes(outputPath, MinimalArtifactBytes.Png());
        }
    }

    // 取得が始まったことを知らせ、取得の中で渡されたログに 1 行書く（PETA は取得を背景のスレッドで行う）
    sealed class SignalingAcquirer : IIrAcquirer
    {
        readonly TextWriter _log;
        readonly ManualResetEventSlim _started;
        public SignalingAcquirer(TextWriter log, ManualResetEventSlim started) { _log = log; _started = started; }
        public IrDocument Acquire(string sourcePath, IrDiagnostics diag)
        {
            _started.Set();
            _log.WriteLine("取得の記録");
            return new SingleNodeAcquirer().Acquire(sourcePath, diag);
        }
    }

    sealed class ThrowingAcquirer : IIrAcquirer
    {
        public IrDocument Acquire(string sourcePath, IrDiagnostics diag) => throw new InvalidDataException("壊れた PDF");
    }

    // 書き込んだスレッドを覚えるログ（作ったスレッドのほかから書かれたかを確かめる）
    sealed class ThreadCheckingWriter : StringWriter
    {
        readonly int _owner = Environment.CurrentManagedThreadId;
        public bool WrittenFromOtherThread { get; private set; }
        public override void Write(char value) { Check(); base.Write(value); }
        public override void Write(string? value) { Check(); base.Write(value); }
        public override void WriteLine(string? value) { Check(); base.WriteLine(value); }
        void Check() { if (Environment.CurrentManagedThreadId != _owner) WrittenFromOtherThread = true; }
    }

    // 図面は <temp>\dwg\a.dwg。設定は <temp>\settings\settings.json（settings が null なら作らない＝既定）
    static PetaEnvironment Env(TempFolder temp, FakeEditor ed, IIrAcquirer? acquirer, IIrOutput? emf = null,
                               Action<string>? clipboard = null, ISelectionPlotPort? selection = null,
                               CadPetaSettings? settings = null, Func<double, IIrOutput>? png = null,
                               Func<TextWriter, IIrAcquirer>? acquirerFactory = null)
    {
        string settingsPath = Path.Combine(temp.Path, "settings", "settings.json");
        if (settings != null)
            Assert.Null(CadPetaSettingsStore.Save(settingsPath, settings));
        ed.DrawingPath = Path.Combine(temp.Path, "dwg", "a.dwg");
        ed.DrawingStem = "a";
        return new PetaEnvironment
        {
            Editor = ed,
            Plotter = new Plotter(),
            SelectionPlotter = selection ?? new UnusedSelectionPlotter(),
            PdfAcquirerFactory = (log, _) => acquirerFactory?.Invoke(log) ?? acquirer!,
            EmfWriterFactory = (_, _, _) => emf ?? new EmfOutput(),
            PngWriterFactory = (dpi, _) => (png ?? (_ => new PngOutput()))(dpi),
            SetClipboardFromFile = clipboard ?? (_ => { }),
            TrueTypeOutlinerFactory = () => new UnusedOutliner(),
            Settings = new SettingsHolder(settingsPath),
        };
    }

    // 範囲は Enter（全体）
    static FakeEditor WholeSheet()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        return ed;
    }

    // 先選択で絞り込みへ入り、範囲の 1 回目でレイヤ別グループ化(L)をオンにし、2 回目の Enter で全体を確定する
    static FakeEditor SelectionWithLayer()
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordLayer));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        return ed;
    }

    static string OutputDir(TempFolder temp) => Path.Combine(temp.Path, "dwg", "CADPeta");

    static string[] OutputNames(TempFolder temp) =>
        Directory.Exists(OutputDir(temp))
            ? Directory.GetFiles(OutputDir(temp)).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    static CadPetaSettings Formats(params ArtifactKind[] kinds) => CadPetaSettings.Default with { Formats = ArtifactKindSet.Of(kinds) };

    // ---- 設定のとおりの出力 ----

    [Fact]
    public void 設定が無ければEMFだけを図面のフォルダのCADPetaに出す()
    {
        using var temp = new TempFolder();

        PetaRunner.Run(Env(temp, WholeSheet(), new SingleNodeAcquirer()), TextWriter.Null, diagnostic: false);

        var names = OutputNames(temp);
        Assert.Single(names);
        Assert.EndsWith(".emf", names[0]);
    }

    [Fact]
    public void 設定の4形式をすべて出す()
    {
        using var temp = new TempFolder();
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer(),
            settings: Formats(ArtifactKind.Pdf, ArtifactKind.Svg, ArtifactKind.Emf, ArtifactKind.Png));

        PetaRunner.Run(env, TextWriter.Null, diagnostic: false);

        Assert.Equal(new[] { ".emf", ".pdf", ".png", ".svg" }, OutputNames(temp).Select(Path.GetExtension).ToArray());
    }

    [Fact]
    public void 形式が0件ならファイルを出さずクリップボードに登録してPETAOPTを案内する()
    {
        using var temp = new TempFolder();
        bool clipboard = false;
        var log = new StringWriter();
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer(), clipboard: _ => clipboard = true, settings: Formats());

        PetaRunner.Run(env, log, diagnostic: false);

        Assert.False(Directory.Exists(OutputDir(temp)));
        Assert.True(clipboard);
        Assert.Contains("PETAOPT", log.ToString());
    }

    [Fact]
    public void PNGは設定の解像度で書く()
    {
        using var temp = new TempFolder();
        var dpis = new List<double>();
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer(),
            settings: Formats(ArtifactKind.Png) with { PngDpi = 150 }, png: dpi => { dpis.Add(dpi); return new PngOutput(); });

        PetaRunner.Run(env, TextWriter.Null, diagnostic: false);

        Assert.Equal(new[] { 150.0 }, dpis);
    }

    [Fact]
    public void ファイル名の形にコメントがあれば範囲の後に聞いて名前に入れる()
    {
        using var temp = new TempFolder();
        var ed = WholeSheet();
        ed.StringResponse = "立面";
        var env = Env(temp, ed, new SingleNodeAcquirer(),
            settings: CadPetaSettings.Default with { FileName = FileNameTemplate.Parse("{drawing}_{comment}").Template! });

        PetaRunner.Run(env, TextWriter.Null, diagnostic: false);

        Assert.Equal(new[] { "a_立面.emf" }, OutputNames(temp));
    }

    [Fact]
    public void コメントのEscはコマンドを中止して何も出さない()
    {
        using var temp = new TempFolder();
        var ed = WholeSheet();
        ed.StringResponse = null;
        bool clipboard = false;
        var env = Env(temp, ed, new SingleNodeAcquirer(), clipboard: _ => clipboard = true,
            settings: CadPetaSettings.Default with { FileName = FileNameTemplate.Parse("{drawing}_{comment}").Template! });

        PetaRunner.Run(env, TextWriter.Null, diagnostic: false);

        Assert.Empty(OutputNames(temp));
        Assert.False(clipboard);
    }

    [Fact]
    public void 出力する形式が無ければコメントを聞かない()
    {
        using var temp = new TempFolder();
        var ed = WholeSheet();
        var env = Env(temp, ed, new SingleNodeAcquirer(),
            settings: Formats() with { FileName = FileNameTemplate.Parse("{drawing}_{comment}").Template! });

        PetaRunner.Run(env, TextWriter.Null, diagnostic: false);

        Assert.Empty(ed.StringMessages);
    }

    [Fact]
    public void 診断は設定の形式にかかわらず全部の形式と診断のファイルを出す()
    {
        using var temp = new TempFolder();
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer(), settings: Formats(ArtifactKind.Emf));

        PetaRunner.Run(env, new StringWriter(), diagnostic: true);

        var names = OutputNames(temp);
        foreach (string ext in new[] { ".emf", ".pdf", ".png", ".svg", ".svg.diag.json" })
            Assert.Contains(names, n => n.EndsWith(ext, StringComparison.Ordinal) && !n.Contains(".stage"));
        Assert.Contains(names, n => n.Contains(".stage"));
    }

    [Fact]
    public void 直した設定はPETAの最初に表示する()
    {
        using var temp = new TempFolder();
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer());
        Directory.CreateDirectory(Path.GetDirectoryName(env.Settings.SettingsPath)!);
        File.WriteAllText(env.Settings.SettingsPath, "{\"schemaVersion\": 1, \"pngDpi\": 5000}");
        var log = new StringWriter();

        PetaRunner.Run(env, log, diagnostic: false);

        Assert.Contains("pngDpi", log.ToString());
    }

    // ---- コメントの入力と変換の重ね合わせ ----

    // PDF の読み取りと変換は背景で進め、その間にコメントを聞く（入力の待ち時間を変換に使う）
    [Fact]
    public void コメントはPDFの読み取りが始まった後に聞く()
    {
        using var temp = new TempFolder();
        using var started = new ManualResetEventSlim();
        bool startedWhenAsked = false;
        var ed = WholeSheet();
        ed.StringResponder = _ => { startedWhenAsked = started.Wait(TimeSpan.FromSeconds(10)); return "立面"; };
        var env = Env(temp, ed, null, acquirerFactory: log => new SignalingAcquirer(log, started),
            settings: CadPetaSettings.Default with { FileName = FileNameTemplate.Parse("{drawing}_{comment}").Template! });

        PetaRunner.Run(env, TextWriter.Null, diagnostic: false);

        Assert.True(startedWhenAsked);
        Assert.Equal(new[] { "a_立面.emf" }, OutputNames(temp));
    }

    // 背景の処理はコマンドのログ（CAD の画面）へ直接書かない。そのメッセージはコマンドの側で表示する
    [Fact]
    public void 背景の処理のメッセージはコマンドのスレッドから表示する()
    {
        using var temp = new TempFolder();
        var log = new ThreadCheckingWriter();
        // コメントの入力の間に取得を背景で始めさせる（入力が無いと、待つ側のスレッドが背景の処理をそのまま実行することがある）
        using var started = new ManualResetEventSlim();
        var ed = WholeSheet();
        ed.StringResponder = _ => { started.Wait(TimeSpan.FromSeconds(10)); return ""; };
        var env = Env(temp, ed, null, acquirerFactory: l => new SignalingAcquirer(l, started),
            settings: CadPetaSettings.Default with { FileName = FileNameTemplate.Parse("{drawing}_{comment}").Template! });

        PetaRunner.Run(env, log, diagnostic: false);

        Assert.False(log.WrittenFromOtherThread);
        Assert.Contains("取得の記録", log.ToString());
    }

    // 入力の途中で例外が出ても、背景の処理が書いたメッセージを捨てない
    [Fact]
    public void 入力の途中の例外でも背景の処理のメッセージを表示する()
    {
        using var temp = new TempFolder();
        var log = new StringWriter();
        using var started = new ManualResetEventSlim();
        var ed = WholeSheet();
        ed.StringResponder = _ => { started.Wait(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("入力の失敗"); };
        var env = Env(temp, ed, null, acquirerFactory: l => new SignalingAcquirer(l, started),
            settings: CadPetaSettings.Default with { FileName = FileNameTemplate.Parse("{drawing}_{comment}").Template! });

        Assert.Throws<InvalidOperationException>(() => PetaRunner.Run(env, log, diagnostic: false));

        Assert.Contains("取得の記録", log.ToString());
    }

    [Fact]
    public void 背景の処理の例外はコマンドへそのまま届く()
    {
        using var temp = new TempFolder();

        var ex = Assert.Throws<InvalidDataException>(() =>
            PetaRunner.Run(Env(temp, WholeSheet(), new ThrowingAcquirer()), TextWriter.Null, diagnostic: false));

        Assert.Equal("壊れた PDF", ex.Message);
    }

    // ---- 取得した図の扱い ----

    [Fact]
    public void 透過の不透明化を検出したらSVGのみ透過表現が残る旨を案内する()
    {
        using var temp = new TempFolder();
        var log = new StringWriter();
        PetaRunner.Run(Env(temp, WholeSheet(), new SingleNodeAcquirer(), emf: new FlatteningEmfOutput()), log, diagnostic: false);

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
        var log = new StringWriter();
        PetaRunner.Run(Env(temp, WholeSheet(), new SingleNodeAcquirer()), log, diagnostic: false);

        Assert.DoesNotContain("透過図形検出", log.ToString());
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
        var emf = new EmfOutput();
        bool clipboardCalled = false;
        var log = new StringWriter();
        PetaRunner.Run(Env(temp, WholeSheet(), new EmptyAcquirer(), emf, _ => clipboardCalled = true), log, diagnostic: false);

        Assert.Contains("警告: IR が空", log.ToString());
        Assert.True(emf.Called);
        Assert.True(clipboardCalled);
    }

    [Fact]
    public void OCGレイヤ情報がない発行PDFはレイヤ別出力を諦めて続行する()
    {
        using var temp = new TempFolder();
        var emf = new EmfOutput();
        bool clipboardCalled = false;
        var log = new StringWriter();
        PetaRunner.Run(Env(temp, SelectionWithLayer(), new SingleNodeAcquirer(), emf, _ => clipboardCalled = true,
            new NoOcgSelectionPlotPort()), log, diagnostic: false);

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
        var selection = new TrackingSelectionPlotPort();
        PetaRunner.Run(Env(temp, SelectionWithLayer(), new DisposalCheckingAcquirer(() => selection.Value.Disposed),
            selection: selection), TextWriter.Null, diagnostic: false);

        Assert.True(selection.Value.Disposed);
    }

    // 出力先が書き込めるかを確かめるために作ったフォルダを、何も出さない実行で残さない
    [Fact]
    public void 絞り込みの図が空で中止したら出力先のフォルダを作らない()
    {
        using var temp = new TempFolder();
        PetaRunner.Run(Env(temp, SelectionWithLayer(), new EmptyAcquirer(), selection: new NoOcgSelectionPlotPort()),
            TextWriter.Null, diagnostic: false);

        Assert.False(Directory.Exists(OutputDir(temp)));
    }

    // PNG は画像を確保できないなどで失敗することがある。PNG だけを失敗として表示し、ほかの形式とクリップボードは続ける
    [Fact]
    public void PNGを書けなくてもほかの形式とクリップボードは続けて失敗を表示する()
    {
        using var temp = new TempFolder();
        var log = new StringWriter();
        bool clipboard = false;
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer(), clipboard: _ => clipboard = true,
            settings: Formats(ArtifactKind.Emf, ArtifactKind.Png), png: _ => new FailingPngOutput());

        PetaRunner.Run(env, log, diagnostic: false);

        Assert.True(clipboard);
        Assert.Equal(new[] { ".emf" }, OutputNames(temp).Select(Path.GetExtension).ToArray());
        Assert.Contains("Parameter is not valid.", log.ToString());
    }

    [Fact]
    public void PNGの解像度を下げたら結果の後に知らせる()
    {
        using var temp = new TempFolder();
        var log = new StringWriter();
        var env = Env(temp, WholeSheet(), new SingleNodeAcquirer(), settings: Formats(ArtifactKind.Png),
            png: _ => new DpiReducingPngOutput());

        PetaRunner.Run(env, log, diagnostic: false);

        Assert.Contains(Msg.Format("runner.png_dpi_reduced", 123, CadPetaSettings.DefaultPngDpi), log.ToString());
    }

    [Fact]
    public void 診断モードの絞り込みが空IRで中止してもdiag_jsonを出力先へ出す()
    {
        using var temp = new TempFolder();
        var emf = new EmfOutput();
        var log = new StringWriter();
        PetaRunner.Run(Env(temp, SelectionWithLayer(), new EmptyAcquirer(), emf, selection: new NoOcgSelectionPlotPort()),
            log, diagnostic: true);

        Assert.False(emf.Called); // 中止したので SVG/EMF/クリップボードは行わない
        Assert.Single(OutputNames(temp), n => n.EndsWith(".diag.json", StringComparison.Ordinal));
        Assert.Contains("診断ログ出力", log.ToString());
    }
}