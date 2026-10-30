using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public sealed class WindowPickSpaceScopeTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(0, 3, true)]
    public void TILEMODEとCVPORTから切替要否を決める(short tileMode, short cvport, bool expected) =>
        Assert.Equal(expected, WindowPickSpaceScope.NeedsPaperSpace(tileMode, cvport, 1));

    [Fact]
    public void 正常終了で元MSPACEへ戻す()
    {
        var calls = new List<string>();
        using (WindowPickSpaceScope.Enter(0, 3, 1,
            () => calls.Add("Paper"), () => calls.Add("Model3"), () => calls.Add("Log")))
            calls.Add("Prompt");
        Assert.Equal(new[] { "Paper", "Log", "Prompt", "Model3" }, calls);
    }

    [Theory]
    [InlineData("Paper")]
    [InlineData("Log")]
    public void 開始途中の例外でも復元する(string failure)
    {
        var calls = new List<string>();
        Assert.Throws<InvalidOperationException>(() => WindowPickSpaceScope.Enter(0, 2, 1,
            () => { calls.Add("Paper"); if (failure == "Paper") throw new InvalidOperationException("開始失敗"); },
            () => calls.Add("Model2"),
            () => { calls.Add("Log"); if (failure == "Log") throw new InvalidOperationException("ログ失敗"); }));
        Assert.Equal("Model2", calls[calls.Count - 1]);
    }

    [Fact]
    public void 復元失敗後は再試行できる()
    {
        int attempts = 0;
        var scope = WindowPickSpaceScope.Enter(0, 2, 1, () => { },
            () => { attempts++; if (attempts == 1) throw new IOException("復元失敗"); }, () => { });
        Assert.Throws<IOException>(() => scope.Dispose());
        scope.Dispose();
        Assert.Equal(2, attempts);
    }
}
