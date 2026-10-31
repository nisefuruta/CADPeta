using System;
using System.IO;
using System.Linq;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETA は成果物を一時フォルダで作り、最後に設定の形式だけを出力先へ写す。名前はファイル名の形から作り、{seq} はフォルダの既存の番号の次、
// 同じ名前があれば (1) を付ける。PETADIAG は全部の形式と診断のファイルを写す。写せなかったファイルは失敗の一覧で返し、ほかのファイルは写す
public sealed class ArtifactPublisherTests : IDisposable
{
    static readonly DateTime Now = new(2026, 10, 28, 18, 0, 0);
    static readonly FileNameFields Fields = new("図面", "A3", Now, "");
    const string Name = "図面_A3_20261028_180000";

    readonly TempFolder _out = new();
    readonly ArtifactScope _work = ArtifactScope.CreateTemp("work");

    public void Dispose()
    {
        _work.Dispose();
        _out.Dispose();
    }

    // 一時フォルダに、検査を通る中身の成果物を作る
    void Make(params ArtifactKind[] kinds)
    {
        foreach (var kind in kinds)
            File.WriteAllBytes(_work.PathOf(kind), MinimalArtifactBytes.Of(kind));
    }

    PublishResult Publish(ArtifactKindSet formats, bool diagnostics = false, FileNameTemplate? template = null, string? dir = null) =>
        ArtifactPublisher.Publish(_work, formats, diagnostics, template ?? FileNameTemplate.Default, Fields, dir ?? _out.Path);

    string[] OutputNames() => Directory.GetFiles(_out.Path).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void 設定の形式だけを出力先に写す()
    {
        Make(ArtifactKind.Pdf, ArtifactKind.Svg, ArtifactKind.Emf, ArtifactKind.Png);

        var r = Publish(ArtifactKindSet.Of(ArtifactKind.Svg, ArtifactKind.Png));

        Assert.Equal(new[] { Name + ".png", Name + ".svg" }, OutputNames());
        Assert.Equal(2, r.Published.Count);
        Assert.Empty(r.Failures);
    }

    [Fact]
    public void 形式が0件なら何も写さず出力先も作らない()
    {
        Make(ArtifactKind.Emf);
        string dir = Path.Combine(_out.Path, "sub");

        var r = Publish(ArtifactKindSet.Empty, dir: dir);

        Assert.Empty(r.Published);
        Assert.Empty(r.Failures);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void 同じ名前のファイルがあれば括弧の番号を付ける()
    {
        Make(ArtifactKind.Emf);
        File.WriteAllText(Path.Combine(_out.Path, Name + ".pdf"), "既存");

        Publish(ArtifactKindSet.Of(ArtifactKind.Emf));

        Assert.Contains(Name + "(1).emf", OutputNames());
        Assert.Equal("既存", File.ReadAllText(Path.Combine(_out.Path, Name + ".pdf")));
    }

    [Fact]
    public void 連番はフォルダの既存の番号の次()
    {
        Make(ArtifactKind.Emf);
        File.WriteAllText(Path.Combine(_out.Path, "図面_004.emf"), "既存");

        Publish(ArtifactKindSet.Of(ArtifactKind.Emf), template: FileNameTemplate.Parse("{drawing}_{seq}").Template);

        Assert.Contains("図面_005.emf", OutputNames());
    }

    [Fact]
    public void 診断は全部の形式と診断のファイルを写す()
    {
        Make(ArtifactKind.Pdf, ArtifactKind.Svg, ArtifactKind.Emf);
        File.WriteAllText(_work.PathOf(ArtifactKind.Svg) + ".diag.json", "{}");
        File.WriteAllText(Path.Combine(_work.Dir, _work.BaseName + ".stage01.crop.svg"), "<svg/>");

        var r = Publish(ArtifactKindSet.Of(ArtifactKind.Emf), diagnostics: true);

        Assert.Equal(new[] { Name + ".emf", Name + ".pdf", Name + ".stage01.crop.svg", Name + ".svg", Name + ".svg.diag.json" }, OutputNames());
        Assert.Empty(r.Failures);
    }

    [Fact]
    public void 写せなかった形式は失敗に入れほかの形式は写す()
    {
        Make(ArtifactKind.Svg);

        var r = Publish(ArtifactKindSet.Of(ArtifactKind.Pdf, ArtifactKind.Svg));

        Assert.Equal(new[] { Name + ".svg" }, OutputNames());
        Assert.Contains(r.Failures, f => f.Contains(Name + ".pdf"));
    }

    [Fact]
    public void 出力先を作れなければ失敗を返す()
    {
        Make(ArtifactKind.Emf);
        string file = Path.Combine(_out.Path, "file");
        File.WriteAllText(file, "");

        var r = Publish(ArtifactKindSet.Of(ArtifactKind.Emf), dir: Path.Combine(file, "sub"));

        Assert.Empty(r.Published);
        Assert.NotEmpty(r.Failures);
    }
}
