using System;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public sealed class PaperFallbackPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 未設定時はISO_A4縦(string? value)
    {
        var size = PaperFallbackPolicy.Resolve(value);
        Assert.Equal(210, size.Width);
        Assert.Equal(297, size.Height);
    }

    [Fact]
    public void 構成値で公称寸法を上書きできる()
    {
        var size = PaperFallbackPolicy.Resolve("297, 420");
        Assert.Equal(297, size.Width); Assert.Equal(420, size.Height);
    }

    [Theory]
    [InlineData("A4")]
    [InlineData("210")]
    [InlineData("0,297")]
    [InlineData("NaN,297")]
    public void 不正な構成値を黙って無視しない(string value) =>
        Assert.Throws<FormatException>(() => PaperFallbackPolicy.Resolve(value));

    [Theory]
    [InlineData(210, 297, 210, 297, false)]
    [InlineData(210, 297, 297, 210, true)]
    [InlineData(420, 297, 297, 420, true)]
    [InlineData(210, 210, 297, 210, false)]
    public void 構成した縦横へ揃える回転を判定する(
        double targetW, double targetH, double mediaW, double mediaH, bool expected) =>
        Assert.Equal(expected, PaperFallbackPolicy.RequiresQuarterTurn(
            new PaperSizeMm(targetW, targetH), mediaW, mediaH));
}
