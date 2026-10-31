using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CadPeta.App;

/// <summary>ページの指定（cadpeta.exe の --pages）を、ページごとの変換の要求にする。</summary>
public static class PdfPetaPages
{
    /// <summary>
    /// "2" や "1-3,5" を、重複の無い昇順のページの一覧にする。解釈できないか、<paramref name="pageCount"/> を超えるページがあれば理由を返す。
    /// ページ数を超える範囲は展開する前に失敗にする（"1-2000000000" で一覧を作り切るまで止まらないように）。
    /// </summary>
    public static (IReadOnlyList<int> Pages, string? Error) ParsePageList(string text, int pageCount)
    {
        var ranges = new List<(int First, int Last)>();
        foreach (string part in text.Split(','))
        {
            string[] bounds = part.Split('-');
            if (bounds.Length > 2 || !TryPage(bounds[0], out int first) || !TryPage(bounds[bounds.Length - 1], out int last) || last < first)
                return (Array.Empty<int>(), $"--pages を解釈できない: {text}（例: 2、1-3,5）");
            if (last > pageCount)
                return (Array.Empty<int>(), $"ページ {last} は無い（PDF は {pageCount} ページ）");
            ranges.Add((first, last));
        }
        var pages = new SortedSet<int>();
        foreach (var (first, last) in ranges)
            for (int page = first; page <= last; page++) pages.Add(page);
        return (pages.ToArray(), null);
    }

    static bool TryPage(string text, out int page) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;

    /// <summary>
    /// <paramref name="pages"/>（<see cref="ParsePageList"/> で検査したページ）ごとの要求を作る。2 ページ以上なら、出力のファイル名に
    /// <see cref="PageSuffix"/> を付ける。2 ページ以上でのクリップボードへの登録は、変換の前に失敗にする（要求は返さない）。
    /// </summary>
    public static (IReadOnlyList<PdfPetaRequest> Requests, IReadOnlyList<string> Errors) ForPages(
        PdfPetaRequest request, IReadOnlyList<int> pages)
    {
        bool multiple = pages.Count > 1;
        if (multiple && request.Outputs.Any(output => output.ToClipboard))
            return (Array.Empty<PdfPetaRequest>(), new[] { "クリップボードに登録できるのは 1 ページだけ。2 ページ以上は <出力.svg> を指定し、--clipboard を付けない" });
        return (pages.Select(page => request with
        {
            PageNumber = page,
            Outputs = multiple ? request.Outputs.Select(output => output with { Path = PagePath(output.Path, page) }).ToArray() : request.Outputs,
        }).ToArray(), Array.Empty<string>());
    }

    /// <summary>ページごとのファイル名の、名前の後ろに付ける部分（-p2）。</summary>
    public static string PageSuffix(int page) => $"-p{page}";
    /// <summary>ファイル名の拡張子の前にページの番号を付ける（図.svg → 図-p2.svg）。</summary>
    public static string PagePath(string path, int page) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + PageSuffix(page) + Path.GetExtension(path));
}
