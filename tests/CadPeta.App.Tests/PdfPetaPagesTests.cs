using System.IO;
using System.Linq;
using CadPeta.Tests;
using CadPeta.UseCases;
using Xunit;
using static CadPeta.App.Tests.TestRequests;

namespace CadPeta.App.Tests;

// ページの指定（cadpeta.exe の --pages）を、ページごとの変換の要求にする約束の試験
public sealed class PdfPetaPagesTests
{
    [Theory]
    [InlineData("2", new[] { 2 })]
    [InlineData("1-3,5", new[] { 1, 2, 3, 5 })]
    [InlineData("5,1-2", new[] { 1, 2, 5 })]
    [InlineData("1,1-2", new[] { 1, 2 })]
    [InlineData(" 2 - 3 ", new[] { 2, 3 })]
    public void ページの指定を昇順のページの一覧にする(string text, int[] pages)
    {
        var (parsed, error) = PdfPetaPages.ParsePageList(text, 9);

        Assert.Null(error);
        Assert.Equal(pages, parsed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3-1")]
    [InlineData("a")]
    [InlineData("1-")]
    [InlineData("1,,2")]
    public void 解釈できないページの指定は指定を付けて理由を返す(string text)
    {
        var (_, error) = PdfPetaPages.ParsePageList(text, 9);

        Assert.Contains(text, error);
    }

    [Fact]
    public void 空のページの指定は理由を返す()
    {
        var (_, error) = PdfPetaPages.ParsePageList("", 9);

        Assert.NotNull(error);
    }

    [Fact]
    public void ページが1つだけならファイル名を変えずにそのページを変換する()
    {
        var (requests, errors) = PdfPetaPages.ForPages(Request(new PdfPetaOutput(ArtifactKind.Svg, @"C:\out\図.svg")), new[] { 2 });

        Assert.Empty(errors);
        Assert.Equal((2, @"C:\out\図.svg"), (requests.Single().PageNumber, requests.Single().Outputs.Single().Path));
    }

    [Fact]
    public void 複数のページはファイル名にページの番号を付ける()
    {
        var (requests, errors) = PdfPetaPages.ForPages(Request(
            new PdfPetaOutput(ArtifactKind.Svg, @"C:\out\図.svg"), new PdfPetaOutput(ArtifactKind.Png, @"C:\out\図.png", 150)), new[] { 1, 3 });

        Assert.Empty(errors);
        Assert.Equal(new[] { (1, @"C:\out\図-p1.svg", @"C:\out\図-p1.png"), (3, @"C:\out\図-p3.svg", @"C:\out\図-p3.png") },
            requests.Select(r => (r.PageNumber, r.Outputs[0].Path, r.Outputs[1].Path)));
    }

    // ページ数を超える番号は、範囲を展開する前に失敗にする（1-2000000000 でも止まらずに理由を返す）
    [Theory]
    [InlineData("2,4", "4")]
    [InlineData("1-2000000000", "2000000000")]
    public void PDFに無いページを指定すると変換の前に失敗する(string text, string missing)
    {
        var (pages, error) = PdfPetaPages.ParsePageList(text, 3);

        Assert.Empty(pages);
        Assert.Contains($"ページ {missing} は無い（PDF は 3 ページ）", error);
    }
    [Fact]
    public void クリップボードに登録できるのは1ページだけ()
    {
        var (requests, errors) = PdfPetaPages.ForPages(Request(
            new PdfPetaOutput(ArtifactKind.Emf, @"C:\out\図.emf", ToClipboard: true)), new[] { 1, 2 });

        Assert.Empty(requests);
        Assert.Contains(errors, x => x.Contains("クリップボード"));
    }

    [Fact]
    public void 変換のログにページの番号とページ数を出す()
    {
        using var temp = new TempFolder();
        using var log = new StringWriter();
        double widthMm = 0;

        PdfPetaPipeline.Run(Request(new PdfPetaOutput(ArtifactKind.Svg, temp.File("p2.svg"))) with
        { InputPdfPath = RepositoryPaths.PdfFixture("pages_fixture.pdf"), PageNumber = 2 }, log,
            new PdfPetaRunOptions { DiagnosticStageSink = (_, name, ir) => { if (name == "acquired") widthMm = ir.WidthMm; } });

        Assert.Equal(120, System.Math.Round(widthMm, 1));
        Assert.Contains("ページ: 2/3", log.ToString());
    }
}
