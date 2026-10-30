// PdfPngDecoder（自前の純マネージドPNGデコーダ）の単体検証。
// 実PNG（GDI+で生成）との往復で画素無損失を確認し、対応範囲外形式（インターレース・
// ビット深度8以外）が明示エラーになることを、実PNGのIHDRバイトを書き換えて検証する。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using CadPeta.Out.Pdf;
using Xunit;

namespace CadPeta.Tests;

public class PdfPngDecoderTests
{
    [Fact]
    public void RGBAの往復で画素無損失()
    {
        using var bmp = new Bitmap(4, 3, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 200, 100, 50));
        bmp.SetPixel(1, 0, Color.FromArgb(128, 10, 20, 30));
        bmp.SetPixel(2, 0, Color.FromArgb(0, 1, 2, 3));
        bmp.SetPixel(3, 0, Color.FromArgb(255, 255, 255, 255));
        bmp.SetPixel(0, 1, Color.FromArgb(255, 0, 0, 0));
        bmp.SetPixel(1, 1, Color.FromArgb(64, 90, 80, 70));
        bmp.SetPixel(2, 1, Color.FromArgb(200, 5, 250, 5));
        bmp.SetPixel(3, 1, Color.FromArgb(255, 128, 128, 128));
        bmp.SetPixel(0, 2, Color.FromArgb(1, 9, 9, 9));
        bmp.SetPixel(1, 2, Color.FromArgb(255, 33, 66, 99));
        bmp.SetPixel(2, 2, Color.FromArgb(255, 250, 0, 250));
        bmp.SetPixel(3, 2, Color.FromArgb(30, 30, 30, 30));

        var decoded = PdfPngDecoder.Decode(TestImages.EncodePng(bmp));

        Assert.Equal(4, decoded.Width);
        Assert.Equal(3, decoded.Height);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
            {
                var expected = bmp.GetPixel(x, y);
                int o = (y * 4 + x) * 4;
                Assert.True(expected.A == decoded.Rgba[o + 3] && expected.R == decoded.Rgba[o]
                         && expected.G == decoded.Rgba[o + 1] && expected.B == decoded.Rgba[o + 2],
                    $"({x},{y}): expected {expected} actual R={decoded.Rgba[o]} G={decoded.Rgba[o + 1]} " +
                    $"B={decoded.Rgba[o + 2]} A={decoded.Rgba[o + 3]}");
            }
    }

    [Fact]
    public void 不透明RGBの往復で画素無損失()
    {
        using var bmp = new Bitmap(3, 2, PixelFormat.Format24bppRgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 10, 20, 30));
        bmp.SetPixel(1, 0, Color.FromArgb(255, 200, 150, 100));
        bmp.SetPixel(2, 0, Color.FromArgb(255, 0, 0, 0));
        bmp.SetPixel(0, 1, Color.FromArgb(255, 255, 255, 255));
        bmp.SetPixel(1, 1, Color.FromArgb(255, 1, 2, 3));
        bmp.SetPixel(2, 1, Color.FromArgb(255, 250, 251, 252));

        var decoded = PdfPngDecoder.Decode(TestImages.EncodePng(bmp));
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                var expected = bmp.GetPixel(x, y);
                int o = (y * 3 + x) * 4;
                Assert.Equal(255, decoded.Rgba[o + 3]);
                Assert.Equal(expected.R, decoded.Rgba[o]);
                Assert.Equal(expected.G, decoded.Rgba[o + 1]);
                Assert.Equal(expected.B, decoded.Rgba[o + 2]);
            }
    }

    // PNGのIHDR（PNG仕様§11.2.2）: signature 8B + [len(4)+"IHDR"(4)+data(13)+crc(4)]。
    // data先頭は width(4)+height(4) で、その直後が bitDepth(offset24)・colorType(25)・
    // compression(26)・filter(27)・interlace(28)。CRCはこのデコーダが検査しないため、
    // IHDRのみ書き換えれば他は正当なPNGのまま「対応範囲外」を再現できる
    const int OffBitDepth = 24, OffInterlace = 28;

    static byte[] SmallOpaquePng()
    {
        using var bmp = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.Red);
        bmp.SetPixel(1, 0, Color.Green);
        bmp.SetPixel(0, 1, Color.Blue);
        bmp.SetPixel(1, 1, Color.White);
        return TestImages.EncodePng(bmp);
    }

    /// <summary>小さい PNG のバイトを <paramref name="corrupt"/> で書き換えた入力が、<paramref name="messagePart"/> を含む NotSupportedException で拒否される。</summary>
    static void AssertUnsupportedPng(Action<byte[]> corrupt, string messagePart)
    {
        var png = SmallOpaquePng();
        corrupt(png);
        var ex = Assert.Throws<NotSupportedException>(() => PdfPngDecoder.Decode(png));
        Assert.Contains(messagePart, ex.Message);
    }

    [Fact]
    public void インターレースPNGは明示エラー()
    {
        AssertUnsupportedPng(png => png[OffInterlace] = 1 /* Adam7 */, "インターレース");
    }

    [Fact]
    public void ビット深度16は明示エラー()
    {
        AssertUnsupportedPng(png => png[OffBitDepth] = 16, "ビット深度");
    }

    [Fact]
    public void Indexed1bitの往復で画素無損失()
    {
        // OLE 貼付由来の CCITT 画像は取得層が 1bit Indexed PNG のまま IR へ保持する。
        // 同形式（白黒2色パレット・幅がバイト境界に揃わない 10px）を GDI+ で作って往復する
        using var bmp = new Bitmap(10, 3, PixelFormat.Format1bppIndexed);
        var pal = bmp.Palette;
        pal.Entries[0] = Color.Black;
        pal.Entries[1] = Color.White;
        bmp.Palette = pal;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        // 行0: 1010101010、行1: 全白、行2: 先頭のみ黒（MSB 先頭詰め）
        var rows = new byte[][] { new byte[] { 0b10101010, 0b10000000 },
                                  new byte[] { 0b11111111, 0b11000000 },
                                  new byte[] { 0b01111111, 0b11000000 } };
        for (int y = 0; y < 3; y++)
            System.Runtime.InteropServices.Marshal.Copy(rows[y], 0, data.Scan0 + y * data.Stride, 2);
        bmp.UnlockBits(data);

        var decoded = PdfPngDecoder.Decode(TestImages.EncodePng(bmp));

        Assert.Equal(10, decoded.Width);
        Assert.Equal(3, decoded.Height);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 10; x++)
            {
                bool white = ((rows[y][x / 8] >> (7 - x % 8)) & 1) == 1;
                int o = (y * 10 + x) * 4;
                byte expected = white ? (byte)255 : (byte)0;
                Assert.True(decoded.Rgba[o] == expected && decoded.Rgba[o + 1] == expected
                         && decoded.Rgba[o + 2] == expected && decoded.Rgba[o + 3] == 255,
                    $"({x},{y}): expected {(white ? "白" : "黒")} actual R={decoded.Rgba[o]}");
            }
    }

    [Fact]
    public void PNG署名が無いバイト列は明示エラー()
    {
        var ex = Assert.Throws<NotSupportedException>(() => PdfPngDecoder.Decode(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains("署名", ex.Message);
    }
}
