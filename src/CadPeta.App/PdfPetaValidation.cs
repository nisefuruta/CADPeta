namespace CadPeta.App;

public static class PdfPetaValidation
{
    public static IReadOnlyList<string> ValidateInvariants(this PdfPetaRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.InputPdfPath)) errors.Add("入力 PDF が指定されていない");
        if (request.Outputs.Count == 0) errors.Add("出力が1つも指定されていない");
        string? input = TryFullPath(request.InputPdfPath);
        if (!Finite(request.CropMarginMm) || request.CropMarginMm < 0) errors.Add("クロップ余白は0以上の有限値が必要");
        if (request.CropRectMm is { } crop && (!Finite(crop.X0) || !Finite(crop.Y0) || !Finite(crop.X1) || !Finite(crop.Y1)
            || crop.X1 <= crop.X0 || crop.Y1 <= crop.Y0)) errors.Add("クロップ範囲は有限値かつ正の幅・高さが必要");
        if (!RgbValid(request.EmfBackground)) errors.Add("EMF背景色は0..1の有限値が必要");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in request.Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.Path)) { errors.Add($"{output.Kind} の出力パスが必要"); continue; }
            string? full = TryFullPath(output.Path);
            if (full is null) { errors.Add($"出力パスを解釈できない: {output.Path}"); continue; }
            if (!paths.Add(full)) errors.Add($"出力パスが重複している: {output.Path}");
            if (input is not null && string.Equals(input, full, StringComparison.OrdinalIgnoreCase))
                errors.Add("入力 PDF と同じパスへ上書きできない");
            string expected = ExpectedExtension(output.Kind);
            if (!string.Equals(Path.GetExtension(full), expected, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{output.Kind} の拡張子は {expected} が必要: {output.Path}");
            if (output.Kind == PdfPetaOutputKind.Png)
            {
                if (output.PngDpi is { } dpi && (!Finite(dpi) || dpi <= 0)) errors.Add("PNG DPIは正の有限値が必要");
            }
            else if (output.PngDpi is not null) errors.Add("DPIを指定できるのはPNGだけ");
        }
        foreach (var group in request.Outputs.GroupBy(x => x.Kind))
            if (group.Count() > 1) errors.Add($"{group.Key} 出力は最大1件");
        return errors.Distinct().ToList();
    }

    static string? TryFullPath(string path) { try { return Path.GetFullPath(path); } catch { return null; } }
    static string ExpectedExtension(PdfPetaOutputKind kind) => kind switch
    { PdfPetaOutputKind.Svg => ".svg", PdfPetaOutputKind.Emf or PdfPetaOutputKind.Clipboard => ".emf",
      PdfPetaOutputKind.Pdf => ".pdf", PdfPetaOutputKind.Png => ".png", _ => "" };
    static bool RgbValid(CadPeta.Ir.IrRgb c) => Finite(c.R) && Finite(c.G) && Finite(c.B)
        && c.R is >= 0 and <= 1 && c.G is >= 0 and <= 1 && c.B is >= 0 and <= 1;
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public static class PdfPetaGuiPresenter
{
    public const long LargePdfWarnBytes = 50L * 1024 * 1024;

    public static IReadOnlyList<string> ValidateForGui(PdfPetaRequest request)
    {
        var errors = request.ValidateInvariants().ToList();
        if (!File.Exists(request.InputPdfPath)) errors.Add($"入力 PDF が存在しない: {request.InputPdfPath}");
        if (!string.Equals(Path.GetExtension(request.InputPdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            errors.Add("入力は PDF ファイルを指定する");
        foreach (var output in request.Outputs)
        {
            string? full = TryFullPath(output.Path);
            if (full is null) continue;
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) errors.Add($"保存先フォルダが存在しない: {dir}");
        }
        return errors.Distinct().ToList();
    }

    static string? TryFullPath(string path) { try { return Path.GetFullPath(path); } catch { return null; } }
    static bool RgbValid(CadPeta.Ir.IrRgb c) => Finite(c.R) && Finite(c.G) && Finite(c.B)
        && c.R is >= 0 and <= 1 && c.G is >= 0 and <= 1 && c.B is >= 0 and <= 1;
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
