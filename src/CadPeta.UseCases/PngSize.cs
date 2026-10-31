using System;
using CadPeta.Geometry;

namespace CadPeta.UseCases;

/// <summary>
/// PNG の画像の大きさの上限と、上限に収まる解像度。画像の 1 辺は <see cref="RasterLimits.MaxDimensionPx"/>、画素数は
/// <see cref="MaxPixels"/> まで。超えるときは解像度を下げて書き、下げたことを診断（<see cref="DpiReducedDiag"/>）に残す。
/// </summary>
public static class PngSize
{
    /// <summary>
    /// 画素数の上限（2^28 = 約 2.7 億画素、32 ビットの画像で 1 GiB）。GDI+ は 4 バイト×画素数が 2^31 を超える画像を確保できない
    /// （23,170 × 23,170 は確保でき、A0 を 600 DPI にした 28,087 × 19,866 は ArgumentException。2026-10-28）。
    /// CAD のプロセスの中で確保するので、その半分にする。A0 は 300 DPI（約 1.4 億画素）まで下げずに書ける。
    /// </summary>
    public const long MaxPixels = 1L << 28;

    /// <summary>解像度を下げたときの診断の種類。値は書いた解像度（DPI）。</summary>
    public const string DpiReducedDiag = "PNG出力: 画像の大きさの上限のため解像度を下げた";

    /// <summary>長さ <paramref name="mm"/> を解像度 <paramref name="dpi"/> で書いたときの画素数（1 以上）。</summary>
    public static int Pixels(double mm, double dpi) => Math.Max(1, (int)Math.Round(mm * dpi / Units.MmPerInch));

    /// <summary>
    /// 幅と高さ（mm）の画像を書く解像度。<paramref name="dpi"/> で上限に収まればそのまま、収まらなければ収まる整数の解像度まで下げる。
    /// </summary>
    public static double DpiFor(double widthMm, double heightMm, double dpi)
    {
        if (Fits(widthMm, heightMm, dpi)) return dpi;
        double side = RasterLimits.MaxDimensionPx * Units.MmPerInch / Math.Max(widthMm, heightMm);
        double area = Math.Sqrt(MaxPixels) * Units.MmPerInch / Math.Sqrt(widthMm * heightMm);
        double reduced = Math.Floor(Math.Min(dpi, Math.Min(side, area)));
        // 画素数の丸めで上限を超えたときは 1 DPI ずつ下げる
        while (reduced > 1 && !Fits(widthMm, heightMm, reduced))
            reduced--;
        return reduced;
    }

    static bool Fits(double widthMm, double heightMm, double dpi)
    {
        int w = Pixels(widthMm, dpi), h = Pixels(heightMm, dpi);
        return w <= RasterLimits.MaxDimensionPx && h <= RasterLimits.MaxDimensionPx && (long)w * h <= MaxPixels;
    }
}
