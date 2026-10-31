// PdfWriter（自前の最小PDFライタ）の単体検証。構造（MediaBox換算・xref整合・
// ExtGState/クリップ/破線/SMask の出現・画像の上下向き）と非可逆処理（文字輪郭化）の
// 診断ログ記録を検査する。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Out.Emf;
using CadPeta.Out.Pdf;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class PdfWriterTests
{
    static readonly Encoding Latin1 = Encoding.GetEncoding("Latin1");

    static (byte[] Bytes, string Text) WritePdf(IrDocument ir, IrDiagnostics? diag = null,
                                                IGlyphOutliner? outliner = null)
    {
        using var temp = new TempFolder();
        string path = temp.File("unit.pdf");
        diag ??= new IrDiagnostics();
        new PdfWriter(outliner ?? new GdiGlyphOutliner()).Write(ir, path, diag);
        var bytes = File.ReadAllBytes(path);
        return (bytes, Latin1.GetString(bytes));
    }

    [Fact]
    public void MediaBoxはmmからptへ正しく換算する()
    {
        var ir = new IrDocument { WidthMm = 210, HeightMm = 297 };
        var (_, text) = WritePdf(ir);
        double wPt = 210 * Units.PtPerMm, hPt = 297 * Units.PtPerMm;
        Assert.Contains($"/MediaBox [0 0 {IrNumber.Coordinate(wPt)} {IrNumber.Coordinate(hPt)}]", text);
        // 1pt=1/72インチ・1インチ=25.4mmの定義どおり（210mm→595.28pt、297mm→841.89pt 相当）
        Assert.Equal(595.2756, wPt, 3);
        Assert.Equal(841.8898, hPt, 3);
    }

    [Fact]
    public void xrefのオフセットは各オブジェクトの実位置と一致する()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 80 };
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(10, 0), new IrLine(10, 10), new IrClose() },
            Fill = new IrRgb(1, 0, 0),
        });
        var (bytes, text) = WritePdf(ir);

        int startxrefAt = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(startxrefAt >= 0);
        string tail = text.Substring(startxrefAt);
        long xrefOffset = long.Parse(tail.Split('\n')[1].Trim());
        Assert.Equal("xref", ReadLineAt(text, xrefOffset));

        // "xref" 行の次が "0 N" ヘッダ行、その次からNオブジェクト分
        //（1件目はフリーエントリ "0000000000 65535 f "）
        int xrefLineEnd = text.IndexOf('\n', (int)xrefOffset);
        int headerLineEnd = text.IndexOf('\n', xrefLineEnd + 1);
        string headerLine = text.Substring(xrefLineEnd + 1, headerLineEnd - xrefLineEnd - 1).Trim();
        int count = int.Parse(headerLine.Split(' ')[1]);
        int pos = headerLineEnd + 1;
        for (int id = 0; id < count; id++)
        {
            string entry = text.Substring(pos, 20);
            Assert.Equal('\n', entry[19]);
            if (id > 0) // id=0 はフリーエントリ、参照する実体を持たない
            {
                long offset = long.Parse(entry.Substring(0, 10));
                string atOffset = ReadLineAt(text, offset);
                Assert.StartsWith($"{id} 0 obj", atOffset);
            }
            pos += 20;
        }
    }

    static string ReadLineAt(string text, long offset)
    {
        int end = text.IndexOf('\n', (int)offset);
        return end < 0 ? text.Substring((int)offset) : text.Substring((int)offset, end - (int)offset);
    }

    [Fact]
    public void 破線はdash配列として出力する()
    {
        var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(20, 0) },
            Stroke = new IrRgb(0, 0, 0), StrokeWidthMm = 0.25,
            StrokeDashMm = new[] { 2.0, 1.0 },
        });
        var (_, text) = WritePdf(ir);
        Assert.Contains($"[{IrNumber.Coordinate(2.0)} {IrNumber.Coordinate(1.0)}] 0 d", text);
    }

    [Fact]
    public void 透過はExtGStateとして出力しネイティブに保つ()
    {
        var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(10, 0), new IrLine(10, 10), new IrClose() },
            Fill = new IrRgb(0, 0, 1), FillOpacity = 0.5,
        });
        var diag = new IrDiagnostics();
        var (_, text) = WritePdf(ir, diag);
        Assert.Contains("/GS0 gs", text);
        Assert.Contains($"/ca {IrNumber.Coordinate(0.5)}", text);
        // PDFはExtGStateでネイティブに透過を表現できるため、EMFと異なり非可逆処理ではない
        Assert.DoesNotContain(diag.Entries, e => e.Type.Contains("不透明化"));
    }

    [Fact]
    public void クリップはネイティブパスクリップで表現する()
    {
        var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
        ir.ClipDefs.Add(new IrClipDef
        {
            Id = "c0",
            Segments = new List<IrSegment>
            {
                new IrMove(5, 5), new IrLine(25, 5), new IrLine(25, 25), new IrLine(5, 25), new IrClose(),
            },
            EvenOdd = true,
        });
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(40, 0), new IrLine(40, 40), new IrClose() },
            Fill = new IrRgb(0, 0, 0), ClipId = "c0",
        });
        var (_, text) = WritePdf(ir);
        Assert.Contains($"{IrNumber.Coordinate(5.0)} {IrNumber.Coordinate(5.0)} m", text);
        Assert.Contains("W* n", text); // EvenOdd=true → W*
    }

    [Fact]
    public void 文字は輪郭化しフォント埋め込みを行わない()
    {
        var ir = new IrDocument { WidthMm = 50, HeightMm = 20 };
        ir.Nodes.Add(new IrText { Value = "AB", XMm = 5, YMm = 10, FontSizeMm = 5, FontFamily = "Arial" });
        var diag = new IrDiagnostics();
        var (_, text) = WritePdf(ir, diag);

        Assert.DoesNotContain("/Font", text);
        Assert.DoesNotContain("BT", text); // PDFのテキスト表示演算子（Tj等）は使わない
        Assert.Contains(diag.Entries, e => e.Type.Contains("文字を輪郭化して埋め込み"));
    }

    [Fact]
    public void 輪郭を取れない文字は省略し診断ログへ記録する()
    {
        var ir = new IrDocument { WidthMm = 50, HeightMm = 20 };
        ir.Nodes.Add(new IrText { Value = "AB", XMm = 5, YMm = 10, FontSizeMm = 5, FontFamily = "存在しない書体" });
        var diag = new IrDiagnostics();
        var alwaysNull = new NullOutliner();
        var (_, _) = WritePdf(ir, diag, alwaysNull);
        Assert.Contains(diag.Entries, e => e.Type.Contains("輪郭を取れないため文字を省略"));
    }

    sealed class NullOutliner : IGlyphOutliner
    {
        public List<IrSegment>? Outline(GlyphRun run) => null;
    }

    [Fact]
    public void 画像はデコードして無圧縮RGBのXObjectとSMaskで埋め込む()
    {
        using var bmp = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, System.Drawing.Color.FromArgb(255, 10, 20, 30));
        bmp.SetPixel(1, 0, System.Drawing.Color.FromArgb(0, 1, 2, 3)); // 完全透過 → SMask=0 で保持
        bmp.SetPixel(0, 1, System.Drawing.Color.FromArgb(255, 250, 251, 252));
        bmp.SetPixel(1, 1, System.Drawing.Color.FromArgb(255, 0, 0, 0));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
        ir.Nodes.Add(new IrImage { XMm = 0, YMm = 0, WidthMm = 20, HeightMm = 20, PngBytes = ms.ToArray() });
        var diag = new IrDiagnostics();
        var (_, text) = WritePdf(ir, diag);

        Assert.Contains("/Subtype /Image", text);
        Assert.Contains("/Width 2", text);
        Assert.Contains("/Height 2", text);
        Assert.Contains("/ColorSpace /DeviceRGB", text);
        Assert.Contains("/BitsPerComponent 8", text);
        // アルファは SMask でネイティブに保持する（劣化なし＝診断ログの対象外）
        Assert.Contains("/SMask", text);
        Assert.Contains("/ColorSpace /DeviceGray", text);
        Assert.DoesNotContain(diag.Entries, e => e.Type.Contains("不透明化"));
    }

    [Fact]
    public void アルファ無し画像はSMaskを書かない()
    {
        using var bmp = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(255, 10, 20, 30));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
        ir.Nodes.Add(new IrImage { XMm = 0, YMm = 0, WidthMm = 20, HeightMm = 20, PngBytes = ms.ToArray() });
        var diag = new IrDiagnostics();
        var (_, text) = WritePdf(ir, diag);
        Assert.DoesNotContain("/SMask", text);
        Assert.DoesNotContain(diag.Entries, e => e.Type.Contains("不透明化"));
    }

    [Fact]
    public void 画像のcmは高さ負で上下向きを保つ()
    {
        // PDF の画像空間はデータ先頭行が単位正方形の上辺に写る。ページ先頭のY反転 cm の下で
        // 幅高さ正の cm を書くと上下鏡像になる（非対称画像の実レンダで確認済みの欠陥の回帰テスト）
        using var bmp = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        bmp.SetPixel(0, 0, System.Drawing.Color.Red);
        bmp.SetPixel(1, 1, System.Drawing.Color.Black);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        var ir = new IrDocument { WidthMm = 50, HeightMm = 50 };
        ir.Nodes.Add(new IrImage { XMm = 5, YMm = 7, WidthMm = 20, HeightMm = 10, PngBytes = ms.ToArray() });
        var (_, text) = WritePdf(ir);
        Assert.Contains($"{IrNumber.Matrix(20.0)} 0 0 {IrNumber.Matrix(-10.0)} {IrNumber.Matrix(5.0)} {IrNumber.Matrix(17.0)} cm", text);
    }

    [Fact]
    public void SplitRgbAlpha_RGBとアルファ面を分離する()
    {
        var png = new DecodedPngForTest(2, 1, new byte[]
        {
            200, 100, 50, 255, // 不透明
            30, 40, 50, 128,   // 半透明 → RGB は生値のまま、アルファ面に 128
        }).ToDecodedPng();

        var (rgb, alpha) = PdfWriter.SplitRgbAlpha(png);
        Assert.NotNull(alpha);
        Assert.Equal(new byte[] { 200, 100, 50, 30, 40, 50 }, rgb);
        Assert.Equal(new byte[] { 255, 128 }, alpha);
    }

    [Fact]
    public void SplitRgbAlpha_全画素不透明ならアルファ面はnull()
    {
        var png = new DecodedPngForTest(2, 1, new byte[]
        {
            200, 100, 50, 255,
            30, 40, 50, 255,
        }).ToDecodedPng();

        var (rgb, alpha) = PdfWriter.SplitRgbAlpha(png);
        Assert.Null(alpha);
        Assert.Equal(new byte[] { 200, 100, 50, 30, 40, 50 }, rgb);
    }

    // DecodedPng は internal sealed のためテストから直接 new できるが、
    // 可読性のため配列引数を明示する薄いラッパーに留める
    sealed class DecodedPngForTest
    {
        readonly int _w, _h;
        readonly byte[] _rgba;
        public DecodedPngForTest(int w, int h, byte[] rgba) { _w = w; _h = h; _rgba = rgba; }
        public DecodedPng ToDecodedPng() => new() { Width = _w, Height = _h, Rgba = _rgba };
    }
}
