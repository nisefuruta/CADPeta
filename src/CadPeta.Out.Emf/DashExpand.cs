// 破線ストロークのセグメント展開（EMF 出力専用）。
// 破線パターン（実線＋間隙の交互列、mm）をパスの道のりへ適用し、実線区間だけの
// 折れ線片（Move＋Line 列）へ展開する。曲線（3次ベジェ）は弦との許容誤差で折れ線化して
// から破線を刻む。プロット経由（PDF印刷経路）の PDF も破線付き曲線を折れ線群で出力しており、
// この展開はPDF印刷経路と同じ表現に揃える。
using CadPeta.Ir;

namespace CadPeta.Out.Emf;

internal static class DashExpand
{
    /// <summary>曲線の折れ線化の許容誤差（mm）。IR のコンテンツ量子 0.03pt≒0.0106mm と同水準。</summary>
    const double FlattenTolMm = 0.01;

    /// <summary>折れ線化の再帰分割の上限深さ（収束しない縮退曲線の保険）。</summary>
    const int MaxSubdivisionDepth = 12;

    /// <summary>数値誤差で残る長さ 0 未満の区間を打ち切る閾値（mm）。</summary>
    const double EpsMm = 1e-9;

    public static List<List<IrSegment>> Apply(List<IrSegment> segs, double[] patternMm, double offsetMm = 0)
    {
        var pieces = new List<List<IrSegment>>();
        if (patternMm.All(d => d <= 0))
        {
            // 全区間 0 のパターンは破線として意味を持たない。実線のまま1片で返す
            pieces.Add(segs);
            return pieces;
        }

        List<IrSegment>? current = null;
        int patIdx = 0;
        double remain = patternMm[0];
        bool inDash = true;
        double sx = 0, sy = 0, cx = 0, cy = 0;

        // stroke-dashoffset と同じ意味（正の値だけ先にパターンを消費した状態から始める）。
        // 構造SVG書き出しは現状常に offset=0 のため実質未使用（将来の拡張点）。SVG は部分パス（サブパス）
        // ごとにこの初期位相を再適用するため、ResetPattern からも呼ぶ
        void ApplyOffset()
        {
            if (offsetMm == 0)
                return;
            double total = patternMm.Sum();
            double consume = offsetMm % total;
            if (consume < 0)
                consume += total;
            while (consume > EpsMm)
            {
                double step = Math.Min(remain, consume);
                remain -= step;
                consume -= step;
                if (remain <= EpsMm)
                {
                    patIdx = (patIdx + 1) % patternMm.Length;
                    remain = patternMm[patIdx];
                    inDash = !inDash;
                }
            }
        }
        ApplyOffset();

        void ResetPattern()
        {
            patIdx = 0;
            remain = patternMm[0];
            inDash = true;
            current = null;
            ApplyOffset();
        }

        void AdvancePattern()
        {
            patIdx = (patIdx + 1) % patternMm.Length;
            remain = patternMm[patIdx];
            inDash = !inDash;
            if (!inDash)
                current = null;   // 間隙に入ったら片を閉じる
        }

        void WalkLine(double x1, double y1)
        {
            double dx = x1 - cx, dy = y1 - cy;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double done = 0;
            while (len - done > EpsMm)
            {
                if (remain <= EpsMm)
                {
                    AdvancePattern();
                    continue;
                }
                double step = Math.Min(remain, len - done);
                double t0 = done / len, t1 = (done + step) / len;
                if (inDash)
                {
                    if (current == null)
                    {
                        current = new List<IrSegment> { new IrMove(cx + dx * t0, cy + dy * t0) };
                        pieces.Add(current);
                    }
                    current.Add(new IrLine(cx + dx * t1, cy + dy * t1));
                }
                done += step;
                remain -= step;
            }
            cx = x1;
            cy = y1;
        }

        void WalkCubic(IrCubic c)
        {
            // De Casteljau の2分割で弦との誤差が許容内になるまで折れ線化してから刻む。
            // 制御点の弦からの距離で平坦度を判定する（弦を含む帯に制御多角形が収まれば
            // 曲線全体も収まる）
            void Split(double p0x, double p0y, double p1x, double p1y,
                       double p2x, double p2y, double p3x, double p3y, int depth)
            {
                double d1 = PointLineDistance(p1x, p1y, p0x, p0y, p3x, p3y);
                double d2 = PointLineDistance(p2x, p2y, p0x, p0y, p3x, p3y);
                if (depth >= MaxSubdivisionDepth || Math.Max(d1, d2) <= FlattenTolMm)
                {
                    WalkLine(p3x, p3y);
                    return;
                }
                double q0x = (p0x + p1x) / 2, q0y = (p0y + p1y) / 2;
                double q1x = (p1x + p2x) / 2, q1y = (p1y + p2y) / 2;
                double q2x = (p2x + p3x) / 2, q2y = (p2y + p3y) / 2;
                double r0x = (q0x + q1x) / 2, r0y = (q0y + q1y) / 2;
                double r1x = (q1x + q2x) / 2, r1y = (q1y + q2y) / 2;
                double mx = (r0x + r1x) / 2, my = (r0y + r1y) / 2;
                Split(p0x, p0y, q0x, q0y, r0x, r0y, mx, my, depth + 1);
                Split(mx, my, r1x, r1y, q2x, q2y, p3x, p3y, depth + 1);
            }
            Split(cx, cy, c.X1, c.Y1, c.X2, c.Y2, c.X, c.Y, 0);
        }

        foreach (var s in segs)
        {
            switch (s)
            {
                case IrMove m:
                    // 部分パス（サブパス）ごとにパターンを先頭から適用し直す（SVG レンダラの挙動と同じ）
                    sx = cx = m.X;
                    sy = cy = m.Y;
                    ResetPattern();
                    break;
                case IrLine l:
                    WalkLine(l.X, l.Y);
                    break;
                case IrCubic c:
                    WalkCubic(c);
                    break;
                case IrClose:
                    WalkLine(sx, sy);
                    current = null;
                    break;
            }
        }
        return pieces;
    }

    static double PointLineDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double len = Math.Sqrt(vx * vx + vy * vy);
        if (len < EpsMm)
        {
            double dx = px - ax, dy = py - ay;
            return Math.Sqrt(dx * dx + dy * dy);
        }
        return Math.Abs(vx * (py - ay) - vy * (px - ax)) / len;
    }
}
