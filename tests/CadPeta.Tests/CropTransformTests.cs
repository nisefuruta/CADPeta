// クロップ変換の単体テスト。
// 範囲外ノードの間引きと余白・viewBox 更新を検査する。
using System.Collections.Generic;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public class CropTransformTests
{
    [Fact]
    public void 範囲外ノードを間引き範囲内と境界交差を保持する()
    {
        var inside = Line(20, 20, 80, 80);
        var crossing = Line(90, 50, 150, 50);   // 境界 x=100 をまたぐ → 全体保持
        var outside = Line(200, 200, 300, 250);
        var ir = A3Page(inside, crossing, outside);
        var diag = new IrDiagnostics();

        int dropped = CropTransform.Apply(ir, new RectMm(0, 0, 100, 100), 0, diag);

        Assert.Equal(1, dropped);
        Assert.Equal(new IrNode[] { inside, crossing }, ir.Nodes);
        Assert.Single(diag.Entries);
    }

    [Fact]
    public void ストローク幅の半分だけ外側まで可視とみなす()
    {
        // 幾何は範囲外（x=101）だが太線の半幅 2mm が範囲に食い込む → 保持
        var thick = Line(101, 10, 101, 90, widthMm: 4);
        // 細線は同じ位置でも範囲外 → 間引き
        var thin = Line(101, 10, 101, 90, widthMm: 0.5);
        var ir = A3Page(thick, thin);

        CropTransform.Apply(ir, new RectMm(0, 0, 100, 100), 0, new IrDiagnostics());

        Assert.Equal(new IrNode[] { thick }, ir.Nodes);
    }

    [Fact]
    public void 文字は保守的な外接矩形で判定する()
    {
        var inside = new IrText { Value = "A", XMm = 50, YMm = 50, FontSizeMm = 5 };
        var outside = new IrText { Value = "B", XMm = 200, YMm = 200, FontSizeMm = 5 };
        var ir = A3Page(inside, outside);

        CropTransform.Apply(ir, new RectMm(0, 0, 100, 100), 0, new IrDiagnostics());

        Assert.Equal(new IrNode[] { inside }, ir.Nodes);
    }

    [Fact]
    public void 余白込みでviewBoxと物理寸法を更新する()
    {
        var ir = A3Page(Line(20, 25, 50, 45));

        CropTransform.Apply(ir, new RectMm(10, 20, 60, 50), 5, new IrDiagnostics());

        Assert.Equal(5, ir.ViewMinXMm);
        Assert.Equal(15, ir.ViewMinYMm);
        Assert.Equal(60, ir.WidthMm);   // (60-10) + 5*2
        Assert.Equal(40, ir.HeightMm);  // (50-20) + 5*2
    }

    [Fact]
    public void クリップ範囲が切り出し範囲外のノードは不可視として間引く()
    {
        var clip = new IrClipDef
        {
            Id = "clip0",
            Segments = new List<IrSegment>
            {
                new IrMove(200, 200), new IrLine(300, 200), new IrLine(300, 300),
                new IrLine(200, 300), new IrClose(),
            },
        };
        // ノード幾何は切り出し範囲と重なるが、クリップ窓が範囲外 → 不可視
        var clipped = Line(10, 10, 290, 290);
        clipped.ClipId = "clip0";
        var ir = A3Page(clipped);
        ir.ClipDefs.Add(clip);

        int dropped = CropTransform.Apply(ir, new RectMm(0, 0, 100, 100), 0, new IrDiagnostics());

        Assert.Equal(1, dropped);
        Assert.Empty(ir.Nodes);
        Assert.Empty(ir.ClipDefs);  // 参照されなくなった定義も除去
    }

    [Fact]
    public void 保持ノードが参照するクリップ定義は残す()
    {
        var clip = new IrClipDef
        {
            Id = "clip0",
            Segments = new List<IrSegment>
            {
                new IrMove(0, 0), new IrLine(100, 0), new IrLine(100, 100),
                new IrLine(0, 100), new IrClose(),
            },
        };
        var node = Line(10, 10, 90, 90);
        node.ClipId = "clip0";
        var ir = A3Page(node);
        ir.ClipDefs.Add(clip);

        CropTransform.Apply(ir, new RectMm(0, 0, 100, 100), 0, new IrDiagnostics());

        Assert.Single(ir.Nodes);
        Assert.Single(ir.ClipDefs);
    }
}
