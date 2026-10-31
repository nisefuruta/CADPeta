using System;
using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETA の最後の処理。クリップボードに登録できなければ、使える EMF（出力した EMF、無ければ一時フォルダから移した EMF）を案内する。
// 写せなかったファイルは、PETADIAG でも必ず表示する
public sealed class PetaOutcomeTests
{
    static string Report(bool diagnostic, PublishResult publish, string? clipError = null, string availableEmf = @"C:\tmp\x.emf",
                         bool groupByLayer = false)
    {
        var sw = new StringWriter();
        PetaOutcome.Report(Msg.Japanese, sw, diagnostic, publish, clipError, availableEmf,
            15204, 47, groupByLayer, false, false, 0);
        return sw.ToString();
    }

    static PublishResult Published(params string[] paths) => new(paths, Array.Empty<string>());

    [Fact]
    public void 成功したら要約と出力したファイルを出す()
    {
        var publish = Published(@"C:\out\dwg_Model.emf");

        string text = Report(false, publish, groupByLayer: true);

        // 要約の文の形は RunSummaryTests が確かめる。ここは Report がその要約を出すことだけを確かめる
        Assert.Contains(RunSummary.Build(Msg.Japanese, publish.Published, 15204, 47, true, true, false, false, 0), text);
        Assert.DoesNotContain("失敗", text);
    }

    [Fact]
    public void 診断は要約を出さず出力したファイルだけを出す()
    {
        string text = Report(true, Published(@"C:\out\x.emf"));

        Assert.Contains(@"出力: x.emf（C:\out）", text);
        Assert.DoesNotContain("bytes", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 写せなかったファイルは診断でも出す(bool diagnostic)
    {
        var publish = new PublishResult(Array.Empty<string>(), new[] { @"出力できなかったファイル: C:\out\x.pdf（拒否）" });

        Assert.Contains(@"出力できなかったファイル: C:\out\x.pdf（拒否）", Report(diagnostic, publish));
    }

    [Fact]
    public void クリップボードに登録できなければ使えるEMFを案内する()
    {
        string text = Report(false, Published(@"C:\out\x.emf"), "クリップボードを開けない", @"C:\out\x.emf");

        Assert.Contains("クリップボード登録に失敗した: クリップボードを開けない", text);
        Assert.Contains(@"利用可能な EMF: C:\out\x.emf", text);
        Assert.Contains("クリップボード登録失敗", text);
    }

    [Fact]
    public void 出力したEMFがあれば一時フォルダのEMFを動かさずにそれを使う()
    {
        using var scope = ArtifactScope.CreateTemp("d_l");
        string emf = scope.PathOf(ArtifactKind.Emf);
        File.WriteAllBytes(emf, MinimalArtifactBytes.Emf());

        string available = PetaOutcome.AvailableEmf(scope, emf, @"C:\out\d_l.emf", warn: _ => { });

        Assert.Equal(@"C:\out\d_l.emf", available);
        Assert.True(File.Exists(emf));
    }

    [Fact]
    public void 出力したEMFが無ければ一時フォルダのEMFを移して使う()
    {
        using var scope = ArtifactScope.CreateTemp("d_l");
        string emf = scope.PathOf(ArtifactKind.Emf);
        File.WriteAllBytes(emf, MinimalArtifactBytes.Emf());

        string available = PetaOutcome.AvailableEmf(scope, emf, null, warn: _ => { });
        try
        {
            Assert.NotEqual(emf, available);
            Assert.False(File.Exists(emf));
            Assert.True(File.Exists(available));
        }
        finally { if (File.Exists(available)) File.Delete(available); }
    }
}