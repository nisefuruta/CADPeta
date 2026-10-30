// EmfWriter.BuildBgrDib（IrImage → 24bpp 無圧縮 BGR DIB）の機械検証。
// 申し送り#2（画像 DIB の 32bpp 無圧縮肥大の解消、2026-07-08）で 32bpp→24bpp 化した際の
// 二つの落とし穴を固定する: ①行末の DWORD パディング（24bpp は 幅×3 が4境界に乗らないと
// 行ごとにズレてせん断する）②アルファ事前合成後の RGB がビット不変＝画素無損失であること。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using CadPeta.Out.Emf;
using Xunit;

namespace CadPeta.Tests;

public class EmfImageDibTests
{
    // 幅3px を選ぶ: 幅×3=9 は4の倍数でないため stride=12（行末に3バイトのパディング）となり、
    // パディング欠落バグが必ず露見する幅である。高さ2px でボトムアップの上下反転も同時に検査する。
    [Fact]
    public void BuildBgrDib_24bppで行パディングと画素無損失を満たす()
    {
        using var bmp = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
        // Color.FromArgb(a, r, g, b)。DIB 出力は下位バイトから B,G,R
        bmp.SetPixel(0, 0, Color.FromArgb(255, 200, 100, 50)); // 不透明 → BGR=(50,100,200)
        bmp.SetPixel(1, 0, Color.FromArgb(128, 0, 0, 0));      // α=128・黒 → 白と合成で (127,127,127)
        bmp.SetPixel(2, 0, Color.FromArgb(255, 1, 2, 3));      // 不透明 → BGR=(3,2,1)
        bmp.SetPixel(0, 1, Color.FromArgb(255, 255, 255, 255));// 不透明白 → (255,255,255)
        bmp.SetPixel(1, 1, Color.FromArgb(255, 0, 0, 0));      // 不透明黒 → (0,0,0)
        bmp.SetPixel(2, 1, Color.FromArgb(0, 123, 45, 67));    // 完全透過 → 白 (255,255,255)

        var bits = EmfWriter.BuildBgrDib(bmp, out var bmi, out bool hadAlpha);

        // ヘッダ: 24bpp・無圧縮・ボトムアップ（Height 正）
        Assert.Equal(24, bmi.BitCount);
        Assert.Equal(Gdi.BI_RGB, bmi.Compression);
        Assert.Equal(3, bmi.Width);
        Assert.Equal(2, bmi.Height);
        Assert.Equal(1, bmi.Planes);

        // stride = ((3*3 + 3) / 4) * 4 = 12（行末3バイトのパディング）。2行で 24 バイト
        const int Stride = 12;
        Assert.Equal(Stride * 2, bits.Length);
        Assert.Equal(EmfWriter.DibRowAlignBytes, 4);

        // 透過画素（α<255）があったため事前合成ログの起点フラグが立つ
        Assert.True(hadAlpha);

        // ボトムアップ: 先頭行（offset 0）が画像の下端 y=1、次行（offset 12）が上端 y=0
        // --- 下端 y=1 ---
        Assert.Equal(new byte[] { 255, 255, 255 }, Sub(bits, 0, 3));   // (0,1) 白
        Assert.Equal(new byte[] { 0, 0, 0 }, Sub(bits, 3, 3));         // (1,1) 黒
        Assert.Equal(new byte[] { 255, 255, 255 }, Sub(bits, 6, 3));   // (2,1) 透過→白
        Assert.Equal(new byte[] { 0, 0, 0 }, Sub(bits, 9, 3));         // 行末パディング=0
        // --- 上端 y=0 ---
        Assert.Equal(new byte[] { 50, 100, 200 }, Sub(bits, 12, 3));   // (0,0) BGR
        Assert.Equal(new byte[] { 127, 127, 127 }, Sub(bits, 15, 3));  // (1,0) α合成
        Assert.Equal(new byte[] { 3, 2, 1 }, Sub(bits, 18, 3));        // (2,0) BGR
        Assert.Equal(new byte[] { 0, 0, 0 }, Sub(bits, 21, 3));        // 行末パディング=0
    }

    // net48 は Range 添字（bits[a..b]）を持たないため部分抽出を明示する
    static byte[] Sub(byte[] a, int start, int len)
    {
        var r = new byte[len];
        System.Array.Copy(a, start, r, 0, len);
        return r;
    }

    // 幅が4境界に乗る場合（幅4px → 幅×3=12）はパディング無しで stride=幅×3 になることを確認する
    [Fact]
    public void BuildBgrDib_幅が4境界のときパディングなし()
    {
        using var bmp = new Bitmap(4, 1, PixelFormat.Format32bppArgb);
        for (int x = 0; x < 4; x++) bmp.SetPixel(x, 0, Color.FromArgb(255, 10, 20, 30));

        var bits = EmfWriter.BuildBgrDib(bmp, out _, out bool hadAlpha);

        Assert.False(hadAlpha);
        Assert.Equal(4 * 3, bits.Length); // stride=12、パディング0
        for (int x = 0; x < 4; x++)
            Assert.Equal(new byte[] { 30, 20, 10 }, Sub(bits, x * 3, 3));
    }

    [Fact]
    public void BuildBgrDib_任意背景色を画像アルファへ同じ整数規則で合成する()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.FromArgb(128, 200, 100, 50));
        var background = new CadPeta.Ir.IrRgb(10 / 255.0, 20 / 255.0, 30 / 255.0);
        byte[] bits = EmfWriter.BuildBgrDib(bmp, background, out _, out bool hadAlpha);
        Assert.True(hadAlpha);
        Assert.Equal(new byte[]
        {
            (byte)((50 * 128 + 30 * 127) / 255),
            (byte)((100 * 128 + 20 * 127) / 255),
            (byte)((200 * 128 + 10 * 127) / 255),
            0,
        }, bits);
    }

    [Fact]
    public void EmfWriter_背景色の非有限値と範囲外を拒否する()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmfWriter(new CadPeta.Ir.IrRgb(double.NaN, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmfWriter(new CadPeta.Ir.IrRgb(0, 0, 1.01)));
    }
}
