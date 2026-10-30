// グリフ輪郭ポートの TrueType 実装。
// GDI のパスブラケット（BeginPath + ExtTextOutW + GetPath）でグリフ輪郭を取り出し、
// テキスト空間（原点=ベースライン左端、Y下向き、mm、幅係数は未適用）のセグメント列を返す。
// 輪郭の量子は 0.01mm（EMF の論理単位と同一。EMF 経路の座標量子を変えないため）。
using System;
using System.Collections.Generic;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Out.Emf;

public sealed class GdiGlyphOutliner : IGlyphOutliner
{
    public List<IrSegment>? Outline(GlyphRun run)
    {
        if (run.IsShx || run.FontFamily == null)
            return null; // GDI 実装が扱うのは TrueType のみ
        int hL = Math.Max(1, (int)Math.Round(run.FontSizeMm * EmfWriter.LogicalPerMm));
        int n = run.Text.Length;
        if (n == 0) return null;

        // グリフ位置列 → dx（幅係数適用前のテキスト空間、0.01mm 単位）
        int[]? dx = null;
        if (run.GlyphXsMm is { Length: > 1 } g && g.Length == n)
        {
            dx = new int[n];
            int prev = 0;
            for (int i = 1; i < n; i++)
            {
                int q = (int)Math.Round((g[i] - g[0]) / run.WidthFactor * EmfWriter.LogicalPerMm);
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
            font = Gdi.CreateFontW(-hL, 0, 0, 0, Gdi.FW_NORMAL, 0, 0, 0, Gdi.DEFAULT_CHARSET,
                                   0, 0, 0, 0, run.FontFamily);
            old = Gdi.SelectObject(hdc, font);
            Gdi.SetBkMode(hdc, Gdi.TRANSPARENT);
            Gdi.SetTextAlign(hdc, Gdi.TA_BASELINE);

            Gdi.BeginPath(hdc);
            Gdi.ExtTextOutW(hdc, 0, 0, 0, IntPtr.Zero, run.Text, (uint)n, dx);
            Gdi.EndPath(hdc);

            int cnt = Gdi.GetPath(hdc, null, null, 0);
            if (cnt <= 0) { Gdi.AbortPath(hdc); return null; }
            var pts = new POINT[cnt];
            var types = new byte[cnt];
            Gdi.GetPath(hdc, pts, types, cnt);

            // 0.01mm 単位 → テキスト空間 mm（幅係数・回転・平行移動は GlyphPlacement が適用する）
            static (double X, double Y) T(POINT p) =>
                (p.X * EmfWriter.MmPerLogical, p.Y * EmfWriter.MmPerLogical);

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
