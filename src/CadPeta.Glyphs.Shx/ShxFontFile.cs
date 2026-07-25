// SHX フォントファイルのバイナリ解析（unifont・bigfont・shapes の3形式）。
// unifont・bigfont のバイト配置は本機実物6ファイル・27,373グリフの全走査で実測したものに従う。
// shapes 形式のバイト配置は本機実物 ltypeshp.shx（複雑線種の記号シェイプ集）の実測で確定した
// （実測ツール: tests/tools/cad-typography/shx_binary_survey.py、本ファイルは製品コードとして独立実装）。
using System.Text;

namespace CadPeta.Glyphs.Shx;

/// <summary>SHX ファイルの形式種別。署名文字列（"AutoCAD-86 &lt;種別&gt; &lt;版&gt;"）から判定する。
/// shapes 1.1 は本機実物に存在せず未検証のため、署名判定では 1.0 のみを受理する。</summary>
public enum ShxFontKind
{
    Unifont,
    BigFont,
    /// <summary>シェイプ番号＋名前で引く形式。複雑線種の記号（線種定義がシェイプ名で参照する
    /// マーク図形）に使われ、テキストフォントとしての shape0（above/below/modes）を
    /// 持たないファイルがある（本機実物 ltypeshp.shx で確認）。</summary>
    Shapes,
}

/// <summary>SHX ファイル1本の解析結果。</summary>
public sealed class ShxFontFile
{
    public ShxFontKind Kind { get; }

    /// <summary>ビッグフォントの拡張形式か（code7,0 の引数がbasepoint+幅+高さの別々指定か、高さのみか）。
    /// unifont では常に false。</summary>
    public bool IsExtended { get; }

    /// <summary>ベースラインから上方向の基準高さ（フォント単位）。大文字の頂点高さ（キャップハイト）に一致し、
    /// グリフ輪郭のスケール基準として使う。0以下は破損データとして Load が拒否する。</summary>
    public int Above { get; }

    /// <summary>ベースラインから下方向の基準深さ（フォント単位）。</summary>
    public int Below { get; }

    public int Modes { get; }

    /// <summary>グリフコード→バイトコード本体。code=0（shape0=フォント諸元）は含まない。</summary>
    public IReadOnlyDictionary<int, byte[]> Glyphs { get; }

    /// <summary>ビッグフォントの変更テーブル（2バイト文字と判定すべきリードバイト範囲）。
    /// 本解釈器はグリフ解決に使わない（文字→グリフの解決は CP932 コードでの直接引きで足りる）ため、
    /// バイト境界を正しく辿るための読み飛ばし専用に保持する（診断・テスト用に公開）。unifont では空。</summary>
    public IReadOnlyList<(int Lo, int Hi)> ChangeTable { get; }

    /// <summary>shapes 形式のシェイプ名（大文字ASCII）→シェイプ番号。線種定義がシェイプを名前で
    /// 参照する（例: 線種の記号シェイプ指定）ための解決に使う。unifont・bigfont は空
    /// （名前は各グリフの説明文字列として埋め込まれてはいるが、文字→グリフの解決には使わないため
    /// 保持しない）。</summary>
    public IReadOnlyDictionary<string, int> ShapeNames { get; }

    /// <summary>shape0（code=0、above/below/modes を持つフォント諸元）が実在したか。
    /// unifont・bigfont は構造上 shape0 が必須（無ければ Load が失敗する）なので常に true。
    /// shapes 形式は shape0 を持たないファイルが実在する（複雑線種の記号シェイプ集はテキスト
    /// フォントではないため above 基準を持つ必要がない）ため false になり得る。</summary>
    public bool HasShape0 { get; }

    ShxFontFile(ShxFontKind kind, bool isExtended, int above, int below, int modes,
                IReadOnlyDictionary<int, byte[]> glyphs, IReadOnlyList<(int, int)> changeTable,
                IReadOnlyDictionary<string, int>? shapeNames = null, bool hasShape0 = true)
    {
        Kind = kind;
        IsExtended = isExtended;
        Above = above;
        Below = below;
        Modes = modes;
        Glyphs = glyphs;
        ChangeTable = changeTable;
        ShapeNames = shapeNames ?? EmptyShapeNames;
        HasShape0 = hasShape0;
    }

    static readonly IReadOnlyDictionary<string, int> EmptyShapeNames =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public static ShxFontFile Load(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new IOException($"SHX ファイルを読めない: {path}\n{ex}", ex);
        }
        return Parse(data, path);
    }

    static readonly object CacheLock = new();
    static readonly Dictionary<string, ShxFontFile> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SHX ファイルの解析結果をパスごとにキャッシュして返す（文字輪郭化・線種記号展開の双方で
    /// 同一フォントを繰り返し解析しないための共有キャッシュ）。ファイルIOはロック外で行う。
    /// </summary>
    public static ShxFontFile LoadCached(string path)
    {
        string key = Path.GetFullPath(path);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        var loaded = Load(path);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var raced)) return raced; // 競合時は先に登録された方を使う
            Cache[key] = loaded;
            return loaded;
        }
    }

    /// <summary>バイト列から直接解析する（テスト用に公開）。</summary>
    public static ShxFontFile Parse(byte[] data, string sourceForError)
    {
        var (signature, contentStart) = ParseHeader(data, sourceForError);
        var parts = signature.Split(' ');
        string fontType = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        // バージョンは "AutoCAD-86 shapes 1.0" の3語目。1.1 は本機実物での実測が無いため、
        // 無言で誤読しないよう明示的に未対応とする（1.0 のみ受理）。
        string version = parts.Length > 2 ? parts[2] : "";

        ShxFontFile font = fontType switch
        {
            "shapes" when version == "1.0" => ParseShapes(data, contentStart),
            "shapes" => throw new NotSupportedException(
                $"SHX shapes 形式は版 '{version}' が未対応（実測済みは 1.0 のみ）。" +
                $"署名: '{signature}' ファイル: {sourceForError}"),
            "unifont" => ParseUnifont(data, contentStart),
            "bigfont" => ParseBigFont(data, contentStart),
            _ => throw new NotSupportedException(
                $"未知の SHX 形式（署名から判定できない）: '{signature}' ファイル: {sourceForError}"),
        };

        // shapes 形式は shape0（above 等のフォント諸元）を持たないファイルが実在する
        // （複雑線種の記号シェイプ集はテキストフォントではないため）。この場合は above=0 が
        // 正常値であり、破損データの判定から除外する。
        if (font.HasShape0 && font.Above <= 0)
            throw new InvalidDataException(
                $"shape0 の above（キャップハイト基準）が0以下: {font.Above}（破損データまたは未対応の shape0 構造） " +
                $"ファイル: {sourceForError}");

        return font;
    }

    // -----------------------------------------------------------------
    // ヘッダ解析
    // -----------------------------------------------------------------

    /// <summary>ヘッダはテキストで "\r\n\x1a" 終端。AutoCAD の旧来テキストファイル EOF 慣習
    /// （Ctrl-Z=0x1A）に由来する。終端が見つからない場合は不正データとして拒否する。</summary>
    static (string Signature, int ContentStart) ParseHeader(byte[] data, string sourceForError)
    {
        int pos = 0;
        var sb = new List<byte>();
        int n = data.Length;
        bool found = false;
        while (pos < n - 2)
        {
            byte b1 = data[pos];
            pos++;
            if (b1 == 0x0D && data[pos] == 0x0A && data[pos + 1] == 0x1A)
            {
                pos += 2;
                found = true;
                break;
            }
            sb.Add(b1);
        }
        if (!found)
            throw new InvalidDataException(
                $"SHX ヘッダ終端（0x0D 0x0A 0x1A）が見つからない。ファイル: {sourceForError}");

        string sig = Encoding.ASCII.GetString(sb.ToArray()).Trim();
        return (sig, pos);
    }

    // -----------------------------------------------------------------
    // unifont
    // -----------------------------------------------------------------

    static ShxFontFile ParseUnifont(byte[] data, int contentStart)
    {
        var r = new ByteCursor(data, contentStart);
        int count = r.I32();       // shape0 を含む総エントリ数
        short infoLen = r.I16();
        byte[] info = r.Bytes(infoLen);

        var glyphs = new Dictionary<int, byte[]>();
        for (int k = 0; k < count - 1; k++)
        {
            ushort code = r.U16();
            ushort length = r.U16();
            if (length > 0)
            {
                byte[] raw = r.Bytes(length);
                var (_, bytecode) = SplitNameAndBytecode(raw);
                if (bytecode.Length > 0)
                    glyphs[code] = bytecode;
            }
        }

        var (above, below, modes) = ParseShape0MetricsSimple(info);
        return new ShxFontFile(ShxFontKind.Unifont, isExtended: false, above, below, modes,
                                glyphs, Array.Empty<(int, int)>(), hasShape0: true);
    }

    /// <summary>shape0 = [ASCII文字列]\0[above:1][below:1][modes:1] という並び
    /// （unifont・shapes 形式共通）。並びに満たない短いデータは above=below=modes=0（既定値）のまま返す。</summary>
    static (int Above, int Below, int Modes) ParseShape0MetricsSimple(byte[] raw)
    {
        int termIdx = -1;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] is 0x0D or 0x0A or 0x00) { termIdx = i; break; }
        }
        if (termIdx < 0 || termIdx + 3 >= raw.Length)
            return (0, 0, 0);
        return (raw[termIdx + 1], raw[termIdx + 2], raw[termIdx + 3]);
    }

    /// <summary>シェイプ名（大文字ASCII）+ NUL + バイトコード の形。NUL が無ければ名前無しとして
    /// 全体をバイトコード扱いする。</summary>
    static (string? Name, byte[] Bytecode) SplitNameAndBytecode(byte[] b)
    {
        int idx = Array.IndexOf(b, (byte)0);
        if (idx < 0) return (null, b);
        var result = new byte[b.Length - idx - 1];
        Array.Copy(b, idx + 1, result, 0, result.Length);
        string? name = idx > 0 ? Encoding.ASCII.GetString(b, 0, idx) : null;
        return (name, result);
    }

    // -----------------------------------------------------------------
    // shapes
    // -----------------------------------------------------------------

    /// <summary>
    /// AutoCAD-86 shapes 1.0 形式。線種定義がシェイプ名／シェイプ番号で参照する記号図形の集合
    /// （本機実物 ltypeshp.shx で実測）。索引は unifont のような固定ヘッダではなく、
    /// (先頭シェイプ番号, 末尾シェイプ番号, エントリ数) に続けて (code, length) の並びが来る。
    /// 本体は索引の並び順のまま (name, NUL, bytecode) で連続配置される。
    /// shape0（code=0）は任意（線種記号専用ファイルには無い）。存在すれば unifont と同じ
    /// [名前]\0[above][below][modes] の並びとして扱う。
    /// </summary>
    static ShxFontFile ParseShapes(byte[] data, int contentStart)
    {
        var r = new ByteCursor(data, contentStart);
        r.U16(); // 先頭シェイプ番号（索引の実エントリで十分読み取れるため保持しない）
        r.U16(); // 末尾シェイプ番号（同上）
        short count = r.I16();

        var index = new (int Code, int Length)[count];
        for (int k = 0; k < count; k++)
        {
            ushort code = r.U16();
            ushort length = r.U16();
            index[k] = (code, length);
        }

        var glyphs = new Dictionary<int, byte[]>();
        var shapeNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        byte[]? shape0Raw = null;
        foreach (var (code, length) in index)
        {
            if (length <= 0) continue;
            byte[] raw = r.Bytes(length);
            var (name, bytecode) = SplitNameAndBytecode(raw);
            if (code == 0)
            {
                shape0Raw = raw; // shape0 は諸元。ParseShape0MetricsSimple が生バイト列から直接読む
                continue;
            }
            if (bytecode.Length > 0)
                glyphs[code] = bytecode;
            if (name != null)
                shapeNames[name] = code; // 同名重複は後勝ち（実物での重複は未確認）
        }

        bool hasShape0 = shape0Raw != null;
        var (above, below, modes) = hasShape0 ? ParseShape0MetricsSimple(shape0Raw!) : (0, 0, 0);
        return new ShxFontFile(ShxFontKind.Shapes, isExtended: false, above, below, modes,
                                glyphs, Array.Empty<(int, int)>(), shapeNames, hasShape0);
    }

    // -----------------------------------------------------------------
    // bigfont
    // -----------------------------------------------------------------

    static ShxFontFile ParseBigFont(byte[] data, int contentStart)
    {
        var r = new ByteCursor(data, contentStart);
        r.I16(); // ヘッダ直後の予約フィールド（実測: 用途未確定。読み飛ばすのみで値は使わない）
        short count = r.I16();
        short changeNumber = r.I16();

        var changeTable = new List<(int, int)>();
        for (int k = 0; k < changeNumber; k++)
        {
            ushort lo = r.U16();
            ushort hi = r.U16();
            changeTable.Add((lo, hi));
        }

        var entries = new List<(int Code, int Length, uint Offset)>();
        for (int k = 0; k < count; k++)
        {
            ushort code = r.U16();
            ushort length = r.U16();
            uint offset = r.U32();
            if (!(code == 0 && length == 0 && offset == 0)) // 全ゼロエントリは空きスロット
                entries.Add((code, length, offset));
        }

        var glyphs = new Dictionary<int, byte[]>();
        byte[]? shape0Raw = null;
        foreach (var e in entries)
        {
            if (e.Offset > int.MaxValue || (long)e.Offset + e.Length > data.Length)
                throw new InvalidDataException(
                    $"グリフ code=0x{e.Code:x4} のオフセットがファイル範囲を超える" +
                    $"（offset=0x{e.Offset:x}, length={e.Length}, ファイルサイズ={data.Length}）");
            var raw = new byte[e.Length];
            Array.Copy(data, (int)e.Offset, raw, 0, e.Length);
            glyphs[e.Code] = raw;
            if (e.Code == 0) shape0Raw = raw;
        }

        if (shape0Raw == null)
            throw new InvalidDataException("ビッグフォントに shape0（code=0、フォント諸元）が無い");

        var (above, below, modes, isExtended) = ParseBigfontMetrics(shape0Raw);
        glyphs.Remove(0); // shape0 はフォント諸元であり描画対象グリフではない
        return new ShxFontFile(ShxFontKind.BigFont, isExtended, above, below, modes, glyphs, changeTable);
    }

    /// <summary>
    /// bigfont の shape0 は 説明文字列 + NUL終端 + 数値メタ、という並びだが、末尾の残りバイト数の
    /// パターンによって extended（幅・高さを別々に持つ）/ non-extended が分かれる。
    /// 実測で判明した隠れ分岐（残り4バイトでも [height,0,modes,width] 並びのケース）を含む
    /// （見逃すと code7 拡張引数の読み取りバイト数が1バイトずれ、後続グリフの解釈が雪崩式に破綻する）。
    /// </summary>
    static (int Above, int Below, int Modes, bool IsExtended) ParseBigfontMetrics(byte[] raw)
    {
        int nul = Array.IndexOf(raw, (byte)0);
        if (nul < 0) return (0, 0, 0, false);

        var tail = new List<byte>(raw.Length - nul - 1);
        for (int i = nul + 1; i < raw.Length; i++) tail.Add(raw[i]);
        while (tail.Count > 0 && tail[tail.Count - 1] == 0) tail.RemoveAt(tail.Count - 1); // 末尾の0パディングを剥がす

        int remaining = tail.Count;
        if (remaining >= 5)
        {
            // extended: height, 0, modes, width, [0]
            return (tail[0], 0, tail[2], true);
        }
        if (remaining == 4 && tail[1] == 0 && tail[3] > 0 && tail[3] != tail[0])
        {
            // 実測で判明した特殊分岐（extfont.shx 系列）: 残り4バイトでも
            // [height, 0, modes, width] という拡張形式と同じ並びになっているケース。
            // tail[1]==0 かつ tail[3]!=tail[0] かつ tail[3]>0 という条件でのみ拡張扱いとする。
            return (tail[0], 0, tail[2], true);
        }
        if (remaining == 4)
        {
            return (tail[0], tail[1], tail[2], false);
        }
        if (remaining == 3)
        {
            // 実測: bigfont.shx（modes=2）はこの分岐に該当し、非拡張（1バイト height のみ）として
            // 全3664グリフをエラーなく解釈できることを確認済み。
            return (tail[0], 0, tail[1], false);
        }
        return (0, 0, 0, false);
    }

    // -----------------------------------------------------------------
    // バイト列カーソル
    // -----------------------------------------------------------------

    /// <summary>バイト列を先頭から順に読み進めるだけの単純なカーソル。範囲外読み取りは例外にする。</summary>
    sealed class ByteCursor
    {
        readonly byte[] _data;
        int _pos;

        public ByteCursor(byte[] data, int start)
        {
            _data = data;
            _pos = start;
        }

        public byte[] Bytes(int n)
        {
            if (n < 0 || _pos + n > _data.Length)
                throw new EndOfStreamException(
                    $"要求 {n} バイトに対しデータが不足（位置 {_pos}、データ長 {_data.Length}）");
            var result = new byte[n];
            Array.Copy(_data, _pos, result, 0, n);
            _pos += n;
            return result;
        }

        public ushort U16() { var b = Bytes(2); return (ushort)(b[0] | (b[1] << 8)); }
        public short I16() { return unchecked((short)U16()); }
        public uint U32()
        {
            var b = Bytes(4);
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }
        public int I32() { return unchecked((int)U32()); }
    }
}
