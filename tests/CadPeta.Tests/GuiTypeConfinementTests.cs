using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests;

// 画面の型（WPF、acmgd の ShowModalWindow、AdWindows のリボン、設定のダイアログ）は、プラグインの GuiCommands*.cs だけで使う。
// accoreconsole にはこれらの DLL が無く、ほかのファイルのメソッドで使うと、そのメソッドを JIT するときに読み込みに失敗する
// （accoreconsole で PETA と -PETAOPT が動かなくなる）。GuiCommands のメソッドは画面のある CAD のときだけ呼ぶ
public sealed class GuiTypeConfinementTests
{
    static readonly Regex[] GuiTypes =
    {
        new(@"\bCadPeta\.Ui\.Wpf\b"),
        new(@"\bShowModalWindow\b"),
        new(@"\bAutodesk\.Windows\b"),
        new(@"\bAutodesk\.AutoCAD\.Ribbon\b"),
        new(@"\bSystem\.Windows\b"),
        new(@"\bSettingsDialog\b"),
        new(@"\bCommentDialog\b"),
    };

    static readonly string[] PluginFolders =
    {
        "CadPeta.Plugin.CadCommon", "CadPeta.Plugin.Shared", "CadPeta.Plugin.Acad",
        "CadPeta.Plugin.Ares", "CadPeta.Plugin.Bricscad", "CadPeta.Plugin.Ijcad",
    };

    /// <summary>GuiCommands で始まる名前のファイルの外で画面の型を使っている箇所（「ファイル: 型」）。</summary>
    internal static string[] Violations(IEnumerable<(string Path, string Text)> sources) =>
        sources
            .Where(source => !Path.GetFileName(source.Path).StartsWith("GuiCommands", StringComparison.Ordinal))
            .SelectMany(source => GuiTypes.Where(type => type.IsMatch(source.Text)).Select(type => source.Path + ": " + type))
            .ToArray();

    static IEnumerable<(string Path, string Text)> PluginSources() =>
        PluginFolders
            .Select(folder => Path.Combine(RepositoryPaths.Root(), "src", folder))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(path => (path, File.ReadAllText(path)));

    [Fact]
    public void プラグインの画面の型はGuiCommandsのファイルだけにある()
    {
        Assert.Empty(Violations(PluginSources()));
    }

    [Fact]
    public void GuiCommandsの外の画面の型を見つける()
    {
        var sources = new[]
        {
            ("PetaCommand.cs", "Application.ShowModalWindow(dialog);"),
            ("GuiCommands.cs", "Application.ShowModalWindow(dialog);"),
            ("PetaOptCommands.cs", "static bool OpenSettingsDialog() => GuiCommands.ShowSettingsDialog();"),
        };

        Assert.Equal(new[] { @"PetaCommand.cs: \bShowModalWindow\b" }, Violations(sources));
    }
}