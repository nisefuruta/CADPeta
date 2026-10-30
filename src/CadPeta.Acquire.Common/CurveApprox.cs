// 曲線→IR セグメントの数値変換（円・円弧・楕円・ポリラインのバルジ・スプライン）。
// 座標変換（図面単位→IR mm・Y軸反転）は呼び出し側から渡される写像 map で行い、
// 本クラスは CAD 座標系（Y上向き・角度は反時計回り正）で曲線を組み立てる。
// 円弧・楕円弧の3次ベジェ近似は標準の接線係数法（区間 ≤90°で最大誤差は半径の 0.03% 未満、
// IR のコンテンツ量子より十分小さい）。特定のCAD製品APIに依存しない共有計算のため、
// 取得アダプタ間で共有する。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Ir;

namespace CadPeta.Acquire.Common;

/// <summary>CAD 座標 (x, y) → IR 座標 (mm) の写像。</summary>
public delegate (double X, double Y) PointMap(double x, double y);

public static class CurveApprox
{
    /// <summary>ベジェ近似の1区間の最大角（90°）。</summary>
    const double MaxArcSpanRad = Math.PI / 2;

    /// <summary>
    /// パラメトリック楕円弧 P(t) = C + U·cos t + V·sin t を3次ベジェ列として segs へ追加する。
    /// 円は U=V直交・同長の特殊形。始点への Move は emitMove が真のときだけ出す。
    /// t0→t1 は増加方向（CAD の反時計回り）。
    /// </summary>
    public static void EllipticArc(List<IrSegment> segs, PointMap map,
                                   double cx, double cy, double ux, double uy,
                                   double vx, double vy, double t0, double t1, bool emitMove)
    {
        (double X, double Y) P(double t) => map(cx + ux * Math.Cos(t) + vx * Math.Sin(t),
                                                cy + uy * Math.Cos(t) + vy * Math.Sin(t));
        (double X, double Y) D(double t) => (-ux * Math.Sin(t) + vx * Math.Cos(t),
                                             -uy * Math.Sin(t) + vy * Math.Cos(t));

        if (t1 < t0)
            t1 += 2 * Math.PI;
        int n = Math.Max(1, (int)Math.Ceiling((t1 - t0) / MaxArcSpanRad));
        double dt = (t1 - t0) / n;
        if (emitMove)
        {
            var p0 = P(t0);
            segs.Add(new IrMove(p0.X, p0.Y));
        }
        // 接線係数: 区間角 δ の円弧を3次ベジェで結ぶ標準係数 k = 4/3·tan(δ/4)
        double k = 4.0 / 3.0 * Math.Tan(dt / 4.0);
        // 導関数（方向ベクトル）は写像の線形部だけを通す。map はアフィンのため
        // 原点の像との差分で線形部を適用できる
        var origin = map(0, 0);
        (double X, double Y) Lin((double X, double Y) v)
        {
            var p = map(v.X, v.Y);
            return (p.X - origin.X, p.Y - origin.Y);
        }
        for (int i = 0; i < n; i++)
        {
            double ta = t0 + i * dt, tb = ta + dt;
            var pa = P(ta);
            var pb = P(tb);
            var daM = Lin(D(ta));
            var dbM = Lin(D(tb));
            segs.Add(new IrCubic(pa.X + k * daM.X, pa.Y + k * daM.Y,
                                 pb.X - k * dbM.X, pb.Y - k * dbM.Y,
                                 pb.X, pb.Y));
        }
    }

    /// <summary>
    /// ポリラインのバルジ付き区間（p1→p2、bulge=tan(挟角/4)、正=反時計回り）を追加する。
    /// 始点は出力済み前提（Line/Cubic の続きとして追加する）。
    /// </summary>
    public static void BulgeArc(List<IrSegment> segs, PointMap map,
                                double x1, double y1, double x2, double y2, double bulge)
    {
        double theta = 4.0 * Math.Atan(bulge);   // 挟角（符号付き。正=反時計回り）
        double chordX = x2 - x1, chordY = y2 - y1;
        double chord = Math.Sqrt(chordX * chordX + chordY * chordY);
        if (chord == 0)
            return;
        double radius = chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0));
        // 中心は弦の中点から左法線方向へ h（反時計回り=中心は進行方向の左、時計回り=右。
        // 実図面の bulge −1/3 の円弧を entget 値と描画結果で突き合わせて確定した符号）
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        double h = radius * Math.Cos(theta / 2.0) * Math.Sign(bulge);
        double nx = -chordY / chord, ny = chordX / chord;
        double cx = mx + nx * h, cy = my + ny * h;
        double a0 = Math.Atan2(y1 - cy, x1 - cx);
        double a1 = a0 + theta;

        // 反時計回り（theta>0）はそのまま、時計回りは向きを反転した区間を逆順で出せないため
        // U/V 基底の V を反転して増加パラメータに直す
        if (theta > 0)
            EllipticArc(segs, map, cx, cy, radius, 0, 0, radius, a0, a1, emitMove: false);
        else
            EllipticArc(segs, map, cx, cy, radius, 0, 0, -radius, -a0, -a1, emitMove: false);
    }

    /// <summary>
    /// 3次・非有理・クランプ端のスプラインをノット挿入で厳密にベジェ列へ変換する。
    /// 変換できない類型（有理・3次以外・非クランプ）は false を返し、呼び出し側が
    /// 折れ線近似（Polyline サンプリング）へ落とす。
    /// </summary>
    public static bool TryNurbsToBeziers(int degree, bool rational,
                                         IReadOnlyList<(double X, double Y)> ctrl,
                                         IReadOnlyList<double> knots,
                                         List<IrSegment> segs, PointMap map, bool emitMove)
    {
        const int CubicDegree = 3;   // 3次ベジェへ厳密変換できる次数
        if (degree != CubicDegree || rational)
            return false;
        // クランプ端: 先頭・末尾ノットが degree+1 重
        int p = degree;
        if (knots.Count != ctrl.Count + p + 1)
            return false;
        for (int i = 1; i <= p; i++)
            if (knots[i] != knots[0] || knots[knots.Count - 1 - i] != knots[knots.Count - 1])
                return false;

        var cp = ctrl.ToList();
        var kn = knots.ToList();
        // 内部ノットをすべて多重度 p まで挿入すると、制御点列が3点刻みのベジェ制御点になる
        int guard = 0;
        // 挿入は最悪でも「区間数×次数」回で終わる。無限ループの保険として十分大きい上限を置く
        int maxInsertions = (kn.Count + 8) * (p + 1);
        for (int i = p + 1; i < kn.Count - p - 1; i++)
        {
            double u = kn[i];
            int mult = 1;
            while (i + mult < kn.Count && kn[i + mult] == u)
                mult++;
            for (int add = mult; add < p; add++)
            {
                InsertKnot(cp, kn, p, u);
                if (++guard > maxInsertions)
                    return false;
            }
            i += p - 1;
        }

        if (emitMove)
        {
            var s = map(cp[0].X, cp[0].Y);
            segs.Add(new IrMove(s.X, s.Y));
        }
        for (int i = 0; i + 3 < cp.Count; i += 3)
        {
            var c1 = map(cp[i + 1].X, cp[i + 1].Y);
            var c2 = map(cp[i + 2].X, cp[i + 2].Y);
            var pe = map(cp[i + 3].X, cp[i + 3].Y);
            segs.Add(new IrCubic(c1.X, c1.Y, c2.X, c2.Y, pe.X, pe.Y));
        }
        return true;
    }

    /// <summary>Boehm のノット挿入（1回）。制御点列とノット列を更新する。</summary>
    static void InsertKnot(List<(double X, double Y)> cp, List<double> kn, int p, double u)
    {
        int k = FindSpan(kn, p, u);
        var updated = new List<(double X, double Y)>(cp.Count + 1);
        for (int i = 0; i <= k - p; i++)
            updated.Add(cp[i]);
        for (int i = k - p + 1; i <= k; i++)
        {
            double denom = kn[i + p] - kn[i];
            double a = denom == 0 ? 0 : (u - kn[i]) / denom;
            updated.Add((cp[i - 1].X * (1 - a) + cp[i].X * a,
                         cp[i - 1].Y * (1 - a) + cp[i].Y * a));
        }
        for (int i = k; i < cp.Count; i++)
            updated.Add(cp[i]);
        cp.Clear();
        cp.AddRange(updated);
        kn.Insert(k + 1, u);
    }

    static int FindSpan(List<double> kn, int p, double u)
    {
        // u が属するノット区間 [kn[k], kn[k+1]) を線形探索（列は短く、挿入回数も少ない）
        for (int i = p; i < kn.Count - p - 1; i++)
            if (u >= kn[i] && u < kn[i + 1])
                return i;
        return kn.Count - p - 2;
    }

    /// <summary>
    /// 汎用曲線の折れ線近似（弦との最大距離 tolerance の適応分割）。
    /// eval はパラメータ t の点を返す。始点は出力済み前提。
    /// </summary>
    public static void AdaptivePolyline(List<IrSegment> segs, Func<double, (double X, double Y)> eval,
                                        double t0, double t1, double toleranceMm)
    {
        void Subdivide(double a, double b, (double X, double Y) pa, (double X, double Y) pb, int depth)
        {
            double tm = (a + b) / 2;
            var pm = eval(tm);
            // 弦の中点と曲線上の中点の距離で平坦度を判定する
            double dx = pm.X - (pa.X + pb.X) / 2, dy = pm.Y - (pa.Y + pb.Y) / 2;
            if (depth >= AcquireDefaults.MaxSubdivisionDepth
                || dx * dx + dy * dy <= toleranceMm * toleranceMm)
            {
                segs.Add(new IrLine(pb.X, pb.Y));
                return;
            }
            Subdivide(a, tm, pa, pm, depth + 1);
            Subdivide(tm, b, pm, pb, depth + 1);
        }
        Subdivide(t0, t1, eval(t0), eval(t1), 0);
    }
}
