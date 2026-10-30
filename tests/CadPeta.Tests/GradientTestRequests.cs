using CadPeta.Acquire.Raster;
using CadPeta.Ir;

namespace CadPeta.Tests;

/// <summary>グラデーションの試験が共通の入力に使う要求(25.4 mm 角を黒から白へ塗る線形グラデーション)。試験は <c>with</c> で必要な項目だけ変える。</summary>
internal static class GradientTestRequests
{
    public static GradientHatchRequest Basic() => new()
    {
        GradientName = "LINEAR",
        Stops = new[] { new GradientStop(0, new IrRgb(0, 0, 0)), new GradientStop(1, new IrRgb(1, 1, 1)) },
        Boundary = new IrSegment[]
        {
            new IrMove(0, 0), new IrLine(25.4, 0), new IrLine(25.4, 25.4), new IrLine(0, 25.4), new IrClose(),
        },
        DocumentFromLocal = IrMatrix.Identity,
        RequestedDpi = 10,
        MinimumDpi = 1,
        MaxDimensionPx = 32767,
        AvailablePixels = 1_000_000,
    };
}
