// 背景除去（三条件による同定）の単体テスト。
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;

namespace CadPeta.Tests;

public class BackgroundRemoverTests
{
    static IrPath Rect(double x0, double y0, double x1, double y1, IrRgb? fill, IrRgb? stroke = null)
        => new()
        {
            Segments = new List<IrSegment>
            {
                new IrMove(x0, y0), new IrLine(x1, y0), new IrLine(x1, y1),
                new IrLine(x0, y1), new IrClose(),
            },
            Fill = fill,
            Stroke = stroke,
            StrokeWidthMm = stroke != null ? 0.5 : 0,
        };

    static IrDocument Page(params IrNode[] nodes)
    {
        var ir = new IrDocument { WidthMm = 420, HeightMm = 297 };
        ir.Nodes.AddRange(nodes);
        return ir;
    }

    [Fact]
    public void 三条件を満たす先頭の塗り矩形を除去する()
    {
        var bg = Rect(0, 0, 420, 297, new IrRgb(1, 1, 1));
        var content = Rect(10, 10, 100, 100, null, new IrRgb(0, 0, 0));
        var ir = Page(bg, content);
        var diag = new IrDiagnostics();

        int removed = BackgroundRemover.Apply(ir, diag);

        Assert.Equal(1, removed);
        Assert.Single(ir.Nodes);
        Assert.Same(content, ir.Nodes[0]);
        Assert.Single(diag.Entries);
    }

    [Fact]
    public void ページ範囲と一致しない塗り矩形は除去しない()
    {
        var ir = Page(Rect(0, 0, 200, 150, new IrRgb(1, 1, 1)));
        Assert.Equal(0, BackgroundRemover.Apply(ir, new IrDiagnostics()));
        Assert.Single(ir.Nodes);
    }

    [Fact]
    public void ストローク付きの矩形は除去しない()
    {
        var ir = Page(Rect(0, 0, 420, 297, new IrRgb(1, 1, 1), new IrRgb(0, 0, 0)));
        Assert.Equal(0, BackgroundRemover.Apply(ir, new IrDiagnostics()));
        Assert.Single(ir.Nodes);
    }

    [Fact]
    public void 描画順が先頭でない塗り矩形は除去しない()
    {
        var content = Rect(10, 10, 100, 100, null, new IrRgb(0, 0, 0));
        var bg = Rect(0, 0, 420, 297, new IrRgb(1, 1, 1));
        var ir = Page(content, bg);  // 背景相当が2番目（ワイプアウトの白塗りは消してはならない）
        Assert.Equal(0, BackgroundRemover.Apply(ir, new IrDiagnostics()));
        Assert.Equal(2, ir.Nodes.Count);
    }
}
