// 文字アウトライン化の変換段の検証。グリフ輪郭ポートはスタブで置き換え、
// 変換段の責務（1:1 置換・属性引き継ぎ・失敗時の text 維持・処理ログ）だけを検査する。
using CadPeta.Ir;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class TextOutlineTransformTests
{
    /// <summary>固定のセグメント列を返すスタブ（テキスト空間の単位正方形）。</summary>
    sealed class StubOutliner : IGlyphOutliner
    {
        public GlyphRun? LastRun;
        public List<IrSegment>? Result = new()
        {
            new IrMove(0, 0), new IrLine(1, 0), new IrLine(1, -1), new IrClose(),
        };
        public List<IrSegment>? Outline(GlyphRun run)
        {
            LastRun = run;
            return Result;
        }
    }

    static IrText MakeText() => new()
    {
        Value = "AB",
        XMm = 10,
        YMm = 20,
        FontFamily = "Arial",
        FontSizeMm = 5,
        WidthFactor = 1.0,
        Fill = new IrRgb(1, 0, 0),
        FillOpacity = 0.5,
        DataLayer = "注記",
        DataEntity = "TEXT",
        DataColorMethod = "byLayer",
        DataAci = 1,
        ClipId = "clip1",
    };

    [Fact]
    public void textを同位置のpathへ置換し属性を引き継ぐ()
    {
        var ir = new IrDocument();
        ir.Nodes.Add(new IrPath());
        ir.Nodes.Add(MakeText());
        var diag = new IrDiagnostics();

        TextOutlineTransform.Apply(ir, new StubOutliner(), diag);

        Assert.Equal(2, ir.Nodes.Count); // 数と順序を変えない（1:1 置換）
        var p = Assert.IsType<IrPath>(ir.Nodes[1]);
        Assert.Equal(new IrRgb(1, 0, 0), p.Fill);
        Assert.Equal(0.5, p.FillOpacity);
        Assert.False(p.FillEvenOdd); // グリフ輪郭は非ゼロ巻き数で塗る
        Assert.Null(p.Stroke);
        Assert.Equal("注記", p.DataLayer);
        Assert.Equal("TEXT", p.DataEntity);
        Assert.Equal("byLayer", p.DataColorMethod);
        Assert.Equal(1, p.DataAci);
        Assert.Equal("clip1", p.ClipId);
        Assert.Contains(diag.Entries, e => e.Type.StartsWith("文字アウトライン化: text を path へ展開"));
    }

    [Fact]
    public void 配置変換は位置と幅係数と回転を適用する()
    {
        var ir = new IrDocument();
        var t = MakeText();
        t.WidthFactor = 2.0;
        ir.Nodes.Add(t);

        TextOutlineTransform.Apply(ir, new StubOutliner(), diag: new IrDiagnostics());

        var p = (IrPath)ir.Nodes[0];
        // テキスト空間 (1,-1) → 幅係数2で (2,-1) → 平行移動 (10,20) → (12,19)
        var line = Assert.IsType<IrLine>(p.Segments[2]);
        Assert.Equal(12, line.X, 12);
        Assert.Equal(19, line.Y, 12);
    }

    [Fact]
    public void グループの子のtextも置換する()
    {
        var ir = new IrDocument();
        var g = new IrGroup();
        g.Children.Add(MakeText());
        ir.Nodes.Add(g);

        TextOutlineTransform.Apply(ir, new StubOutliner(), new IrDiagnostics());

        Assert.IsType<IrPath>(g.Children[0]);
    }

    [Fact]
    public void 輪郭を取れない場合はtextを維持し理由を記録する()
    {
        var ir = new IrDocument();
        ir.Nodes.Add(MakeText());
        var diag = new IrDiagnostics();

        TextOutlineTransform.Apply(ir, new StubOutliner { Result = null }, diag);

        Assert.IsType<IrText>(ir.Nodes[0]);
        Assert.Contains(diag.Entries, e => e.Type.StartsWith("文字アウトライン化: 輪郭を取れないため"));
    }

    [Fact]
    public void スタブへ渡るランはtextの属性を写している()
    {
        var ir = new IrDocument();
        var t = MakeText();
        t.GlyphXsMm = new[] { 10.0, 13.0 };
        ir.Nodes.Add(t);
        var stub = new StubOutliner();

        TextOutlineTransform.Apply(ir, stub, new IrDiagnostics());

        Assert.NotNull(stub.LastRun);
        Assert.Equal("AB", stub.LastRun!.Text);
        Assert.Equal("Arial", stub.LastRun.FontFamily);
        Assert.False(stub.LastRun.IsShx);
        Assert.Equal(5, stub.LastRun.FontSizeMm);
        Assert.Equal(new[] { 10.0, 13.0 }, stub.LastRun.GlyphXsMm);
    }

    [Fact]
    public void オプション無効ならパイプラインへ段が入らない()
    {
        var ir = new IrDocument();
        ir.Nodes.Add(MakeText());
        // TextOutline=false（既定）: 段が列挙されず text がそのまま残る
        ConvertPipeline.Apply(ir, new ConvertOptions { RemoveBackground = false }, new IrDiagnostics());
        Assert.IsType<IrText>(ir.Nodes[0]);
    }

    [Fact]
    public void オプション有効でポート未注入は結線漏れとして失敗する()
    {
        var ir = new IrDocument();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConvertPipeline.Apply(ir,
                new ConvertOptions { RemoveBackground = false, TextOutline = true },
                new IrDiagnostics()));
        Assert.Contains("TextOutliner", ex.Message);
    }
}
