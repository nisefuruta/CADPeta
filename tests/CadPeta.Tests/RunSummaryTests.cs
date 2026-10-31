using System;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETA の結果の要約。1 行目は EMF の大きさ・図形の数・クリップボード、2 行目は出力したファイル（無ければ PETAOPT の案内）
public sealed class RunSummaryTests
{
    [Fact]
    public void 要約と出力したファイルの名前とフォルダを出す()
    {
        string text = RunSummary.Build(Msg.Japanese, new[] { @"C:\out\a.emf" }, 15204, 47, true,
            false, false, false, 0);

        Assert.Equal("15,204 bytes / 47図形 → クリップボード登録" + Environment.NewLine + @"出力: a.emf（C:\out）", text);
    }

    [Fact]
    public void 複数のファイルは名前を並べる()
    {
        string text = RunSummary.Build(Msg.Japanese, new[] { @"C:\out\a.emf", @"C:\out\a.pdf" }, 1, 1, true,
            false, false, false, 0);

        Assert.Contains(@"出力: a.emf、a.pdf（C:\out）", text);
    }

    [Fact]
    public void ファイルを出力しなかったときはPETAOPTを案内する()
    {
        string text = RunSummary.Build(Msg.Japanese, Array.Empty<string>(), 1200, 3, true,
            false, false, false, 0);

        Assert.StartsWith("1,200 bytes / 3図形 → クリップボード登録", text);
        Assert.Contains("PETAOPT", text);
    }

    [Fact]
    public void 既定と違うオプションだけを並べる()
    {
        string text = RunSummary.Build(Msg.Japanese, new[] { @"C:\out\x.emf" }, 1, 2, false,
            true, true, true, 5);

        Assert.Contains("クリップボード登録失敗", text);
        Assert.Contains("オプション: レイヤ別グループ化, SHX代替書体, 文字輪郭化, 余白5mm", text);
    }
}