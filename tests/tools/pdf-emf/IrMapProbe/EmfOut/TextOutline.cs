// クリップ境界と交差する文字の輪郭化（設計書§6.1: 文字と交差するクリップは
// 輪郭化を要するため、非可逆処理として処理ログに記録する）。
// GDI のパスブラケット（BeginPath + ExtTextOutW + GetPath）でグリフ輪郭を取り出し、
// IR と同じ変換順（scale(wf,1) → rotate(θ) → translate(X,Y)）で mm 座標へ写す。
using CadPeta.Ir;

namespace CadPeta.EmfOut;

internal static class TextOutline
{
    /// <summary>文字ランをグリフ輪郭のセグメント列（mm、変換適用済み）へ展開する。</summary>
    public static List<IrSegment>? Outline(IrText t)
    {
        // 論理単位 0.01mm（MM_TEXT のメモリDCでは論理=デバイスの数値をそのまま使う）
        int hL = Math.Max(1, (int)Math.Round(t.FontSizeMm * 100));
        int n = t.Value.Length;
        if (n == 0) return null;

        // グリフ位置列 → dx（幅係数適用前のテキスト空間、0.01mm 単位）
        int[]? dx = null;
        if (t.GlyphXsMm is { Length: > 1 } g && g.Length == n)
        {
            dx = new int[n];
            int prev = 0;
            for (int i = 1; i < n; i++)
            {
                int q = (int)Math.Round((g[i] - g[0]) / t.WidthFactor * 100);
                dx[i - 1] = q - prev;
                prev = q;
            }
            dx[n - 1] = hL; // 最終グリフの送りは輪郭に影響しない
        }

        IntPtr hdc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        IntPtr font = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            font = Gdi.CreateFontW(-hL, 0, 0, 0, 400, 0, 0, 0, Gdi.DEFAULT_CHARSET,
                                   0, 0, 0, 0, t.FontFamily);
            old = Gdi.SelectObject(hdc, font);
            Gdi.SetBkMode(hdc, Gdi.TRANSPARENT);
            Gdi.SetTextAlign(hdc, Gdi.TA_BASELINE);

            Gdi.BeginPath(hdc);
            Gdi.ExtTextOutW(hdc, 0, 0, 0, IntPtr.Zero, t.Value, (uint)n, dx);
            Gdi.EndPath(hdc);

            int cnt = Gdi.GetPath(hdc, null, null, 0);
            if (cnt <= 0) { Gdi.AbortPath(hdc); return null; }
            var pts = new POINT[cnt];
            var types = new byte[cnt];
            Gdi.GetPath(hdc, pts, types, cnt);

            // 変換: 0.01mm→mm、scale(wf,1)→rotate(θ)→translate(XMm,YMm)
            //（SVG の rotate は Y下向き座標で時計回り正 = 行列 [c,-s;s,c]）
            double th = t.RotationDeg * Math.PI / 180.0;
            double c = Math.Cos(th), s = Math.Sin(th);
            (double X, double Y) T(POINT p)
            {
                double x = p.X * 0.01 * t.WidthFactor, y = p.Y * 0.01;
                return (t.XMm + c * x - s * y, t.YMm + s * x + c * y);
            }

            var segs = new List<IrSegment>();
            for (int i = 0; i < cnt; i++)
            {
                int kind = types[i] & ~Gdi.PT_CLOSEFIGURE;
                if (kind == Gdi.PT_MOVETO)
                {
                    var p = T(pts[i]);
                    segs.Add(new IrMove(p.X, p.Y));
                }
                else if (kind == Gdi.PT_LINETO)
                {
                    var p = T(pts[i]);
                    segs.Add(new IrLine(p.X, p.Y));
                }
                else if (kind == Gdi.PT_BEZIERTO && i + 2 < cnt)
                {
                    var p1 = T(pts[i]);
                    var p2 = T(pts[i + 1]);
                    var p3 = T(pts[i + 2]);
                    segs.Add(new IrCubic(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y));
                    i += 2;
                }
                if ((types[i] & Gdi.PT_CLOSEFIGURE) != 0)
                    segs.Add(new IrClose());
            }
            return segs.Count > 0 ? segs : null;
        }
        finally
        {
            if (old != IntPtr.Zero) Gdi.SelectObject(hdc, old);
            if (font != IntPtr.Zero) Gdi.DeleteObject(font);
            Gdi.DeleteDC(hdc);
        }
    }
}
