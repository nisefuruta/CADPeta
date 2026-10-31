using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests.Duplication;

/// <summary>
/// プラグインが印刷に使う PDF のデバイス名（各製品の *PluginDefaults.PdfDeviceName）と、試験の補助に書いた名前が食い違わないことの試験。
/// 試験の補助は、.scr の応答や C# の計測器のように製品の定数を読めない形が多いので、名前を書いている。
/// 製品の名前を変えたときに、古い名前のまま残った試験の補助をここで見つける。
/// </summary>
public sealed class PdfDeviceNameTests
{
    static readonly string Root = RepositoryPaths.Root();

    // 製品ごとの PDF のデバイス名（src/CadPeta.Plugin.<製品>/<製品>PluginDefaults.cs の PdfDeviceName）
    static Dictionary<string, string> ProductDeviceNames() =>
        new[] { "Acad", "Ares", "Ijcad", "Bricscad" }.ToDictionary(product => product, product =>
        {
            string source = File.ReadAllText(Path.Combine(Root, "src", $"CadPeta.Plugin.{product}", $"{product}PluginDefaults.cs"));
            var match = Regex.Match(source, @"PdfDeviceName\s*=\s*""([^""]+)""");
            Assert.True(match.Success, $"{product}PluginDefaults.cs に PdfDeviceName が無い");
            return match.Groups[1].Value;
        });

    // ファイルとデバイス名の組を 1 回ずつ返す（同じファイルに同じ名前が 2 回あると、xUnit は同じ ID の試験を実行せずに除外する）
    public static IEnumerable<object[]> PlotDeviceLiteralsInTestTools() =>
        Directory.GetFiles(Path.Combine(Root, "tests", "tools"), "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".ps1", ".scr", ".cs", ".py", ".lsp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".venv"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"[A-Za-z][A-Za-z0-9 ()]*\.pc3").Cast<Match>()
                .Select(match => (File: Path.GetRelativePath(Root, path), Device: match.Value)))
            .Distinct()
            .Select(row => new object[] { row.File, row.Device });

    [Theory]
    [MemberData(nameof(PlotDeviceLiteralsInTestTools))]
    public void 試験の補助に書いたpc3のデバイス名は製品のデバイス名のどれかと同じ(string file, string deviceName)
    {
        Assert.True(ProductDeviceNames().ContainsValue(deviceName), $"{file}: {deviceName} はどの製品の PdfDeviceName とも違う");
    }

    // 互換 CAD の COM の試験の共通の表（tests/tools/common/compat_cad_com.ps1 の $CompatProducts）は、製品ごとに同じ名前を持つ
    [Theory]
    [InlineData("Acad2026", "Acad")]
    [InlineData("Acad2027", "Acad")]
    [InlineData("Ares2020", "Ares")]
    [InlineData("Ares2023", "Ares")]
    [InlineData("Ares2024", "Ares")]
    [InlineData("Ares2027", "Ares")]
    [InlineData("Ijcad2026", "Ijcad")]
    [InlineData("BricscadV26", "Bricscad")]
    public void 互換CADの試験の表のデバイス名は製品と同じ(string compatProduct, string product)
    {
        string table = File.ReadAllText(Path.Combine(Root, "tests", "tools", "common", "compat_cad_com.ps1"));
        var row = Regex.Match(table, $@"(?m)^  {compatProduct} = @\{{(?<body>(?:.|\n)*?)\}}\r?$");
        Assert.True(row.Success, $"$CompatProducts に {compatProduct} の行が無い");
        var device = Regex.Match(row.Groups["body"].Value, @"PdfDevice = ""([^""]+)""");

        Assert.Equal(ProductDeviceNames()[product], device.Groups[1].Value);
    }

    // 上の Theory が 0 件で合格しないように、各製品の名前が試験の補助から見つかることを確かめる
    [Fact]
    public void 試験の補助からAutoCADのデバイス名が見つかる()
    {
        Assert.Contains(PlotDeviceLiteralsInTestTools(), row => (string)row[1] == ProductDeviceNames()["Acad"]);
    }
}
