// IRノードの保守的な外接矩形（純幾何、内核）。
// クロップ変換の範囲外間引きと EMF出力のクリップ交差判定が共用する。
// 「保守的」= 実描画範囲を必ず含む（広めに出る分には間引き・交差判定は安全側に倒れる）。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Ir;

namespace CadPeta.Geometry;

public static class IrBounds
{
    // 保守的なフォント縦メトリクス（em 比）。一般的な TrueType（ascent≈1.0em・descent≈0.25em）を
    // 上回る値で bbox を広めに取る（広い分には間引き・交差判定が安全側に倒れる）
    const double AscentEm = 1.1;
    const double DescentEm = 0.35;

    /// <summary>文字ランの保守的な外接矩形（回転・幅係数込み）。</summary>
    public static double[] TextQuadBbox(IrText t)
    {
        double span = t.GlyphXsMm is { Length: > 1 } g
            ? (g.Max() - g.Min()) + t.FontSizeMm * Math.Max(1.0, t.WidthFactor)
            : t.Value.Length * t.FontSizeMm * Math.Max(1.0, t.WidthFactor);
        double th = t.RotationDeg * Math.PI / 180.0;
        double ux = Math.Cos(th), uy = Math.Sin(th);   // ベースライン方向
        double axv = Math.Sin(th), ayv = -Math.Cos(th); // アセント方向（-Y を回転）
        double asc = t.FontSizeMm * AscentEm, desc = t.FontSizeMm * DescentEm;
        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue;
        foreach (double e1 in new[] { 0.0, span })
            foreach (double e2 in new[] { -desc, asc })
            {
                double x = t.XMm + ux * e1 + axv * e2;
                double y = t.YMm + uy * e1 + ayv * e2;
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
                miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
            }
        return new[] { minx, miny, maxx, maxy };
    }

    /// <summary>ノードの保守的な外接矩形。パスはストローク幅の半分だけ外側へ広げる。</summary>
    public static double[] NodeBbox(IrNode node)
    {
        switch (node)
        {
            case IrPath p:
            {
                var b = GeoClip.Bbox(p.Segments);
                if (p.Stroke != null && p.StrokeWidthMm > 0)
                {
                    double half = p.StrokeWidthMm / 2;
                    b = new[] { b[0] - half, b[1] - half, b[2] + half, b[3] + half };
                }
                return b;
            }
            case IrText t:
                return TextQuadBbox(t);
            case IrImage im:
                return new[] { im.XMm, im.YMm, im.XMm + im.WidthMm, im.YMm + im.HeightMm };
            case IrGroup g:
            {
                // 子の bbox の四隅を変換行列で写して合併する。回転では bbox の bbox になるため
                // 広めに出るが、保守的（実描画範囲を必ず含む）という本メソッドの規約は保たれる
                double minx = double.MaxValue, miny = double.MaxValue;
                double maxx = double.MinValue, maxy = double.MinValue;
                foreach (var child in g.Children)
                {
                    var b = NodeBbox(child);
                    if (b[0] > b[2])
                        continue; // 空グループ等の縮退 bbox は面積を持たない
                    foreach (double cx in new[] { b[0], b[2] })
                        foreach (double cy in new[] { b[1], b[3] })
                        {
                            var (x, y) = g.Transform.Apply(cx, cy);
                            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
                            miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                        }
                }
                // 子が無い場合は縮退 bbox（min>max）を返し、交差判定が常に偽＝不可視になる
                return new[] { minx, miny, maxx, maxy };
            }
            default:
                throw new InvalidOperationException($"未知のノード: {node.GetType().Name}");
        }
    }

    /// <summary>複数の bbox を包含する外接矩形へ合併する。縮退 bbox（min&gt;max）は無視する。
    /// 有効な bbox が1件も無ければ縮退 bbox（min&gt;max）を返す。</summary>
    public static double[] Union(IEnumerable<double[]> boxes)
    {
        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue;
        foreach (var b in boxes)
        {
            if (b[0] > b[2] || b[1] > b[3]) continue;
            minx = Math.Min(minx, b[0]); miny = Math.Min(miny, b[1]);
            maxx = Math.Max(maxx, b[2]); maxy = Math.Max(maxy, b[3]);
        }
        return new[] { minx, miny, maxx, maxy };
    }
}
