using System.Collections.Generic;
using CadPeta.Ir;

namespace CadPeta.Tests;

/// <summary>複数の試験が同じ形で使う IR の入力を組み立てる補助。試験ごとに同じ組み立てを書き写さない。</summary>
internal static class IrTestBuilders
{
    /// <summary>黒の細線 1 本(始点から終点への直線、線幅 <paramref name="widthMm"/>)。</summary>
    public static IrPath Line(double x0, double y0, double x1, double y1, double widthMm = 0.5) => new()
    {
        Segments = new List<IrSegment> { new IrMove(x0, y0), new IrLine(x1, y1) },
        Stroke = new IrRgb(0, 0, 0),
        StrokeWidthMm = widthMm,
    };

    /// <summary>(0,0) から (10,10) への斜めの 2 区間。線幅や色は試験が決める。</summary>
    public static List<IrSegment> DiagonalSegments() => new() { new IrMove(0, 0), new IrLine(10, 10) };

    /// <summary>A3 横(420 x 297 mm)の図面へ <paramref name="nodes"/> を順に入れたもの。</summary>
    public static IrDocument A3Page(params IrNode[] nodes) => Page(420, 297, nodes);

    /// <summary>100 x 100 mm の図面へ <paramref name="nodes"/> を順に入れたもの。</summary>
    public static IrDocument Square100Page(params IrNode[] nodes) => Page(100, 100, nodes);

    private static IrDocument Page(double widthMm, double heightMm, IrNode[] nodes)
    {
        var ir = new IrDocument { WidthMm = widthMm, HeightMm = heightMm };
        ir.Nodes.AddRange(nodes);
        return ir;
    }
}
