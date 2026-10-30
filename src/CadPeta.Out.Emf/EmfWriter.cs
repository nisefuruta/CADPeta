// 出力アダプタ: IrDocument → クラシックGDI EMF。
//
// 確定事項を実装する:
// - クラシックGDIレコードのみ（EMF+ を出さない）。P/Invoke 直書き
// - rclFrame は IR の物理寸法（mm）から 0.01mm 単位で直接算出し、実寸を保証する
// - クリップ（clipPath）は EMF レコードとして出力せず、ジオメトリへ適用（事前クリップ）
//   してから出力する。文字と交差するクリップは輪郭化し、非可逆処理として診断ログに記録
// - 透過（ca/CA<1）はクラシックGDIに対応概念がないため、指定背景色との事前合成で不透明化し、
//   診断ログに記録する（劣化の判断は出力バックエンドに委ねる方針の適用）
// - パスブラケット外の描画は始点込み絶対座標レコード（POLYLINE16/POLYBEZIER16等）のみ。
//   現在位置依存の "To" 系・SETWINDOWORGEX は使わない（絶対座標レコード規約）
//
// 座標系: MM_ANISOTROPIC、論理単位 0.01mm（rclFrame と同じ量子）、原点は viewBox 左上。
// IR の Y下向きと GDI の Y下向きが一致するため反転は不要。
using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Out.Emf;

public sealed class EmfWriter : IIrOutput
{
    readonly TextWriter? _log;
    readonly TextWriter? _verbose;
    readonly IrRgb _background;

    /// <param name="log">情報メッセージの出力先（null で沈黙）。診断ログとは別の進行報告。</param>
    public EmfWriter(TextWriter? log = null, TextWriter? verbose = null)
        : this(new IrRgb(1, 1, 1), log, verbose)
    {
    }

    public EmfWriter(IrRgb background, TextWriter? log = null, TextWriter? verbose = null)
    {
        if (!Finite01(background.R) || !Finite01(background.G) || !Finite01(background.B))
            throw new ArgumentOutOfRangeException(nameof(background), "EMF合成下地色は各成分0..1の有限値が必要");
        _background = background;
        _log = log;
        _verbose = verbose ?? log;
    }

    /// <summary>論理単位/mm。EMF の rclFrame と同じ 0.01mm を論理単位に採る
    /// （0.01mm 量子で物理寸法を実寸精度で制御する）。</summary>
    internal const int LogicalPerMm = 100;
    /// <summary>mm/論理単位（1.0/LogicalPerMm。逆変換用）。</summary>
    internal const double MmPerLogical = 1.0 / LogicalPerMm;

    // 幅係数（Tz由来）が標準とみなせる帯。これ未満の差で幅指定フォントを作らない
    const double WidthFactorTol = 0.001;

    static int L(double mm) => (int)Math.Round(mm * LogicalPerMm);

    // Math.Clamp は net48 に無いため Min/Max で等価に書く（3系マルチターゲット共通）
    static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    IrRgb MixBackground(IrRgb c, double a) =>
        new(c.R * a + _background.R * (1 - a),
            c.G * a + _background.G * (1 - a),
            c.B * a + _background.B * (1 - a));

    /// <summary>透過を下地色と合成して不透明化したときの診断カテゴリの接頭辞（言語不変）。
    /// PetaRunner がこの接頭辞で件数を数え、透過表現が SVG 出力にしか残らない旨を
    /// コマンドラインへ案内する（両者の一致は単体テストで固定）。</summary>
    public const string OpacityFlattenedDiagPrefix = "透過の不透明化";

    string OpacityFlattenedCategory => $"{OpacityFlattenedDiagPrefix}（合成下地色={_background.ToHex()}）";

    static bool Finite01(double value) => Numbers.IsFinite(value) && value >= 0 && value <= 1;

    static uint Col(IrRgb c) => Gdi.Rgb(IrRgb.ToByte(c.R), IrRgb.ToByte(c.G), IrRgb.ToByte(c.B));

    public void Write(IrDocument ir, string outputPath, IrDiagnostics diag)
    {
        // EMF はグループ（変換行列付き g）の概念を持たないため、変換行列を座標へ焼き込んだ
        // フラットな文書に展開してから出力する。グループが無い文書はそのまま返るため無変更
        ir = CadPeta.Transforms.GroupFlatten.Apply(ir, diag);

        string path = Path.GetFullPath(outputPath);
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        // ---------- クリップ定義の矩形化（軸平行矩形のみ対応、他は想定外として記録） ----------
        var clipRects = new Dictionary<string, RectMm>();
        foreach (var c in ir.ClipDefs)
        {
            if (GeoClip.IsAxisRect(c.Segments, out var r))
                clipRects[c.Id] = r;
            else
            {
                var b = GeoClip.Bbox(c.Segments);
                clipRects[c.Id] = new RectMm(b[0], b[1], b[2], b[3]);
                diag.Add("想定外: 非矩形クリップ（bbox矩形で近似、要写像規則追加）", $"clip id={c.Id}");
            }
        }
        // クロップ範囲（viewBox）自体も事前クリップの矩形として常に効かせる
        var page = new RectMm(ir.ViewMinXMm, ir.ViewMinYMm,
                              ir.ViewMinXMm + ir.WidthMm, ir.ViewMinYMm + ir.HeightMm);
        RectMm? EffClip(string? clipId)
        {
            if (clipId == null) return page;
            return clipRects.TryGetValue(clipId, out var cr)
                ? RectMm.Intersect(page, cr) : page;
        }

        // ---------- メタファイルDCの準備 ----------
        IntPtr hdcRef = Gdi.GetDC(IntPtr.Zero);

        // デバイス解像度は実画面ではなく 96dpi に固定する（＋出力後にヘッダの szl を
        // 96dpi 比へパッチする）。Office の貼り付けサイズは実測で
        // 「rclFrame(mm) × ヘッダszl比(px/mm) ÷ 96dpi」であり、ヘッダの実解像度を無視して
        // 96dpi を仮定するため、記録デバイスを 96dpi に合わせないと実寸がずれる
        // （実画面108.9dpiで +13.5% を実測）
        const double OfficeAssumedDpi = 96.0;  // Office が EMF デバイスに仮定する固定 dpi（上記実測）
        const double PxPerMm = OfficeAssumedDpi / Units.MmPerInch;

        var frame = new RECT { L = 0, T = 0, R = L(ir.WidthMm), B = L(ir.HeightMm) };
        IntPtr hdc = Gdi.CreateEnhMetaFileW(hdcRef, path, ref frame,
            "CADPeta EmfOut\0SVG IR→クラシックGDI\0\0");
        if (hdc == IntPtr.Zero)
        {
            Gdi.ReleaseDC(IntPtr.Zero, hdcRef);
            throw new InvalidOperationException($"CreateEnhMetaFile 失敗: {path}");
        }

        // 論理 0.01mm → 96dpi デバイスpx の写像。viewport 丸めの寸法誤差は全幅で最大0.5px
        //（96dpi で 0.13mm、比率 0.03% 以下）。貼り付けサイズは rclFrame 起点のため影響しない。
        // クロップ原点（viewBox 左上）は SETWINDOWORGEX を使わず座標へ焼き込む——Office の
        // 「図形に変換」は窓の伸縮（EXT）を座標へ反映する一方、原点シフト（ORG）を無視するため、
        // ORG を使うとクロップEMFの分解後に全図形が原点分ずれて崩壊する（利用者GUI実測）
        Gdi.SetMapMode(hdc, Gdi.MM_ANISOTROPIC);
        Gdi.SetWindowExtEx(hdc, L(ir.WidthMm), L(ir.HeightMm), IntPtr.Zero);
        Gdi.SetViewportExtEx(hdc, (int)Math.Round(ir.WidthMm * PxPerMm),
                                  (int)Math.Round(ir.HeightMm * PxPerMm), IntPtr.Zero);
        Gdi.SetBkMode(hdc, Gdi.TRANSPARENT);
        Gdi.SetTextAlign(hdc, Gdi.TA_BASELINE);
        Gdi.SetStretchBltMode(hdc, Gdi.COLORONCOLOR);

        // 原点焼き込み済みの座標変換（0.01mm 論理単位）
        double ox = ir.ViewMinXMm, oy = ir.ViewMinYMm;
        int LX(double mm) => (int)Math.Round((mm - ox) * LogicalPerMm);
        int LY(double mm) => (int)Math.Round((mm - oy) * LogicalPerMm);

        // ---------- GDIオブジェクトのキャッシュと選択状態 ----------
        var pens = new Dictionary<(int w, uint c), IntPtr>();
        var brushes = new Dictionary<uint, IntPtr>();
        var fonts = new Dictionary<(string f, int h, int esc, int w), IntPtr>();
        var aveWidths = new Dictionary<(string f, int h), int>();
        IntPtr hdcMeasure = Gdi.CreateCompatibleDC(IntPtr.Zero);
        IntPtr curPen = IntPtr.Zero, curBrush = IntPtr.Zero, curFont = IntPtr.Zero;
        uint curTextColor = 0xFFFFFFFF; // COLORREF は 0x00BBGGRR 形式のため上位バイト≠0 は「未設定」の番兵
        int curFillMode = 0;

        // 選択中のオブジェクトと同じなら SelectObject を省く（ペン・ブラシ・フォントで共通）
        void Select(ref IntPtr current, IntPtr h) { if (h != current) { Gdi.SelectObject(hdc, h); current = h; } }
        void SelPen(IntPtr h) => Select(ref curPen, h);
        void SelBrush(IntPtr h) => Select(ref curBrush, h);
        void SelFont(IntPtr h) => Select(ref curFont, h);

        IntPtr Pen(double mm, IrRgb c)
        {
            // 幅0のペンは GDI でデバイス最細線（1デバイスpx）扱いになるため、最低1論理単位
            //（0.01mm）を保証して mm 指定の意図を保つ
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
                fonts[key] = h = Gdi.CreateFontW(-hL, widthL, esc10, esc10, Gdi.FW_NORMAL, 0, 0, 0,
                    Gdi.DEFAULT_CHARSET, 0, 0, 0, 0, family);
            return h;
        }
        int AveWidth(string family, int hL)
        {
            var key = (family, hL);
            if (aveWidths.TryGetValue(key, out var w)) return w;
            IntPtr f = Gdi.CreateFontW(-hL, 0, 0, 0, Gdi.FW_NORMAL, 0, 0, 0, Gdi.DEFAULT_CHARSET, 0, 0, 0, 0, family);
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
        // （利用者GUI実測。分解実績のある構成は絶対座標レコードのみだった）。
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
                            stroke = MixBackground(stroke.Value, p.StrokeOpacity);
                            diag.Add(OpacityFlattenedCategory,
                                     $"path seq={seq} stroke CA={F(p.StrokeOpacity)}");
                        }
                        if (fill != null && p.FillOpacity < 1.0)
                        {
                            fill = MixBackground(fill.Value, p.FillOpacity);
                            diag.Add(OpacityFlattenedCategory,
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

                        bool dashed = p.StrokeDashMm is { Length: > 0 };
                        if (fill != null && stroke != null && fillSegs != null && strokeSegs != null
                            && !mod && !dashed)
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
                                if (dashed)
                                {
                                    // 破線はセグメント群へ展開して実線ペンで書く。破線の幾何ペン
                                    //（EXTCREATEPEN）は Office の「図形に変換」を全面的に失敗させる
                                    //（TESTCHART2 実測: 破線入り=変換不可、破線なし=正常分解）。
                                    // プロット経由（PDF印刷経路）も破線を短セグメント群として出力しており、
                                    // 見た目・分解品質はそれと同等になる
                                    var dashes = DashExpand.Apply(strokeSegs, p.StrokeDashMm!, p.StrokeDashOffsetMm);
                                    foreach (var piece in dashes)
                                        EmitSegments(piece, inPath: false);
                                    diag.Add("EMF: 破線のセグメント展開（図形変換互換のため）",
                                             $"path seq={seq} {dashes.Count}片");
                                }
                                else
                                {
                                    EmitSegments(strokeSegs, inPath: false);
                                }
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
                            fill = MixBackground(fill, t.FillOpacity);
                            diag.Add(OpacityFlattenedCategory,
                                     $"text seq={seq} ca={F(t.FillOpacity)}");
                        }

                        var quad = IrBounds.TextQuadBbox(t);
                        if (!rect.OverlapsBbox(quad))
                        {
                            diag.Add("事前クリップ: クリップ外ノードの間引き", $"text seq={seq} '{t.Value}'");
                            nDropped++;
                            break;
                        }
                        if (!rect.ContainsBbox(quad))
                        {
                            // クリップ境界と交差する文字は輪郭化する（非可逆処理として診断ログに記録する）
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
                        // CreateFont の escapement/orientation は 0.1度単位（Win32仕様）。
                        // IR の時計回り正 → GDI の反時計回り正のため符号反転
                        int esc10 = (int)Math.Round(-t.RotationDeg * Gdi.FONT_ANGLE_UNITS_PER_DEG);
                        int widthL = Math.Abs(t.WidthFactor - 1.0) > WidthFactorTol
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
                                int q = (int)Math.Round((g[i] - g[0]) * LogicalPerMm);
                                dx[i - 1] = q - prev;
                                prev = q;
                            }
                            dx[n - 1] = hL;
                        }
                        // dx=null（自然送り）でも GDI が自前計算の dx 配列をレコードへ書き込む。
                        // 非決定要素は文字列の DWORD 詰めの未初期化バイトだけで、出力後に
                        // ZeroTextRecordPadding が 0 へ正規化する
                        Gdi.ExtTextOutW(hdc, LX(t.XMm), LY(t.YMm), 0, IntPtr.Zero, t.Value, (uint)n, dx);
                        nTexts++;
                        break;
                    }

                    case IrImage im:
                    {
                        var eff = EffClip(im.ClipId);
                        var imgRect = new RectMm(im.XMm, im.YMm,
                                                 im.XMm + im.WidthMm, im.YMm + im.HeightMm);
                        var inter = eff != null ? RectMm.Intersect(eff.Value, imgRect) : null;
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

        _verbose?.WriteLine($"EMF: paths={nPaths} texts={nTexts} outlined={nOutlined} " +
                        $"images={nImages} dropped={nDropped} " +
                        $"frame={frame.R * MmPerLogical:F2}x{frame.B * MmPerLogical:F2}mm");
    }

    /// <summary>文字レコード（EXTTEXTOUTW）内の文字列と文字送り配列の間にある DWORD 詰めを
    /// 0 に固定する。GDI は奇数文字数の詰めへ未初期化メモリを書くため、同一入力でも EMF が
    /// バイト再現しない（同一図面2回実行の比較で実測）。描画に関与しない領域のため 0 へ正規化する。</summary>
    static void ZeroTextRecordPadding(byte[] bytes)
    {
        // EMREXTTEXTOUTW のフィールド位置（MS-EMF 2.3.5.8。レコード先頭からのバイト位置）
        const int EmrExtTextOutW = 84;
        const int OffNChars = 44, OffOffString = 48, OffOffDx = 72;
        int pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            int type = BitConverter.ToInt32(bytes, pos);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (size <= 0 || pos + size > bytes.Length)
                break;
            if (type == EmrExtTextOutW && pos + OffOffDx + 4 <= bytes.Length)
            {
                int nChars = BitConverter.ToInt32(bytes, pos + OffNChars);
                int offString = BitConverter.ToInt32(bytes, pos + OffOffString);
                int offDx = BitConverter.ToInt32(bytes, pos + OffOffDx);
                int strEnd = offString + 2 * nChars;   // UTF-16 は1文字2バイト
                if (offDx > strEnd && offDx <= size && strEnd > 0)
                    Array.Clear(bytes, pos + strEnd, offDx - strEnd);
            }
            pos += size;
        }
    }

    /// <summary>ヘッダの szlDevice / szlMillimeters / szlMicrometers を 96dpi 比へ書き換える。
    /// CreateEnhMetaFile は参照DC（実画面）の解像度を書き込むが、Office は貼り付けサイズの
    /// 算出でこの比を使いつつデバイスを 96dpi と仮定するため、比を 96dpi に固定して
    /// 恒等にする。記録本体（viewport ext）も同じ 96dpi 比で書いてあり、整合する。</summary>
    static void PatchHeaderTo96Dpi(string path)
    {
        // ENHMETAHEADER のフィールドオフセット（MS-EMF 2.2.9。先頭からのバイト位置）
        const int OffHeaderSize = 4;         // nSize
        const int OffSzlDeviceX = 72, OffSzlDeviceY = 76;             // szlDevice（px）
        const int OffSzlMillimetersX = 80, OffSzlMillimetersY = 84;   // szlMillimeters（mm）
        const int OffSzlMicrometersX = 100, OffSzlMicrometersY = 104; // szlMicrometers（µm）
        const int HeaderSizeWithMicrometers = 108; // szlMicrometers 拡張を含むヘッダ長
        // 仮想デバイス寸法: 9600px / 2540mm（=100インチ）= 96dpi ちょうど
        const int DevicePx = 9600, DeviceMm = 2540, DeviceUm = DeviceMm * 1000;

        var b = File.ReadAllBytes(path);
        int nSize = BitConverter.ToInt32(b, OffHeaderSize);
        void W(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
        W(OffSzlDeviceX, DevicePx); W(OffSzlDeviceY, DevicePx);
        W(OffSzlMillimetersX, DeviceMm); W(OffSzlMillimetersY, DeviceMm);
        if (nSize >= HeaderSizeWithMicrometers)
        {
            W(OffSzlMicrometersX, DeviceUm); W(OffSzlMicrometersY, DeviceUm);
        }
        ZeroTextRecordPadding(b);
        File.WriteAllBytes(path, b);
    }

    /// <summary>IrImage を StretchDIBits（クラシックレコード）で出力する。inter はクリップ後の可視範囲。
    /// ox/oy は viewBox 原点（座標焼き込み用）。</summary>
    void EmitImage(IntPtr hdc, IrImage im, RectMm inter, double ox, double oy,
                   int seq, IrDiagnostics diag)
    {
        using var src = new Bitmap(new MemoryStream(im.PngBytes));
        Bitmap bmp = src;
        const double EpsMm = 1e-9; // 幾何判定の数値誤差吸収（GeoClip.Eps と同値）
        double x0 = im.XMm, y0 = im.YMm, w = im.WidthMm, h = im.HeightMm;
        bool cropped = inter.X0 > x0 + EpsMm || inter.Y0 > y0 + EpsMm
                    || inter.X1 < x0 + w - EpsMm || inter.Y1 < y0 + h - EpsMm;
        if (cropped)
        {
            int sx = Clamp((int)Math.Floor((inter.X0 - x0) / w * src.Width), 0, src.Width - 1);
            int sy = Clamp((int)Math.Floor((inter.Y0 - y0) / h * src.Height), 0, src.Height - 1);
            int sw = Clamp((int)Math.Ceiling((inter.X1 - inter.X0) / w * src.Width), 1, src.Width - sx);
            int sh = Clamp((int)Math.Ceiling((inter.Y1 - inter.Y0) / h * src.Height), 1, src.Height - sy);
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
            var bits = BuildBgrDib(bmp, _background, out var bmi, out bool hadAlpha);
            if (hadAlpha)
                diag.Add(OpacityFlattenedCategory, $"image seq={seq} SMask由来アルファ");
            Gdi.StretchDIBits(hdc, L(x0 - ox), L(y0 - oy), L(w), L(h),
                              0, 0, bw, bh, bits, ref bmi, Gdi.DIB_RGB_COLORS, Gdi.SRCCOPY);
        }
        finally
        {
            if (!ReferenceEquals(bmp, src)) bmp.Dispose();
        }
    }

    /// <summary>DIB スキャンラインを揃える DWORD 境界（Win32 DIB 仕様: 各行は4バイトの倍数）。
    /// 32bpp は 幅×4 で常に境界一致だが、24bpp は行末に0〜3バイトのパディングを要する。</summary>
    internal const int DibRowAlignBytes = 4;
    /// <summary>24bpp BGR の1画素バイト数。アルファは事前合成済みのため未使用の X バイトを持たない。</summary>
    const int BgrBytesPerPixel = 3;

    /// <summary>Bitmap（32bpp ARGB）を 24bpp ボトムアップ BGR の無圧縮DIBバイト列へ詰める。
    /// アルファ&lt;255 の画素は指定背景色と事前合成する（クラシックGDIはアルファを持てない）。
    /// 出力は下位バイトから B,G,R。旧 32bpp BGRX の未使用 X バイトを落とすだけで RGB はビット不変＝
    /// 画素無損失であり（非可逆処理ではないため診断ログは不要）、画像を含む EMF の DIB を約25%縮める。
    /// 行は DibRowAlignBytes 境界へパディングする（24bpp の落とし穴。怠ると行ごとにズレてせん断する）。</summary>
    internal static byte[] BuildBgrDib(Bitmap bmp, out BITMAPINFOHEADER bmi, out bool hadAlpha)
        => BuildBgrDib(bmp, new IrRgb(1, 1, 1), out bmi, out hadAlpha);

    internal static byte[] BuildBgrDib(Bitmap bmp, IrRgb background,
                                       out BITMAPINFOHEADER bmi, out bool hadAlpha)
    {
        int bw = bmp.Width, bh = bmp.Height;
        int stride = (bw * BgrBytesPerPixel + DibRowAlignBytes - 1)
                     / DibRowAlignBytes * DibRowAlignBytes;
        var bits = new byte[stride * bh];
        var row = new byte[bw * 4];
        hadAlpha = false;
        int bgB = IrRgb.ToByte(background.B), bgG = IrRgb.ToByte(background.G), bgR = IrRgb.ToByte(background.R);
        var data = bmp.LockBits(new Rectangle(0, 0, bw, bh),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bh; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bw * 4);
                int dst = (bh - 1 - y) * stride;  // ボトムアップDIB
                for (int x = 0; x < bw; x++)
                {
                    int a = row[x * 4 + 3];
                    int o = dst + x * BgrBytesPerPixel;
                    if (a < 255)
                    {
                        hadAlpha = true;
                        // 指定背景色との事前合成（クラシックGDIはアルファを持てない）
                        bits[o + 0] = (byte)((row[x * 4 + 0] * a + bgB * (255 - a)) / 255);
                        bits[o + 1] = (byte)((row[x * 4 + 1] * a + bgG * (255 - a)) / 255);
                        bits[o + 2] = (byte)((row[x * 4 + 2] * a + bgR * (255 - a)) / 255);
                    }
                    else
                    {
                        bits[o + 0] = row[x * 4 + 0];
                        bits[o + 1] = row[x * 4 + 1];
                        bits[o + 2] = row[x * 4 + 2];
                    }
                    // 行末パディング（stride - bw*3 バイト）は 0 のまま＝DIB 仕様で不参照
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        bmi = new BITMAPINFOHEADER
        {
            Size = Marshal.SizeOf<BITMAPINFOHEADER>(),
            Width = bw,
            Height = bh,   // 正=ボトムアップ
            Planes = 1,          // BITMAPINFOHEADER 仕様の固定値
            BitCount = 24,       // 24bpp BGR（アルファは事前合成済み、下位バイトから B,G,R）
            Compression = Gdi.BI_RGB,
        };
        return bits;
    }
}
