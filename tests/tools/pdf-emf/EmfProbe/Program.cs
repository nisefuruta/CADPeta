// EmfProbe: Phase 0 仮説2・仮説3 検証用の最小EMF生成器
//
// サブコマンド:
//   generate <出力ディレクトリ>  … クラシックGDI EMF / EMF+ のサンプル一式を生成
//   clipboard <emfファイル>      … CF_ENHMETAFILE としてクリップボードへ登録
//   dump <emfファイル>...        … ヘッダ寸法とレコード種別の一覧を表示
//   render <emf> <png> [dpi]     … PlayEnhMetaFile（クラシックGDI再生）で画像化（既定150dpi）
//   hairline <出力ディレクトリ>  … 幅0ペン（コスメティック）の実挙動検証チャートを生成
//
// 図の内容（全ファイル共通、キャンバス 200×120mm）:
//   外枠 / 100mmスケールバー / 線 / 円弧 / ベジェ / 塗り多角形 /
//   TrueType文字（ＭＳ ゴシック・Arial） / ワイプアウト相当（クリップ or 白塗り）

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("使い方: EmfProbe generate <出力ディレクトリ> | clipboard <emf> | dump <emf>...");
            return 1;
        }
        switch (args[0])
        {
            case "generate":
                Generate(args[1]);
                return 0;
            case "clipboard":
                return ClipboardHelper.SetEmf(args[1]) ? 0 : 2;
            case "dump":
                foreach (var f in args.Skip(1)) EmfDump.Dump(f);
                return 0;
            case "render":
                EmfRender.Render(args[1], args[2],
                    args.Length > 3 ? double.Parse(args[3]) : 150.0);
                return 0;
            case "hairline":
                HairlineChart.Generate(args[1]);
                return 0;
            default:
                Console.Error.WriteLine($"不明なサブコマンド: {args[0]}");
                return 1;
        }
    }

    private static void Generate(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string P(string name) => Path.Combine(outDir, name);

        // クラシックGDI（P/Invoke 直書き）
        ClassicEmf.Generate(P("classic_clip.emf"), 1.0, WipeMode.Clip, "CLASSIC CLIP");
        ClassicEmf.Generate(P("classic_white.emf"), 1.0, WipeMode.WhiteFill, "CLASSIC WHITEFILL");
        ClassicEmf.Generate(P("classic_clip_50pct.emf"), 0.5, WipeMode.Clip, "CLASSIC CLIP 50%");

        // EMF+（System.Drawing.Metafile 経由）
        PlusEmf.Generate(P("plusonly_clip.emf"), EmfType.EmfPlusOnly, WipeMode.Clip, "EMF+ONLY CLIP");
        PlusEmf.Generate(P("plusonly_white.emf"), EmfType.EmfPlusOnly, WipeMode.WhiteFill, "EMF+ONLY WHITEFILL");
        PlusEmf.Generate(P("dual_clip.emf"), EmfType.EmfPlusDual, WipeMode.Clip, "EMF+DUAL CLIP");
        PlusEmf.Generate(P("dual_white.emf"), EmfType.EmfPlusDual, WipeMode.WhiteFill, "EMF+DUAL WHITEFILL");

        foreach (var f in Directory.GetFiles(outDir, "*.emf").OrderBy(x => x))
            Console.WriteLine($"{Path.GetFileName(f),-24} {new FileInfo(f).Length,8:N0} bytes");
    }
}

internal enum WipeMode { Clip, WhiteFill }

/// <summary>図の内容定義（mm単位）。クラシック/EMF+ の両生成器が共有する。</summary>
internal static class Spec
{
    public const double W = 200.0, H = 120.0;

    // 外枠
    public const double FrameL = 5, FrameT = 5, FrameR = 195, FrameB = 115;

    // 赤の斜め線
    public static readonly (double x1, double y1, double x2, double y2) RedLine = (10, 10, 60, 40);

    // 円弧（外接矩形 + 角度指定。GDI+ は時計回り角度、GDI は始点終点で表現）
    public static readonly (double l, double t, double r, double b) ArcBox = (65, 10, 95, 40);

    // ベジェ（4制御点）
    public static readonly (double x, double y)[] Bezier =
        [(100, 35), (110, 8), (125, 42), (135, 15)];

    // 塗り三角形
    public static readonly (double x, double y)[] Triangle =
        [(140, 10), (190, 10), (165, 40)];
    public static readonly (byte r, byte g, byte b) TriangleFill = (0, 153, 204);

    // TrueType 文字
    public const string Text1 = "文字編集テスト あ漢123";
    public const string Font1 = "ＭＳ ゴシック";
    public const double Text1X = 10, Text1Y = 45, TextH = 5;
    public const string Text2 = "Arial Edit Test 456";
    public const string Font2 = "Arial";
    public const double Text2X = 10, Text2Y = 53;
    public const double LabelX = 120, LabelY = 53, LabelH = 4;

    // ワイプアウト検証ゾーン: 緑の水平ハッチ線の上にワイプアウト矩形、最前面に赤線
    public const double HatchX1 = 10, HatchX2 = 90, HatchY1 = 62, HatchY2 = 92, HatchStep = 3;
    public const double WipeL = 30, WipeT = 65, WipeR = 60, WipeB = 90;
    public static readonly (double x1, double y1, double x2, double y2) TopLine = (15, 67, 85, 88);

    // 100mm スケールバー（FR-6 実寸検証の基準）
    public const double BarY = 105, BarX1 = 10, BarX2 = 110;
    public const double TickShort = 3, TickLong = 5, TickStep = 10;
}

/// <summary>クラシックGDIレコードのみで構成される EMF を GDI32 P/Invoke で生成する。</summary>
internal static class ClassicEmf
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEnhMetaFileW(IntPtr hdcRef, string file, ref RECT rect, string desc);
    [DllImport("gdi32.dll")] private static extern IntPtr CloseEnhMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr hemf);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
        uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec,
        uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool Arc(IntPtr hdc, int l, int t, int r, int b,
        int xs, int ys, int xe, int ye);
    [DllImport("gdi32.dll")] private static extern bool PolyBezier(IntPtr hdc, POINT[] pts, uint n);
    [DllImport("gdi32.dll")] private static extern bool Polygon(IntPtr hdc, POINT[] pts, int n);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint opt, IntPtr rect,
        string s, uint c, IntPtr dx);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern int SelectClipRgn(IntPtr hdc, IntPtr rgn);

    private const int HORZSIZE = 4, VERTSIZE = 6, HORZRES = 8, VERTRES = 10;
    private const int PS_SOLID = 0, RGN_DIFF = 4, TRANSPARENT = 1;
    private const int NULL_BRUSH = 5, NULL_PEN = 8, WHITE_BRUSH = 0;

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public static void Generate(string path, double scale, WipeMode mode, string label)
    {
        IntPtr hdcRef = GetDC(IntPtr.Zero);
        double pxmmX = (double)GetDeviceCaps(hdcRef, HORZRES) / GetDeviceCaps(hdcRef, HORZSIZE);
        double pxmmY = (double)GetDeviceCaps(hdcRef, VERTRES) / GetDeviceCaps(hdcRef, VERTSIZE);

        int X(double mm) => (int)Math.Round(mm * scale * pxmmX);
        int Y(double mm) => (int)Math.Round(mm * scale * pxmmY);
        int Wpx(double mm) => Math.Max(1, (int)Math.Round(mm * scale * pxmmX));
        POINT Pt(double mx, double my) => new() { X = X(mx), Y = Y(my) };

        // rclFrame = 意図した物理寸法（0.01mm 単位）
        var frame = new RECT { L = 0, T = 0, R = (int)(Spec.W * 100 * scale), B = (int)(Spec.H * 100 * scale) };
        IntPtr hdc = CreateEnhMetaFileW(hdcRef, path, ref frame,
            "CADPeta EmfProbe\0クラシックGDI最小生成\0\0");
        if (hdc == IntPtr.Zero) throw new InvalidOperationException($"CreateEnhMetaFile 失敗: {path}");

        var created = new List<IntPtr>();
        IntPtr Pen(double mm, byte r, byte g, byte b)
        { var h = CreatePen(PS_SOLID, Wpx(mm), Rgb(r, g, b)); created.Add(h); return h; }
        IntPtr Brush(byte r, byte g, byte b)
        { var h = CreateSolidBrush(Rgb(r, g, b)); created.Add(h); return h; }
        IntPtr Font(double mmH, string face)
        { var h = CreateFontW(-Y(mmH), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, face); created.Add(h); return h; }
        void Line(double x1, double y1, double x2, double y2)
        { MoveToEx(hdc, X(x1), Y(y1), IntPtr.Zero); LineTo(hdc, X(x2), Y(y2)); }

        SetBkMode(hdc, TRANSPARENT);

        // 外枠（塗りなし矩形）
        SelectObject(hdc, GetStockObject(NULL_BRUSH));
        SelectObject(hdc, Pen(0.35, 0, 0, 0));
        Rectangle(hdc, X(Spec.FrameL), Y(Spec.FrameT), X(Spec.FrameR), Y(Spec.FrameB));

        // 赤の斜め線
        SelectObject(hdc, Pen(0.5, 255, 0, 0));
        Line(Spec.RedLine.x1, Spec.RedLine.y1, Spec.RedLine.x2, Spec.RedLine.y2);

        // 円弧（270°。始点=右中央、終点=上中央）
        var (al, at, ar, ab) = Spec.ArcBox;
        double acx = (al + ar) / 2, acy = (at + ab) / 2;
        SelectObject(hdc, Pen(0.35, 0, 0, 255));
        Arc(hdc, X(al), Y(at), X(ar), Y(ab), X(ar), Y(acy), X(acx), Y(at));

        // ベジェ
        SelectObject(hdc, Pen(0.35, 0, 128, 0));
        PolyBezier(hdc, Spec.Bezier.Select(p => Pt(p.x, p.y)).ToArray(), (uint)Spec.Bezier.Length);

        // 塗り三角形
        SelectObject(hdc, Pen(0.25, 0, 0, 0));
        SelectObject(hdc, Brush(Spec.TriangleFill.r, Spec.TriangleFill.g, Spec.TriangleFill.b));
        Polygon(hdc, Spec.Triangle.Select(p => Pt(p.x, p.y)).ToArray(), Spec.Triangle.Length);

        // TrueType 文字（ExtTextOutW、フォント指定付き）
        SetTextColor(hdc, Rgb(0, 0, 0));
        SelectObject(hdc, Font(Spec.TextH, Spec.Font1));
        ExtTextOutW(hdc, X(Spec.Text1X), Y(Spec.Text1Y), 0, IntPtr.Zero, Spec.Text1, (uint)Spec.Text1.Length, IntPtr.Zero);
        SelectObject(hdc, Font(Spec.TextH, Spec.Font2));
        ExtTextOutW(hdc, X(Spec.Text2X), Y(Spec.Text2Y), 0, IntPtr.Zero, Spec.Text2, (uint)Spec.Text2.Length, IntPtr.Zero);

        // 変種ラベル
        SetTextColor(hdc, Rgb(102, 102, 102));
        SelectObject(hdc, Font(Spec.LabelH, Spec.Font2));
        ExtTextOutW(hdc, X(Spec.LabelX), Y(Spec.LabelY), 0, IntPtr.Zero, label, (uint)label.Length, IntPtr.Zero);

        // ワイプアウト検証ゾーン
        var hatchPen = Pen(0.25, 0, 160, 0);
        if (mode == WipeMode.Clip)
        {
            // 真のクリップ: 全面からワイプアウト矩形を除いた領域でハッチ線を描く
            IntPtr rgn = CreateRectRgn(X(0), Y(0), X(Spec.W), Y(Spec.H));
            IntPtr wipe = CreateRectRgn(X(Spec.WipeL), Y(Spec.WipeT), X(Spec.WipeR), Y(Spec.WipeB));
            CombineRgn(rgn, rgn, wipe, RGN_DIFF);
            SelectClipRgn(hdc, rgn);
            SelectObject(hdc, hatchPen);
            for (double y = Spec.HatchY1; y <= Spec.HatchY2; y += Spec.HatchStep)
                Line(Spec.HatchX1, y, Spec.HatchX2, y);
            SelectClipRgn(hdc, IntPtr.Zero);
            DeleteObject(rgn); DeleteObject(wipe);
        }
        else
        {
            // 白塗り: ハッチ線を全部描いた上に背景色ポリゴンを被せる
            SelectObject(hdc, hatchPen);
            for (double y = Spec.HatchY1; y <= Spec.HatchY2; y += Spec.HatchStep)
                Line(Spec.HatchX1, y, Spec.HatchX2, y);
            SelectObject(hdc, GetStockObject(WHITE_BRUSH));
            SelectObject(hdc, GetStockObject(NULL_PEN));
            Rectangle(hdc, X(Spec.WipeL), Y(Spec.WipeT), X(Spec.WipeR) + 1, Y(Spec.WipeB) + 1);
        }

        // 最前面の赤線（ワイプアウトの上に載る）
        SelectObject(hdc, Pen(0.5, 255, 0, 0));
        Line(Spec.TopLine.x1, Spec.TopLine.y1, Spec.TopLine.x2, Spec.TopLine.y2);

        // 100mm スケールバー
        SelectObject(hdc, Pen(0.3, 0, 0, 0));
        Line(Spec.BarX1, Spec.BarY, Spec.BarX2, Spec.BarY);
        for (double x = Spec.BarX1; x <= Spec.BarX2; x += Spec.TickStep)
        {
            double tick = (x == Spec.BarX1 || x == Spec.BarX2 || Math.Abs(x - (Spec.BarX1 + 50)) < 0.01)
                ? Spec.TickLong : Spec.TickShort;
            Line(x, Spec.BarY, x, Spec.BarY - tick);
        }
        SetTextColor(hdc, Rgb(0, 0, 0));
        SelectObject(hdc, Font(3, Spec.Font2));
        ExtTextOutW(hdc, X(Spec.BarX1 - 1), Y(Spec.BarY + 1), 0, IntPtr.Zero, "0", 1, IntPtr.Zero);
        var lbl = "100mm";
        ExtTextOutW(hdc, X(Spec.BarX2 - 4), Y(Spec.BarY + 1), 0, IntPtr.Zero, lbl, (uint)lbl.Length, IntPtr.Zero);

        IntPtr hemf = CloseEnhMetaFile(hdc);
        DeleteEnhMetaFile(hemf);
        foreach (var h in created) DeleteObject(h);
        ReleaseDC(IntPtr.Zero, hdcRef);
        Console.WriteLine($"生成: {path}");
    }
}

/// <summary>System.Drawing の Metafile 経由で EMF+（Only / Dual）を生成する。内容はクラシック版と同一。</summary>
internal static class PlusEmf
{
    public static void Generate(string path, EmfType type, WipeMode mode, string label)
    {
        using var refG = Graphics.FromHwnd(IntPtr.Zero);
        IntPtr hdc = refG.GetHdc();
        try
        {
            using var mf = new Metafile(path, hdc,
                new RectangleF(0, 0, (float)Spec.W, (float)Spec.H),
                MetafileFrameUnit.Millimeter, type, "CADPeta EmfProbe EMF+");
            using var g = Graphics.FromImage(mf);
            g.PageUnit = GraphicsUnit.Millimeter;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Draw(g, mode, label);
        }
        finally
        {
            refG.ReleaseHdc(hdc);
        }
        Console.WriteLine($"生成: {path}");
    }

    private static void Draw(Graphics g, WipeMode mode, string label)
    {
        static PointF Pt((double x, double y) p) => new((float)p.x, (float)p.y);
        static RectangleF Rc(double l, double t, double r, double b) =>
            new((float)l, (float)t, (float)(r - l), (float)(b - t));

        using var black035 = new Pen(Color.Black, 0.35f);
        using var black025 = new Pen(Color.Black, 0.25f);
        using var black03 = new Pen(Color.Black, 0.3f);
        using var red05 = new Pen(Color.Red, 0.5f);
        using var blue035 = new Pen(Color.Blue, 0.35f);
        using var green035 = new Pen(Color.FromArgb(0, 128, 0), 0.35f);
        using var hatch025 = new Pen(Color.FromArgb(0, 160, 0), 0.25f);
        using var fill = new SolidBrush(Color.FromArgb(Spec.TriangleFill.r, Spec.TriangleFill.g, Spec.TriangleFill.b));
        var fmt = StringFormat.GenericTypographic;

        // 外枠
        g.DrawRectangle(black035, (float)Spec.FrameL, (float)Spec.FrameT,
            (float)(Spec.FrameR - Spec.FrameL), (float)(Spec.FrameB - Spec.FrameT));

        // 赤の斜め線
        g.DrawLine(red05, Pt((Spec.RedLine.x1, Spec.RedLine.y1)), Pt((Spec.RedLine.x2, Spec.RedLine.y2)));

        // 円弧（クラシック版と同じ円・同じ90°区間: 上中央→右中央）
        var (al, at, ar, ab) = Spec.ArcBox;
        g.DrawArc(blue035, Rc(al, at, ar, ab), 270f, 90f);

        // ベジェ
        g.DrawBezier(green035, Pt(Spec.Bezier[0]), Pt(Spec.Bezier[1]), Pt(Spec.Bezier[2]), Pt(Spec.Bezier[3]));

        // 塗り三角形
        var tri = Spec.Triangle.Select(Pt).ToArray();
        g.FillPolygon(fill, tri);
        g.DrawPolygon(black025, tri);

        // TrueType 文字
        using var f1 = new Font(Spec.Font1, (float)Spec.TextH, FontStyle.Regular, GraphicsUnit.Millimeter);
        using var f2 = new Font(Spec.Font2, (float)Spec.TextH, FontStyle.Regular, GraphicsUnit.Millimeter);
        using var fLabel = new Font(Spec.Font2, (float)Spec.LabelH, FontStyle.Regular, GraphicsUnit.Millimeter);
        using var fSmall = new Font(Spec.Font2, 3f, FontStyle.Regular, GraphicsUnit.Millimeter);
        g.DrawString(Spec.Text1, f1, Brushes.Black, (float)Spec.Text1X, (float)Spec.Text1Y, fmt);
        g.DrawString(Spec.Text2, f2, Brushes.Black, (float)Spec.Text2X, (float)Spec.Text2Y, fmt);
        using var gray = new SolidBrush(Color.FromArgb(102, 102, 102));
        g.DrawString(label, fLabel, gray, (float)Spec.LabelX, (float)Spec.LabelY, fmt);

        // ワイプアウト検証ゾーン
        var wipeRect = Rc(Spec.WipeL, Spec.WipeT, Spec.WipeR, Spec.WipeB);
        if (mode == WipeMode.Clip)
        {
            using var wipeRegion = new Region(wipeRect);
            g.SetClip(Rc(0, 0, Spec.W, Spec.H));
            g.ExcludeClip(wipeRegion);
            for (double y = Spec.HatchY1; y <= Spec.HatchY2; y += Spec.HatchStep)
                g.DrawLine(hatch025, (float)Spec.HatchX1, (float)y, (float)Spec.HatchX2, (float)y);
            g.ResetClip();
        }
        else
        {
            for (double y = Spec.HatchY1; y <= Spec.HatchY2; y += Spec.HatchStep)
                g.DrawLine(hatch025, (float)Spec.HatchX1, (float)y, (float)Spec.HatchX2, (float)y);
            g.FillRectangle(Brushes.White, wipeRect);
        }

        // 最前面の赤線
        g.DrawLine(red05, Pt((Spec.TopLine.x1, Spec.TopLine.y1)), Pt((Spec.TopLine.x2, Spec.TopLine.y2)));

        // 100mm スケールバー
        g.DrawLine(black03, (float)Spec.BarX1, (float)Spec.BarY, (float)Spec.BarX2, (float)Spec.BarY);
        for (double x = Spec.BarX1; x <= Spec.BarX2; x += Spec.TickStep)
        {
            double tick = (x == Spec.BarX1 || x == Spec.BarX2 || Math.Abs(x - (Spec.BarX1 + 50)) < 0.01)
                ? Spec.TickLong : Spec.TickShort;
            g.DrawLine(black03, (float)x, (float)Spec.BarY, (float)x, (float)(Spec.BarY - tick));
        }
        g.DrawString("0", fSmall, Brushes.Black, (float)(Spec.BarX1 - 1), (float)(Spec.BarY + 1), fmt);
        g.DrawString("100mm", fSmall, Brushes.Black, (float)(Spec.BarX2 - 4), (float)(Spec.BarY + 1), fmt);
    }
}

/// <summary>CF_ENHMETAFILE のクリップボード登録。</summary>
internal static class ClipboardHelper
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetEnhMetaFileW(string file);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CopyEnhMetaFileW(IntPtr hemf, string? file);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr hemf);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();

    private const uint CF_ENHMETAFILE = 14;

    public static bool SetEmf(string path)
    {
        IntPtr hemf = GetEnhMetaFileW(Path.GetFullPath(path));
        if (hemf == IntPtr.Zero) { Console.Error.WriteLine($"読み込み失敗: {path}"); return false; }
        IntPtr hCopy = CopyEnhMetaFileW(hemf, null);
        DeleteEnhMetaFile(hemf);
        if (!OpenClipboard(IntPtr.Zero)) { Console.Error.WriteLine("OpenClipboard 失敗"); return false; }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_ENHMETAFILE, hCopy) == IntPtr.Zero)
            { Console.Error.WriteLine("SetClipboardData 失敗"); return false; }
        }
        finally { CloseClipboard(); }
        Console.WriteLine($"クリップボードへ登録: {path}");
        return true;
    }
}

/// <summary>PlayEnhMetaFile（クラシックGDIの再生系）で EMF を PNG 化する。
/// GDI+ 再生（Graphics.DrawImage）ではなく GDI 再生を使い、Office の表示系に近づける。</summary>
internal static class EmfRender
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetEnhMetaFileW(string file);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr hemf);
    [DllImport("gdi32.dll")] private static extern bool PlayEnhMetaFile(IntPtr hdc, IntPtr hemf, ref RECT rect);

    public static void Render(string emfPath, string pngPath, double dpi)
    {
        byte[] b = File.ReadAllBytes(emfPath);
        int I(int o) => BitConverter.ToInt32(b, o);
        double wMm = (I(32) - I(24)) / 100.0, hMm = (I(36) - I(28)) / 100.0;
        int w = (int)Math.Round(wMm / 25.4 * dpi);
        int h = (int)Math.Round(hMm / 25.4 * dpi);

        IntPtr hemf = GetEnhMetaFileW(Path.GetFullPath(emfPath));
        if (hemf == IntPtr.Zero) throw new InvalidOperationException($"読み込み失敗: {emfPath}");
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            IntPtr hdc = g.GetHdc();
            try
            {
                var r = new RECT { L = 0, T = 0, R = w, B = h };
                PlayEnhMetaFile(hdc, hemf, ref r);
            }
            finally { g.ReleaseHdc(hdc); }
        }
        DeleteEnhMetaFile(hemf);
        bmp.Save(pngPath, ImageFormat.Png);
        Console.WriteLine($"レンダ: {pngPath} ({w}x{h}px, {dpi}dpi, {wMm:F2}x{hMm:F2}mm)");
    }
}

/// <summary>幅0ペン（コスメティックペン=デバイス最細線）の実挙動検証チャート（todo §3.5 最細線）。
/// EmfOut と同じ MM_ANISOTROPIC・論理0.01mm 単位で、幅0と固定幅を並置する。</summary>
internal static class HairlineChart
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEnhMetaFileW(IntPtr hdcRef, string file, ref RECT rect, string desc);
    [DllImport("gdi32.dll")] private static extern IntPtr CloseEnhMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr hemf);
    [DllImport("gdi32.dll")] private static extern int SetMapMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetWindowExtEx(IntPtr hdc, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] private static extern bool SetViewportExtEx(IntPtr hdc, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
        uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec,
        uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint opt, IntPtr rect,
        string s, uint c, IntPtr dx);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);

    private const int MM_ANISOTROPIC = 8, HORZSIZE = 4, VERTSIZE = 6, HORZRES = 8, VERTRES = 10;

    // 行構成: (ペン幅[論理0.01mm]、ラベル)。幅0=コスメティックペン（デバイス最細線）
    private static readonly (int w, string label)[] Rows =
    [
        (0, "w=0 (cosmetic)"),
        (1, "w=1 (0.01mm)"),
        (5, "w=5 (0.05mm=IR置換値)"),
        (13, "w=13 (0.13mm)"),
        (17, "w=17 (0.169mm=1px@150dpi)"),
        (35, "w=35 (0.35mm)"),
    ];

    public const double W = 150.0, H = 70.0;

    public static void Generate(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string path = Path.GetFullPath(Path.Combine(outDir, "hairline.emf"));

        IntPtr hdcRef = GetDC(IntPtr.Zero);
        // 96dpi 固定の写像（Office は EMF のデバイスを 96dpi と仮定する。EmfOut と同じ規約）
        const double pxPerMm = 96.0 / 25.4;
        var frame = new RECT { L = 0, T = 0, R = (int)(W * 100), B = (int)(H * 100) };
        IntPtr hdc = CreateEnhMetaFileW(hdcRef, path, ref frame, "CADPeta EmfProbe\0hairline\0\0");
        SetMapMode(hdc, MM_ANISOTROPIC);
        SetWindowExtEx(hdc, (int)(W * 100), (int)(H * 100), IntPtr.Zero);
        SetViewportExtEx(hdc, (int)Math.Round(W * pxPerMm), (int)Math.Round(H * pxPerMm), IntPtr.Zero);
        SetBkMode(hdc, 1 /* TRANSPARENT */);

        IntPtr font = CreateFontW(-300, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Arial");
        SelectObject(hdc, font);

        int y = 800;
        foreach (var (w, label) in Rows)
        {
            IntPtr pen = CreatePen(0, w, 0);
            IntPtr old = SelectObject(hdc, pen);
            // 水平線100mm + 斜め線（コスメティックの角度依存を見る）
            MoveToEx(hdc, 500, y, IntPtr.Zero); LineTo(hdc, 10500, y);
            MoveToEx(hdc, 11000, y - 300, IntPtr.Zero); LineTo(hdc, 12000, y + 300);
            SelectObject(hdc, old);
            DeleteObject(pen);
            ExtTextOutW(hdc, 12300, y + 100, 0, IntPtr.Zero, label, (uint)label.Length, IntPtr.Zero);
            y += 1000;
        }

        IntPtr hemf = CloseEnhMetaFile(hdc);
        DeleteEnhMetaFile(hemf);
        DeleteObject(font);
        ReleaseDC(IntPtr.Zero, hdcRef);

        // ヘッダ szl を 96dpi 比へパッチ（EmfOut.PatchHeaderTo96Dpi と同じ理由・同じ値）
        var b = File.ReadAllBytes(path);
        void Wr(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
        Wr(72, 9600); Wr(76, 9600);
        Wr(80, 2540); Wr(84, 2540);
        if (BitConverter.ToInt32(b, 4) >= 108) { Wr(100, 2540000); Wr(104, 2540000); }
        File.WriteAllBytes(path, b);
        Console.WriteLine($"生成: {path}");
    }
}

/// <summary>EMF ファイルのヘッダ寸法とレコード構成を表示する（検証の一次証拠）。</summary>
internal static class EmfDump
{
    private static readonly Dictionary<int, string> EmrNames = new()
    {
        [1] = "HEADER", [2] = "POLYBEZIER", [3] = "POLYGON", [4] = "POLYLINE",
        [5] = "POLYBEZIERTO", [6] = "POLYLINETO", [7] = "POLYPOLYLINE", [8] = "POLYPOLYGON",
        [9] = "SETWINDOWEXTEX", [10] = "SETWINDOWORGEX", [11] = "SETVIEWPORTEXTEX", [12] = "SETVIEWPORTORGEX",
        [13] = "SETBRUSHORGEX", [14] = "EOF", [17] = "SETMAPMODE", [18] = "SETBKMODE",
        [19] = "SETPOLYFILLMODE", [20] = "SETROP2", [21] = "SETSTRETCHBLTMODE", [22] = "SETTEXTALIGN",
        [24] = "SETTEXTCOLOR", [25] = "SETBKCOLOR", [27] = "MOVETOEX", [28] = "SETMETARGN",
        [29] = "EXCLUDECLIPRECT", [30] = "INTERSECTCLIPRECT", [33] = "SAVEDC", [34] = "RESTOREDC",
        [35] = "SETWORLDTRANSFORM", [36] = "MODIFYWORLDTRANSFORM", [37] = "SELECTOBJECT",
        [38] = "CREATEPEN", [39] = "CREATEBRUSHINDIRECT", [40] = "DELETEOBJECT",
        [41] = "ANGLEARC", [42] = "ELLIPSE", [43] = "RECTANGLE", [44] = "ROUNDRECT",
        [45] = "ARC", [46] = "CHORD", [47] = "PIE", [54] = "LINETO", [55] = "ARCTO",
        [56] = "POLYDRAW", [57] = "SETARCDIRECTION", [58] = "SETMITERLIMIT",
        [59] = "BEGINPATH", [60] = "ENDPATH", [61] = "CLOSEFIGURE", [62] = "FILLPATH",
        [63] = "STROKEANDFILLPATH", [64] = "STROKEPATH", [67] = "SELECTCLIPPATH",
        [70] = "GDICOMMENT", [75] = "EXTSELECTCLIPRGN", [76] = "BITBLT", [77] = "STRETCHBLT",
        [81] = "STRETCHDIBITS", [82] = "EXTCREATEFONTINDIRECTW", [83] = "EXTTEXTOUTA", [84] = "EXTTEXTOUTW",
        [85] = "POLYBEZIER16", [86] = "POLYGON16", [87] = "POLYLINE16", [88] = "POLYBEZIERTO16",
        [89] = "POLYLINETO16", [90] = "POLYPOLYLINE16", [91] = "POLYPOLYGON16",
        [95] = "EXTCREATEPEN", [98] = "SETICMMODE",
    };

    private static readonly Dictionary<int, string> PlusNames = new()
    {
        [0x4001] = "Header", [0x4002] = "EndOfFile", [0x4003] = "Comment", [0x4004] = "GetDC",
        [0x4008] = "Object", [0x4009] = "Clear", [0x400A] = "FillRects", [0x400B] = "DrawRects",
        [0x400C] = "FillPolygon", [0x400D] = "DrawLines", [0x400E] = "FillEllipse", [0x400F] = "DrawEllipse",
        [0x4010] = "FillPie", [0x4011] = "DrawPie", [0x4012] = "DrawArc", [0x4013] = "FillRegion",
        [0x4014] = "FillPath", [0x4015] = "DrawPath", [0x4019] = "DrawBeziers",
        [0x401A] = "DrawImage", [0x401B] = "DrawImagePoints", [0x401C] = "DrawString",
        [0x401E] = "SetAntiAliasMode", [0x401F] = "SetTextRenderingHint", [0x4021] = "SetInterpolationMode",
        [0x4022] = "SetPixelOffsetMode", [0x4023] = "SetCompositingMode", [0x4024] = "SetCompositingQuality",
        [0x4025] = "Save", [0x4026] = "Restore", [0x402A] = "SetWorldTransform", [0x4030] = "SetPageTransform",
        [0x4031] = "ResetClip", [0x4032] = "SetClipRect", [0x4033] = "SetClipPath", [0x4034] = "SetClipRegion",
        [0x4036] = "DrawDriverString",
    };

    public static void Dump(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int I(int o) => BitConverter.ToInt32(b, o);

        Console.WriteLine($"== {Path.GetFileName(path)} ({b.Length:N0} bytes) ==");
        if (I(0) != 1 || I(40) != 0x464D4520)
        { Console.WriteLine("  EMF ヘッダではない"); return; }

        Console.WriteLine($"  rclBounds(dev px) : ({I(8)},{I(12)})-({I(16)},{I(20)})");
        Console.WriteLine($"  rclFrame (0.01mm) : ({I(24)},{I(28)})-({I(32)},{I(36)})  → {(I(32) - I(24)) / 100.0:F2} × {(I(36) - I(28)) / 100.0:F2} mm");
        Console.WriteLine($"  nRecords={I(52)}  szlDevice=({I(72)},{I(76)})px  szlMillimeters=({I(80)},{I(84)})mm");

        var classic = new SortedDictionary<string, int>();
        var plus = new SortedDictionary<string, int>();
        int off = 0;
        while (off + 8 <= b.Length)
        {
            int type = I(off), size = I(off + 4);
            if (size < 8 || off + size > b.Length) { Console.WriteLine($"  [壊れたレコード @ {off}]"); break; }
            string name = EmrNames.TryGetValue(type, out var n) ? n : $"EMR_{type}";
            if (type == 70 && size >= 16 && I(off + 12) == 0x2B464D45) // "EMF+" コメント
            {
                name = "GDICOMMENT(EMF+)";
                int p = off + 16, end = off + 8 + I(off + 8);
                while (p + 12 <= Math.Min(end, off + size))
                {
                    int ptype = BitConverter.ToUInt16(b, p);
                    int psize = I(p + 4);
                    string pname = PlusNames.TryGetValue(ptype, out var pn) ? pn : $"0x{ptype:X4}";
                    plus[pname] = plus.GetValueOrDefault(pname) + 1;
                    if (psize < 12) break;
                    p += psize;
                }
            }
            classic[name] = classic.GetValueOrDefault(name) + 1;
            if (type == 14) break; // EOF
            off += size;
        }

        Console.WriteLine("  [クラシックGDIレコード]");
        foreach (var (k, v) in classic) Console.WriteLine($"    {k,-24} × {v}");
        if (plus.Count > 0)
        {
            Console.WriteLine("  [EMF+ レコード（GDICOMMENT 内）]");
            foreach (var (k, v) in plus) Console.WriteLine($"    {k,-24} × {v}");
        }
        bool hasPlusHeader = plus.ContainsKey("Header");
        bool hasClassicDrawing = classic.Keys.Any(k =>
            k is "POLYLINE16" or "POLYBEZIER16" or "POLYGON16" or "EXTTEXTOUTW" or "ARC" or "RECTANGLE" or "LINETO"
              or "POLYLINE" or "POLYBEZIER" or "POLYGON" or "STROKEPATH" or "FILLPATH");
        string cls = !hasPlusHeader ? "クラシックGDIのみ"
            : hasClassicDrawing ? "EMF+ Dual（クラシック併記）" : "EMF+ Only";
        Console.WriteLine($"  判定: {cls}");
        Console.WriteLine();
    }
}
