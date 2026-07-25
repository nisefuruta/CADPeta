// 矩形クリップの幾何演算（Win32 非依存の純幾何、内核）。
// EMF出力の事前クリップ（クリップはEMFレコードとして出さず、生成時に
// ジオメトリへ適用する）と、変換層のクロップ間引きが共用する。
//
// - ストローク: 線分は Liang-Barsky で切断。ベジェは全内なら保持・全外なら破棄、
//   境界交差時のみ折れ線化して切断（非可逆、呼び出し側が診断ログに記録する）
// - 塗り: サブパス（リング）単位で Sutherland-Hodgman。境界交差リングは折れ線化する
using CadPeta.Ir;

namespace CadPeta.Geometry;

public static class GeoClip
{
    const double Eps = 1e-9;           // 幾何判定の数値誤差吸収（mm。コンテンツ量子 0.0106mm より十分小さい）
    const double JoinEps = 1e-6;       // 出力済み端点との連続判定（mm。同上、量子より十分小さい）
    const double FlattenTolMm = 0.005; // ベジェ折れ線化の許容偏差。コンテンツ量子 0.0106mm（実測 0.03pt）
                                       // の半分で、量子以下の偏差に収める
    const double AxisTolMm = 1e-6;     // 辺の軸平行判定（mm）

    // 折れ線化の分割数: 弦偏差 d の平方根則 n = ceil(sqrt(d/許容偏差)) × 係数。
    // 係数と上下限は予行実装から継承した安全側の経験値で、
    // テストチャート＋実物図面の回帰（バイト一致・レンダ画素比較）で妥当性を確認済み
    const int FlattenSegFactor = 3;
    const int FlattenMinSegs = 4;
    const int FlattenMaxSegs = 64;

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
        if (pts.Count is < 4 or > 5) return false; // 矩形リング=4頂点（始点の重複で5点目まで許容）
        for (int i = 1; i < pts.Count; i++)
            if (Math.Abs(pts[i].X - pts[i - 1].X) > AxisTolMm && Math.Abs(pts[i].Y - pts[i - 1].Y) > AxisTolMm)
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
        // Math.Clamp は netstandard2.0 に無いため Min/Max で等価に書く
        int n = Math.Min(Math.Max(
            (int)Math.Ceiling(Math.Sqrt(d / FlattenTolMm) * FlattenSegFactor),
            FlattenMinSegs), FlattenMaxSegs);
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
        var prev = poly[poly.Count - 1]; // ^1 は netstandard2.0（System.Index 不在）で使えない
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
