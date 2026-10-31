using System.Linq;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// 成果物の形式の一覧は ArtifactKind の 1 つ。設定のファイルの名前と拡張子は、どちらも列挙の名前から作る
public class ArtifactKindsTests
{
    [Theory]
    [InlineData(ArtifactKind.Pdf, "pdf", ".pdf")]
    [InlineData(ArtifactKind.Svg, "svg", ".svg")]
    [InlineData(ArtifactKind.Emf, "emf", ".emf")]
    [InlineData(ArtifactKind.Png, "png", ".png")]
    public void 名前と拡張子は列挙の名前の小文字(ArtifactKind kind, string name, string extension)
    {
        Assert.Equal(name, ArtifactKinds.Name(kind));
        Assert.Equal(extension, ArtifactKinds.Extension(kind));
        Assert.Equal(kind, ArtifactKinds.FromName(name));
    }

    [Theory]
    [InlineData("bmp")]
    [InlineData("EMF")]
    [InlineData("")]
    public void 知らない名前は形式にしない(string name)
    {
        Assert.Null(ArtifactKinds.FromName(name));
    }

    // -PETAOPT の形式の入力（-OSNAP と同じく、オンにする形式をカンマで区切って並べる。大文字と小文字を区別しない。NONE は 0 件）
    [Theory]
    [InlineData("PDF,EMF", new[] { ArtifactKind.Pdf, ArtifactKind.Emf })]
    [InlineData(" png , svg ", new[] { ArtifactKind.Svg, ArtifactKind.Png })]
    [InlineData("emf,EMF", new[] { ArtifactKind.Emf })]
    [InlineData("NONE", new ArtifactKind[0])]
    [InlineData("none", new ArtifactKind[0])]
    public void 形式の一覧はカンマ区切りの名前でNONEは0件(string text, ArtifactKind[] expected)
    {
        var (set, error) = ArtifactKinds.ParseList(text);

        Assert.Null(error);
        Assert.Equal(ArtifactKindSet.Of(expected), set);
    }

    // OSMODE と同じく、形式ごとの値の合計でも指定できる。値は利用者とスクリプトとの約束なので数のまま固定する
    [Theory]
    [InlineData(ArtifactKind.Pdf, 1)]
    [InlineData(ArtifactKind.Svg, 2)]
    [InlineData(ArtifactKind.Emf, 4)]
    [InlineData(ArtifactKind.Png, 8)]
    public void 形式の値はPDFが1でSVGが2でEMFが4でPNGが8(ArtifactKind kind, int value)
    {
        Assert.Equal(value, ArtifactKindSet.Of(kind).Value);
    }

    [Theory]
    [InlineData("5", new[] { ArtifactKind.Pdf, ArtifactKind.Emf })]
    [InlineData("15", new[] { ArtifactKind.Pdf, ArtifactKind.Svg, ArtifactKind.Emf, ArtifactKind.Png })]
    [InlineData("0", new ArtifactKind[0])]
    public void 形式は値の合計でも指定できる(string text, ArtifactKind[] expected)
    {
        var (set, error) = ArtifactKinds.ParseList(text);

        Assert.Null(error);
        Assert.Equal(ArtifactKindSet.Of(expected), set);
    }

    [Theory]
    [InlineData("16")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public void 範囲の外の値は理由を返す(string text)
    {
        var (set, error) = ArtifactKinds.ParseList(text);

        Assert.Null(set);
        Assert.Contains(text, error);
    }

    [Theory]
    [InlineData("PDF,BMP", "BMP")]
    [InlineData("PDF,,EMF", "")]
    [InlineData("PDF,NONE", "NONE")]
    public void 知らない名前と空の項目とNONEの混在は理由を返す(string text, string bad)
    {
        var (set, error) = ArtifactKinds.ParseList(text);

        Assert.Null(set);
        Assert.Contains(bad, error);
    }

    [Fact]
    public void 形式の集合は列挙の順に並び同じ形式を2回入れても1つ()
    {
        var set = ArtifactKindSet.Of(ArtifactKind.Png, ArtifactKind.Pdf, ArtifactKind.Png);

        Assert.Equal(new[] { ArtifactKind.Pdf, ArtifactKind.Png }, set.Kinds.ToArray());
        Assert.True(set.Contains(ArtifactKind.Png));
        Assert.False(set.Contains(ArtifactKind.Emf));
        Assert.Equal(ArtifactKindSet.Of(ArtifactKind.Pdf), set.With(ArtifactKind.Png, false));
        Assert.True(ArtifactKindSet.Empty.IsEmpty);
    }
}
