// SVG IR の文字列化。IR は SVG のサブセットなので、この書き出しが IR の正準表現であり、
// そのままブラウザで開ける診断ダンプ（設計書§7）を兼ねる。外部ライブラリ参照ゼロ。
using System.Globalization;
using System.Text;

namespace CadPeta.Ir;

public static class SvgWriter
{
    // 0.03pt（コンテンツ量子）= 0.0106mm。小数4桁（0.1µm）で量子を十分下回る
    static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;")
                                    .Replace(">", "&gt;").Replace("\"", "&quot;");

    static string D(IEnumerable<IrSegment> segs)
    {
        var sb = new StringBuilder();
        foreach (var s in segs)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s switch
            {
                IrMove m => $"M {N(m.X)} {N(m.Y)}",
                IrLine l => $"L {N(l.X)} {N(l.Y)}",
                IrCubic c => $"C {N(c.X1)} {N(c.Y1)} {N(c.X2)} {N(c.Y2)} {N(c.X)} {N(c.Y)}",
                IrClose => "Z",
                _ => throw new InvalidOperationException($"未知のセグメント: {s}"),
            });
        }
        return sb.ToString();
    }

    static void Attr(StringBuilder sb, string name, string? value)
    {
        if (value != null) sb.Append($" {name}=\"{Esc(value)}\"");
    }

    static void CommonAttrs(StringBuilder sb, IrNode n)
    {
        Attr(sb, "clip-path", n.ClipId != null ? $"url(#{n.ClipId})" : null);
        Attr(sb, "data-layer", n.DataLayer);
    }

    public static string Write(IrDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                      $"width=\"{N(doc.WidthMm)}mm\" height=\"{N(doc.HeightMm)}mm\" " +
                      $"viewBox=\"{N(doc.ViewMinXMm)} {N(doc.ViewMinYMm)} " +
                      $"{N(doc.WidthMm)} {N(doc.HeightMm)}\">");

        if (doc.ClipDefs.Count > 0)
        {
            sb.AppendLine("<defs>");
            foreach (var c in doc.ClipDefs)
            {
                sb.Append($"<clipPath id=\"{Esc(c.Id)}\"><path d=\"{D(c.Segments)}\"");
                if (c.EvenOdd) sb.Append(" clip-rule=\"evenodd\"");
                sb.AppendLine("/></clipPath>");
            }
            sb.AppendLine("</defs>");
        }

        foreach (var node in doc.Nodes)
        {
            switch (node)
            {
                case IrPath p:
                {
                    sb.Append($"<path d=\"{D(p.Segments)}\"");
                    Attr(sb, "fill", p.Fill?.ToSvg() ?? "none");
                    if (p.Fill != null && p.FillEvenOdd) Attr(sb, "fill-rule", "evenodd");
                    if (p.Fill != null && p.FillOpacity < 1.0) Attr(sb, "fill-opacity", N(p.FillOpacity));
                    if (p.Stroke != null)
                    {
                        Attr(sb, "stroke", p.Stroke.Value.ToSvg());
                        Attr(sb, "stroke-width", N(p.StrokeWidthMm));
                        if (p.StrokeOpacity < 1.0) Attr(sb, "stroke-opacity", N(p.StrokeOpacity));
                    }
                    CommonAttrs(sb, p);
                    sb.AppendLine("/>");
                    break;
                }
                case IrText t:
                {
                    // clip-path と transform を同一要素に置くと、クリップ幾何が要素自身の
                    // transform で変換されて別位置へ移る（SVG仕様）。transform を使う場合は
                    // クリップを親 g へ退避する（g の clip-path は §2.1 で許可済み）
                    bool hasTransform = t.WidthFactor != 1.0 || t.RotationDeg != 0.0;
                    bool clipOnGroup = t.ClipId != null && hasTransform;
                    if (clipOnGroup)
                        sb.Append($"<g clip-path=\"url(#{t.ClipId})\">");
                    sb.Append("<text");
                    if (hasTransform)
                    {
                        // 幅係数は transform のXスケール、回転は rotate で表現する（設計書§2.1）。
                        // 変換空間へ移すため、グリフ位置は原点からの相対値を係数で割って与える
                        var tf = $"translate({N(t.XMm)} {N(t.YMm)})";
                        if (t.RotationDeg != 0.0) tf += $" rotate({N(t.RotationDeg)})";
                        if (t.WidthFactor != 1.0) tf += $" scale({N(t.WidthFactor)} 1)";
                        Attr(sb, "transform", tf);
                        Attr(sb, "x", t.GlyphXsMm != null
                            ? string.Join(" ", t.GlyphXsMm.Select(x => N((x - t.XMm) / t.WidthFactor)))
                            : "0");
                        Attr(sb, "y", "0");
                    }
                    else
                    {
                        Attr(sb, "x", t.GlyphXsMm != null
                            ? string.Join(" ", t.GlyphXsMm.Select(N))
                            : N(t.XMm));
                        Attr(sb, "y", N(t.YMm));
                    }
                    // 空白保持: SVG の既定は text 内の連続空白を1つへ畳み、先頭空白を落とすため、
                    // グリフごとの x 位置列と文字数がずれる（「縮尺␣␣1:150」で実測）。
                    // Chrome はルート指定を無視し、text 要素直付けのみ有効（xml:space と
                    // CSS white-space の両方を付けて他レンダラにも備える）
                    if (t.Value.StartsWith(' ') || t.Value.Contains("  "))
                    {
                        Attr(sb, "xml:space", "preserve");
                        Attr(sb, "style", "white-space:pre");
                    }
                    Attr(sb, "font-family", t.FontFamily);
                    Attr(sb, "font-size", N(t.FontSizeMm));
                    Attr(sb, "fill", t.Fill?.ToSvg() ?? "none");
                    if (t.FillOpacity < 1.0) Attr(sb, "fill-opacity", N(t.FillOpacity));
                    Attr(sb, "clip-path", !clipOnGroup && t.ClipId != null ? $"url(#{t.ClipId})" : null);
                    Attr(sb, "data-layer", t.DataLayer);
                    sb.Append($">{Esc(t.Value)}</text>");
                    sb.AppendLine(clipOnGroup ? "</g>" : "");
                    break;
                }
                case IrImage im:
                {
                    sb.Append($"<image x=\"{N(im.XMm)}\" y=\"{N(im.YMm)}\" " +
                              $"width=\"{N(im.WidthMm)}\" height=\"{N(im.HeightMm)}\" " +
                              $"preserveAspectRatio=\"none\" " +
                              $"href=\"data:image/png;base64,{Convert.ToBase64String(im.PngBytes)}\"");
                    CommonAttrs(sb, im);
                    sb.AppendLine("/>");
                    break;
                }
                default:
                    throw new InvalidOperationException($"未知のノード: {node.GetType().Name}");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
