// 色の一括置換の許容差の試験。許容差は「R・G・B のそれぞれの差（8 ビット）がこの値以内」で、0 は同じ色だけ。
using System.Linq;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public sealed class ColorToleranceTests
{
    static readonly IrRgb Red = IrRgb.ParseHex("#FF0000");
    static readonly IrRgb Black = new(0, 0, 0);

    static IrPath Stroke(string hex) => new() { Segments = DiagonalSegments(), Stroke = IrRgb.ParseHex(hex), StrokeWidthMm = 0.25 };

    // #FF0000 との各成分の差の最大: #FE0101 = 1、#F80000 = 7、#E00000 = 31
    [Theory]
    [InlineData(0, "#FF0000", true)]
    [InlineData(0, "#FE0101", false)]
    [InlineData(1, "#FE0101", true)]
    [InlineData(7, "#F80000", true)]
    [InlineData(7, "#E00000", false)]
    [InlineData(31, "#E00000", true)]
    public void 許容差の範囲の色だけを置き換える(int tolerance, string hex, bool replaced)
    {
        var path = Stroke(hex);

        ColorTransform.Apply(Square100Page(path), ColorMode.None, new[] { new ColorReplacement(Red, Black, tolerance) }, new IrDiagnostics());

        Assert.Equal(replaced ? Black : IrRgb.ParseHex(hex), path.Stroke);
    }

    [Fact]
    public void 許容差で置き換えた色は元の色ごとに診断に記録する()
    {
        var diag = new IrDiagnostics();

        ColorTransform.Apply(Square100Page(Stroke("#FF0000"), Stroke("#FE0101"), Stroke("#FE0101")), ColorMode.None,
            new[] { new ColorReplacement(Red, Black, 1) }, diag);

        Assert.Equal(new[] { "path stroke rgb(255,0,0)→rgb(0,0,0) 1件", "path stroke rgb(254,1,1)→rgb(0,0,0) 2件" },
            diag.Entries.Where(e => e.Type == "色変換（色の一括置換）").Select(e => e.Target));
    }

    [Fact]
    public void 許容差の範囲が重なれば上の行を使う()
    {
        var path = Stroke("#FE0101");

        ColorTransform.Apply(Square100Page(path), ColorMode.None, new[]
        {
            new ColorReplacement(Red, Black, 7),
            new ColorReplacement(IrRgb.ParseHex("#FE0101"), IrRgb.ParseHex("#00FF00")),
        }, new IrDiagnostics());

        Assert.Equal(Black, path.Stroke);
    }

    // 行ごとの一致は、置き換えと同じ規則（上の行を優先）で、使用色の一覧から色の数と件数を数える
    [Fact]
    public void 行ごとに一致する色の数と件数を数える()
    {
        var inventory = new[]
        {
            new ColorUse(Red, 10), new ColorUse(IrRgb.ParseHex("#FE0101"), 5), new ColorUse(IrRgb.ParseHex("#E00000"), 2),
        };

        var matches = ColorTransform.SummarizeMatches(inventory, new[]
        {
            new ColorReplacement(Red, Black, 1),
            new ColorReplacement(IrRgb.ParseHex("#E00000"), Black),
            new ColorReplacement(Red, Black),
        });

        Assert.Equal(new[] { new ReplacementMatch(2, 15), new ReplacementMatch(1, 2), new ReplacementMatch(0, 0) }, matches);
    }

    // 許容差の文字の解釈（cadpeta.exe の --tolerance と GUI の欄が共有する）
    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 12 ", 12)]
    [InlineData("255", 255)]
    public void 許容差の文字を数にする(string text, int expected)
    {
        Assert.Equal(((string?)null, expected), (ColorReplacement.ParseTolerance(text, out int tolerance), tolerance));
    }

    [Theory]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void 許容差が0から255の整数でなければ理由を返す(string text)
    {
        Assert.Contains(text.Trim(), ColorReplacement.ParseTolerance(text, out _));
    }
}
