using CadPeta.Ir;

namespace CadPeta.Acquire.Raster;

public sealed record GradientStop(double Position, IrRgb Color);

public sealed record GradientHatchRequest
{
    public string GradientName { get; init; } = "";
    public double AngleRadians { get; init; }
    public double Shift { get; init; }
    public IReadOnlyList<GradientStop> Stops { get; init; } = Array.Empty<GradientStop>();
    public IReadOnlyList<IrSegment> Boundary { get; init; } = Array.Empty<IrSegment>();
    public IrMatrix DocumentFromLocal { get; init; } = IrMatrix.Identity;
    public double Opacity { get; init; } = 1;
    public double RequestedDpi { get; init; }
    public double MinimumDpi { get; init; }
    public int MaxDimensionPx { get; init; }
    public long AvailablePixels { get; init; }
}

public enum GradientHatchStatus
{
    Rasterized,
    SolidFallback,
    SkippedInvalid,
}

public enum GradientFallbackKind
{
    None,
    UnknownShape,
    PixelBudget,
    InvalidDefinition,
}

public sealed record GradientHatchResult
{
    public GradientHatchStatus Status { get; init; }
    public byte[] PngBytes { get; init; } = Array.Empty<byte>();
    public double XMm { get; init; }
    public double YMm { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public double ActualDpi { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public long PixelCount { get; init; }
    public IrRgb SolidColor { get; init; }
    public GradientFallbackKind FallbackKind { get; init; }
    public string? FallbackReason { get; init; }
    public string? DpiReductionReason { get; init; }
}
