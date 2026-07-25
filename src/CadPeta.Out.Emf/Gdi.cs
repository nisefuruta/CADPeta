// GDI32 / USER32 の P/Invoke 宣言（クラシックGDIレコードの直書き用）。
// Win32 依存はこのアダプタプロジェクトに閉じる。
using System.Runtime.InteropServices;

namespace CadPeta.Out.Emf;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int L, T, R, B; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TEXTMETRICW
{
    public int Height, Ascent, Descent, InternalLeading, ExternalLeading;
    public int AveCharWidth, MaxCharWidth, Weight, Overhang;
    public int DigitizedAspectX, DigitizedAspectY;
    public char FirstChar, LastChar, DefaultChar, BreakChar;
    public byte Italic, Underlined, StruckOut, PitchAndFamily, TmCharSet;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public int Size, Width, Height;
    public short Planes, BitCount;
    public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
}

internal static class Gdi
{
    public const int MM_ANISOTROPIC = 8;
    public const int TRANSPARENT = 1;
    public const int TA_BASELINE = 24;          // TA_LEFT(0) | TA_BASELINE(24)
    public const int ALTERNATE = 1, WINDING = 2;
    public const int NULL_PEN = 8;
    public const int PS_SOLID = 0;
    public const int COLORONCOLOR = 3;
    public const int DIB_RGB_COLORS = 0;
    public const uint SRCCOPY = 0x00CC0020;
    public const byte PT_CLOSEFIGURE = 1, PT_LINETO = 2, PT_BEZIERTO = 4, PT_MOVETO = 6;
    public const uint DEFAULT_CHARSET = 1;
    public const int FW_NORMAL = 400;           // フォントウェイト標準（wingdi.h）
    public const int BI_RGB = 0;                // DIB 無圧縮（wingdi.h）
    public const int FONT_ANGLE_UNITS_PER_DEG = 10; // CreateFont の escapement/orientation は 0.1度単位

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEnhMetaFileW(IntPtr hdcRef, string file, ref RECT rect, string desc);
    [DllImport("gdi32.dll")] public static extern IntPtr CloseEnhMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteEnhMetaFile(IntPtr hemf);

    [DllImport("gdi32.dll")] public static extern int SetMapMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetWindowExtEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern bool SetViewportExtEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextAlign(IntPtr hdc, uint align);
    [DllImport("gdi32.dll")] public static extern int SetPolyFillMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")] public static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
        uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec,
        uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int i);

    [DllImport("gdi32.dll")] public static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern bool PolylineTo(IntPtr hdc, POINT[] pts, uint n);
    [DllImport("gdi32.dll")] public static extern bool PolyBezierTo(IntPtr hdc, POINT[] pts, uint n);
    [DllImport("gdi32.dll")] public static extern bool Polyline(IntPtr hdc, POINT[] pts, int n);
    [DllImport("gdi32.dll")] public static extern bool PolyBezier(IntPtr hdc, POINT[] pts, uint n);

    [DllImport("gdi32.dll")] public static extern bool BeginPath(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool EndPath(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool CloseFigure(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool AbortPath(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool FillPath(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool StrokePath(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool StrokeAndFillPath(IntPtr hdc);
    [DllImport("gdi32.dll")]
    public static extern int GetPath(IntPtr hdc, [Out] POINT[]? pts, [Out] byte[]? types, int size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint opt, IntPtr rect,
        string s, uint c, int[]? dx);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW tm);

    [DllImport("gdi32.dll")]
    public static extern int StretchDIBits(IntPtr hdc, int xd, int yd, int wd, int hd,
        int xs, int ys, int ws, int hs, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);

    // ==== クリップボード（CF_ENHMETAFILE 登録）====
    // 失敗時に Win32 エラーコードを文脈へ含めるため SetLastError=true を付ける
    // （既存の描画系宣言は EMF 記録専用でエラー分岐しないため付けていない）

    /// <summary>拡張メタファイルの標準クリップボード形式（winuser.h CF_ENHMETAFILE）。</summary>
    public const uint CF_ENHMETAFILE = 14;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetEnhMetaFileW(string file);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CopyEnhMetaFileW(IntPtr hemf, string? file);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern uint GetEnhMetaFileBits(IntPtr hemf, uint size, byte[]? buf);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
