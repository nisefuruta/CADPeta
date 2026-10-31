using CadPeta.Geometry;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PNG の解像度は、画像の 1 辺と画素数が上限に収まる値まで下げる（GDI+ が画像を確保できる大きさにする）
public sealed class PngSizeTests
{
    [Fact]
    public void 上限に収まれば設定の解像度のまま()
    {
        // A0 を既定の 300 DPI（14,043 × 9,933 画素）
        Assert.Equal(300, PngSize.DpiFor(1189, 841, 300));
    }

    [Fact]
    public void A0を最大の解像度にすると画素数が上限に収まるまで下げる()
    {
        double dpi = PngSize.DpiFor(1189, 841, CadPetaSettings.MaxPngDpi);

        Assert.True(dpi < CadPetaSettings.MaxPngDpi);
        Assert.True(PngSize.Pixels(1189, dpi) * PngSize.Pixels(841, dpi) <= PngSize.MaxPixels);
    }

    [Fact]
    public void 長い用紙は1辺が上限に収まるまで下げる()
    {
        double dpi = PngSize.DpiFor(2000, 10, CadPetaSettings.MaxPngDpi);

        Assert.True(dpi < CadPetaSettings.MaxPngDpi);
        Assert.True(PngSize.Pixels(2000, dpi) <= RasterLimits.MaxDimensionPx);
    }
}
