// SVG IR の文字列化。IR は SVG のサブセットなので、この書き出しが IR の正準表現であり、
// そのままブラウザで開ける診断ダンプを兼ねる。SVG出力は
// この直列化にファイル書き出しを足すだけで成立する。外部ライブラリ参照ゼロ。
// 通常出力と診断ダンプの唯一の差は出所メタデータ（data-*）の有無で、includeProvenance が
// 決める（既定 false=通常出力は除去、診断ダンプのみ true で保持）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;

namespace CadPeta.Ir;

public static class SvgWriter
{
    static string N(double v) => IrNumber.Coordinate(v);

    static string T(double v) => IrNumber.Matrix(v);

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

    // 出所メタデータ（data-*）の唯一の関門。通常出力では除去し（既定）、
    // 診断用出力でのみ残す。呼び忘れても include 既定 false=除去に倒れ、
    // 安全側で寄る。
    static void ProvenanceAttr(StringBuilder sb, string name, string? value, bool include)
    {
        if (include) Attr(sb, name, value);
    }

    // 出所メタデータ4種をまとめて関門へ通す。data-layer 以外は構造SVG書き出し（DB直接取得）だけが持つ。
    static void ProvenanceAttrs(StringBuilder sb, IrNode n, bool include)
    {
        ProvenanceAttr(sb, "data-layer", n.DataLayer, include);
        ProvenanceAttr(sb, "data-color-method", n.DataColorMethod, include);
        ProvenanceAttr(sb, "data-aci", n.DataAci?.ToString(CultureInfo.InvariantCulture), include);
        ProvenanceAttr(sb, "data-entity", n.DataEntity, include);
    }

    static void CommonAttrs(StringBuilder sb, IrNode n, bool includeProvenance)
    {
        Attr(sb, "clip-path", n.ClipId != null ? $"url(#{n.ClipId})" : null);
        ProvenanceAttrs(sb, n, includeProvenance);
    }

    // includeProvenance: 出所メタデータ（data-*）を残すか。既定 false=除去は
    // 通常出力（SVG出力）の規約。診断用ダンプだけが true で正準表現ごと残す。
    // 除去/保持の選択は合成ルート（CLI・プラグイン）が担い、内核はこの引数で受けるだけ。
    public static string Write(IrDocument doc, bool includeProvenance = false)
    {
        // レイヤ別グループ化の整合検査。グループ境界は Nodes と別持ちのため、総和の不一致は
        // 「グループ化（変換パイプラインの最終段）の後に Nodes を増減させた」規約違反であり、
        // ずれたグループを黙って書くより直列化前に止める
        if (doc.LayerGroups != null && doc.LayerGroups.Sum(g => g.Count) != doc.Nodes.Count)
            throw new InvalidOperationException(
                $"LayerGroups の要素数総和 {doc.LayerGroups.Sum(g => g.Count)} が" +
                $" Nodes 数 {doc.Nodes.Count} と一致しない。レイヤ別グループ化" +
                "（変換の最終段）の後に Nodes を変更した処理を確認すること");

        var sb = new StringBuilder();
        // Inkscape がレイヤとして認識する属性（下の inkscape:groupmode/label）はレイヤ別
        // グループ化の適用時だけ使うため、名前空間宣言もそのときだけ出す（未適用時はバイト不変）
        string inkscapeXmlns = doc.LayerGroups != null
            ? "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " : "";
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" {inkscapeXmlns}" +
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

        if (doc.LayerGroups == null)
        {
            foreach (var node in doc.Nodes)
                WriteNode(node);
        }
        else
        {
            // レイヤ別グループ化: Nodes の先頭から Count 個ずつを g で包む。
            // inkscape:groupmode="layer" と inkscape:label（レイヤ名）は、Inkscape が
            // このグループをレイヤパネルにレイヤとして表示するための規約属性。
            // id はレイヤ初出順の連番
            int next = 0;
            for (int gi = 0; gi < doc.LayerGroups.Count; gi++)
            {
                var g = doc.LayerGroups[gi];
                sb.AppendLine($"<g id=\"layer_{gi}\" inkscape:groupmode=\"layer\" " +
                              $"inkscape:label=\"{Esc(g.Label)}\">");
                for (int k = 0; k < g.Count; k++)
                    WriteNode(doc.Nodes[next++]);
                sb.AppendLine("</g>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();

        void WriteNode(IrNode node)
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
                        if (p.StrokeDashMm is { Length: > 0 } dash)
                        {
                            Attr(sb, "stroke-dasharray", string.Join(" ", dash.Select(N)));
                            if (p.StrokeDashOffsetMm != 0)
                                Attr(sb, "stroke-dashoffset", N(p.StrokeDashOffsetMm));
                        }
                        if (p.StrokeOpacity < 1.0) Attr(sb, "stroke-opacity", N(p.StrokeOpacity));
                    }
                    CommonAttrs(sb, p, includeProvenance);
                    sb.AppendLine("/>");
                    break;
                }
                case IrText t:
                {
                    // clip-path と transform を同一要素に置くと、クリップ幾何が要素自身の
                    // transform で変換されて別位置へ移る（SVG仕様）。transform を使う場合は
                    // クリップを親 g へ退避する（g への clip-path 付与は許可された生成規約であり、
                    // 実測で根拠を確定済み）
                    bool hasTransform = t.WidthFactor != 1.0 || t.RotationDeg != 0.0;
                    bool clipOnGroup = t.ClipId != null && hasTransform;
                    if (clipOnGroup)
                        sb.Append($"<g clip-path=\"url(#{t.ClipId})\">");
                    sb.Append("<text");
                    if (hasTransform)
                    {
                        // 幅係数は transform のXスケール、回転は rotate で表現する（生成規約）。
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
                    // CSS white-space の両方を付けて他レンダラにも備える。生成規約に基づく）
                    if (t.Value.StartsWith(" ", StringComparison.Ordinal) || t.Value.Contains("  ")) // char 版 StartsWith は netstandard2.0 に無い
                    {
                        Attr(sb, "xml:space", "preserve");
                        Attr(sb, "style", "white-space:pre");
                    }
                    Attr(sb, "font-family", t.FontFamily);
                    Attr(sb, "font-size", N(t.FontSizeMm));
                    Attr(sb, "fill", t.Fill?.ToSvg() ?? "none");
                    if (t.FillOpacity < 1.0) Attr(sb, "fill-opacity", N(t.FillOpacity));
                    Attr(sb, "clip-path", !clipOnGroup && t.ClipId != null ? $"url(#{t.ClipId})" : null);
                    ProvenanceAttrs(sb, t, includeProvenance);
                    sb.Append($">{Esc(t.Value)}</text>");
                    sb.AppendLine(clipOnGroup ? "</g>" : "");
                    break;
                }
                case IrImage im:
                {
                    // Word は image に直付けした clip-path を image の x,y だけずらして適用し、
                    // クリップが画像から外れると画像を表示しない（実測）。クリップは外側 g に置く
                    bool clipOnWrapper = im.ClipId != null;
                    if (clipOnWrapper)
                        sb.Append($"<g clip-path=\"url(#{im.ClipId})\">");
                    sb.Append($"<image x=\"{N(im.XMm)}\" y=\"{N(im.YMm)}\" " +
                              $"width=\"{N(im.WidthMm)}\" height=\"{N(im.HeightMm)}\" " +
                              $"preserveAspectRatio=\"none\" " +
                              $"href=\"data:image/png;base64,{Convert.ToBase64String(im.PngBytes)}\"");
                    ProvenanceAttrs(sb, im, includeProvenance);
                    sb.Append("/>");
                    sb.AppendLine(clipOnWrapper ? "</g>" : "");
                    break;
                }
                case IrGroup grp:
                {
                    // clip-path は transform を持つ要素に直付けしない（クリップ幾何が要素自身の
                    // transform で変換されるため）。クリップ付きグループは外側 g へ退避する
                    bool clipOnWrapper = grp.ClipId != null;
                    if (clipOnWrapper)
                        sb.AppendLine($"<g clip-path=\"url(#{grp.ClipId})\">");
                    sb.Append("<g");
                    if (!grp.Transform.IsIdentity)
                    {
                        var m = grp.Transform;
                        // 行列成分は座標より1桁細かく書く（回転成分の丸めが座標誤差へ増幅されるため）
                        Attr(sb, "transform",
                             $"matrix({T(m.A)} {T(m.B)} {T(m.C)} {T(m.D)} {T(m.E)} {T(m.F)})");
                    }
                    ProvenanceAttrs(sb, grp, includeProvenance);
                    sb.AppendLine(">");
                    foreach (var child in grp.Children)
                        WriteNode(child);
                    sb.AppendLine("</g>");
                    if (clipOnWrapper)
                        sb.AppendLine("</g>");
                    break;
                }
                default:
                    throw new InvalidOperationException($"未知のノード: {node.GetType().Name}");
            }
        }
    }
}
