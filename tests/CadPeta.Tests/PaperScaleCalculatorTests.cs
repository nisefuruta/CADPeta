using System;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public sealed class PaperScaleCalculatorTests
{
    [Theory]
    [InlineData(PaperUnitKind.Millimeters, PaperScaleKind.Standard, 0.5, 1, 1, 0.5)]
    [InlineData(PaperUnitKind.Inches, PaperScaleKind.Standard, 0.5, 1, 1, 12.7)]
    [InlineData(PaperUnitKind.Millimeters, PaperScaleKind.Custom, 1, 1, 50, 0.02)]
    [InlineData(PaperUnitKind.Inches, PaperScaleKind.Custom, 1, 1, 50, 0.508)]
    public void 単位と標準_custom尺度をmm毎図面単位へ変換する(
        PaperUnitKind unit, PaperScaleKind kind, double standard, double numerator,
        double denominator, double expected)
    {
        Assert.Equal(expected, PaperScaleCalculator.MmPerDrawingUnit(
            unit, kind, standard, numerator, denominator), 12);
    }

    [Theory]
    [InlineData(PaperUnitKind.Millimeters, PaperScaleKind.Fit)]
    [InlineData(PaperUnitKind.Inches, PaperScaleKind.Fit)]
    [InlineData(PaperUnitKind.Pixels, PaperScaleKind.Standard)]
    [InlineData(PaperUnitKind.Pixels, PaperScaleKind.Custom)]
    public void FitとPixelsは明示尺度を持たない(PaperUnitKind unit, PaperScaleKind kind)
    {
        Assert.True(double.IsNaN(PaperScaleCalculator.MmPerDrawingUnit(unit, kind, 1, 1, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void custom尺度の非正分母を拒否する(double denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PaperScaleCalculator.MmPerDrawingUnit(
            PaperUnitKind.Millimeters, PaperScaleKind.Custom, 1, 1, denominator));
    }
}
