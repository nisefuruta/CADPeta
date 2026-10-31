using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CadPeta.App;
using CadPeta.Geometry;
using CadPeta.UseCases;

namespace CadPeta.Gui;

/// <summary>画面の入力欄の文字を、変換の要求の値にする。</summary>
internal static class GuiInputs
{
    /// <summary>切り出しの範囲と余白（mm）。<paramref name="on"/> が false なら欄を読まない。数でない欄や負の余白があれば理由を返す。</summary>
    public static (RectMm? Rect, double Margin, string? Error) ParseCrop(bool on, string x0, string y0, string x1, string y1, string margin)
    {
        if (!on) return (null, 0, null);
        var values = new[] { x0, y0, x1, y1, margin }.Select(text => (Ok: TryNumber(text, out double v), Value: v)).ToArray();
        if (values.Any(v => !v.Ok)) return (null, 0, "切り出しの範囲と余白は数で指定してください。");
        if (values[4].Value < 0) return (null, 0, "切り出しの余白は 0 以上で指定してください。");
        return (new RectMm(values[0].Value, values[1].Value, values[2].Value, values[3].Value), values[4].Value, null);
    }

    /// <summary>保存する PNG の解像度。PNG を保存しないときは欄を読まずに null を返す（ほかの形式は解像度を使わない）。</summary>
    public static (double? Dpi, string? Error) PngDpiFor(IReadOnlyList<ArtifactKind> kinds, string dpiText)
    {
        if (!kinds.Contains(ArtifactKind.Png)) return (null, null);
        return CadPetaSettings.ParsePngDpi(dpiText, out double dpi) is { } error ? (null, error) : (dpi, null);
    }

    /// <summary>ページの欄の文字をページの番号（1 からページ数まで）にする。整数でなければ理由を返す。</summary>
    public static (int Page, string? Error) ParsePage(string text, int pageCount) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int page)
            ? (ClampPage(page, pageCount), null) : (0, $"ページは数で指定してください: {text.Trim()}");

    /// <summary>ページの番号を 1 からページ数までにする（ページ数が分からない 0 のときは 1）。</summary>
    public static int ClampPage(int requested, int pageCount) => Math.Max(1, Math.Min(requested, Math.Max(1, pageCount)));

    /// <summary>
    /// 保存する形式ごとの出力。形式が 1 つなら <paramref name="chosenPath"/> はファイル名の選択の画面で選んだファイル、
    /// 2 つ以上なら基本のファイル名で、形式ごとの拡張子を付ける。PNG だけに解像度を付ける。
    /// </summary>
    public static IReadOnlyList<PdfPetaOutput> SaveTargets(IReadOnlyList<ArtifactKind> kinds, string chosenPath, double? pngDpi) =>
        kinds.Select(kind => new PdfPetaOutput(kind, kinds.Count == 1 ? chosenPath : chosenPath + ArtifactKinds.Extension(kind),
            kind == ArtifactKind.Png ? pngDpi : null)).ToArray();

    /// <summary>保存のファイル名の既定。2 ページ以上の PDF ならページの番号を付ける（cadpeta.exe の --pages と同じ -p番号）。</summary>
    public static string DefaultFileName(string inputPdf, int page, int pageCount)
    {
        string name = Path.GetFileNameWithoutExtension(inputPdf);
        return pageCount > 1 ? name + PdfPetaPages.PageSuffix(page) : name;
    }

    static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Numbers.IsFinite(value);
}
