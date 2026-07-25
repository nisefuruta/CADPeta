using System.Text;
using System.Xml;

namespace CadPeta.UseCases;

/// <summary>成果物の形式。拡張子ではなく、呼び出し側が意図した形式を明示する。</summary>
public enum ArtifactKind { Pdf, Svg, Emf, Png }

/// <summary>
/// 成果物を同一ディレクトリの一時ファイルへ書き、形式検証後にだけ公開する。
/// 既存ファイルがある場合も、書込・検証失敗では元ファイルを変更しない。
/// </summary>
public static class ArtifactFile
{
    public static void WriteAtomically(string destination, ArtifactKind kind, Action<string> write)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (write == null) throw new ArgumentNullException(nameof(write));
        string full = Path.GetFullPath(destination);
        string? dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir)) throw new ArgumentException("出力先ディレクトリを特定できない", nameof(destination));
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".bak");
        bool published = false;
        try
        {
            write(temp);
            Validate(temp, kind);
            if (File.Exists(full))
                File.Replace(temp, full, backup);
            else
                File.Move(temp, full);
            published = true;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* 公開済み成果物の成否を一時ファイル削除失敗で上書きしない */ }
            try
            {
                if (File.Exists(backup))
                {
                    // Replace が途中で失敗する希少経路では、旧成果物が backup へ移った後で
                    // 公開先だけが消えていることがある。その場合は旧成果物として戻し、
                    // 公開先が残っていれば backup は残骸なので削除する
                    if (!published && !File.Exists(full)) File.Move(backup, full);
                    else File.Delete(backup);
                }
            }
            catch { /* 本処理の成否をバックアップ後始末の失敗で上書きしない */ }
        }
    }

    public static void Validate(string path, ArtifactKind kind)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"成果物が生成されていない: {path}");
        if (info.Length == 0) throw new InvalidDataException($"成果物が空である: {path}");
        byte[] bytes = File.ReadAllBytes(path);
        switch (kind)
        {
            case ArtifactKind.Pdf: ValidatePdf(bytes, path); break;
            case ArtifactKind.Svg: ValidateSvg(bytes, path); break;
            case ArtifactKind.Emf: ValidateEmf(bytes, path); break;
            case ArtifactKind.Png: ValidatePng(bytes, path); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    static void ValidatePdf(byte[] b, string path)
    {
        if (b.Length < 16 || !AsciiEquals(b, 0, "%PDF-"))
            throw Invalid("PDFヘッダーがない", path);
        int end = b.Length - 1;
        while (end >= 0 && IsPdfWhitespace(b[end])) end--;
        if (end < 4 || !AsciiEquals(b, end - 4, "%%EOF"))
            throw Invalid("PDF終端がない（途中書きの可能性）", path);
        string tail = Encoding.ASCII.GetString(b, Math.Max(0, end - 4096), Math.Min(4096, end + 1));
        int startXref = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startXref < 0)
            throw Invalid("PDFのstartxrefがない", path);
        string after = tail.Substring(startXref + "startxref".Length).TrimStart();
        int lineEnd = after.IndexOfAny(new[] { '\r', '\n', ' ', '\t' });
        string number = lineEnd < 0 ? after : after.Substring(0, lineEnd);
        if (!long.TryParse(number, out long offset) || offset < 0 || offset >= b.Length)
            throw Invalid("PDFのstartxref位置が不正", path);
    }

    static void ValidateSvg(byte[] b, string path)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var stream = new MemoryStream(b, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read() && reader.NodeType != XmlNodeType.Element) { }
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "svg")
                throw Invalid("SVGルート要素がない", path);
            while (reader.Read()) { }
        }
        catch (XmlException ex) { throw Invalid("SVG XMLが完結していない", path, ex); }
    }

    static void ValidateEmf(byte[] b, string path)
    {
        const uint EmrHeader = 1, EmrEof = 14, EmfSignature = 0x464D4520;
        if (b.Length < 88 || U32(b, 0) != EmrHeader || U32(b, 40) != EmfSignature)
            throw Invalid("EMFヘッダーが不正", path);
        uint declaredBytes = U32(b, 48);
        if (declaredBytes != b.Length)
            throw Invalid("EMF宣言長とファイル長が一致しない", path);
        int offset = 0;
        uint lastType = 0;
        while (offset < b.Length)
        {
            if (offset > b.Length - 8) throw Invalid("EMFレコードヘッダーが途中で切れている", path);
            uint type = U32(b, offset), size = U32(b, offset + 4);
            if (size < 8 || (size & 3) != 0 || size > int.MaxValue || offset > b.Length - (int)size)
                throw Invalid("EMFレコード長が不正", path);
            lastType = type;
            offset += (int)size;
        }
        if (offset != b.Length || lastType != EmrEof)
            throw Invalid("EMF終端レコードがない（途中書きの可能性）", path);
    }

    static void ValidatePng(byte[] b, string path)
    {
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (b.Length < signature.Length || !BytesEqual(b, 0, signature))
            throw Invalid("PNGシグネチャがない", path);
        int offset = 8;
        bool sawHeader = false, sawData = false, sawEnd = false;
        while (offset < b.Length)
        {
            if (offset > b.Length - 12) throw Invalid("PNGチャンクが途中で切れている", path);
            uint length = U32Be(b, offset);
            if (length > int.MaxValue || offset > b.Length - 12 - (int)length)
                throw Invalid("PNGチャンク長が不正", path);
            string type = Encoding.ASCII.GetString(b, offset + 4, 4);
            if (!sawHeader && type != "IHDR") throw Invalid("PNG先頭チャンクがIHDRでない", path);
            if (type == "IHDR") { if (sawHeader || length != 13) throw Invalid("PNGのIHDRが不正", path); sawHeader = true; }
            if (type == "IDAT") sawData = true;
            if (type == "IEND")
            {
                if (length != 0) throw Invalid("PNGのIENDが不正", path);
                sawEnd = true;
            }
            offset += 12 + (int)length;
            if (sawEnd) break;
        }
        if (!sawHeader || !sawData || !sawEnd || offset != b.Length)
            throw Invalid("PNGが完結していない", path);
    }

    static uint U32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
    static uint U32Be(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    static bool AsciiEquals(byte[] b, int offset, string text)
    {
        if (offset < 0 || offset > b.Length - text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (b[offset + i] != (byte)text[i]) return false;
        return true;
    }
    static bool BytesEqual(byte[] b, int offset, byte[] expected)
    {
        if (offset < 0 || offset > b.Length - expected.Length) return false;
        for (int i = 0; i < expected.Length; i++) if (b[offset + i] != expected[i]) return false;
        return true;
    }
    static bool IsPdfWhitespace(byte value) => value == 0 || value == 9 || value == 10 || value == 12 || value == 13 || value == 32;
    static InvalidDataException Invalid(string reason, string path, Exception? inner = null) =>
        new($"成果物が不正: {reason}: {path}", inner);
}
