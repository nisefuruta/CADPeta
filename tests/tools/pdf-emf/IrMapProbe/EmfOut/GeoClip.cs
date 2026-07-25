// 事前クリップの幾何演算（設計書§6.1: クリップはEMFレコードとして出さず、
// 生成時にジオメトリへ適用する）。軸平行矩形クリップを対象とし、Win32 に依存しない純幾何。
//
// - ストローク: 線分は Liang-Barsky で切断。ベジェは全内なら保持・全外なら破棄、
//   境界交差時のみ折れ線化して切断（非可逆、呼び出し側が診断ログに記録する）
// - 塗り: サブパス（リング）単位で Sutherland-Hodgman。境界交差リングは折れ線化する
using CadPeta.Ir;

namespace CadPeta.EmfOut;

internal static class GeoClip
{
    const double Eps = 1e-9;
    const double JoinEps = 1e-6;      // 出力済み端点との連続判定（mm）
    const double FlattenTolMm = 0.005; // ベジェ折れ線化の許容偏差（コンテンツ量子 0.0106mm の半分）

    public readonly record struct RectMm(double X0, double Y0, double X1, double Y1)
    {
        public bool ContainsBbox(double[] b) =>
            b[0] >= X0 - Eps && b[1] >= Y0 - Eps && b[2] <= X1 + Eps && b[3] <= Y1 + Eps;
        public bool OverlapsBbox(double[] b) =>
            b[0] <= X1 + Eps && b[2] >= X0 - Eps && b[1] <= Y1 + Eps && b[3] >= Y0 - Eps;

        public static RectMm? Intersect(RectMm a, RectMm b)
        {
            double x0 = Math.Max(a.X0, b.X0), y0 = Math.Max(a.Y0, b.Y0);
            double x1 = Math.Min(a.X1, b.X1), y1 = Math.Min(a.Y1, b.Y1);
            return x0 < x1 && y0 < y1 ? new RectMm(x0, y0, x1, y1) : null;
        }
    }

    /// <summary>セグメント列の bbox（ベジェは制御点を含む保守的評価）。</summary>
    public static double[] Bbox(IEnumerable<IrSegment> segs)
    {
        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue;
        void U(double x, double y)
        {
            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
            miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
        }
        foreach (var s in segs)
            switch (s)
            {
                case IrMove m: U(m.X, m.Y); break;
                case IrLine l: U(l.X, l.Y); break;
                case IrCubic c: U(c.X1, c.Y1); U(c.X2, c.Y2); U(c.X, c.Y); break;
            }
        return minx <= maxx ? new[] { minx, miny, maxx, maxy } : new[] { 0.0, 0, 0, 0 };
    }

    /// <summary>クリップ定義が軸平行矩形か判定する（Move/Line/Close のみ・全辺が軸平行）。</summary>
    public static bool IsAxisRect(IReadOnlyList<IrSegment> segs, out RectMm rect)
    {
        rect = default;
        var pts = new List<(double X, double Y)>();
        foreach (var s in segs)
            switch (s)
            {
                case IrMove m: pts.Add((m.X, m.Y)); break;
                case IrLine l: pts.Add((l.X, l.Y)); break;
                case IrClose: break;
                default: return false; // ベジェを含むクリップは矩形ではない
            }
        if (pts.Count is < 4 or > 5) return false;
        for (int i = 1; i < pts.Count; i++)
            if (Math.Abs(pts[i].X - pts[i - 1].X) > 1e-6 && Math.Abs(pts[i].Y - pts[i - 1].Y) > 1e-6)
                return false; // 斜め辺あり
        double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y);
        double x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y);
        if (x1 - x0 < Eps || y1 - y0 < Eps) return false;
        rect = new RectMm(x0, y0, x1, y1);
        return true;
    }

    // ---------- ストロークのクリップ ----------

    /// <summary>ストローク幾何を矩形で切断する。null=全体がクリップ外。</summary>
    public static List<IrSegment>? ClipStroke(List<IrSegment> segs, RectMm r,
                                              out bool modified, out bool flattened)
    {
        modified = false; flattened = false;
        var bb = Bbox(segs);
        if (r.ContainsBbox(bb)) return segs;
        modified = true;
        if (!r.OverlapsBbox(bb)) return null;

        var res = new List<IrSegment>();
        double sx = 0, sy = 0, cx = 0, cy = 0;      // サブパス始点・論理カレント
        double ex = double.NaN, ey = double.NaN;    // 出力済みカレント
        bool flat = false;

        void EmitLine(double x1, double y1, double x2, double y2)
        {
            if (!LiangBarsky(r, x1, y1, x2, y2, out var ax, out var ay, out var bx, out var by))
                return;
            if (double.IsNaN(ex) || Math.Abs(ax - ex) > JoinEps || Math.Abs(ay - ey) > JoinEps)
                res.Add(new IrMove(ax, ay));
            res.Add(new IrLine(bx, by));
            ex = bx; ey = by;
        }

        foreach (var s in segs)
            switch (s)
            {
                case IrMove m:
                    cx = sx = m.X; cy = sy = m.Y;
                    break;
                case IrLine l:
                    EmitLine(cx, cy, l.X, l.Y);
                    cx = l.X; cy = l.Y;
                    break;
                case IrCubic c:
                {
                    var cb = new[]
                    {
                        Math.Min(Math.Min(cx, c.X1), Math.Min(c.X2, c.X)),
                        Math.Min(Math.Min(cy, c.Y1), Math.Min(c.Y2, c.Y)),
                        Math.Max(Math.Max(cx, c.X1), Math.Max(c.X2, c.X)),
                        Math.Max(Math.Max(cy, c.Y1), Math.Max(c.Y2, c.Y)),
                    };
                    if (r.ContainsBbox(cb))
                    {
                        if (double.IsNaN(ex) || Math.Abs(cx - ex) > JoinEps || Math.Abs(cy - ey) > JoinEps)
                            res.Add(new IrMove(cx, cy));
                        res.Add(c);
                        ex = c.X; ey = c.Y;
                    }
                    else if (r.OverlapsBbox(cb))
                    {
                        flat = true;
                        double px = cx, py = cy;
                        foreach (var (x, y) in FlattenCubic(cx, cy, c))
                        {
                            EmitLine(px, py, x, y);
                            px = x; py = y;
                        }
                    }
                    // 全外は破棄（曲線は制御点凸包内にあるため bbox 非交差なら安全）
                    cx = c.X; cy = c.Y;
                    break;
                }
                case IrClose:
                    EmitLine(cx, cy, sx, sy);
                    cx = sx; cy = sy;
                    break;
            }
        flattened = flat;
        return res.Count > 0 ? res : null;
    }

    /// <summary>Liang-Barsky 線分クリップ。true なら (ax,ay)-(bx,by) が可視部分。</summary>
    static bool LiangBarsky(RectMm r, double x1, double y1, double x2, double y2,
                            out double ax, out double ay, out double bx, out double by)
    {
        double dx = x2 - x1, dy = y2 - y1, t0 = 0, t1 = 1;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < Eps) return q >= -Eps;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        ax = ay = bx = by = 0;
        if (!Clip(-dx, x1 - r.X0) || !Clip(dx, r.X1 - x1)
            || !Clip(-dy, y1 - r.Y0) || !Clip(dy, r.Y1 - y1)) return false;
        ax = x1 + t0 * dx; ay = y1 + t0 * dy;
        bx = x1 + t1 * dx; by = y1 + t1 * dy;
        return Math.Abs(bx - ax) > Eps || Math.Abs(by - ay) > Eps;
    }

    // ---------- 塗りのクリップ ----------

    /// <summary>塗り幾何をリング単位で矩形クリップする。null=全体がクリップ外。</summary>
    public static List<IrSegment>? ClipFill(List<IrSegment> segs, RectMm r,
                                            out bool modified, out bool flattened)
    {
        modified = false; flattened = false;
        var bb = Bbox(segs);
        if (r.ContainsBbox(bb)) return segs;
        modified = true;
        if (!r.OverlapsBbox(bb)) return null;

        var res = new List<IrSegment>();
        bool flat = false;
        foreach (var ring in Subpaths(segs))
        {
            var rb = Bbox(ring);
            if (r.ContainsBbox(rb)) { res.AddRange(ring); continue; }
            if (!r.OverlapsBbox(rb)) continue;
            var pts = FlattenRing(ring, out bool hadCurve);
            flat |= hadCurve;
            var clipped = SutherlandHodgman(pts, r);
            if (clipped.Count < 3) continue;
            res.Add(new IrMove(clipped[0].X, clipped[0].Y));
            for (int i = 1; i < clipped.Count; i++)
                res.Add(new IrLine(clipped[i].X, clipped[i].Y));
            res.Add(new IrClose());
        }
        flattened = flat;
        return res.Count > 0 ? res : null;
    }

    /// <summary>セグメント列を Move 区切りのサブパスへ分割する。</summary>
    public static IEnumerable<List<IrSegment>> Subpaths(List<IrSegment> segs)
    {
        var cur = new List<IrSegment>();
        foreach (var s in segs)
        {
            if (s is IrMove && cur.Count > 0) { yield return cur; cur = new List<IrSegment>(); }
            cur.Add(s);
        }
        if (cur.Count > 0) yield return cur;
    }

    static List<(double X, double Y)> FlattenRing(List<IrSegment> ring, out bool hadCurve)
    {
        hadCurve = false;
        var pts = new List<(double X, double Y)>();
        double cx = 0, cy = 0;
        foreach (var s in ring)
            switch (s)
            {
                case IrMove m: pts.Add((m.X, m.Y)); cx = m.X; cy = m.Y; break;
                case IrLine l: pts.Add((l.X, l.Y)); cx = l.X; cy = l.Y; break;
                case IrCubic c:
                    hadCurve = true;
                    pts.AddRange(FlattenCubic(cx, cy, c));
                    cx = c.X; cy = c.Y;
                    break;
                case IrClose: break; // リングは暗黙に閉じる
            }
        return pts;
    }

    /// <summary>3次ベジェの折れ線化（許容偏差 FlattenTolMm、始点は含まない）。</summary>
    public static IEnumerable<(double X, double Y)> FlattenCubic(double x0, double y0, IrCubic c)
    {
        // 制御点の弦からの最大距離で分割数を決める（保守的な平坦性評価）
        double d1 = PointLineDist(c.X1, c.Y1, x0, y0, c.X, c.Y);
        double d2 = PointLineDist(c.X2, c.Y2, x0, y0, c.X, c.Y);
        double d = Math.Max(d1, d2);
        int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(d / FlattenTolMm) * 3), 4, 64);
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            double x = u * u * u * x0 + 3 * u * u * t * c.X1 + 3 * u * t * t * c.X2 + t * t * t * c.X;
            double y = u * u * u * y0 + 3 * u * u * t * c.Y1 + 3 * u * t * t * c.Y2 + t * t * t * c.Y;
            yield return (x, y);
        }
    }

    static double PointLineDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < Eps) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        return Math.Abs((px - ax) * dy - (py - ay) * dx) / len;
    }

    /// <summary>凸クリッパ（Sutherland-Hodgman）。矩形の4半平面で順にクリップする。</summary>
    static List<(double X, double Y)> SutherlandHodgman(List<(double X, double Y)> poly, RectMm r)
    {
        var p = poly;
        p = ClipHalfPlane(p, (x, y) => x >= r.X0 - Eps, (a, b) => LerpX(a, b, r.X0));
        p = ClipHalfPlane(p, (x, y) => x <= r.X1 + Eps, (a, b) => LerpX(a, b, r.X1));
        p = ClipHalfPlane(p, (x, y) => y >= r.Y0 - Eps, (a, b) => LerpY(a, b, r.Y0));
        p = ClipHalfPlane(p, (x, y) => y <= r.Y1 + Eps, (a, b) => LerpY(a, b, r.Y1));
        return p;
    }

    static (double X, double Y) LerpX((double X, double Y) a, (double X, double Y) b, double x)
    {
        double t = (x - a.X) / (b.X - a.X);
        return (x, a.Y + t * (b.Y - a.Y));
    }

    static (double X, double Y) LerpY((double X, double Y) a, (double X, double Y) b, double y)
    {
        double t = (y - a.Y) / (b.Y - a.Y);
        return (a.X + t * (b.X - a.X), y);
    }

    static List<(double X, double Y)> ClipHalfPlane(
        List<(double X, double Y)> poly,
        Func<double, double, bool> inside,
        Func<(double X, double Y), (double X, double Y), (double X, double Y)> cross)
    {
        var res = new List<(double X, double Y)>();
        if (poly.Count == 0) return res;
        var prev = poly[^1];
        bool prevIn = inside(prev.X, prev.Y);
        foreach (var cur in poly)
        {
            bool curIn = inside(cur.X, cur.Y);
            if (curIn)
            {
                if (!prevIn) res.Add(cross(prev, cur));
                res.Add(cur);
            }
            else if (prevIn)
                res.Add(cross(prev, cur));
            prev = cur; prevIn = curIn;
        }
        return res;
    }
}
