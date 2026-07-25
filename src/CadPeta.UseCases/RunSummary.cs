namespace CadPeta.UseCases;

public static class RunSummary
{
    public static string Build(MessageCatalog msgs,
                               string? fileName, long emfBytes, int nodeCount, bool clipboardOk,
                               bool groupByLayer, bool shxSubstitute, bool textOutline,
                               double marginMm)
    {
        string result = msgs.Format(clipboardOk ? "summary.clipboard_ok" : "summary.clipboard_failed");
        string prefix = fileName == null ? "" : fileName + "  ";
        string suffix = fileName == null ? msgs.Format("summary.file_output_off") : "";
        var lines = new List<string>
        {
            msgs.Format("summary.line", prefix, emfBytes, nodeCount, result, suffix)
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
}
