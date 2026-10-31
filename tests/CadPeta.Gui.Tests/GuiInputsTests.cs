using System.Linq;
using CadPeta.App;
using CadPeta.Geometry;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Gui.Tests;

// 画面の入力欄の文字を、変換の要求の値にする約束の試験
public sealed class GuiInputsTests
{
    [Fact]
    public void 切り出しがオフなら範囲の欄を読まない()
    {
        Assert.Equal((null, 0.0, null), GuiInputs.ParseCrop(false, "x", "", "", "", ""));
    }

    [Fact]
    public void 切り出しの範囲と余白を読む()
    {
        Assert.Equal((new RectMm(12, 8, 180, 140.5), 2.0, null), GuiInputs.ParseCrop(true, "12", "8", "180", "140.5", "2"));
    }

    [Theory]
    [InlineData("a", "0")]
    [InlineData("10", "-1")]
    [InlineData("", "0")]
    public void 数でない範囲と負の余白は理由を返す(string x0, string margin)
    {
        var (rect, _, error) = GuiInputs.ParseCrop(true, x0, "0", "100", "100", margin);

        Assert.Null(rect);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(2, 3, 2)]
    [InlineData(4, 3, 3)]
    [InlineData(1, 0, 1)]
    public void ページの番号を1からページ数までにする(int requested, int count, int expected)
    {
        Assert.Equal(expected, GuiInputs.ClampPage(requested, count));
    }

    // PNG の解像度の欄は、PNG を保存するときだけ読む（ほかの形式は解像度を使わないので、欄が空や誤りでも保存できる）
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void PNGを保存しなければ解像度の欄を読まない(string dpiText)
    {
        Assert.Equal(((double?)null, (string?)null), GuiInputs.PngDpiFor(new[] { ArtifactKind.Emf, ArtifactKind.Svg, ArtifactKind.Pdf }, dpiText));
    }

    // 数でない値と、範囲（PETA の設定と同じ）の外の値は、解像度を返さずに理由を返す
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("5000")]
    public void PNGを保存するときは解像度の欄の誤りを返す(string dpiText)
    {
        var (dpi, error) = GuiInputs.PngDpiFor(new[] { ArtifactKind.Emf, ArtifactKind.Png }, dpiText);

        Assert.Equal((null, true), (dpi, error != null));
    }

    [Fact]
    public void PNGを保存するときは解像度の欄を読む()
    {
        Assert.Equal(((double?)150, (string?)null), GuiInputs.PngDpiFor(new[] { ArtifactKind.Png }, "150"));
    }

    [Fact]
    public void 形式が1つならダイアログで選んだファイルに保存する()
    {
        var outputs = GuiInputs.SaveTargets(new[] { ArtifactKind.Png }, @"C:\out\図面.png", 300);

        Assert.Equal(new[] { new PdfPetaOutput(ArtifactKind.Png, @"C:\out\図面.png", 300) }, outputs);
    }

    [Fact]
    public void 形式が2つ以上なら基本のファイル名に形式ごとの拡張子を付けて保存する()
    {
        var outputs = GuiInputs.SaveTargets(new[] { ArtifactKind.Emf, ArtifactKind.Png }, @"C:\out\図面", 150);

        Assert.Equal(new[] { (ArtifactKind.Emf, @"C:\out\図面.emf", (double?)null), (ArtifactKind.Png, @"C:\out\図面.png", (double?)150) },
            outputs.Select(o => (o.Format, o.Path, o.PngDpi)));
    }

    [Theory]
    [InlineData(2, 3, "図面-p2")]
    [InlineData(1, 3, "図面-p1")]
    [InlineData(1, 1, "図面")]
    public void 複数ページのPDFの既定のファイル名にはページの番号を付ける(int page, int count, string expected)
    {
        Assert.Equal(expected, GuiInputs.DefaultFileName(@"C:\in\図面.pdf", page, count));
    }

    // 名前にドットを含む PDF でも、拡張子の .pdf だけを除いてページの番号を付ける
    [Fact]
    public void 名前にドットを含むPDFでも名前の全体にページの番号を付ける()
    {
        Assert.Equal("plan.v2-p2", GuiInputs.DefaultFileName(@"C:\in\plan.v2.pdf", 2, 3));
    }

    [Theory]
    [InlineData("2", 3, 2)]
    [InlineData(" 9 ", 3, 3)]
    [InlineData("0", 3, 1)]
    public void ページの欄の数をページの番号にする(string text, int count, int expected)
    {
        Assert.Equal((expected, (string?)null), GuiInputs.ParsePage(text, count));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    public void ページの欄が数でなければ理由を返す(string text)
    {
        Assert.NotNull(GuiInputs.ParsePage(text, 3).Error);
    }
}
