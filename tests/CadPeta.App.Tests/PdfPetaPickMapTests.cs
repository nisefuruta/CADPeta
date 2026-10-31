using System.IO;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Tests;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.App.Tests;

// スポイトの色番号の画像を実際の PDF から作り、クリックした位置の図形の色をそのまま求める約束の試験。
// near_colors_fixture.pdf の図形の位置は tests/tools/pdf-emf/gen_page_color_fixtures.py の表（mm、左上原点）
public sealed class PdfPetaPickMapTests
{
    const double Dpi = 150;

    static PdfPetaPickMap Render(TempFolder temp) => PdfPetaPipeline.RenderPickMap(new PdfPetaRequest
    {
        InputPdfPath = RepositoryPaths.PdfFixture("near_colors_fixture.pdf"),
        Outputs = new[] { new PdfPetaOutput(ArtifactKind.Png, temp.File("pick.png"), Dpi) },
    }, TextWriter.Null);

    static int Px(double mm) => (int)(mm * Dpi / Units.MmPerInch);

    // 近い色の塗り（#FF0000 との差 0、1、7、31）は、それぞれの元の色になる
    [Theory]
    [InlineData(30, "#FF0000")]
    [InlineData(75, "#FE0101")]
    [InlineData(120, "#F80000")]
    [InlineData(165, "#E00000")]
    public void 近い色の図形でもクリックした図形の色を返す(double xMm, string hex)
    {
        using var temp = new TempFolder();

        Assert.Equal(IrRgb.ParseHex(hex).ToHex(), Render(temp).ColorAt(Px(xMm), Px(25), 0)?.ToHex());
    }

    [Theory]
    [InlineData(65, 65, "#00FF00")]
    [InlineData(30, 60, "#0000FF")]
    public void 重なった図形では上に描いた図形の色を返す(double xMm, double yMm, string hex)
    {
        using var temp = new TempFolder();

        Assert.Equal(IrRgb.ParseHex(hex).ToHex(), Render(temp).ColorAt(Px(xMm), Px(yMm), 0)?.ToHex());
    }

    // 細い線（0.1mm）の 1mm 横をクリックしても、近くを探す範囲の中なら線の色を返す
    [Fact]
    public void 細い線の近くをクリックすると線の色を返す()
    {
        using var temp = new TempFolder();

        Assert.Equal("#FF00FF", Render(temp).ColorAt(Px(100), Px(96), Px(1.5))?.ToHex());
    }

    [Fact]
    public void 図形の無い場所は色を返さない()
    {
        using var temp = new TempFolder();

        Assert.Null(Render(temp).ColorAt(Px(100), Px(47), 2));
    }

    // layers_fixture.pdf の重なりの中心 (50, 30)mm は、そのままなら赤、レイヤ別にグループ化すると緑が上になる。
    // スポイトは、どちらの設定でも、プレビューに見えている図形の色を返す
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void レイヤ別のグループ化でもプレビューに見えている図形の色を返す(bool groupByLayer)
    {
        using var temp = new TempFolder();
        var request = new PdfPetaRequest
        {
            InputPdfPath = RepositoryPaths.PdfFixture("layers_fixture.pdf"), GroupByLayer = groupByLayer,
            Outputs = new[] { new PdfPetaOutput(ArtifactKind.Png, temp.File("preview.png"), Dpi) },
        };
        PdfPetaPipeline.Run(request, TextWriter.Null);
        using var preview = new System.Drawing.Bitmap(temp.File("preview.png"));
        var shown = preview.GetPixel(Px(50), Px(30));

        var picked = PdfPetaPipeline.RenderPickMap(request with
        { Outputs = new[] { new PdfPetaOutput(ArtifactKind.Png, temp.File("pick.png"), Dpi) } }, TextWriter.Null).ColorAt(Px(50), Px(30), 0);

        Assert.Equal($"#{shown.R:X2}{shown.G:X2}{shown.B:X2}", picked?.ToHex());
    }

    [Fact]
    public void 使用色の一覧を返す()
    {
        using var temp = new TempFolder();

        Assert.Equal(7, Render(temp).Colors.Count);
    }
}
