using System;

namespace CadPeta.Acquire.Raster;

/// <summary>取得1回で共有する決定的な画素予算。成功画像だけを消費する。</summary>
public sealed class GradientRasterBudget
{
    public long RemainingPixels { get; private set; }

    public GradientRasterBudget(long totalPixels)
    {
        if (totalPixels < 0) throw new ArgumentOutOfRangeException(nameof(totalPixels));
        RemainingPixels = totalPixels;
    }

    public bool TryConsume(long pixels)
    {
        if (pixels < 0) throw new ArgumentOutOfRangeException(nameof(pixels));
        if (pixels > RemainingPixels) return false;
        RemainingPixels -= pixels;
        return true;
    }
}
