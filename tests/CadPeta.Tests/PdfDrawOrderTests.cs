// PDF印刷経路（PDF取得）の描画順（z順）が「真の内容ストリーム順」で復元されることの回帰。
// 旧実装は Nodes を型別3バッチ（全パス→全文字→全画像）で積み、型をまたぐ z順を失っていた。
//
// (a) PdfWriter 往復: Path/Image の型交錯。PdfWriter は文字を輪郭パス化して出力し（PdfWriter.cs:168-193）
//     PDFテキスト演算子を出さないため、往復では文字を含めない。
// (b) 固定 fixture interleave_ptx.pdf: 実 BT/Tj/ET を含む Path/Text/Image 交錯。
// (c) 固定 fixture subtype_form_*.pdf: XObject Subtype 判別と Form 再帰描画順の検証。
//     fixture は tests/tools/pdf-emf/gen_fixtures.py が決定的に生成し commit 済み。
using System.Drawing;
using System.Drawing.Imaging;
using CadPeta.Acquire.Pdf;
using CadPeta.Ir;
using CadPeta.Out.Emf;
using CadPeta.Out.Pdf;
using Xunit;

namespace CadPeta.Tests;

public class PdfDrawOrderTests
{
    static string KindOf(IrNode n) => n switch
    {
        IrPath => "path",
        IrText => "text",
        IrImage => "image",
        IrGroup => "group",
        _ => "other",
    };

    // 連続同一 kind を1つに畳む（dissect と acquirer の per-op 分割粒度差を吸収する compare_draworder と同規則）。
    static List<string> Collapse(IEnumerable<IrNode> nodes)
    {
        var outSeq = new List<string>();
        foreach (var n in nodes)
        {
            var k = KindOf(n);
            if (outSeq.Count == 0 || outSeq[outSeq.Count - 1] != k) outSeq.Add(k);
        }
        return outSeq;
    }

    static byte[] SolidPng(int r, int g, int b)
    {
        using var bmp = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(bmp)) gr.Clear(Color.FromArgb(255, r, g, b));
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static List<IrSegment> Rect(double x, double y, double w, double h) => new()
    {
        new IrMove(x, y), new IrLine(x + w, y), new IrLine(x + w, y + h),
        new IrLine(x, y + h), new IrClose(),
    };

    static string FindRepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CadPeta.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("CadPeta.sln を親ディレクトリから発見できない");
    }

    static string Fixture(string name) =>
        Path.Combine(FindRepositoryRoot(), "tests", "assets", "pdf-fixtures", name);

    // (a) PdfWriter 往復: [塗りパス, 画像, 線パス, 画像] が交錯順のまま復元される。
    [Fact]
    public void PdfWriter往復で画像とパスの型交錯z順が保存される()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 80 };
        ir.Nodes.Add(new IrPath { Segments = Rect(10, 10, 30, 20), Fill = new IrRgb(1, 0, 0) });
        ir.Nodes.Add(new IrImage
        {
            XMm = 50, YMm = 10, WidthMm = 20, HeightMm = 20, PngBytes = SolidPng(0, 200, 0),
        });
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(10, 50), new IrLine(40, 50) },
            Stroke = new IrRgb(0, 0, 1), StrokeWidthMm = 1,
        });
        ir.Nodes.Add(new IrImage
        {
            XMm = 50, YMm = 50, WidthMm = 20, HeightMm = 20, PngBytes = SolidPng(240, 240, 0),
        });

        string dir = Path.Combine(Path.GetTempPath(), "cadpeta_draworder");
        Directory.CreateDirectory(dir);
        string pdf = Path.Combine(dir, "rt_" + Guid.NewGuid().ToString("N") + ".pdf");
        new PdfWriter(new GdiGlyphOutliner()).Write(ir, pdf, new IrDiagnostics());

        var back = new PdfIrAcquirer().Acquire(pdf, new IrDiagnostics());
        var kinds = back.Nodes.Select(KindOf).ToList();
        // 旧実装なら [path, path, image, image] へ化ける。真順序では交錯が保存される。
        Assert.Equal(new[] { "path", "image", "path", "image" }, kinds);
        Assert.NotNull(((IrPath)back.Nodes[0]).Fill);    // 0=塗り矩形
        Assert.NotNull(((IrPath)back.Nodes[2]).Stroke);  // 2=線
    }

    // (b) 実テキスト演算子を含む型交錯: path → text → image → path。
    [Fact]
    public void 固定PDF_文字を含む型交錯のz順が保存される()
    {
        var back = new PdfIrAcquirer().Acquire(Fixture("interleave_ptx.pdf"), new IrDiagnostics());
        // 旧実装なら [path, path, text, image]（全パス→全文字→全画像）へ化ける。
        Assert.Equal(new[] { "path", "text", "image", "path" }, Collapse(back.Nodes));
    }

    // (c-i) Form Do は画像状態を汚さない（Subtype 判別で除外）。2画像が正しい順序・レイヤで並ぶ。
    [Fact]
    public void Form_Doは画像の順序と属性対応を汚さない()
    {
        var diag = new IrDiagnostics();
        var back = new PdfIrAcquirer().Acquire(Fixture("subtype_form_noimg.pdf"), diag);
        var imgs = back.Nodes.OfType<IrImage>().ToList();
        Assert.Equal(2, imgs.Count);
        Assert.Equal("LayerA", imgs[0].DataLayer);
        Assert.Equal("LayerB", imgs[1].DataLayer);
        // 件数一致（Form Do 除外で imageStates==GetImages）のため確定不能診断は出ない。
        Assert.DoesNotContain(diag.Entries, e => e.Type.Contains("確定不能"));
    }

    // (c-ii) Form の一般描画を depth-first で追跡し、後続ページ要素との z 順を保つ。
    [Fact]
    public void Form内のパス文字画像と後続パスのz順が保存される()
    {
        var diag = new IrDiagnostics();
        var back = new PdfIrAcquirer().Acquire(Fixture("subtype_form_withimg.pdf"), diag);
        Assert.Equal(new[] { "path", "text", "image", "path" }, Collapse(back.Nodes));
        Assert.DoesNotContain(diag.Entries, e => e.Type.Contains("確定不能"));
    }
}
