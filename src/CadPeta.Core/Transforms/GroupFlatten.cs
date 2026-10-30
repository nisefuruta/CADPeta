// グループ（変換行列付き g）の平坦化: グループ概念を持たない出力（EMF）の前段で、
// 変換行列を子ノードの座標へ焼き込み、フラットな描画順リストへ展開する。
// 入力文書は変更しない（グループが無ければ入力をそのまま返し、あればコピー文書を返す）。
//
// 相似変換（等方スケール×回転。ブロック参照の通常の挿入）は無劣化に写る。
// 非等方スケール・せん断・反転は、座標は正確に写る一方で座標と独立な属性
// （線幅・文字の字形・画像の向き）を完全には表現できないため近似し、
// 件数と対象を診断ログへ記録する。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Ir;

namespace CadPeta.Transforms;

public static class GroupFlatten
{
    // 平坦化の変換が「軸平行のまま」とみなせる成分許容。回転成分（B・C）がこの絶対値未満なら
    // 画像を無劣化の軸平行配置で写せる（行列演算の丸め誤差レベル）
    const double AxisAlignedTol = 1e-12;

    /// <summary>グループを展開した文書を返す。グループが無ければ入力をそのまま返す。</summary>
    public static IrDocument Apply(IrDocument ir, IrDiagnostics diag)
    {
        if (!ir.Nodes.Any(n => n is IrGroup))
            return ir;

        var flat = new IrDocument
        {
            WidthMm = ir.WidthMm,
            HeightMm = ir.HeightMm,
            ViewMinXMm = ir.ViewMinXMm,
            ViewMinYMm = ir.ViewMinYMm,
            ClipDefs = ir.ClipDefs,   // クリップ幾何は文書座標系のため展開後もそのまま有効
        };

        // レイヤ別グループ化の境界は「先頭から Count 個」で数えるため、展開で増えた
        // ノード数に合わせて境界を数え直す
        List<IrLayerGroup>? newLayerGroups = ir.LayerGroups != null ? new() : null;
        int boundaryIndex = 0, remainingInBoundary = ir.LayerGroups?.Count > 0 ? ir.LayerGroups[0].Count : 0;
        int emittedInBoundary = 0;

        foreach (var node in ir.Nodes)
        {
            int before = flat.Nodes.Count;
            Emit(node, IrMatrix.Identity, inheritedClip: null, flat.Nodes, diag);
            if (newLayerGroups == null)
                continue;
            emittedInBoundary += flat.Nodes.Count - before;
            if (--remainingInBoundary == 0)
            {
                var g = ir.LayerGroups![boundaryIndex];
                newLayerGroups.Add(new IrLayerGroup(g.Label, emittedInBoundary));
                emittedInBoundary = 0;
                boundaryIndex++;
                if (boundaryIndex < ir.LayerGroups.Count)
                    remainingInBoundary = ir.LayerGroups[boundaryIndex].Count;
            }
        }
        flat.LayerGroups = newLayerGroups;
        return flat;
    }

    static void Emit(IrNode node, IrMatrix m, string? inheritedClip,
                     List<IrNode> output, IrDiagnostics diag)
    {
        // グループ外・恒等変換のノードは複製せずそのまま通す（PDF印刷経路の文書は全ノードがここを通り、
        // 出力は展開前とバイト一致する）
        if (node is not IrGroup && m.IsIdentity && inheritedClip == null)
        {
            output.Add(node);
            return;
        }

        switch (node)
        {
            case IrGroup grp:
            {
                var m2 = m.Multiply(grp.Transform);
                // グループのクリップは子へ引き継ぐ（クリップ幾何は文書座標系で定義済みのため、
                // 座標を焼き込んだ子にそのまま適用できる）。子が自身のクリップを持つ場合は
                // 二重クリップを表現できないため子側を優先し、近似として記録する
                string? clip = grp.ClipId ?? inheritedClip;
                foreach (var child in grp.Children)
                {
                    if (clip != null && child.ClipId != null && child.ClipId != clip)
                        diag.Add("グループ平坦化: 二重クリップの近似（子側を優先）",
                                 $"group clip={clip} child clip={child.ClipId}");
                    Emit(child, m2, clip, output, diag);
                }
                break;
            }
            case IrPath p:
            {
                var q = new IrPath
                {
                    Segments = p.Segments.Select(s => TransformSegment(s, m)).ToList(),
                    Stroke = p.Stroke,
                    StrokeWidthMm = p.StrokeWidthMm * m.ScaleFactor,
                    StrokeDashMm = p.StrokeDashMm?.Select(d => d * m.ScaleFactor).ToArray(),
                    StrokeOpacity = p.StrokeOpacity,
                    Fill = p.Fill,
                    FillEvenOdd = p.FillEvenOdd,
                    FillOpacity = p.FillOpacity,
                };
                CopyMeta(p, q, inheritedClip);
                if (p.Stroke != null && !m.IsSimilarity)
                    diag.Add("グループ平坦化: 非等方変換の線幅近似（等方成分で代表）",
                             $"path 線幅 {p.StrokeWidthMm:0.###}→{q.StrokeWidthMm:0.###}mm");
                output.Add(q);
                break;
            }
            case IrText t:
            {
                double s = m.ScaleFactor;
                var (ox, oy) = m.Apply(t.XMm, t.YMm);
                // ベースライン方向ベクトルを変換して新しい回転角を得る（Y下向き・時計回り正）
                double th = t.RotationDeg * Math.PI / 180.0;
                double ux = Math.Cos(th), uy = Math.Sin(th);
                double vx = m.A * ux + m.C * uy, vy = m.B * ux + m.D * uy;
                double baselineScale = Math.Sqrt(vx * vx + vy * vy);
                var q = new IrText
                {
                    Value = t.Value,
                    XMm = ox,
                    YMm = oy,
                    FontFamily = t.FontFamily,
                    FontSizeMm = t.FontSizeMm * s,
                    WidthFactor = t.WidthFactor,
                    RotationDeg = Math.Atan2(vy, vx) * 180.0 / Math.PI,
                    GlyphXsMm = t.GlyphXsMm?.Select(x => ox + (x - t.XMm) * baselineScale).ToArray(),
                    Fill = t.Fill,
                    FillOpacity = t.FillOpacity,
                };
                CopyMeta(t, q, inheritedClip);
                if (!m.IsSimilarity)
                    diag.Add("グループ平坦化: 非等方変換の文字近似（字形は等方成分で代表）",
                             $"text '{t.Value}'");
                else if (m.Det < 0)
                    diag.Add("グループ平坦化: 反転変換の文字近似（鏡像を非反転で配置）",
                             $"text '{t.Value}'");
                output.Add(q);
                break;
            }
            case IrImage im:
            {
                var q = new IrImage { PngBytes = im.PngBytes };
                CopyMeta(im, q, inheritedClip);
                bool axisAligned = Math.Abs(m.B) < AxisAlignedTol && Math.Abs(m.C) < AxisAlignedTol
                                   && m.A > 0 && m.D > 0;
                if (axisAligned)
                {
                    (q.XMm, q.YMm) = m.Apply(im.XMm, im.YMm);
                    q.WidthMm = im.WidthMm * m.A;
                    q.HeightMm = im.HeightMm * m.D;
                }
                else
                {
                    // 画像の回転・反転は画素の再標本化を要し、内核は画像デコーダを持たない。
                    // 変換後の外接矩形へ軸平行のまま配置する近似とし、記録する
                    double minx = double.MaxValue, miny = double.MaxValue;
                    double maxx = double.MinValue, maxy = double.MinValue;
                    foreach (double cx in new[] { im.XMm, im.XMm + im.WidthMm })
                        foreach (double cy in new[] { im.YMm, im.YMm + im.HeightMm })
                        {
                            var (x, y) = m.Apply(cx, cy);
                            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
                            miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                        }
                    q.XMm = minx; q.YMm = miny;
                    q.WidthMm = maxx - minx; q.HeightMm = maxy - miny;
                    diag.Add("グループ平坦化: 画像の回転・反転を軸平行配置で近似",
                             $"image bbox_mm=({minx:0.#},{miny:0.#})-({maxx:0.#},{maxy:0.#})");
                }
                output.Add(q);
                break;
            }
            default:
                throw new InvalidOperationException($"未知のノード: {node.GetType().Name}");
        }
    }

    static IrSegment TransformSegment(IrSegment s, IrMatrix m)
    {
        switch (s)
        {
            case IrMove mv:
            {
                var (x, y) = m.Apply(mv.X, mv.Y);
                return new IrMove(x, y);
            }
            case IrLine ln:
            {
                var (x, y) = m.Apply(ln.X, ln.Y);
                return new IrLine(x, y);
            }
            case IrCubic c:
            {
                var (x1, y1) = m.Apply(c.X1, c.Y1);
                var (x2, y2) = m.Apply(c.X2, c.Y2);
                var (x, y) = m.Apply(c.X, c.Y);
                return new IrCubic(x1, y1, x2, y2, x, y);
            }
            case IrClose:
                return s;
            default:
                throw new InvalidOperationException($"未知のセグメント: {s}");
        }
    }

    static void CopyMeta(IrNode src, IrNode dst, string? inheritedClip)
    {
        dst.DataLayer = src.DataLayer;
        dst.DataColorMethod = src.DataColorMethod;
        dst.DataAci = src.DataAci;
        dst.DataEntity = src.DataEntity;
        dst.ClipId = src.ClipId ?? inheritedClip;
    }
}
