using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests.Duplication;

/// <summary>
/// 互換 CAD のオプションの試験（tests/tools/regression/peta_option_smoke_compat.ps1）が製品ごとに選ぶ窓の期待値の表と、
/// 製品の PDF のページの丸め（各製品の *PluginDefaults.PdfPointSnapping）が食い違わないことの試験。
/// 窓の viewBox の Y はページの高さで変わるので、製品の丸めを変えたら、試験の期待値の表もここで直すことになる。
/// </summary>
public sealed class PdfPointSnappingExpectTests
{
    static readonly string Root = RepositoryPaths.Root();

    [Theory]
    [InlineData("Ares2020", "Ares", "PageSize", "PageHeight842pt")]
    [InlineData("Ares2023", "Ares", "PageSize", "PageHeight842pt")]
    [InlineData("Ares2024", "Ares", "PageSize", "PageHeight842pt")]
    [InlineData("Ares2027", "Ares", "PageSize", "PageHeight842pt")]
    [InlineData("Ijcad2026", "Ijcad", "None", "PageHeight297mm")]
    [InlineData("BricscadV26", "Bricscad", "None", "PageHeight297mm")]
    public void 互換CADの試験が選ぶ窓の期待値は製品のページの丸めに合う(string compatProduct, string product, string snapping, string windowExpect)
    {
        string defaults = File.ReadAllText(Path.Combine(Root, "src", $"CadPeta.Plugin.{product}", $"{product}PluginDefaults.cs"));
        string smoke = File.ReadAllText(Path.Combine(Root, "tests", "tools", "regression", "peta_option_smoke_compat.ps1"));
        string expectTable = File.ReadAllText(Path.Combine(Root, "tests", "tools", "regression", "peta_a3_window_expect.ps1"));

        var productSnapping = Regex.Match(defaults, @"PdfPointSnapping PdfPointSnapping = Shared\.PdfPointSnapping\.(\w+);");
        var row = Regex.Match(smoke, $@"(?m)^  {compatProduct} = @\{{(?<body>(?:.|\n)*?)\}}\r?$");
        var chosen = Regex.Match(row.Groups["body"].Value, @"WindowExpect = ""([^""]+)""");
        var table = Regex.Match(expectTable, $@"(?m)^    {windowExpect} = @\{{(?<body>(?:.|\n)*?)^    \}}");

        Assert.Equal(snapping, productSnapping.Groups[1].Value);
        Assert.Equal(windowExpect, chosen.Groups[1].Value);
        Assert.Matches(@"(?m)^\s+M5\s+= @\{ W = ", table.Groups["body"].Value);
    }
}
