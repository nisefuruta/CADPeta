// CP932（Shift_JIS）への Unicode 変換。
// 実行系の Encoding.GetEncoding(932) を使わない理由: .NET Framework は OS の NLS、
// .NET 8/10 は System.Text.Encoding.CodePages パッケージの独自データを使い、
// ベストフィット変換の縁で写像が食い違い得る。埋め込み表を使えば 3系 TFM・
// ホスト環境に依らずバイト決定的になる（表の生成手順は tests/tools/cad-typography/gen_cp932_table.ps1）。
using System.Reflection;

namespace CadPeta.Glyphs.Shx;

/// <summary>
/// Unicode → CP932 の変換。埋め込みリソース Cp932Encode.bin（リトルエンディアンの
/// (uint16 unicode, uint16 code) 連続配置、Unicode 昇順）を初回参照時に一度だけ読み込む。
/// </summary>
public static class Cp932
{
    // U+0000-007F は ASCII と CP932 の 1 バイト表現が恒等のため、表へ含めない
    // （生成側 gen_cp932_table.ps1 のコメントと同一の規約）。
    const char AsciiRangeMaxInclusive = (char)0x7F;

    static readonly object Lock = new();
    static ushort[]? _unicodes; // 二分探索キー（昇順）
    static ushort[]? _codes;    // _unicodes と同じ添字に対応する CP932 コード

    /// <summary>
    /// 文字を CP932 コードへ変換する。1バイト符号（半角カナ等）は 0xA1-0xDF の
    /// バイト値そのもの、2バイト符号は (先行バイト&lt;&lt;8)|後続バイト を返す。
    /// 未収載文字（CP932 で表現できない文字）は false。
    /// </summary>
    public static bool TryGetCode(char ch, out ushort code)
    {
        if (ch <= AsciiRangeMaxInclusive)
        {
            code = ch;
            return true;
        }

        EnsureLoaded();
        int idx = Array.BinarySearch(_unicodes!, ch);
        if (idx < 0)
        {
            code = 0;
            return false;
        }
        code = _codes![idx];
        return true;
    }

    static void EnsureLoaded()
    {
        if (_unicodes != null) return;
        lock (Lock)
        {
            if (_unicodes != null) return;

            var asm = typeof(Cp932).GetTypeInfo().Assembly;
            string? resName = null;
            foreach (var n in asm.GetManifestResourceNames())
            {
                if (n.EndsWith("Cp932Encode.bin", StringComparison.Ordinal)) { resName = n; break; }
            }
            if (resName == null)
                throw new InvalidOperationException(
                    "埋め込みリソース Cp932Encode.bin が見つからない（ビルド構成の破損の疑い）");

            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new InvalidOperationException($"埋め込みリソース {resName} を開けない");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] buf = ms.ToArray();

            const int EntrySizeBytes = 4; // uint16 unicode + uint16 code
            if (buf.Length % EntrySizeBytes != 0)
                throw new InvalidDataException(
                    $"Cp932Encode.bin のサイズが不正（{EntrySizeBytes}の倍数でない）: {buf.Length} バイト");

            int count = buf.Length / EntrySizeBytes;
            var unicodes = new ushort[count];
            var codes = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int off = i * EntrySizeBytes;
                unicodes[i] = (ushort)(buf[off] | (buf[off + 1] << 8));
                codes[i] = (ushort)(buf[off + 2] | (buf[off + 3] << 8));
                if (i > 0 && unicodes[i] <= unicodes[i - 1])
                    throw new InvalidDataException(
                        $"Cp932Encode.bin が Unicode 昇順でない（二分探索の前提が崩れる）: index={i}");
            }
            _codes = codes;
            _unicodes = unicodes; // 最後に代入（他スレッドが _unicodes!=null を見た時点で _codes も揃っている）
        }
    }
}
