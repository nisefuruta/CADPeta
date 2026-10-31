using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;
using CadPeta.UseCases;

namespace CadPeta.App;

/// <summary>
/// 1 つの出力。<paramref name="PngDpi"/> は PNG だけに指定でき、省略すると設定の既定値（<see cref="CadPetaSettings.DefaultPngDpi"/>）。
/// <paramref name="ToClipboard"/> は EMF だけに指定でき、書いたファイルをクリップボードにも登録する。
/// </summary>
public sealed record PdfPetaOutput(ArtifactKind Format, string Path, double? PngDpi = null, bool ToClipboard = false);

public sealed record PdfPetaRequest
{
    public required string InputPdfPath { get; init; }
    /// <summary>変換するページ（1 始まり）。</summary>
    public int PageNumber { get; init; } = 1;
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
