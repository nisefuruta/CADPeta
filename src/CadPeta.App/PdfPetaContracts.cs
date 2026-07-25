using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;

namespace CadPeta.App;

public enum PdfPetaOutputKind { Clipboard, Svg, Emf, Pdf, Png }

public static class PdfPetaDefaults
{
    public const double PngDpi = CadPeta.Out.Png.PngWriter.DefaultDpi;
}

public sealed record PdfPetaOutput(PdfPetaOutputKind Kind, string Path, double? PngDpi = null);

public sealed record PdfPetaRequest
{
    public required string InputPdfPath { get; init; }
    public bool KeepBackground { get; init; }
    public RectMm? CropRectMm { get; init; }
    public double CropMarginMm { get; init; }
    public ColorMode ColorMode { get; init; }
    public IReadOnlyList<ColorReplacement> ColorReplacements { get; init; } = Array.Empty<ColorReplacement>();
    public bool TextOutline { get; init; }
    public bool GroupByLayer { get; init; }
    public IrRgb EmfBackground { get; init; } = new(1, 1, 1);
    public required IReadOnlyList<PdfPetaOutput> Outputs { get; init; }
}

public sealed record PdfPetaDiagnostic(string Type, int Count, IReadOnlyList<string> Targets);

public sealed record PdfPetaResult(IReadOnlyList<PdfPetaDiagnostic> Diagnostics,
    string? ClipboardEmfPath, string? ClipboardError);

public sealed record PdfPetaRunOptions
{
    public Action<int, string, IrDocument>? DiagnosticStageSink { get; init; }
    public Action<string, TextWriter>? ClipboardSetter { get; init; }
}
