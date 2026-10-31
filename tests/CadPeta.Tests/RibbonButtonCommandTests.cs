using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

// リボンのボタンが送る文字列。コマンドの実行中だけ取り消しの文字（Ctrl+C）を送る。
// 実行中でないときに送ると、先に選んだオブジェクト（先選択）が解除され、PETA の絞り込みに使えない
public sealed class RibbonButtonCommandTests
{
    [Fact]
    public void コマンドが実行中でなければ取り消しの文字を送らない()
    {
        Assert.Equal("_.PETA ", RibbonLayout.ButtonCommand("PETA", commandActive: false));
    }

    [Fact]
    public void コマンドの実行中は取り消してから送る()
    {
        Assert.Equal("\x03\x03_.PETA ", RibbonLayout.ButtonCommand("PETA", commandActive: true));
    }
}
