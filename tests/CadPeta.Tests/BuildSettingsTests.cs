using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests;

// ビルドの共通の設定の検査。ImplicitUsings は全プロジェクトで disable にし、using を明示する
// （使われていない using を厳密に確かめるためのユーザーの決定）。
public class BuildSettingsTests
{
    static readonly Regex ImplicitUsingsEnabled = new(@"<ImplicitUsings>\s*(enable|true)\s*</ImplicitUsings>",
        RegexOptions.IgnoreCase);

    [Fact]
    public void ImplicitUsingsを有効にしているプロジェクトと共通の設定が無い()
    {
        string root = RepositoryPaths.Root();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csproj") || f.EndsWith(".props") || f.EndsWith(".targets"))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git"))
            .ToArray();
        Assert.NotEmpty(files);
        var enabled = files.Where(f => ImplicitUsingsEnabled.IsMatch(File.ReadAllText(f)))
            .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)).ToArray();
        Assert.True(enabled.Length == 0, "ImplicitUsings を有効にしている: " + string.Join(", ", enabled));
        Assert.Contains("<ImplicitUsings>disable</ImplicitUsings>",
            File.ReadAllText(Path.Combine(root, "src", "Directory.Build.props")));
    }
}
