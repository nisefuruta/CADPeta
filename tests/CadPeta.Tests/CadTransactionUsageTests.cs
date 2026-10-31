using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests;

/// <summary>
/// CAD の図面のトランザクションを、開いたオブジェクトを Dispose する CadTransaction（Acquire.CadCommon）だけで始めることの試験。
/// ARES（Teigha）の Transaction は GetObject が返したラッパーを解放せず、図面を閉じた後にファイナライザが解放すると
/// CAD が異常終了する。CAD の SDK を使うコードが Transaction を直接始めると、その経路のラッパーが解放されない。
/// </summary>
public sealed class CadTransactionUsageTests
{
    static readonly string Root = RepositoryPaths.Root();

    // CAD の SDK を使うプロジェクト（4製品の共有コードと、製品ごとのプラグインと取得器）
    static readonly string[] CadSdkFolders =
    {
        "CadPeta.Plugin.CadCommon", "CadPeta.Acquire.CadCommon",
        "CadPeta.Plugin.Acad", "CadPeta.Plugin.Ares", "CadPeta.Plugin.Ijcad", "CadPeta.Plugin.Bricscad",
        "CadPeta.Acquire.Acad", "CadPeta.Acquire.Ares", "CadPeta.Acquire.Ijcad", "CadPeta.Acquire.Bricscad",
    };

    // トランザクションを始める呼び出しと、トランザクションの外でオブジェクトを開く呼び出し（ObjectId の GetObject・Open）。
    // CadTransaction の GetObject は ObjectId を最初の引数に取るので、OpenMode から始まる GetObject には一致しない
    [Theory]
    [InlineData(@"\bStart(OpenClose)?Transaction\s*\(")]
    [InlineData(@"\bnew\s+OpenCloseTransaction\s*\(")]
    [InlineData(@"\.GetObject\(\s*OpenMode\b")]
    [InlineData(@"\.Open\(\s*OpenMode\b")]
    public void 図面のオブジェクトはCadTransactionだけが開く(string pattern)
    {
        var calls = CadSdkFolders
            .SelectMany(folder => Directory.GetFiles(Path.Combine(Root, "src", folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => Path.GetFileName(path) != "CadTransaction.cs")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(row => !row.line.TrimStart().StartsWith("//"))
            .Where(row => Regex.IsMatch(row.line, pattern))
            .Select(row => $"{row.path.Substring(Root.Length).TrimStart(Path.DirectorySeparatorChar)}:{row.index + 1}: {row.line.Trim()}")
            .ToArray();

        Assert.True(calls.Length == 0, "CadTransaction を使わずに図面のオブジェクトを開いている:\n" + string.Join("\n", calls));
    }
}
