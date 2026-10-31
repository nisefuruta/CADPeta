using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadPeta.Geometry;
using CadPeta.Transforms;
using CadPeta.UseCases;

namespace CadPeta.App;

public static class PdfPetaValidation
{
    public static IReadOnlyList<string> ValidateInvariants(this PdfPetaRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.InputPdfPath)) errors.Add("入力 PDF が指定されていない");
        if (request.Outputs.Count == 0) errors.Add("出力が1つも指定されていない");
        string? input = TryFullPath(request.InputPdfPath);
        if (request.PageNumber < 1) errors.Add($"ページの番号は1以上が必要: {request.PageNumber}");
        foreach (var replacement in request.ColorReplacements)
            if (replacement.Tolerance is < 0 or > ColorReplacement.MaxTolerance)
                errors.Add($"色置換の許容差は0～{ColorReplacement.MaxTolerance}が必要: {replacement.Tolerance}");
        if (!Numbers.IsFinite(request.CropMarginMm) || request.CropMarginMm < 0) errors.Add("クロップ余白は0以上の有限値が必要");
        if (request.CropRectMm is { } crop && (!Numbers.IsFinite(crop.X0) || !Numbers.IsFinite(crop.Y0) || !Numbers.IsFinite(crop.X1) || !Numbers.IsFinite(crop.Y1)
            || crop.X1 <= crop.X0 || crop.Y1 <= crop.Y0)) errors.Add("クロップ範囲は有限値かつ正の幅・高さが必要");
        if (!RgbValid(request.EmfBackground)) errors.Add("EMF背景色は0..1の有限値が必要");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in request.Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.Path)) { errors.Add($"{Label(output)} の出力パスが必要"); continue; }
            string? full = TryFullPath(output.Path);
            if (full is null) { errors.Add($"出力パスを解釈できない: {output.Path}"); continue; }
            if (!paths.Add(full)) errors.Add($"出力パスが重複している: {output.Path}");
            if (input is not null && string.Equals(input, full, StringComparison.OrdinalIgnoreCase))
                errors.Add("入力 PDF と同じパスへ上書きできない");
            string expected = ArtifactKinds.Extension(output.Format);
            if (!string.Equals(Path.GetExtension(full), expected, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{Label(output)} の拡張子は {expected} が必要: {output.Path}");
            if (output.Format == ArtifactKind.Png)
            {
                if (output.PngDpi is { } dpi && CadPetaSettings.ValidatePngDpi(dpi) is { } dpiError) errors.Add(dpiError);
            }
            else if (output.PngDpi is not null) errors.Add("DPIを指定できるのはPNGだけ");
            if (output.ToClipboard && output.Format != ArtifactKind.Emf) errors.Add("クリップボードに登録できるのはEMFだけ");
        }
        foreach (var group in request.Outputs.GroupBy(Label))
            if (group.Count() > 1) errors.Add($"{group.Key} 出力は最大1件");
        return errors.Distinct().ToList();
    }

    internal static string? TryFullPath(string path) { try { return Path.GetFullPath(path); } catch { return null; } }
    // 利用者に見せる出力の名前（クリップボードに登録する EMF は、ファイルだけの EMF と別の出力として数える）
    static string Label(PdfPetaOutput output) => output.ToClipboard ? "Clipboard" : output.Format.ToString();
    static bool RgbValid(CadPeta.Ir.IrRgb c) => Numbers.IsFinite(c.R) && Numbers.IsFinite(c.G) && Numbers.IsFinite(c.B)
        && c.R is >= 0 and <= 1 && c.G is >= 0 and <= 1 && c.B is >= 0 and <= 1;
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
            string? full = PdfPetaValidation.TryFullPath(output.Path);
            if (full is null) continue;
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) errors.Add($"保存先フォルダが存在しない: {dir}");
        }
        return errors.Distinct().ToList();
    }

}
