using System;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;

namespace CadPeta.Gui.Tests;

public sealed class ColorReplacementRowsTests
{
    [Fact]
    public void 色変換を使わない要求は書きかけの置換の行で止まらない()
    {
        var replacements = ColorReplacementRows.Parse(new (string?, string?, string?)[] { ("#FF00", "#000000", "0") }, applyColor: false);

        Assert.Empty(replacements);
    }

    [Fact]
    public void 色変換を使う要求は書きかけの置換の行で止まる()
    {
        Assert.Throws<ArgumentException>(() =>
            ColorReplacementRows.Parse(new (string?, string?, string?)[] { ("#FF00", "#000000", "0") }, applyColor: true));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("", 0)]
    [InlineData(" 12 ", 12)]
    public void 色変換を使う要求は置換の行を許容差つきの色置換にする(string tolerance, int expected)
    {
        var replacements = ColorReplacementRows.Parse(new (string?, string?, string?)[] { ("#FF0000", "#0000FF", tolerance) }, applyColor: true);

        Assert.Equal(new[] { new ColorReplacement(new IrRgb(1, 0, 0), new IrRgb(0, 0, 1), expected) }, replacements);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("256")]
    public void 許容差が0から255の整数でなければ止まる(string tolerance)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ColorReplacementRows.Parse(new (string?, string?, string?)[] { ("#FF0000", "#0000FF", tolerance) }, applyColor: true));

        Assert.Contains("許容差", error.Message);
    }
}
