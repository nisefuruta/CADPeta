// TrueType フォントの「CAD 文字高さ → em フォントサイズ」係数の取得。
// AutoCAD は TrueType 文字を「Height = 大文字の高さ（キャップハイト）」で描画する。
// SVG/EMF の font-size は em 単位のため、font-size = Height × EMSquare / CapEmHeight で
// 換算する（PDF印刷経路の実測と一致: Arial 1.397・MS Gothic 1.2996。プロット PDF の Tf 値で確認）。
// キャップハイトは GDI のアウトラインメトリクスから取る。Win32 依存はこのクラスに閉じ、
// CAD製品APIには依存しないため取得アダプタ間で共有できる。
using System.Runtime.InteropServices;

namespace CadPeta.Acquire.Common;

public static class GdiFontMetrics
{
    /// <summary>アウトラインメトリクスが取れないフォントの予備係数。
    /// 一般的な欧文・和文 TrueType の em/キャップハイト比（1.3〜1.4）の中庸。</summary>
    const double FallbackEmPerCap = 1.35;

    /// <summary>メトリクス問い合わせ用のフォント高さ（論理単位）。EMSquare の取得だけに使う
    /// ため任意の十分大きい値でよい。</summary>
    const int ProbeFontHeight = 1000;

    static readonly Dictionary<(string face, bool bold, bool italic), double> Cache = new();
    static readonly Dictionary<string, string> FamilyNameCache = new();
    static readonly Dictionary<string, string?> PostScriptNameCache = new();

    /// <summary>フォントの英語ファミリ名。AutoCAD の文字スタイルはローカライズ名
    ///（例「ＭＳ ゴシック」）を持ち得るが、SVG の font-family がローカライズ名だと
    /// 他環境のレンダラが解決できず代替フォントへ落ちる（Chrome 実測）。
    /// name テーブルの英語名（例 MS Gothic）へ正規化し、PDF印刷経路の表記とも揃える。</summary>
    public static string EnglishFamilyName(string typeface)
    {
        lock (FamilyNameCache)
        {
            if (FamilyNameCache.TryGetValue(typeface, out var cached))
                return cached;
        }
        string resolved = ReadEnglishFamilyName(typeface) ?? typeface;
        lock (FamilyNameCache)
        {
            FamilyNameCache[typeface] = resolved;
        }
        return resolved;
    }

    static string? ReadEnglishFamilyName(string typeface)
    {
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return null;
        try
        {
            return WithFont<string?>(hdc, typeface, bold: false, italic: false, ProbeFontHeight, () =>
            {
                // name テーブル（タグ文字列のリトルエンディアン詰め）から
                // Windows プラットフォーム(3)・英語(0x0409)・ファミリ名(nameID=1)を探す
                const uint NameTag = 0x656D616E;
                const uint GdiError = 0xFFFFFFFF;
                uint size = GetFontData(hdc, NameTag, 0, null, 0);
                const int HeaderLen = 6, RecordLen = 12;
                if (size == GdiError || size < HeaderLen)
                    return null;
                var buf = new byte[size];
                if (GetFontData(hdc, NameTag, 0, buf, size) == GdiError)
                    return null;
                int BE16(int off) => (buf[off] << 8) | buf[off + 1];
                int count = BE16(2), stringsAt = BE16(4);
                for (int i = 0; i < count; i++)
                {
                    int rec = HeaderLen + i * RecordLen;
                    if (rec + RecordLen > buf.Length)
                        break;
                    const int WindowsPlatform = 3, FamilyNameId = 1, EnglishUs = 0x0409;
                    if (BE16(rec) != WindowsPlatform || BE16(rec + 6) != FamilyNameId
                        || BE16(rec + 4) != EnglishUs)
                        continue;
                    int len = BE16(rec + 8), off = stringsAt + BE16(rec + 10);
                    if (off + len > buf.Length || len == 0)
                        continue;
                    // Windows プラットフォームの name 文字列は UTF-16BE
                    var chars = new char[len / 2];
                    for (int k = 0; k < chars.Length; k++)
                        chars[k] = (char)((buf[off + 2 * k] << 8) | buf[off + 2 * k + 1]);
                    return new string(chars);
                }
                return null;
            });
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    /// <summary>
    /// PDF の BaseFont 名（PostScript 名）を、GDI が解決できるファミリ名へ直す。解決できなければ null。
    ///
    /// なぜ必要か: PostScript 名はファミリ名と一致しない書体が多い。GDI は未知の名前を渡されても
    /// 失敗せず既定書体へ黙って差し替えるため、字形も字送りも変わったまま出力される
    /// （実測: TimesNewRomanPSMT・CourierNewPSMT・YuGothic-Regular はいずれも
    /// Microsoft Sans Serif に化ける）。
    ///
    /// 手口: PostScript 名から候補のファミリ名を組み立て、GDI に解決させて name テーブルの
    /// 英語ファミリ名を読み戻し、**候補と一致したものだけ採用する**。GDI が差し替えたら
    /// 読み戻した名前が候補と食い違うため、当て推量が通ることはない（自己検証）。
    /// </summary>
    public static string? FamilyNameForPostScriptName(string postScriptName)
    {
        if (string.IsNullOrWhiteSpace(postScriptName))
            return null;
        lock (PostScriptNameCache)
        {
            if (PostScriptNameCache.TryGetValue(postScriptName, out var cached))
                return cached;
        }
        string? resolved = null;
        foreach (string candidate in FamilyNameCandidates(postScriptName))
        {
            string english = EnglishFamilyName(candidate);
            if (NormalizeFamily(english) == NormalizeFamily(candidate))
            {
                resolved = english; // 読み戻しが候補と一致＝GDI が差し替えていない
                break;
            }
        }
        lock (PostScriptNameCache)
        {
            PostScriptNameCache[postScriptName] = resolved;
        }
        return resolved;
    }

    /// <summary>PostScript 名から試すファミリ名の候補（重複を除いた順序つき）。</summary>
    static IEnumerable<string> FamilyNameCandidates(string postScriptName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string c in Build(postScriptName))
        {
            if (!string.IsNullOrWhiteSpace(c) && seen.Add(c))
                yield return c;
        }

        static IEnumerable<string> Build(string ps)
        {
            yield return ps;                                   // 例 Meiryo（そのまま通る書体）
            yield return ps.Replace('-', ' ');                 // 例 MS-Gothic → MS Gothic
            // 字体を表す接尾（-Bold・-Italic・-Regular 等）を落とした語幹
            string stem = ps.Split('-')[0];
            yield return stem;
            // PostScript 名の慣用接尾（MT・PS・PSMT）を落とす。例 TimesNewRomanPSMT → TimesNewRoman
            string trimmed = TrimPostScriptSuffix(stem);
            yield return trimmed;
            // 連結された語を分かち書きへ戻す。例 TimesNewRoman → Times New Roman
            yield return SplitCamelCase(trimmed);
            yield return SplitCamelCase(stem);
        }
    }

    static string TrimPostScriptSuffix(string name)
    {
        foreach (string suffix in new[] { "PSMT", "PS", "MT" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - suffix.Length);
        }
        return name;
    }

    /// <summary>連結表記を分かち書きへ戻す。頭字語は割らない（MSGothic → MS Gothic）。</summary>
    static string SplitCamelCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1])                                   // 小文字→大文字
                    || (i + 1 < name.Length && char.IsUpper(name[i - 1]) && char.IsLower(name[i + 1]))); // 頭字語の直後
            if (boundary && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>比較用の正規化。空白と大小文字の違いは書体の違いを意味しない。</summary>
    static string NormalizeFamily(string name) => name.Replace(" ", "").ToLowerInvariant();

    /// <summary>em フォントサイズ / CAD 文字高さ の係数。</summary>
    public static double EmPerCapHeight(string typeface, bool bold, bool italic)
    {
        var key = (typeface, bold, italic);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out double cached))
                return cached;
        }
        double factor = Measure(typeface, bold, italic);
        lock (Cache)
        {
            Cache[key] = factor;
        }
        return factor;
    }

    static double Measure(string typeface, bool bold, bool italic)
    {
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return FallbackEmPerCap;
        try
        {
            // EMSquare（フォント設計単位）を得る
            var otm = QueryOutlineMetrics(hdc, typeface, bold, italic, ProbeFontHeight);
            if (otm == null)
                return FallbackEmPerCap;
            int emSquare = (int)otm.Value.otmEMSquare;
            if (emSquare <= 0)
                return FallbackEmPerCap;

            // キャップハイト = 大文字 X のグリフ上端（ベースラインからの黒箱上端、設計単位）。
            // AutoCAD の TrueType 描画はこの値で Height を字面へ割り当てており、PDF印刷経路
            //（プロット PDF）の font-size と厳密一致する（実測: Arial 1466 → 3.5mm×2048/1466
            // =4.8895、MS Gothic 1576 → ×1.29949。GDI の otmsCapEmHeight は PCLT テーブル
            // 由来の既定値 em/2 が返り使えない。OS/2 sCapHeight も MS Gothic で不一致）
            int capHeight = MeasureGlyphTop(hdc, typeface, bold, italic, emSquare, 'X');
            if (capHeight <= 0)
            {
                // X を持たないフォントは OS/2 テーブルの sCapHeight で代替する
                capHeight = ReadOs2CapHeight(hdc, typeface, bold, italic, emSquare);
            }
            if (capHeight <= 0)
                return FallbackEmPerCap;
            return (double)emSquare / capHeight;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    // OS/2 テーブル内オフセット（OpenType 仕様）: version=0、sCapHeight=88（version 2 以降）
    const int Os2VersionOffset = 0;
    const int Os2CapHeightOffset = 88;
    const int Os2MinVersionWithCapHeight = 2;

    static int ReadOs2CapHeight(IntPtr hdc, string typeface, bool bold, bool italic, int emSquare)
    {
        return WithFont(hdc, typeface, bold, italic, emSquare, () =>
        {
            // GetFontData のテーブル指定はタグ文字列のリトルエンディアン詰め（'OS/2'）
            const uint Os2Tag = 0x322F534F;
            const uint GdiError = 0xFFFFFFFF;
            uint size = GetFontData(hdc, Os2Tag, 0, null, 0);
            if (size == GdiError || size < Os2CapHeightOffset + 2)
                return 0;
            var buf = new byte[size];
            if (GetFontData(hdc, Os2Tag, 0, buf, size) == GdiError)
                return 0;
            // フォントテーブルはビッグエンディアン
            int version = (buf[Os2VersionOffset] << 8) | buf[Os2VersionOffset + 1];
            if (version < Os2MinVersionWithCapHeight)
                return 0;
            return (short)((buf[Os2CapHeightOffset] << 8) | buf[Os2CapHeightOffset + 1]);
        });
    }

    static int MeasureGlyphTop(IntPtr hdc, string typeface, bool bold, bool italic,
                               int emSquare, char glyph)
    {
        return WithFont(hdc, typeface, bold, italic, emSquare, () =>
        {
            const uint GgoMetrics = 0;      // GGO_METRICS
            const uint GdiError = 0xFFFFFFFF;
            var mat = new MAT2 { M11Value = 1, M22Value = 1 }; // 恒等変換（固定小数の整数部）
            if (GetGlyphOutlineW(hdc, glyph, GgoMetrics, out var gm, 0, IntPtr.Zero, ref mat) == GdiError)
                return 0;
            // GlyphOrigin.Y は黒箱上端のベースラインからの高さ（Y上向き正）
            return gm.GlyphOriginY;
        });
    }

    static T? WithFont<T>(IntPtr hdc, string typeface, bool bold, bool italic, int height, Func<T> body)
    {
        const int FwNormal = 400, FwBold = 700;
        const uint DefaultCharset = 1;
        IntPtr font = CreateFontW(-height, 0, 0, 0, bold ? FwBold : FwNormal,
            italic ? 1u : 0u, 0, 0, DefaultCharset, 0, 0, 0, 0, typeface);
        if (font == IntPtr.Zero)
            return default;
        IntPtr old = SelectObject(hdc, font);
        try
        {
            return body();
        }
        finally
        {
            SelectObject(hdc, old);
            DeleteObject(font);
        }
    }

    static OUTLINETEXTMETRICW? QueryOutlineMetrics(IntPtr hdc, string typeface,
                                                   bool bold, bool italic, int height)
    {
        const int FwNormal = 400, FwBold = 700;          // wingdi.h のフォントウェイト
        const uint DefaultCharset = 1;                   // DEFAULT_CHARSET
        IntPtr font = CreateFontW(-height, 0, 0, 0, bold ? FwBold : FwNormal,
            italic ? 1u : 0u, 0, 0, DefaultCharset, 0, 0, 0, 0, typeface);
        if (font == IntPtr.Zero)
            return null;
        IntPtr old = SelectObject(hdc, font);
        try
        {
            uint size = GetOutlineTextMetricsW(hdc, 0, IntPtr.Zero);
            if (size == 0)
                return null;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetOutlineTextMetricsW(hdc, size, buf) == 0)
                    return null;
                return Marshal.PtrToStructure<OUTLINETEXTMETRICW>(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            SelectObject(hdc, old);
            DeleteObject(font);
        }
    }

    // OUTLINETEXTMETRICW の先頭部分（otmsCapEmHeight まで）。後続フィールドは可変長文字列
    // オフセットのため、必要なフィールドまでを定義して PtrToStructure で読む
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OUTLINETEXTMETRICW
    {
        public uint otmSize;
        public TEXTMETRICW otmTextMetrics;
        public byte otmFiller;
        public PANOSE otmPanoseNumber;
        public uint otmfsSelection;
        public uint otmfsType;
        public int otmsCharSlopeRise;
        public int otmsCharSlopeRun;
        public int otmItalicAngle;
        public uint otmEMSquare;
        public int otmAscent;
        public int otmDescent;
        public uint otmLineGap;
        public uint otmsCapEmHeight;
        public uint otmsXHeight;
        // 以降のフィールド（otmrcFontBox 等）は不使用
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TEXTMETRICW
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading;
        public int AveCharWidth, MaxCharWidth, Weight, Overhang;
        public int DigitizedAspectX, DigitizedAspectY;
        public char FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, TmCharSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PANOSE
    {
        public byte FamilyType, SerifStyle, Weight, Proportion, Contrast,
                    StrokeVariation, ArmStyle, Letterform, Midline, XHeight;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
        uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec,
        uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    struct MAT2
    {
        public short M11Fract, M11Value, M12Fract, M12Value;
        public short M21Fract, M21Value, M22Fract, M22Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GLYPHMETRICS
    {
        public uint BlackBoxX, BlackBoxY;
        public int GlyphOriginX, GlyphOriginY;
        public short CellIncX, CellIncY;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetOutlineTextMetricsW(IntPtr hdc, uint cb, IntPtr otm);
    [DllImport("gdi32.dll")]
    static extern uint GetFontData(IntPtr hdc, uint table, uint offset, byte[]? buffer, uint length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetGlyphOutlineW(IntPtr hdc, char ch, uint format, out GLYPHMETRICS gm,
        uint bufSize, IntPtr buffer, ref MAT2 mat2);
}
