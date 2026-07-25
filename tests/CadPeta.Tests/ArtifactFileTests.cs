using System.Text;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public sealed class ArtifactFileTests
{
    [Fact]
    public void 四形式の完結した成果物を受理する()
    {
        WithTemp(dir =>
        {
            Write(Path.Combine(dir, "a.pdf"), ValidPdf());
            File.WriteAllText(Path.Combine(dir, "a.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"><path/></svg>", Encoding.UTF8);
            Write(Path.Combine(dir, "a.emf"), ValidEmf());
            Write(Path.Combine(dir, "a.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            ArtifactFile.Validate(Path.Combine(dir, "a.pdf"), ArtifactKind.Pdf);
            ArtifactFile.Validate(Path.Combine(dir, "a.svg"), ArtifactKind.Svg);
            ArtifactFile.Validate(Path.Combine(dir, "a.emf"), ArtifactKind.Emf);
            ArtifactFile.Validate(Path.Combine(dir, "a.png"), ArtifactKind.Png);
        });
    }

    [Theory]
    [InlineData(ArtifactKind.Pdf)]
    [InlineData(ArtifactKind.Svg)]
    [InlineData(ArtifactKind.Emf)]
    [InlineData(ArtifactKind.Png)]
    public void 空ファイルを拒否する(ArtifactKind kind) => WithTemp(dir =>
    {
        string path = Path.Combine(dir, "empty"); File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(path, kind));
    });

    [Fact]
    public void PDFのEOF欠落と不正xrefを拒否する() => WithTemp(dir =>
    {
        string noEof = Path.Combine(dir, "no-eof.pdf"); Write(noEof, Encoding.ASCII.GetBytes("%PDF-1.4\nstartxref\n9\n"));
        string badXref = Path.Combine(dir, "bad-xref.pdf"); Write(badXref, Encoding.ASCII.GetBytes("%PDF-1.4\nstartxref\n999999\n%%EOF\n"));
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(noEof, ArtifactKind.Pdf));
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(badXref, ArtifactKind.Pdf));
    });

    [Fact]
    public void SVGの閉じタグ欠落を拒否する() => WithTemp(dir =>
    {
        string path = Path.Combine(dir, "broken.svg"); File.WriteAllText(path, "<svg><g>");
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(path, ArtifactKind.Svg));
    });

    [Fact]
    public void EMFの宣言長不一致とEOF欠落を拒否する() => WithTemp(dir =>
    {
        byte[] wrongLength = ValidEmf(); PutU32(wrongLength, 48, 999);
        byte[] noEof = ValidEmf(); PutU32(noEof, 88, 2);
        string a = Path.Combine(dir, "length.emf"), b = Path.Combine(dir, "eof.emf"); Write(a, wrongLength); Write(b, noEof);
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(a, ArtifactKind.Emf));
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(b, ArtifactKind.Emf));
    });

    [Fact]
    public void PNGのIEND欠落と末尾ごみを拒否する() => WithTemp(dir =>
    {
        byte[] valid = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        string cut = Path.Combine(dir, "cut.png"), junk = Path.Combine(dir, "junk.png");
        Write(cut, valid.Take(valid.Length - 12).ToArray()); Write(junk, valid.Concat(new byte[] { 1 }).ToArray());
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(cut, ArtifactKind.Png));
        Assert.Throws<InvalidDataException>(() => ArtifactFile.Validate(junk, ArtifactKind.Png));
    });

    [Fact]
    public void 書込失敗と検証失敗では既存成果物を壊さない() => WithTemp(dir =>
    {
        string path = Path.Combine(dir, "result.svg"); File.WriteAllText(path, "既存成果物", Encoding.UTF8);
        Assert.Throws<IOException>(() => ArtifactFile.WriteAtomically(path, ArtifactKind.Svg, temp =>
        { File.WriteAllText(temp, "途中"); throw new IOException("故障注入"); }));
        Assert.Equal("既存成果物", File.ReadAllText(path, Encoding.UTF8));
        Assert.Throws<InvalidDataException>(() => ArtifactFile.WriteAtomically(path, ArtifactKind.Svg,
            temp => File.WriteAllText(temp, "<svg><g>")));
        Assert.Equal("既存成果物", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(dir, ".result.svg.*.tmp"));
    });

    [Fact]
    public void 検証済み一時成果物だけを公開する() => WithTemp(dir =>
    {
        string path = Path.Combine(dir, "result.svg"); File.WriteAllText(path, "旧");
        ArtifactFile.WriteAtomically(path, ArtifactKind.Svg,
            temp => File.WriteAllText(temp, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", Encoding.UTF8));
        Assert.Contains("<svg", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(dir, ".result.svg.*.tmp"));
    });

    static byte[] ValidPdf() => MinimalArtifactBytes.Pdf();
    static byte[] ValidEmf() => MinimalArtifactBytes.Emf();
    static void PutU32(byte[] b, int o, uint v) => MinimalArtifactBytes.PutU32(b, o, v);
    static void Write(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
    static void WithTemp(Action<string> run)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CadPetaArtifactTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { run(dir); } finally { Directory.Delete(dir, true); }
    }
}
