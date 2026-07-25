// グリフ輪郭の配置変換。IGlyphOutliner が返すテキスト空間のセグメント列へ
// 幅係数 → 回転 → 平行移動 の順で変換を適用し、文字の描画位置（mm）へ写す。
// text 要素の描画（SVG の translate/rotate/scale(wf,1)）と同一の変換順である。
using CadPeta.Ir;

namespace CadPeta.UseCases;

public static class GlyphPlacement
{
    /// <summary>テキスト空間のセグメント列を、文字の位置・回転・幅係数で配置する。</summary>
    public static List<IrSegment> Place(List<IrSegment> textSpace, double xMm, double yMm,
                                        double rotationDeg, double widthFactor)
    {
        // SVG の rotate は Y下向き座標で時計回り正 = 行列 [c,-s;s,c]
        double th = rotationDeg * Math.PI / 180.0;
        double c = Math.Cos(th), s = Math.Sin(th);
        (double X, double Y) T(double xs, double ys)
        {
            double x = xs * widthFactor, y = ys;
            return (xMm + c * x - s * y, yMm + s * x + c * y);
        }

        var placed = new List<IrSegment>(textSpace.Count);
        foreach (var seg in textSpace)
        {
            switch (seg)
            {
                case IrMove m:
                {
                    var p = T(m.X, m.Y);
                    placed.Add(new IrMove(p.X, p.Y));
                    break;
                }
                case IrLine l:
                {
                    var p = T(l.X, l.Y);
                    placed.Add(new IrLine(p.X, p.Y));
                    break;
                }
                case IrCubic b:
                {
                    var p1 = T(b.X1, b.Y1);
                    var p2 = T(b.X2, b.Y2);
                    var p3 = T(b.X, b.Y);
                    placed.Add(new IrCubic(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y));
                    break;
                }
                case IrClose:
                    placed.Add(new IrClose());
                    break;
            }
        }
        return placed;
    }
}
