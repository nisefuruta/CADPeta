// OLE 貼付（Excel 表など）のプロット結果に現れる CCITT G4 1bit Indexed 画像の取得検証。
// fixture は tests/tools/pdf-emf/gen_fixtures.py が生成し、実機 PDF の癖
// （/DecodeParms の /Rows が /Columns と同値で /Height と食い違う）を含む。
// 左上 1/4 と右下隅 1 画素が黒、他は白のパターンを画素で照合する。
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using CadPeta.Acquire.Pdf;
using CadPeta.Ir;
using CadPeta.Out.Pdf;
using Xunit;

namespace CadPeta.Tests;

public class PdfCcittImageTests
{
    const int W = 24;
    const int H = 40;

    static string FindRepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CadPeta.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("CadPeta.sln を親ディレクトリから発見できない");
    }

    static string Fixture(string name) =>
        Path.Combine(FindRepositoryRoot(), "tests", "assets", "pdf-fixtures", name);

    [Fact]
    public void CCITT_G4のIndexed1bit画像を取得できる()
    {
        var back = new PdfIrAcquirer().Acquire(
            Fixture("ccitt_g4_indexed1bit.pdf"), new IrDiagnostics());

        var img = Assert.Single(back.Nodes.OfType<IrImage>());
        using var bmp = new Bitmap(new MemoryStream(img.PngBytes));
        Assert.Equal(W, bmp.Width);
        Assert.Equal(H, bmp.Height);
        // 1bit はパレット PNG のまま渡す（32bpp 展開は SMask 合成時のみ）。肥大の回帰を防ぐ
        Assert.Equal(PixelFormat.Format1bppIndexed, bmp.PixelFormat);

        int black = Color.FromArgb(255, 0, 0, 0).ToArgb();
        int white = Color.FromArgb(255, 255, 255, 255).ToArgb();
        Assert.Equal(black, bmp.GetPixel(0, 0).ToArgb());              // 黒象限の角
        Assert.Equal(black, bmp.GetPixel(W / 2 - 1, H / 2 - 1).ToArgb());
        Assert.Equal(white, bmp.GetPixel(W / 2, 0).ToArgb());          // 黒象限の右隣
        Assert.Equal(white, bmp.GetPixel(0, H / 2).ToArgb());          // 黒象限の直下
        Assert.Equal(black, bmp.GetPixel(W - 1, H - 1).ToArgb());      // 右下隅の孤立黒
        Assert.Equal(white, bmp.GetPixel(W - 2, H - 1).ToArgb());
    }

    [Fact]
    public void CCITT_G4のIndexed1bit画像をPDFへ書き出せる()
    {
        // 取得層は 1bit Indexed PNG のまま IR へ保持するため、PDF 書き出し側（PdfPngDecoder）が
        // 1bit を受けられないと --pdf 出力だけが失敗する（実測: 未対応時は未処理例外で落ちた）
        var diag = new IrDiagnostics();
        var ir = new PdfIrAcquirer().Acquire(Fixture("ccitt_g4_indexed1bit.pdf"), diag);
        string outPath = Path.Combine(Path.GetTempPath(), $"cadpeta_ccitt_{Guid.NewGuid():N}.pdf");
        try
        {
            new PdfWriter(new CadPeta.Out.Emf.GdiGlyphOutliner()).Write(ir, outPath, diag);
            var head = File.ReadAllBytes(outPath);
            Assert.True(head.Length > 100, "出力 PDF が小さすぎる");
            Assert.Equal("%PDF", Encoding.ASCII.GetString(head, 0, 4));
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
