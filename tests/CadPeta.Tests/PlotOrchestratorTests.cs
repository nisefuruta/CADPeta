// PlotOrchestrator（PDF印刷経路の判断ロジック）の port 境界テスト。偽の IPlotPort を差し込み、
// 尺度採否（フィット vs ページ設定尺度維持）・窓クロップ矩形の算出・呼び出し順を検査する。
// 実機プロット（AutoCAD＋PDFドライバ）を持たない環境でもPDF印刷経路の中核判断を機械検証できる。
using System.IO;
using CadPeta.Geometry;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class PlotOrchestratorTests
{
    const int Places = 6;

    sealed class FakePlotPort : IPlotPort
    {
        public List<string> Calls { get; } = new();
        public PlotWindow? WindowSet;
        public bool? WindowScaleUseFit;
        public string? PublishedPath;
        public bool SessionDisposed;
        public string? ThrowOn;
        int _metricsCallCount;

        // フィット判定前のページ設定尺度（mm/単位）。SetWindowScale(true) で NaN（用紙合わせ）へ移る
        public PaperMetrics ExplicitMetrics;
        public PaperMetrics FitMetrics;
        PaperMetrics _current;

        public FakePlotPort(PaperMetrics explicitMetrics, PaperMetrics fitMetrics)
        {
            ExplicitMetrics = explicitMetrics;
            FitMetrics = fitMetrics;
            _current = explicitMetrics;
        }

        public IDisposable BeginSession(TextWriter log, TextWriter verbose)
        {
            Calls.Add("BeginSession");
            return new Disposer(() => { Calls.Add("EndSession"); SessionDisposed = true; });
        }

        void FailIfRequested(string stage)
        {
            if (ThrowOn == stage) throw new InvalidOperationException($"故障注入: {stage}");
        }

        public void SetWindow(PlotWindow window)
        {
            Calls.Add("SetWindow");
            FailIfRequested("SetWindow");
            WindowSet = window;
        }

        public void SetWindowScale(bool useFitScale)
        {
            Calls.Add("SetWindowScale");
            FailIfRequested("SetWindowScale");
            WindowScaleUseFit = useFitScale;
            if (useFitScale) _current = FitMetrics; // 用紙合わせ = 尺度 NaN
        }

        public void SetFullPlotType() { Calls.Add("SetFullPlotType"); FailIfRequested("SetFullPlotType"); }
        public void Validate(TextWriter log) { Calls.Add("Validate"); FailIfRequested("Validate"); }
        public PaperMetrics GetPaperMetrics(TextWriter log)
        {
            Calls.Add("GetPaperMetrics");
            _metricsCallCount++;
            FailIfRequested("GetPaperMetrics" + _metricsCallCount);
            return _current;
        }
        public void Publish(string outputPdfPath, TextWriter log, TextWriter verbose)
        {
            Calls.Add("Publish");
            FailIfRequested("Publish");
            PublishedPath = outputPdfPath;
        }

        sealed class Disposer : IDisposable
        {
            readonly Action _a;
            public Disposer(Action a) => _a = a;
            public void Dispose() => _a();
        }
    }

    // 印刷可能領域 200×140mm・用紙 210×148 中心 (100,70)・尺度既定 1mm/単位 のペーパー空間
    static PaperMetrics Paper(double scaleMmPerUnit, bool isModel = false) =>
        new(pageWidthMm: 210, pageHeightMm: 148,
            printableWidthMm: 200, printableHeightMm: 140,
            printCenterXMm: 100, printCenterYMm: 70,
            scaleMmPerUnit: scaleMmPerUnit, quarterTurnsCw: 0, isModel: isModel);

    // 縦置きA4（余白付き: 印刷可能 200×263・中心=用紙中心）を回転90でプロットする設定。
    // 寸法は accoreconsole 実測（AutoCAD PDF・ISO A4）と同一。
    static PaperMetrics A4Rot90(double scaleMmPerUnit, bool isModel = false) =>
        new(pageWidthMm: 210, pageHeightMm: 297,
            printableWidthMm: 200, printableHeightMm: 263,
            printCenterXMm: 105, printCenterYMm: 148.5,
            scaleMmPerUnit: scaleMmPerUnit, quarterTurnsCw: 1, isModel: isModel);

    [Fact]
    public void 窓がページ設定尺度で収まればフィットせず明示尺度でクロップを算出する()
    {
        var port = new FakePlotPort(Paper(1.0), Paper(double.NaN));
        var win = new PlotWindow(0, 0, 100, 70); // 100×70 単位 × 1mm = 100×70mm ≦ 200×140mm
        var crop = PlotOrchestrator.Plot(port, win, "out.pdf", TextWriter.Null, TextWriter.Null);

        Assert.False(port.WindowScaleUseFit); // フィットしない（尺度維持）
        Assert.NotNull(crop);
        // 中央 (100,70) 用紙座標 → ページ Y 反転 (100, 148-70=78)、内容 100×70
        var expected = PlotWindowMapper.MapCenteredWindow(100, 70, 100, 70, 210, 148, 0);
        Assert.Equal(expected.X0, crop!.Value.X0, Places);
        Assert.Equal(expected.Y0, crop.Value.Y0, Places);
        Assert.Equal(expected.X1, crop.Value.X1, Places);
        Assert.Equal(expected.Y1, crop.Value.Y1, Places);
    }

    [Fact]
    public void 窓が印刷可能領域を超えるとフィットへ切り替えFit尺度でクロップを算出する()
    {
        var port = new FakePlotPort(Paper(1.0), Paper(double.NaN));
        var win = new PlotWindow(0, 0, 300, 70); // 300×1mm = 300mm > 印刷可能 200mm → フィット
        var crop = PlotOrchestrator.Plot(port, win, "out.pdf", TextWriter.Null, TextWriter.Null);

        Assert.True(port.WindowScaleUseFit);
        double s = PlotWindowMapper.FitScale(300, 70, 200, 140); // = min(200/300, 140/70)=0.6667
        var expected = PlotWindowMapper.MapCenteredWindow(300 * s, 70 * s, 100, 70, 210, 148, 0);
        Assert.Equal(expected.X0, crop!.Value.X0, Places);
        Assert.Equal(expected.Y1, crop.Value.Y1, Places);
    }

    [Fact]
    public void 回転90のフィット尺度は印刷可能領域の縦横を入れ替えて計算する()
    {
        // accoreconsole 実測（2026-07-17、ISO A4 余白付き・回転90・窓 92.028×62.445）:
        // 実効尺度は min(263/92.028, 200/62.445)=2.857825…（回転前の 200×263 で計算する
        // 旧実装の 2.173252 では、実機の印字よりクロップ矩形が小さく窓の周辺が欠落した）
        var port = new FakePlotPort(A4Rot90(1.0, isModel: true), A4Rot90(double.NaN, isModel: true));
        var win = new PlotWindow(50, 40, 142.028, 102.445);
        var crop = PlotOrchestrator.Plot(port, win, "out.pdf", TextWriter.Null, TextWriter.Null);

        double s = 263.0 / 92.028; // 表示フレームの幅基準が効く
        Assert.Equal(s, PlotWindowMapper.FitScale(92.028, 62.445, 263, 200), Places);
        var expected = PlotWindowMapper.MapCenteredWindow(
            92.028 * s, 62.445 * s, 105, 148.5, 210, 297, 1);
        Assert.Equal(expected.X0, crop!.Value.X0, Places);
        Assert.Equal(expected.Y0, crop.Value.Y0, Places);
        Assert.Equal(expected.X1, crop.Value.X1, Places);
        Assert.Equal(expected.Y1, crop.Value.Y1, Places);
        // 内容幅は表示ページ（297×210）上で 263mm（実測の「有効な印刷領域 高さ=263.00」に一致）
        Assert.Equal(263.0, crop.Value.X1 - crop.Value.X0, 2);
    }

    [Fact]
    public void 回転90のペーパー窓の収まり判定は回転後の印刷可能領域と比べる()
    {
        // 窓 250×100×1mm は回転後の印刷可能領域（263×200）に収まる。回転前の 200×263 と
        // 比べる旧実装は幅超過と誤判定してフィットへ切り替えていた
        var fits = new FakePlotPort(A4Rot90(1.0), A4Rot90(double.NaN));
        PlotOrchestrator.Plot(fits, new PlotWindow(0, 0, 250, 100), "out.pdf",
                              TextWriter.Null, TextWriter.Null);
        Assert.False(fits.WindowScaleUseFit);

        // 窓 100×230 は高さ 230 > 200 で収まらない。旧実装は 230 ≤ 263 で収まると誤判定していた
        var overflows = new FakePlotPort(A4Rot90(1.0), A4Rot90(double.NaN));
        PlotOrchestrator.Plot(overflows, new PlotWindow(0, 0, 100, 230), "out.pdf",
                              TextWriter.Null, TextWriter.Null);
        Assert.True(overflows.WindowScaleUseFit);
    }

    [Fact]
    public void 表示フレームの印刷可能領域は奇数回転で縦横が入れ替わる()
    {
        Assert.Equal(263, A4Rot90(1.0).DisplayPrintableWidthMm, Places);
        Assert.Equal(200, A4Rot90(1.0).DisplayPrintableHeightMm, Places);
        Assert.Equal(200, Paper(1.0).DisplayPrintableWidthMm, Places);
        Assert.Equal(140, Paper(1.0).DisplayPrintableHeightMm, Places);
    }

    [Fact]
    public void モデル窓は常にフィットする()
    {
        var port = new FakePlotPort(Paper(1.0, isModel: true), Paper(double.NaN, isModel: true));
        var win = new PlotWindow(0, 0, 10, 10); // 小さくてもモデルはフィット
        PlotOrchestrator.Plot(port, win, "out.pdf", TextWriter.Null, TextWriter.Null);
        Assert.True(port.WindowScaleUseFit);
    }

    [Fact]
    public void 窓プロットの呼び出し順は設定検証発行後始末の順である()
    {
        var port = new FakePlotPort(Paper(1.0), Paper(double.NaN));
        PlotOrchestrator.Plot(port, new PlotWindow(0, 0, 50, 50), "w.pdf", TextWriter.Null, TextWriter.Null);
        Assert.Equal(
            new[] { "BeginSession", "SetWindow", "GetPaperMetrics", "SetWindowScale",
                    "Validate", "GetPaperMetrics", "Publish", "EndSession" },
            port.Calls);
        Assert.Equal("w.pdf", port.PublishedPath);
        Assert.True(port.SessionDisposed);
    }

    [Fact]
    public void 全体プロットは窓設定を行わずクロップを返さない()
    {
        var port = new FakePlotPort(Paper(1.0), Paper(double.NaN));
        var crop = PlotOrchestrator.Plot(port, null, "full.pdf", TextWriter.Null, TextWriter.Null);
        Assert.Null(crop);
        Assert.Equal(
            new[] { "BeginSession", "SetFullPlotType", "Validate", "Publish", "EndSession" },
            port.Calls);
        Assert.Null(port.WindowSet);
        Assert.True(port.SessionDisposed);
    }

    [Theory]
    [InlineData("SetWindow")]
    [InlineData("GetPaperMetrics1")]
    [InlineData("SetWindowScale")]
    [InlineData("Validate")]
    [InlineData("GetPaperMetrics2")]
    [InlineData("Publish")]
    public void 窓プロットの各段階で例外が出てもセッションを必ず後始末する(string failureStage)
    {
        var port = new FakePlotPort(Paper(1.0), Paper(double.NaN)) { ThrowOn = failureStage };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlotOrchestrator.Plot(port, new PlotWindow(0, 0, 50, 50), "w.pdf",
                                  TextWriter.Null, TextWriter.Null));

        Assert.Contains(failureStage, ex.Message);
        Assert.True(port.SessionDisposed);
        Assert.Equal("EndSession", port.Calls[port.Calls.Count - 1]);
    }
}
