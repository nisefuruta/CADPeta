// IrImage.PngBytes の純マネージドPNGデコーダ（外部パッケージ参照ゼロの制約のため自前実装）。
// 圧縮解除は System.IO.Compression.DeflateStream（BCL 標準、netstandard2.0 で利用可）を使い、
// PNG 特有のフィルタ逆適用（5種）だけを自前で書く。対応範囲は非インターレースの
// ビット深度8（グレースケール／RGB／パレット／グレー+アルファ／RGBA）と、
// ビット深度1・2・4（グレースケール／パレット。OLE 貼付由来の CCITT G4 画像は取得層が
// 1bit Indexed PNG のまま IR へ保持するため、ここで受けられないと PDF 出力だけが落ちる）。
// それ以外（インターレース・16bit）はこのデコーダの対応範囲外として明示エラーにする。
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;

namespace CadPeta.Out.Pdf;

/// <summary>デコード結果。RGBA各8bit、行はトップダウン（先頭行が画像の上端）。</summary>
internal sealed class DecodedPng
{
    public int Width;
    public int Height;
    /// <summary>1画素4バイト（R,G,B,A）×Width×Height、トップダウン。</summary>
    public byte[] Rgba = Array.Empty<byte>();
}

internal static class PdfPngDecoder
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // PNG カラータイプ（PNG仕様 §11.2.2）
    const int ColorGray = 0, ColorRgb = 2, ColorPalette = 3, ColorGrayAlpha = 4, ColorRgba = 6;
    const int SupportedInterlace = 0; // 非インターレースのみ対応

    public static DecodedPng Decode(byte[] png)
    {
        if (png.Length < Signature.Length || !png.Take(Signature.Length).SequenceEqual(Signature))
            throw new NotSupportedException("PNG 署名が一致しない（PNG形式ではないか、破損している）");

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = -1;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();

        int pos = Signature.Length;
        while (pos + 8 <= png.Length)
        {
            uint len = ReadUInt32BE(png, pos);
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int dataOff = pos + 8;
            if (dataOff + (int)len + 4 > png.Length)
                throw new NotSupportedException($"PNG チャンク長が範囲外（破損している）: type={type} len={len}");

            switch (type)
            {
                case "IHDR":
                    width = (int)ReadUInt32BE(png, dataOff);
                    height = (int)ReadUInt32BE(png, dataOff + 4);
                    bitDepth = png[dataOff + 8];
                    colorType = png[dataOff + 9];
                    interlace = png[dataOff + 12];
                    break;
                case "PLTE":
                    palette = new byte[len];
                    Array.Copy(png, dataOff, palette, 0, (int)len);
                    break;
                case "tRNS":
                    trns = new byte[len];
                    Array.Copy(png, dataOff, trns, 0, (int)len);
                    break;
                case "IDAT":
                    idat.Write(png, dataOff, (int)len);
                    break;
                case "IEND":
                    goto doneChunks;
            }
            pos = dataOff + (int)len + 4; // +4 = CRC
        }
        doneChunks:

        if (colorType < 0)
            throw new NotSupportedException("PNG に IHDR チャンクがない（破損している）");
        if (interlace != SupportedInterlace)
            throw new NotSupportedException(
                $"インターレースPNGは対応範囲外（明示エラー）: interlace={interlace}");
        // ビット深度の対応範囲: 8 は全カラータイプ、1・2・4 はグレー／パレットのみ
        // （PNG 仕様 §11.2.2 の許容組と同じ）。16 は対応範囲外として明示エラー
        bool subByte = bitDepth is 1 or 2 or 4;
        if (bitDepth != 8 && !(subByte && colorType is ColorGray or ColorPalette))
            throw new NotSupportedException(
                $"対応範囲外のPNGビット深度（明示エラー）: bitDepth={bitDepth} colorType={colorType}");
        if (colorType is not (ColorGray or ColorRgb or ColorPalette or ColorGrayAlpha or ColorRgba))
            throw new NotSupportedException($"未知のPNGカラータイプ（明示エラー）: colorType={colorType}");
        if (colorType == ColorPalette && palette == null)
            throw new NotSupportedException("パレットPNGにPLTEチャンクがない（破損している）");

        byte[] raw = Inflate(idat.ToArray());

        int channels = colorType switch
        {
            ColorGray => 1,
            ColorRgb => 3,
            ColorPalette => 1,
            ColorGrayAlpha => 2,
            ColorRgba => 4,
            _ => throw new NotSupportedException($"未知のPNGカラータイプ: {colorType}"),
        };
        // stride はビット単位で計算し端数バイトへ切り上げる。フィルタの参照距離 bpp は
        // 1バイト未満にならない（PNG 仕様 §9.2: 8bit 未満は 1 バイトとして扱う）
        int stride = (width * channels * bitDepth + 7) / 8;
        int bpp = Math.Max(1, channels * bitDepth / 8);
        var unfiltered = Unfilter(raw, width, height, stride, bpp);
        if (subByte)
        {
            // 1・2・4bit のサンプル（MSB 先頭）を1バイト1サンプルへ展開する。
            // グレーは最大値が 255 になるよう線形伸長し、パレットは索引値のまま使う
            unfiltered = ExpandSubByte(unfiltered, width, height, stride, bitDepth,
                                       scaleToByte: colorType == ColorGray);
            stride = width;
        }

        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowOff = y * stride;
            int outRowOff = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int o = outRowOff + x * 4;
                switch (colorType)
                {
                    case ColorGray:
                    {
                        byte g = unfiltered[rowOff + x];
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                        rgba[o + 3] = 255;
                        break;
                    }
                    case ColorGrayAlpha:
                    {
                        byte g = unfiltered[rowOff + x * 2];
                        byte a = unfiltered[rowOff + x * 2 + 1];
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = g;
                        rgba[o + 3] = a;
                        break;
                    }
                    case ColorRgb:
                    {
                        int i = rowOff + x * 3;
                        rgba[o] = unfiltered[i]; rgba[o + 1] = unfiltered[i + 1]; rgba[o + 2] = unfiltered[i + 2];
                        rgba[o + 3] = 255;
                        break;
                    }
                    case ColorRgba:
                    {
                        int i = rowOff + x * 4;
                        rgba[o] = unfiltered[i]; rgba[o + 1] = unfiltered[i + 1];
                        rgba[o + 2] = unfiltered[i + 2]; rgba[o + 3] = unfiltered[i + 3];
                        break;
                    }
                    case ColorPalette:
                    {
                        int idx = unfiltered[rowOff + x];
                        int pOff = idx * 3;
                        if (pOff + 2 >= palette!.Length)
                            throw new NotSupportedException(
                                $"パレット索引がPLTE範囲外（破損している）: idx={idx}");
                        rgba[o] = palette[pOff]; rgba[o + 1] = palette[pOff + 1]; rgba[o + 2] = palette[pOff + 2];
                        rgba[o + 3] = trns != null && idx < trns.Length ? trns[idx] : (byte)255;
                        break;
                    }
                }
            }
        }
        return new DecodedPng { Width = width, Height = height, Rgba = rgba };
    }

    /// <summary>1・2・4bit のスキャンライン（MSB 先頭詰め）を1バイト1サンプルへ展開する。
    /// scaleToByte はグレー値の線形伸長（最大値→255）。パレット索引は値のまま返す。</summary>
    static byte[] ExpandSubByte(byte[] packed, int width, int height, int packedStride,
                                int bitDepth, bool scaleToByte)
    {
        var outBuf = new byte[width * height];
        int samplesPerByte = 8 / bitDepth;
        int maxValue = (1 << bitDepth) - 1;
        for (int y = 0; y < height; y++)
        {
            int rowOff = y * packedStride;
            for (int x = 0; x < width; x++)
            {
                byte b = packed[rowOff + x / samplesPerByte];
                int shift = 8 - bitDepth - (x % samplesPerByte) * bitDepth;
                int v = (b >> shift) & maxValue;
                outBuf[y * width + x] = scaleToByte ? (byte)(v * 255 / maxValue) : (byte)v;
            }
        }
        return outBuf;
    }

    static uint ReadUInt32BE(byte[] b, int off) =>
        ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    /// <summary>zlib（RFC1950）ストリームを解凍する。DeflateStream は生deflate（RFC1951）のみ
    /// 扱うため、2バイトのzlibヘッダを読み飛ばして渡す（末尾4バイトのAdler32検査は行わない。
    /// 解凍自体の成否はDeflateStreamが検査するため、内容の破損検出としては十分）。</summary>
    static byte[] Inflate(byte[] zlibData)
    {
        if (zlibData.Length < 2)
            throw new NotSupportedException("IDAT データが短すぎる（破損している）");
        using var src = new MemoryStream(zlibData, 2, zlibData.Length - 2, writable: false);
        using var deflate = new DeflateStream(src, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        deflate.CopyTo(outMs);
        return outMs.ToArray();
    }

    /// <summary>PNG のスキャンライン逆フィルタ（None/Sub/Up/Average/Paeth、PNG仕様§9）。</summary>
    static byte[] Unfilter(byte[] raw, int width, int height, int stride, int bpp)
    {
        var outBuf = new byte[stride * height];
        int inPos = 0;
        var prevRow = new byte[stride];
        var curRow = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            if (inPos >= raw.Length)
                throw new NotSupportedException("PNG のスキャンラインデータが不足している（破損している）");
            int filter = raw[inPos++];
            if (inPos + stride > raw.Length)
                throw new NotSupportedException("PNG のスキャンラインデータが不足している（破損している）");
            Array.Copy(raw, inPos, curRow, 0, stride);
            inPos += stride;

            for (int i = 0; i < stride; i++)
            {
                byte a = i >= bpp ? curRow[i - bpp] : (byte)0; // 左
                byte b = prevRow[i];                            // 上
                byte c = i >= bpp ? prevRow[i - bpp] : (byte)0;  // 左上
                byte x = curRow[i];
                curRow[i] = filter switch
                {
                    0 => x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + (a + b) / 2),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => throw new NotSupportedException($"未知のPNGフィルタ種別（明示エラー）: {filter}"),
                };
            }
            Array.Copy(curRow, 0, outBuf, y * stride, stride);
            (prevRow, curRow) = (curRow, prevRow);
        }
        return outBuf;
    }

    static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
