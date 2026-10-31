using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CadPeta.UseCases;

public static class RunSummary
{
    /// <summary>
    /// PETA の結果の要約。1 行目は EMF の大きさ・図形の数・クリップボードの結果、2 行目は出力したファイル（名前とフォルダ）か、
    /// 出力しなかったときの PETAOPT の案内。既定と違うオプションがあれば 3 行目に並べる。
    /// </summary>
    public static string Build(MessageCatalog msgs,
                               IReadOnlyList<string> published, long emfBytes, int nodeCount, bool clipboardOk,
                               bool groupByLayer, bool shxSubstitute, bool textOutline,
                               double marginMm)
    {
        string result = msgs.Format(clipboardOk ? "summary.clipboard_ok" : "summary.clipboard_failed");
        var lines = new List<string>
        {
            msgs.Format("summary.line", emfBytes, nodeCount, result),
            published.Count > 0 ? Published(msgs, published) : msgs.Format("summary.no_files"),
        };

        var options = new List<string>();
        if (groupByLayer) options.Add(msgs.Format("summary.option.layer"));
        if (shxSubstitute) options.Add(msgs.Format("summary.option.shx"));
        if (textOutline) options.Add(msgs.Format("summary.option.outline"));
        if (marginMm != 0)
            options.Add(msgs.Format("summary.option.margin", marginMm));
        if (options.Count > 0)
            lines.Add(msgs.Format("summary.options", string.Join(", ", options)));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>出力したファイルの名前とフォルダの 1 行（ファイルは 1 つのフォルダに出す）。</summary>
    public static string Published(MessageCatalog msgs, IReadOnlyList<string> published) =>
        msgs.Format("summary.published",
            string.Join(msgs.Format("summary.name_separator"), published.Select(Path.GetFileName)),
            Path.GetDirectoryName(published[0]) ?? "");
}