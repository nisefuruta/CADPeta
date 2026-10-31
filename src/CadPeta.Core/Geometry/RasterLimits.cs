namespace CadPeta.Geometry;

/// <summary>画像（ラスタ）を作るときの限界。グラデーションの画像と PNG の書き出しの両方が使う 1 か所。</summary>
public static class RasterLimits
{
    /// <summary>画像の 1 辺の上限（ピクセル）。GDI+ のビットマップの 1 辺の上限。</summary>
    public const int MaxDimensionPx = 32_767;

    /// <summary>画像にする最小の解像度（DPI）。グラデーションの画像の下限として使ってきた値で、PNG の書き出しの下限にも使う。</summary>
    public const double MinDpi = 24.0;
}
