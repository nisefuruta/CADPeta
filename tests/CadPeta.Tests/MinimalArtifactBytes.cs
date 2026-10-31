using System.Linq;
using System.Text;
using CadPeta.UseCases;

namespace CadPeta.Tests;

/// <summary>
/// ArtifactFile.Validate を通る最小の成果物バイト列。複数テストで同じ最小EMF/PDFを
/// 組み立てていた重複を一本化する（CadPeta.Gui.Tests へはリンク共有）。
/// </summary>
internal static class MinimalArtifactBytes
{
    /// <summary>startxref の指示先がファイル内であることまでを検証で通る最小PDF。</summary>
    public static byte[] Pdf() => Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<<>>endobj\nstartxref\n9\n%%EOF\n");

    /// <summary>ヘッダー＋EOFレコードだけの完結したEMF。marker は未使用領域(offset 56)へ書き、個体識別に使う。</summary>
    public static byte[] Emf(uint marker = 0)
    {
        var b = new byte[108];
        PutU32(b, 0, 1); PutU32(b, 4, 88); PutU32(b, 40, 0x464D4520); PutU32(b, 48, (uint)b.Length);
        PutU32(b, 52, 2); PutU32(b, 56, marker); PutU32(b, 88, 14); PutU32(b, 92, 20);
        return b;
    }

    /// <summary>ルート要素だけの SVG。</summary>
    public static byte[] Svg() => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

    /// <summary>シグネチャと IHDR・IDAT・IEND のチャンクだけの PNG（検査はチャンクの並びと長さだけを見る）。</summary>
    public static byte[] Png() =>
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }
            .Concat(Chunk("IHDR", 13)).Concat(Chunk("IDAT", 0)).Concat(Chunk("IEND", 0)).ToArray();

    /// <summary>形式ごとの最小の成果物。</summary>
    public static byte[] Of(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Pdf => Pdf(),
        ArtifactKind.Svg => Svg(),
        ArtifactKind.Emf => Emf(),
        _ => Png(),
    };

    // 長さ（ビッグエンディアン）・種類・中身（0）・CRC（0）の PNG のチャンク
    static byte[] Chunk(string type, int length) =>
        new byte[] { 0, 0, 0, (byte)length }.Concat(Encoding.ASCII.GetBytes(type)).Concat(new byte[length + 4]).ToArray();

    public static void PutU32(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)value; b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16); b[offset + 3] = (byte)(value >> 24);
    }

    public static uint ReadU32(byte[] b, int offset) =>
        (uint)(b[offset] | b[offset + 1] << 8 | b[offset + 2] << 16 | b[offset + 3] << 24);
}
