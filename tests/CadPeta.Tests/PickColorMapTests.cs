// スポイトの色番号の画像の試験。色番号の画像は、図形の色を使用色の一覧の番号を表す色に塗り替えて描き、
// クリックした画素の色から図形の元の色をそのまま求める（アンチエイリアスで混ざった色から近い色を推測しない）。
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public sealed class PickColorMapTests
{
    static readonly IrRgb Red = IrRgb.ParseHex("#FF0000");
    static readonly IrRgb Green = IrRgb.ParseHex("#00FF00");
    static readonly ColorUse[] Inventory = { new(Red, 3), new(Green, 1) };

    [Fact]
    public void 塗り替えた色から使用色の番号を求める()
    {
        var path = new IrPath { Segments = DiagonalSegments(), Stroke = Green, StrokeWidthMm = 0.25 };

        PickColorMap.Apply(Square100Page(path), Inventory);

        var (r, g, b) = path.Stroke!.Value.ToRgb8();
        Assert.Equal(1, PickColorMap.IndexOf((byte)r, (byte)g, (byte)b));
    }

    // 白（背景）と、使用色の一覧に無い黒（色の無い文字の既定の色）は、どの番号にもならない
    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    public void 背景と既定の色は番号にならない(byte r, byte g, byte b)
    {
        Assert.Equal(PickColorMap.None, PickColorMap.IndexOf(r, g, b));
    }

    // 半透明の図形は下の色と混ざって番号が変わるので、不透明にして描く
    [Fact]
    public void 半透明の図形は不透明にする()
    {
        var path = new IrPath { Segments = DiagonalSegments(), Fill = Red, FillOpacity = 0.4 };

        PickColorMap.Apply(Square100Page(path), Inventory);

        Assert.Equal(1.0, path.FillOpacity);
    }

    // 画像は色置換の対象外なので、色番号の画像には描かない（画素の色が番号と重なって別の色を選ばないように）。
    // ノードの数はレイヤ別のグループ化の境界に使うので、画像は空のグループに置き換えて数を変えない
    [Fact]
    public void 画像は空のグループに置き換える()
    {
        var group = new IrGroup { Children = { new IrImage() } };
        var ir = Square100Page(new IrImage { WidthMm = 10, HeightMm = 10 }, group);

        PickColorMap.Apply(ir, Inventory);

        Assert.Equal((0, 2), (((IrGroup)ir.Nodes[0]).Children.Count, ir.Nodes.Count));
        Assert.IsType<IrGroup>(Assert.Single(group.Children));
    }
    const int N = PickColorMap.None;

    static int[] OneDot() => new[]
    {
        N, N, N, N, N,
        N, N, N, N, N,
        N, N, N, N, 1,
        N, N, N, N, N,
        N, N, N, N, N,
    };
    [Theory]
    [InlineData(4, 2, 0, 1)]
    [InlineData(2, 2, 2, 1)]
    [InlineData(2, 2, 1, PickColorMap.None)]
    public void クリックした画素に図形が無ければ近くの図形を探す(int x, int y, int radius, int expected)
    {
        Assert.Equal(expected, PickColorMap.FindNearest(OneDot(), 5, 5, x, y, radius));
    }
}
