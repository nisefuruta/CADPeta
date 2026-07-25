// PngWriter（GDI+直接ラスタライズ）の単体検証。寸法・DPIメタデータと、
// 図形・文字・画像・クリップ・グループ変換を例外なく描画できることを検査する。
using System.Drawing;
using CadPeta.Ir;
using CadPeta.Out.Png;
using Xunit;

namespace CadPeta.Tests;

public class PngWriterTests
{
    static string TempPath() => Path.Combine(Path.GetTempPath(), "cadpeta_p3b_pdfpng",
        "unit_" + Guid.NewGuid().ToString("N") + ".png");

    [Fact]
    public void 出力ピクセル寸法はDPIから正しく算出する()
    {
        var ir = new IrDocument { WidthMm = 50, HeightMm = 20 };
        string path = TempPath();
        new PngWriter(96).Write(ir, path, new IrDiagnostics());

        using var img = Image.FromFile(path);
        int expectedW = (int)Math.Round(50 * 96 / 25.4);
        int expectedH = (int)Math.Round(20 * 96 / 25.4);
        Assert.Equal(expectedW, img.Width);
        Assert.Equal(expectedH, img.Height);
    }

    [Fact]
    public void 解像度メタデータはDPIに一致する()
    {
        var ir = new IrDocument { WidthMm = 30, HeightMm = 30 };
        string path = TempPath();
        new PngWriter(150).Write(ir, path, new IrDiagnostics());

        using var img = Image.FromFile(path);
        Assert.Equal(150, img.HorizontalResolution, 1);
        Assert.Equal(150, img.VerticalResolution, 1);
    }

    [Fact]
    public void 既定DPIは150である()
    {
        Assert.Equal(150.0, PngWriter.DefaultDpi);
    }

    [Fact]
    public void 白背景で塗りつぶす()
    {
        var ir = new IrDocument { WidthMm = 10, HeightMm = 10 }; // ノード無し＝背景だけ
        string path = TempPath();
        new PngWriter(96).Write(ir, path, new IrDiagnostics());

        using var bmp = new Bitmap(path);
        var c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, c.R); Assert.Equal(255, c.G); Assert.Equal(255, c.B);
    }

    [Fact]
    public void 図形と文字と画像とクリップとグループ変換を例外なく描画する()
    {
        using var srcBmp = new Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        srcBmp.SetPixel(0, 0, Color.Red);
        srcBmp.SetPixel(1, 0, Color.FromArgb(128, 0, 255, 0));
        srcBmp.SetPixel(0, 1, Color.Blue);
        srcBmp.SetPixel(1, 1, Color.White);
        using var ms = new MemoryStream();
        srcBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.ClipDefs.Add(new IrClipDef
        {
            Id = "c0",
            Segments = new List<IrSegment>
            {
                new IrMove(0, 0), new IrLine(80, 0), new IrLine(80, 80), new IrLine(0, 80), new IrClose(),
            },
        });
        var group = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 10, 10) };
        group.Children.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(20, 0), new IrLine(20, 20), new IrClose() },
            Fill = new IrRgb(1, 0, 0), Stroke = new IrRgb(0, 0, 0), StrokeWidthMm = 0.5,
            StrokeDashMm = new[] { 2.0, 1.0 }, FillOpacity = 0.5, ClipId = "c0",
        });
        ir.Nodes.Add(group);
        ir.Nodes.Add(new IrText
        {
            Value = "テスト123", XMm = 30, YMm = 40, FontSizeMm = 5, FontFamily = "Arial",
            RotationDeg = 15, WidthFactor = 1.2, Fill = new IrRgb(0, 0, 1),
        });
        ir.Nodes.Add(new IrImage { XMm = 50, YMm = 50, WidthMm = 15, HeightMm = 15, PngBytes = ms.ToArray() });

        string path = TempPath();
        var diag = new IrDiagnostics();
        new PngWriter(96).Write(ir, path, diag);

        Assert.True(new FileInfo(path).Length > 0);
        using var result = Image.FromFile(path);
        Assert.True(result.Width > 0 && result.Height > 0);
    }

    // 実物図面（原寸0.0035mmの文字）でGraphics.DrawStringが「Generic error」を返すことを
    // 実測した。デバイス空間で0.5px未満になる極小文字は描画を省略し診断ログへ記録する
    // （クラッシュより実害が小さい安全側の判断。GdiGlyphOutliner側は整数論理単位の
    // 丸めで別途1論理単位=0.01mmへ自然に下駆けするため同じ問題を起こさない）
    [Fact]
    public void 極小の文字サイズは描画を省略しクラッシュしない()
    {
        var ir = new IrDocument { WidthMm = 30, HeightMm = 10 };
        ir.Nodes.Add(new IrText
        {
            Value = "2999.63", XMm = 5, YMm = 5, FontSizeMm = 0.0035277777777777777,
            FontFamily = "MS PGothic", RotationDeg = -90,
        });
        var diag = new IrDiagnostics();
        var ex = Record.Exception(() => new PngWriter(150).Write(ir, TempPath(), diag));
        Assert.Null(ex);
        Assert.Contains(diag.Entries, e => e.Type.Contains("文字サイズが微小のため描画を省略"));
    }

    [Fact]
    public void 存在しないフォントは代替しログへ記録する()
    {
        var ir = new IrDocument { WidthMm = 30, HeightMm = 10 };
        ir.Nodes.Add(new IrText { Value = "X", XMm = 5, YMm = 5, FontSizeMm = 4, FontFamily = "存在しない書体名XYZ" });
        var diag = new IrDiagnostics();
        new PngWriter(96).Write(ir, TempPath(), diag);
        Assert.Contains(diag.Entries, e => e.Type.Contains("フォント代替"));
    }
}
