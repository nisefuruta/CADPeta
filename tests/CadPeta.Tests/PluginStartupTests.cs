using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

// 読み込み時の初期化: 処理の失敗は報告して、読み込みと後の処理を続ける
public class PluginStartupTests
{
    [Fact]
    public void 失敗は報告して後の処理も実行する()
    {
        var reported = new List<string>();
        bool laterRan = false;
        PluginStartup.Run("Plugin.Load", () => throw new IOException("記録できない"),
            (step, ex) => reported.Add(step + ":" + ex.Message));
        PluginStartup.Run("Version", () => laterRan = true, (step, _) => reported.Add(step));

        Assert.Equal(new[] { "Plugin.Load:記録できない" }, reported);
        Assert.True(laterRan);
    }

    [Fact]
    public void 報告の関数が失敗しても読み込みを止めない()
    {
        bool laterRan = false;
        PluginStartup.Run("Version", () => throw new IOException("表示できない"),
            (_, _) => throw new InvalidOperationException("報告できない"));
        PluginStartup.Run("Plugin.Load", () => laterRan = true, (_, _) => { });

        Assert.True(laterRan);
    }
}
