using System;
using System.Globalization;

using CadPeta.Geometry;

namespace CadPeta.Plugin.Shared;

public readonly struct PaperSizeMm
{
    public PaperSizeMm(double width, double height) { Width = width; Height = height; }
    public double Width { get; }
    public double Height { get; }
}

/// <summary>一致用紙がない場合のOffice貼付向け代替用紙。既定はISO A4縦。</summary>
public static class PaperFallbackPolicy
{
    public static PaperSizeMm Resolve(string? configuredSize)
    {
        if (string.IsNullOrWhiteSpace(configuredSize))
            return new PaperSizeMm(PluginDefaults.DefaultFallbackPaperWidthMm,
                                   PluginDefaults.DefaultFallbackPaperHeightMm);
        string[] parts = configuredSize!.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            || !Numbers.IsPositiveFinite(width) || !Numbers.IsPositiveFinite(height))
            throw new FormatException($"{PluginDefaults.FallbackPaperSizeEnvironmentVariable} は「幅mm,高さmm」の正数で指定する: {configuredSize}");
        return new PaperSizeMm(width, height);
    }

    /// <summary>選択した用紙を構成値の縦横へ揃えるため、90度回転が必要かを返す。</summary>
    public static bool RequiresQuarterTurn(PaperSizeMm target, double mediaWidthMm, double mediaHeightMm)
    {
        if (!Numbers.IsPositiveFinite(mediaWidthMm) || !Numbers.IsPositiveFinite(mediaHeightMm))
            throw new ArgumentOutOfRangeException(nameof(mediaWidthMm), "用紙寸法は正数でなければならない");
        if (target.Width == target.Height || mediaWidthMm == mediaHeightMm) return false;
        bool targetLandscape = target.Width > target.Height;
        bool mediaLandscape = mediaWidthMm > mediaHeightMm;
        return targetLandscape != mediaLandscape;
    }

}
