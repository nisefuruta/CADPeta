using System;
using CadPeta.Geometry;

namespace CadPeta.Plugin.Shared;

public enum PaperUnitKind { Millimeters, Inches, Pixels }
public enum PaperScaleKind { Fit, Standard, Custom }

/// <summary>CAD SDKに依存しないページ尺度計算。4製品のPlotPortで同じ規則を使う。</summary>
public static class PaperScaleCalculator
{
    /// <summary>用紙mm/図面単位を返す。FitとPixelsは明示尺度を定義できないためNaN。</summary>
    public static double MmPerDrawingUnit(PaperUnitKind unit, PaperScaleKind scale,
                                          double standardScale, double customNumerator,
                                          double customDenominator)
    {
        if (scale == PaperScaleKind.Fit || unit == PaperUnitKind.Pixels) return double.NaN;
        double unitToMm = unit == PaperUnitKind.Inches ? Units.MmPerInch : 1.0;
        double value = scale == PaperScaleKind.Standard
            ? standardScale
            : customNumerator / customDenominator;
        if (!Numbers.IsPositiveFinite(value))
            throw new ArgumentOutOfRangeException(
                scale == PaperScaleKind.Standard ? nameof(standardScale) : nameof(customDenominator),
                "ページ尺度は有限の正数でなければならない");
        return value * unitToMm;
    }
}
