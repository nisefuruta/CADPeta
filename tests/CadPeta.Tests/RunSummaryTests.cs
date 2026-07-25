using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// 日本語カタログ（Msg.Japanese）経由で従来文言と一致することの回帰を兼ねる
public sealed class RunSummaryTests
{
    [Fact]
    public void Build_DefaultOptions_IsOneLine()
    {
        string text = RunSummary.Build(Msg.Japanese, "drawing_layout.emf", 15204, 47, true,
            false, false, false, 0);

        Assert.Equal("drawing_layout.emf  15,204 bytes / 47図形 → クリップボード登録", text);
    }

    [Fact]
    public void Build_FileOutputOff_ReportsFactWithoutDebugGuidance()
    {
        string text = RunSummary.Build(Msg.Japanese, null, 1200, 3, true,
            false, false, false, 0);

        Assert.Equal("1,200 bytes / 3図形 → クリップボード登録（ファイル出力オフ）", text);
        Assert.DoesNotContain("系統", text);
        Assert.DoesNotContain("Ctrl+V", text);
        Assert.DoesNotContain("非可逆", text);
    }

    [Fact]
    public void Build_ListsOnlyEnabledNonDefaultOptions()
    {
        string text = RunSummary.Build(Msg.Japanese, "x.emf", 1, 2, false,
            true, true, true, 5);

        Assert.Contains("クリップボード登録失敗", text);
        Assert.Contains("オプション: レイヤ別グループ化, SHX代替書体, 文字輪郭化, 余白5mm", text);
    }
}
