// PDF印刷経路の往復（自作PDF出力→CLI再入力）で画像が壊れないことの回帰。
// PdfWriter は画像を /DeviceRGB 8bpc の無圧縮ストリームで書く。取得側 PdfImageDecoder は
// これを ColorSpace に従って3バイト/画素で復元しなければならない（かつて先頭バイトのグレーと
// 誤読していた欠陥の回帰。docs/validation/architecture-decisions.md §3.4）。
using System.Drawing;
using System.Drawing.Imaging;
using CadPeta.Acquire.Pdf;
using CadPeta.Ir;
using CadPeta.Out.Emf;
using CadPeta.Out.Pdf;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class PdfImageRoundTripTests
{
    static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact]
    public void DeviceRGB画像はPDF往復で色と向きを保つ()
    {
        // 非対称・多色パターン。グレー誤読なら色が総崩れ、上下反転なら配置がずれるため両方を捕える。
        // アルファは 255 以外を1点入れて SMask 往復も確認する（0 は GDI+ 保存が RGB を落とすため避ける）
        var src = new (int A, int R, int G, int B)[,]
        {
            { (255, 200, 10, 10), (255, 10, 200, 10), (255, 10, 10, 200), (255, 240, 240, 0) },
            { (255, 0, 0, 0),     (128, 90, 80, 70),  (255, 5, 250, 5),   (255, 128, 64, 32) },
            { (255, 33, 66, 99),  (255, 250, 0, 250), (255, 70, 140, 210),(255, 255, 255, 255) },
        };
        int w = src.GetLength(1), h = src.GetLength(0);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = src[y, x];
                bmp.SetPixel(x, y, Color.FromArgb(p.A, p.R, p.G, p.B));
            }

        var ir = new IrDocument { WidthMm = 40, HeightMm = 30 };
        ir.Nodes.Add(new IrImage
        {
            XMm = 0, YMm = 0, WidthMm = 40, HeightMm = 30, PngBytes = EncodePng(bmp),
        });

        string dir = Path.Combine(Path.GetTempPath(), "cadpeta_p4_imgroundtrip");
        Directory.CreateDirectory(dir);
        string pdfPath = Path.Combine(dir, "rt_" + Guid.NewGuid().ToString("N") + ".pdf");
        new PdfWriter(new GdiGlyphOutliner()).Write(ir, pdfPath, new IrDiagnostics());

        var back = new PdfIrAcquirer().Acquire(pdfPath, new IrDiagnostics());
        var img = Assert.Single(back.Nodes.OfType<IrImage>());
        var decoded = PdfPngDecoder.Decode(img.PngBytes!);
        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = src[y, x];
                int o = (y * w + x) * 4;
                Assert.True(
                    p.R == decoded.Rgba[o] && p.G == decoded.Rgba[o + 1] && p.B == decoded.Rgba[o + 2]
                    && p.A == decoded.Rgba[o + 3],
                    $"({x},{y}): 期待 R{p.R} G{p.G} B{p.B} A{p.A} / 実 " +
                    $"R{decoded.Rgba[o]} G{decoded.Rgba[o + 1]} B{decoded.Rgba[o + 2]} A{decoded.Rgba[o + 3]}");
            }
    }
}
