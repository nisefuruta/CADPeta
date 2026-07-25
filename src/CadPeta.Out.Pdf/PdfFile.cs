// PDF ファイルの最小の低レベル組み立て（オブジェクト表・xref・trailer）。
// クラシックxref（オブジェクトストリームは使わない）で、あらゆるPDFビューアが読める
// 最も枯れた形式に寄せる。ストリームは常に無圧縮（呼び出し側がそのまま渡したバイト列）。
using System.Globalization;
using System.Text;

namespace CadPeta.Out.Pdf;

/// <summary>数値の文字列化（座標・行列成分に共用）。SvgWriter.N/T と同じ理由
/// （.NET Framework と .NET Core 系で "-0" の文字列化が割れるため "0" へ畳む）で
/// 決定的にする。</summary>
internal static class PdfNum
{
    // 4桁: mm量子として十分（SvgWriter.N と同一桁数）。この PDF ライタは座標を
    // mm のまま書き、ページ先頭の cm 一回だけで pt へ変換する（PdfWriter 参照）
    public static string N(double v) => Fold(v.ToString("0.####", CultureInfo.InvariantCulture));

    // 6桁: 変換行列成分用。回転成分の丸め誤差は座標の大きさ倍で効くため座標より細かく書く
    // （SvgWriter.T と同一桁数・同一理由）
    public static string M(double v) => Fold(v.ToString("0.######", CultureInfo.InvariantCulture));

    static string Fold(string s) => s == "-0" ? "0" : s;
}

/// <summary>間接オブジェクト1件。Stream が非nullならストリームオブジェクト（/Length は自動付与）。</summary>
internal sealed class PdfObject
{
    public int Id;
    /// <summary>辞書の中身（"<<" "&gt;&gt;" を含まない、または連想配列を含む本体そのもの）。</summary>
    public string Dict = "";
    public byte[]? Stream;
}

/// <summary>PDFファイル全体の組み立て。オブジェクトを登録順に確定し、最後に
/// ヘッダ・xref・trailer をまとめて書く。</summary>
internal sealed class PdfFile
{
    readonly List<PdfObject> _objects = new();
    int _nextId = 1;

    public int Allocate() => _nextId++;

    public void Add(PdfObject obj) => _objects.Add(obj);

    public int AddDict(string dict)
    {
        int id = Allocate();
        Add(new PdfObject { Id = id, Dict = dict });
        return id;
    }

    public int AddStream(string extraDict, byte[] data)
    {
        int id = Allocate();
        Add(new PdfObject { Id = id, Dict = extraDict, Stream = data });
        return id;
    }

    /// <summary>ヘッダ・全オブジェクト・xref・trailer を直列化してファイル全体のバイト列を返す。</summary>
    public byte[] Build(int catalogId)
    {
        // PDF構造はASCII範囲の可変長トークンのみのため Latin1 で1バイト=1文字を保証する
        // （UTF-8だとマルチバイト化してオフセット計算が壊れる。図面由来の文字列はここに現れない
        // ——文字はグリフ輪郭ポートで path 化するため、PDF側に文字列オペランドを持たない）
        var enc = Encoding.GetEncoding("Latin1");
        using var buf = new MemoryStream();
        void WriteAscii(string s)
        {
            var bytes = enc.GetBytes(s);
            buf.Write(bytes, 0, bytes.Length);
        }

        // バイナリを含み得るファイルであることを示す慣用のコメント行（PDF32000-1 §7.5.2）
        WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        var offsets = new long[_nextId]; // index 0 は未使用（xref先頭のフリーエントリ用）
        foreach (var obj in _objects)
        {
            offsets[obj.Id] = buf.Position;
            WriteAscii($"{obj.Id} 0 obj\n");
            if (obj.Stream == null)
            {
                WriteAscii("<< " + obj.Dict + " >>\nendobj\n");
            }
            else
            {
                WriteAscii("<< " + obj.Dict + $" /Length {obj.Stream.Length} >>\nstream\n");
                buf.Write(obj.Stream, 0, obj.Stream.Length);
                WriteAscii("\nendstream\nendobj\n");
            }
        }

        long xrefOffset = buf.Position;
        WriteAscii($"xref\n0 {_nextId}\n");
        WriteAscii("0000000000 65535 f \n");
        for (int id = 1; id < _nextId; id++)
        {
            // 各エントリはちょうど20バイト（PDF32000-1 §7.5.4）: 10桁オフセット+空白+5桁世代+空白+n/f+空白+EOL
            WriteAscii($"{offsets[id]:D10} 00000 n \n");
        }
        WriteAscii($"trailer\n<< /Size {_nextId} /Root {catalogId} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return buf.ToArray();
    }
}
