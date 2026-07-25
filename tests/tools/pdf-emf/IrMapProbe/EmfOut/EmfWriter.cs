// IrDocument → クラシックGDI EMF の出力バックエンド予行（Phase 0「SVG IR→EMF 接続」）。
//
// 設計書§6.1 の確定事項を実装する:
// - クラシックGDIレコードのみ（EMF+ を出さない）。P/Invoke 直書き
// - rclFrame は IR の物理寸法（mm）から 0.01mm 単位で直接算出（FR-6）
// - クリップ（clipPath）は EMF レコードとして出力せず、ジオメトリへ適用（事前クリップ）
//   してから出力する。文字と交差するクリップは輪郭化し、非可逆処理として診断ログに記録
// - 透過（ca/CA<1）はクラシックGDIに対応概念がないため、白背景との事前合成で不透明化し、
//   診断ログに記録する（§2.1「劣化の判断は出力バックエンドに委ねる」の初適用）
//
// 座標系: MM_ANISOTROPIC、論理単位 0.01mm（rclFrame と同じ量子）、原点は viewBox 左上。
// IR の Y下向きと GDI の Y下向きが一致するため反転は不要。
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using CadPeta.Ir;

namespace CadPeta.EmfOut;

public static class EmfWriter
{
    static int L(double mm) => (int)Math.Round(mm * 100);
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static IrRgb MixWhite(IrRgb c, double a) =>
        new(c.R * a + (1 - a), c.G * a + (1 - a), c.B * a + (1 - a));

    static uint Col(IrRgb c) => Gdi.Rgb(
        (byte)Math.Clamp((int)Math.Round(c.R * 255), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.G * 255), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.B * 255), 0, 255));

    public static void Write(IrDocument ir, string path, IrDiagnostics diag)
    {
        path = Path.GetFullPath(path);
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        // ---------- クリップ定義の矩形化（軸平行矩形のみ対応、他は想定外として記録） ----------
        var clipRects = new Dictionary<string, GeoClip.RectMm>();
        foreach (var c in ir.ClipDefs)
        {
            if (GeoClip.IsAxisRect(c.Segments, out var r))
                clipRects[c.Id] = r;
            else
            {
                var b = GeoClip.Bbox(c.Segments);
                clipRects[c.Id] = new GeoClip.RectMm(b[0], b[1], b[2], b[3]);
                diag.Add("想定外: 非矩形クリップ（bbox矩形で近似、要写像規則追加）", $"clip id={c.Id}");
            }
        }
        // クロップ範囲（viewBox）自体も事前クリップの矩形として常に効かせる
        var page = new GeoClip.RectMm(ir.ViewMinXMm, ir.ViewMinYMm,
                                      ir.ViewMinXMm + ir.WidthMm, ir.ViewMinYMm + ir.HeightMm);
        GeoClip.RectMm? EffClip(string? clipId)
        {
            if (clipId == null) return page;
            return clipRects.TryGetValue(clipId, out var cr)
                ? GeoClip.RectMm.Intersect(page, cr) : page;
        }

        // ---------- メタファイルDCの準備 ----------
        IntPtr hdcRef = Gdi.GetDC(IntPtr.Zero);

        // デバイス解像度は実画面ではなく 96dpi に固定する（＋出力後にヘッダの szl を
        // 96dpi 比へパッチする）。Office の貼り付けサイズは実測で
        // 「rclFrame(mm) × ヘッダszl比(px/mm) ÷ 96dpi」であり、ヘッダの実解像度を無視して
        // 96dpi を仮定するため、記録デバイスを 96dpi に合わせないと実寸がずれる
        // （実画面108.9dpiで +13.5% を実測。検証記録_EMF接続.md §4）
        const double PxPerMm = 96.0 / 25.4;

        var frame = new RECT { L = 0, T = 0, R = L(ir.WidthMm), B = L(ir.HeightMm) };
        IntPtr hdc = Gdi.CreateEnhMetaFileW(hdcRef, path, ref frame,
            "CADPeta EmfOut\0SVG IR→クラシックGDI予行\0\0");
        if (hdc == IntPtr.Zero)
        {
            Gdi.ReleaseDC(IntPtr.Zero, hdcRef);
            throw new InvalidOperationException($"CreateEnhMetaFile 失敗: {path}");
        }

        // 論理 0.01mm → 96dpi デバイスpx の写像。viewport 丸めの寸法誤差は全幅で最大0.5px
        //（96dpi で 0.13mm、比率 0.03% 以下）。貼り付けサイズは rclFrame 起点のため影響しない。
        // クロップ原点（viewBox 左上）は SETWINDOWORGEX を使わず座標へ焼き込む——Office の
        // 「図形に変換」は窓の伸縮（EXT）を座標へ反映する一方、原点シフト（ORG）を無視するため、
        // ORG を使うとクロップEMFの分解後に全図形が原点分ずれて崩壊する（利用者GUI実測、
        // 検証記録_EMF接続.md §6）
        Gdi.SetMapMode(hdc, Gdi.MM_ANISOTROPIC);
        Gdi.SetWindowExtEx(hdc, L(ir.WidthMm), L(ir.HeightMm), IntPtr.Zero);
        Gdi.SetViewportExtEx(hdc, (int)Math.Round(ir.WidthMm * PxPerMm),
                                  (int)Math.Round(ir.HeightMm * PxPerMm), IntPtr.Zero);
        Gdi.SetBkMode(hdc, Gdi.TRANSPARENT);
        Gdi.SetTextAlign(hdc, Gdi.TA_BASELINE);
        Gdi.SetStretchBltMode(hdc, Gdi.COLORONCOLOR);

        // 原点焼き込み済みの座標変換（0.01mm 論理単位）
        double ox = ir.ViewMinXMm, oy = ir.ViewMinYMm;
        int LX(double mm) => (int)Math.Round((mm - ox) * 100);
        int LY(double mm) => (int)Math.Round((mm - oy) * 100);

        // ---------- GDIオブジェクトのキャッシュと選択状態 ----------
        var pens = new Dictionary<(int w, uint c), IntPtr>();
        var brushes = new Dictionary<uint, IntPtr>();
        var fonts = new Dictionary<(string f, int h, int esc, int w), IntPtr>();
        var aveWidths = new Dictionary<(string f, int h), int>();
        IntPtr hdcMeasure = Gdi.CreateCompatibleDC(IntPtr.Zero);
        IntPtr curPen = IntPtr.Zero, curBrush = IntPtr.Zero, curFont = IntPtr.Zero;
        uint curTextColor = 0xFFFFFFFF;
        int curFillMode = 0;

        void SelPen(IntPtr h) { if (h != curPen) { Gdi.SelectObject(hdc, h); curPen = h; } }
        void SelBrush(IntPtr h) { if (h != curBrush) { Gdi.SelectObject(hdc, h); curBrush = h; } }
        void SelFont(IntPtr h) { if (h != curFont) { Gdi.SelectObject(hdc, h); curFont = h; } }

        IntPtr Pen(double mm, IrRgb c)
        {
            var key = (Math.Max(1, L(mm)), Col(c));
            if (!pens.TryGetValue(key, out var h))
                pens[key] = h = Gdi.CreatePen(Gdi.PS_SOLID, key.Item1, key.Item2);
            return h;
        }
        IntPtr Brush(IrRgb c)
        {
            uint col = Col(c);
            if (!brushes.TryGetValue(col, out var h))
                brushes[col] = h = Gdi.CreateSolidBrush(col);
            return h;
        }
        IntPtr Font(string family, int hL, int esc10, int widthL)
        {
            var key = (family, hL, esc10, widthL);
            if (!fonts.TryGetValue(key, out var h))
                fonts[key] = h = Gdi.CreateFontW(-hL, widthL, esc10, esc10, 400, 0, 0, 0,
                    Gdi.DEFAULT_CHARSET, 0, 0, 0, 0, family);
            return h;
        }
        int AveWidth(string family, int hL)
        {
            var key = (family, hL);
            if (aveWidths.TryGetValue(key, out var w)) return w;
            IntPtr f = Gdi.CreateFontW(-hL, 0, 0, 0, 400, 0, 0, 0, Gdi.DEFAULT_CHARSET, 0, 0, 0, 0, family);
            IntPtr old = Gdi.SelectObject(hdcMeasure, f);
            Gdi.GetTextMetricsW(hdcMeasure, out var tm);
            Gdi.SelectObject(hdcMeasure, old);
            Gdi.DeleteObject(f);
            return aveWidths[key] = Math.Max(1, tm.AveCharWidth);
        }
        void FillMode(bool evenOdd)
        {
            int m = evenOdd ? Gdi.ALTERNATE : Gdi.WINDING;
            if (m != curFillMode) { Gdi.SetPolyFillMode(hdc, m); curFillMode = m; }
        }
        void TextColor(IrRgb c)
        {
            uint col = Col(c);
            if (col != curTextColor) { Gdi.SetTextColor(hdc, col); curTextColor = col; }
        }

        // ---------- セグメント列の出力（パスブラケット内/外 兼用） ----------
        // ブラケット外（直接描画）は始点込みの絶対座標レコード（POLYLINE16/POLYBEZIER16）で書く。
        // "To" 系（POLYLINETO16 等）は現在位置から続くレコードで、GDI の描画では正しいが、
        // Office の「図形に変換」が現在位置の引き継ぎを処理せず終点だけが飛んで図が崩壊する
        // （利用者GUI実測。分解実績のある classic_clip は絶対座標レコードのみで構成されていた）。
        // ブラケット内はパス構築なので "To" 系のままでよい（FILLPATH 単位で変換される）
        void EmitSegments(List<IrSegment> segs, bool inPath)
        {
            double sx = 0, sy = 0, cx = 0, cy = 0;
            var lines = new List<POINT>();   // 直接描画: 先頭=始点の絶対座標列
            var bez = new List<POINT>();     // 直接描画: 先頭=始点、以降3点1組
            POINT P(double x, double y) => new() { X = LX(x), Y = LY(y) };
            void FlushLines()
            {
                if (lines.Count == 0) return;
                if (inPath) Gdi.PolylineTo(hdc, lines.ToArray(), (uint)lines.Count);
                else if (lines.Count >= 2) Gdi.Polyline(hdc, lines.ToArray(), lines.Count);
                lines.Clear();
            }
            void FlushBez()
            {
                if (bez.Count == 0) return;
                if (inPath) Gdi.PolyBezierTo(hdc, bez.ToArray(), (uint)bez.Count);
                else if (bez.Count >= 4) Gdi.PolyBezier(hdc, bez.ToArray(), (uint)bez.Count);
                bez.Clear();
            }
            foreach (var s in segs)
                switch (s)
                {
                    case IrMove m:
                        FlushLines(); FlushBez();
                        if (inPath) Gdi.MoveToEx(hdc, LX(m.X), LY(m.Y), IntPtr.Zero);
                        sx = cx = m.X; sy = cy = m.Y;
                        break;
                    case IrLine l:
                        FlushBez();
                        if (!inPath && lines.Count == 0) lines.Add(P(cx, cy));
                        lines.Add(P(l.X, l.Y));
                        cx = l.X; cy = l.Y;
                        break;
                    case IrCubic c:
                        FlushLines();
                        if (!inPath && bez.Count == 0) bez.Add(P(cx, cy));
                        bez.Add(P(c.X1, c.Y1));
                        bez.Add(P(c.X2, c.Y2));
                        bez.Add(P(c.X, c.Y));
                        cx = c.X; cy = c.Y;
                        break;
                    case IrClose:
                        FlushBez();
                        if (inPath)
                        {
                            FlushLines();
                            Gdi.CloseFigure(hdc);
                        }
                        else
                        {
                            if (lines.Count == 0) lines.Add(P(cx, cy));
                            lines.Add(P(sx, sy));
                        }
                        cx = sx; cy = sy;
                        break;
                }
            FlushLines(); FlushBez();
        }

        void FillWithPath(List<IrSegment> segs, IrRgb fill, bool evenOdd)
        {
            SelBrush(Brush(fill));
            SelPen(Gdi.GetStockObject(Gdi.NULL_PEN));
            FillMode(evenOdd);
            Gdi.BeginPath(hdc);
            EmitSegments(segs, inPath: true);
            Gdi.EndPath(hdc);
            Gdi.FillPath(hdc);
        }

        // ---------- ノード出力（z順） ----------
        int seq = -1, nPaths = 0, nTexts = 0, nOutlined = 0, nImages = 0, nDropped = 0;
        try
        {
            foreach (var node in ir.Nodes)
            {
                seq++;
                switch (node)
                {
                    case IrPath p:
                    {
                        var eff = EffClip(p.ClipId);
                        if (eff == null)
                        {
                            diag.Add("事前クリップ: クリップ外ノードの間引き", $"path seq={seq}");
                            nDropped++;
                            break;
                        }
                        var rect = eff.Value;

                        IrRgb? stroke = p.Stroke, fill = p.Fill;
                        if (stroke != null && p.StrokeOpacity < 1.0)
                        {
                            stroke = MixWhite(stroke.Value, p.StrokeOpacity);
                            diag.Add("透過の不透明化（白背景との事前合成）",
                                     $"path seq={seq} stroke CA={F(p.StrokeOpacity)}");
                        }
                        if (fill != null && p.FillOpacity < 1.0)
                        {
                            fill = MixWhite(fill.Value, p.FillOpacity);
                            diag.Add("透過の不透明化（白背景との事前合成）",
                                     $"path seq={seq} fill ca={F(p.FillOpacity)}");
                        }

                        List<IrSegment>? fillSegs = null, strokeSegs = null;
                        bool mod = false, flat = false;
                        if (fill != null)
                        {
                            fillSegs = GeoClip.ClipFill(p.Segments, rect, out var m1, out var f1);
                            mod |= m1; flat |= f1;
                        }
                        if (stroke != null)
                        {
                            strokeSegs = GeoClip.ClipStroke(p.Segments, rect, out var m2, out var f2);
                            mod |= m2; flat |= f2;
                        }
                        if (fillSegs == null && strokeSegs == null)
                        {
                            diag.Add("事前クリップ: クリップ外ノードの間引き", $"path seq={seq}");
                            nDropped++;
                            break;
                        }
                        if (mod)
                            diag.Add("事前クリップ: ジオメトリ切断", $"path seq={seq}");
                        if (flat)
                            diag.Add("事前クリップ: 境界交差曲線の折れ線化", $"path seq={seq}");

                        if (fill != null && stroke != null && fillSegs != null && strokeSegs != null && !mod)
                        {
                            // 塗り+線で無切断なら1ブラケットで両方（レコード最少）
                            SelBrush(Brush(fill.Value));
                            SelPen(Pen(p.StrokeWidthMm, stroke.Value));
                            FillMode(p.FillEvenOdd);
                            Gdi.BeginPath(hdc);
                            EmitSegments(fillSegs, inPath: true);
                            Gdi.EndPath(hdc);
                            Gdi.StrokeAndFillPath(hdc);
                        }
                        else
                        {
                            if (fill != null && fillSegs != null)
                                FillWithPath(fillSegs, fill.Value, p.FillEvenOdd);
                            if (stroke != null && strokeSegs != null)
                            {
                                SelPen(Pen(p.StrokeWidthMm, stroke.Value));
                                EmitSegments(strokeSegs, inPath: false);
                            }
                        }
                        nPaths++;
                        break;
                    }

                    case IrText t:
                    {
                        var eff = EffClip(t.ClipId);
                        if (eff == null)
                        {
                            diag.Add("事前クリップ: クリップ外ノードの間引き", $"text seq={seq} '{t.Value}'");
                            nDropped++;
                            break;
                        }
                        var rect = eff.Value;
                        var fill = t.Fill ?? new IrRgb(0, 0, 0);
                        if (t.FillOpacity < 1.0)
                        {
                            fill = MixWhite(fill, t.FillOpacity);
                            diag.Add("透過の不透明化（白背景との事前合成）",
                                     $"text seq={seq} ca={F(t.FillOpacity)}");
                        }

                        var quad = TextQuadBbox(t);
                        if (!rect.OverlapsBbox(quad))
                        {
                            diag.Add("事前クリップ: クリップ外ノードの間引き", $"text seq={seq} '{t.Value}'");
                            nDropped++;
                            break;
                        }
                        if (!rect.ContainsBbox(quad))
                        {
                            // クリップ境界と交差する文字は輪郭化する（非可逆、設計書§6.1・§7）
                            var outline = TextOutline.Outline(t);
                            diag.Add("事前クリップ: 境界交差文字の輪郭化（文字編集性の喪失）",
                                     $"text seq={seq} '{t.Value}' @({F(t.XMm)},{F(t.YMm)})mm");
                            if (outline != null)
                            {
                                var clipped = GeoClip.ClipFill(outline, rect, out _, out _);
                                if (clipped != null)
                                    FillWithPath(clipped, fill, evenOdd: false);
                            }
                            nOutlined++;
                            break;
                        }

                        int hL = Math.Max(1, L(t.FontSizeMm));
                        int esc10 = (int)Math.Round(-t.RotationDeg * 10);
                        int widthL = Math.Abs(t.WidthFactor - 1.0) > 0.001
                            ? Math.Max(1, (int)Math.Round(AveWidth(t.FontFamily, hL) * t.WidthFactor))
                            : 0;
                        SelFont(Font(t.FontFamily, hL, esc10, widthL));
                        TextColor(fill);

                        int n = t.Value.Length;
                        int[]? dx = null;
                        if (t.GlyphXsMm is { Length: > 1 } g && g.Length == n)
                        {
                            // グリフ位置列 → 文字送り（ベースライン方向の実距離、0.01mm 単位）
                            dx = new int[n];
                            int prev = 0;
                            for (int i = 1; i < n; i++)
                            {
                                int q = (int)Math.Round((g[i] - g[0]) * 100);
                                dx[i - 1] = q - prev;
                                prev = q;
                            }
                            dx[n - 1] = hL;
                        }
                        Gdi.ExtTextOutW(hdc, LX(t.XMm), LY(t.YMm), 0, IntPtr.Zero, t.Value, (uint)n, dx);
                        nTexts++;
                        break;
                    }

                    case IrImage im:
                    {
                        var eff = EffClip(im.ClipId);
                        var imgRect = new GeoClip.RectMm(im.XMm, im.YMm,
                                                         im.XMm + im.WidthMm, im.YMm + im.HeightMm);
                        var inter = eff != null ? GeoClip.RectMm.Intersect(eff.Value, imgRect) : null;
                        if (inter == null)
                        {
                            diag.Add("事前クリップ: クリップ外ノードの間引き", $"image seq={seq}");
                            nDropped++;
                            break;
                        }
                        EmitImage(hdc, im, inter.Value, ox, oy, seq, diag);
                        nImages++;
                        break;
                    }
                }
            }
        }
        finally
        {
            IntPtr hemf = Gdi.CloseEnhMetaFile(hdc);
            if (hemf != IntPtr.Zero) Gdi.DeleteEnhMetaFile(hemf);
            foreach (var h in pens.Values) Gdi.DeleteObject(h);
            foreach (var h in brushes.Values) Gdi.DeleteObject(h);
            foreach (var h in fonts.Values) Gdi.DeleteObject(h);
            Gdi.DeleteDC(hdcMeasure);
            Gdi.ReleaseDC(IntPtr.Zero, hdcRef);
        }

        PatchHeaderTo96Dpi(path);

        Console.WriteLine($"EMF: paths={nPaths} texts={nTexts} outlined={nOutlined} " +
                          $"images={nImages} dropped={nDropped} " +
                          $"frame={frame.R / 100.0:F2}x{frame.B / 100.0:F2}mm");
    }

    /// <summary>ヘッダの szlDevice / szlMillimeters / szlMicrometers を 96dpi 比へ書き換える。
    /// CreateEnhMetaFile は参照DC（実画面）の解像度を書き込むが、Office は貼り付けサイズの
    /// 算出でこの比を使いつつデバイスを 96dpi と仮定するため、比を 96dpi に固定して
    /// 恒等にする。記録本体（viewport ext）も同じ 96dpi 比で書いてあり、整合する。</summary>
    static void PatchHeaderTo96Dpi(string path)
    {
        var b = File.ReadAllBytes(path);
        int nSize = BitConverter.ToInt32(b, 4);
        void W(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
        W(72, 9600); W(76, 9600);   // szlDevice (px)
        W(80, 2540); W(84, 2540);   // szlMillimeters → 9600/2540 = 96dpi ちょうど
        if (nSize >= 108) { W(100, 2540000); W(104, 2540000); }  // szlMicrometers
        File.WriteAllBytes(path, b);
    }

    /// <summary>文字ランの保守的な外接矩形（クリップ交差判定用）。</summary>
    static double[] TextQuadBbox(IrText t)
    {
        double span = t.GlyphXsMm is { Length: > 1 } g
            ? (g.Max() - g.Min()) + t.FontSizeMm * Math.Max(1.0, t.WidthFactor)
            : t.Value.Length * t.FontSizeMm * Math.Max(1.0, t.WidthFactor);
        double th = t.RotationDeg * Math.PI / 180.0;
        double ux = Math.Cos(th), uy = Math.Sin(th);   // ベースライン方向
        double axv = Math.Sin(th), ayv = -Math.Cos(th); // アセント方向（-Y を回転）
        double asc = t.FontSizeMm * 1.1, desc = t.FontSizeMm * 0.35;
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

    /// <summary>IrImage を StretchDIBits（クラシックレコード）で出力する。inter はクリップ後の可視範囲。
    /// ox/oy は viewBox 原点（座標焼き込み用）。</summary>
    static void EmitImage(IntPtr hdc, IrImage im, GeoClip.RectMm inter, double ox, double oy,
                          int seq, IrDiagnostics diag)
    {
        using var src = new Bitmap(new MemoryStream(im.PngBytes));
        Bitmap bmp = src;
        double x0 = im.XMm, y0 = im.YMm, w = im.WidthMm, h = im.HeightMm;
        bool cropped = inter.X0 > x0 + 1e-9 || inter.Y0 > y0 + 1e-9
                    || inter.X1 < x0 + w - 1e-9 || inter.Y1 < y0 + h - 1e-9;
        if (cropped)
        {
            int sx = Math.Clamp((int)Math.Floor((inter.X0 - x0) / w * src.Width), 0, src.Width - 1);
            int sy = Math.Clamp((int)Math.Floor((inter.Y0 - y0) / h * src.Height), 0, src.Height - 1);
            int sw = Math.Clamp((int)Math.Ceiling((inter.X1 - inter.X0) / w * src.Width), 1, src.Width - sx);
            int sh = Math.Clamp((int)Math.Ceiling((inter.Y1 - inter.Y0) / h * src.Height), 1, src.Height - sy);
            bmp = src.Clone(new Rectangle(sx, sy, sw, sh), PixelFormat.Format32bppArgb);
            // 出力先はピクセル境界へスナップした mm 範囲（切り出しに伴う半px未満の再配置）
            x0 = im.XMm + (double)sx / src.Width * w;
            y0 = im.YMm + (double)sy / src.Height * h;
            w = (double)sw / src.Width * im.WidthMm;
            h = (double)sh / src.Height * im.HeightMm;
            diag.Add("事前クリップ: 画像の切り出し", $"image seq={seq} src=({sx},{sy}) {sw}x{sh}px");
        }

        try
        {
            int bw = bmp.Width, bh = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, bw, bh),
                                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bits = new byte[bw * bh * 4];
            var row = new byte[bw * 4];
            bool hadAlpha = false;
            for (int y = 0; y < bh; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bw * 4);
                int dst = (bh - 1 - y) * bw * 4;  // ボトムアップDIB
                for (int x = 0; x < bw; x++)
                {
                    int a = row[x * 4 + 3];
                    if (a < 255)
                    {
                        hadAlpha = true;
                        // 白背景との事前合成（クラシックGDIはアルファを持てない）
                        bits[dst + x * 4 + 0] = (byte)((row[x * 4 + 0] * a + 255 * (255 - a)) / 255);
                        bits[dst + x * 4 + 1] = (byte)((row[x * 4 + 1] * a + 255 * (255 - a)) / 255);
                        bits[dst + x * 4 + 2] = (byte)((row[x * 4 + 2] * a + 255 * (255 - a)) / 255);
                    }
                    else
                    {
                        bits[dst + x * 4 + 0] = row[x * 4 + 0];
                        bits[dst + x * 4 + 1] = row[x * 4 + 1];
                        bits[dst + x * 4 + 2] = row[x * 4 + 2];
                    }
                    bits[dst + x * 4 + 3] = 255;
                }
            }
            bmp.UnlockBits(data);
            if (hadAlpha)
                diag.Add("透過の不透明化（白背景との事前合成）", $"image seq={seq} SMask由来アルファ");

            var bmi = new BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = bw,
                Height = bh,   // 正=ボトムアップ
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };
            Gdi.StretchDIBits(hdc, L(x0 - ox), L(y0 - oy), L(w), L(h),
                              0, 0, bw, bh, bits, ref bmi, Gdi.DIB_RGB_COLORS, Gdi.SRCCOPY);
        }
        finally
        {
            if (!ReferenceEquals(bmp, src)) bmp.Dispose();
        }
    }
}
