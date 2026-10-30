using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CadPeta.Tests.Duplication;

/// <summary>
/// dist と tests/tools の .ps1 に、同じ目的の関数が 2 か所以上にないことの機械検査。構文解析は <c>tests/tools/lint/find_script_duplicates.ps1</c> が行い、
/// 同じ名前の関数が 2 つ以上のファイルにあるものと、空白とコメントを除いて本体が同じ関数を見つける。許可の一覧と判定はここにある。
/// </summary>
public sealed class ScriptDuplicationTests
{
    private sealed record Finding(string Kind, string Names, string[] Places);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static Finding[] Find(string? folder = null)
    {
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-File", Path.Combine(RepositoryPaths.Root(), "tests", "tools", "lint", "find_script_duplicates.ps1") };
        if (folder != null) arguments.AddRange(new[] { "-Folder", folder, "-Root", folder });
        var result = ProcessRunner.RunPowerShell("pwsh", arguments, Timeout);
        Assert.True(result.ExitCode == 0, "find_script_duplicates.ps1 が失敗しました: " + result.Error);
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Select(parts => new Finding(parts[0], parts[1], parts[2].Split(';')))
            .ToArray();
    }

    /// <summary>
    /// 同じ名前の関数を、別のファイルに定義してよいものと、その理由(キーは関数名)。
    /// 理由を書けないものは許さず、関数を 1 か所に置いて読み込む。
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSameNames = new();

    /// <summary>本体が同じでも別々に持ってよい関数の組と、その理由(キーは関数名を並べ替えて「,」でつないだもの)。</summary>
    private static readonly Dictionary<string, string> AllowedSameBodies = new();

    private static string Join(Finding finding) => finding.Names + "  " + string.Join(" ", finding.Places);

    private static string KeyOf(Finding finding) => string.Join(",", finding.Names.Split(',').OrderBy(name => name, StringComparer.Ordinal));

    /// <summary><paramref name="kind"/> の検出が許可の一覧の外に無く、許可の一覧の項目がすべてまだ検出されることを確かめる。</summary>
    private static void AssertNoFindings(string kind, Dictionary<string, string> allowed, Func<Finding, string> keyOf, string header)
    {
        var findings = Find().Where(each => each.Kind == kind).ToArray();
        var unexpected = findings.Where(each => !allowed.ContainsKey(keyOf(each))).ToArray();
        Assert.True(unexpected.Length == 0, header + "(" + unexpected.Length + " 件):\n" + string.Join("\n", unexpected.Select(Join)));
        CodeDuplicationTests.AssertAllowedStillFound(allowed.Keys, findings.Select(keyOf));
    }

    // 引数を省略した lint が調べるフォルダ（tests/tools/lint/lint_common.ps1 の Resolve-LintScope）。
    // 根を誤ると、存在しないフォルダを読み飛ばして何も検査せずに合格する
    [Fact]
    public void 引数を省略したlintはリポジトリのdistとtests_toolsを調べる()
    {
        var result = ProcessRunner.RunPowerShell("pwsh",
            new[] { "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference = 'Stop'; . $env:CP_LINT; $s = Resolve-LintScope @() ''; [Console]::Out.Write($s.Root + '|' + ($s.Folder -join '|'))" },
            Timeout,
            new Dictionary<string, string> { ["CP_LINT"] = Path.Combine(RepositoryPaths.Root(), "tests", "tools", "lint", "lint_common.ps1") });
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        string root = Path.GetFullPath(RepositoryPaths.Root()).TrimEnd(Path.DirectorySeparatorChar);
        var parts = result.Output.Split('|').Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        Assert.Equal(new[] { root, Path.Combine(root, "dist"), Path.Combine(root, "tests", "tools") }, parts);
    }

    // 指定したフォルダが無いときは、読み飛ばして合格にせず失敗する
    [Fact]
    public void lintは調べるフォルダが無ければ失敗する()
    {
        using var temp = new TempFolder();
        string missing = temp.File("missing");
        foreach (var script in new[] { "find_script_duplicates.ps1", "find_ref_without_variable.ps1" })
        {
            var result = ProcessRunner.RunPowerShell("pwsh",
                new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(RepositoryPaths.Root(), "tests", "tools", "lint", script), "-Folder", missing, "-Root", temp.Path },
                Timeout);
            Assert.True(result.ExitCode != 0, script + " が、無いフォルダで成功した: " + result.Output);
        }
    }

    [Fact]
    public void distとtests_toolsのps1に同じ名前の関数が2つ以上のファイルにない()
    {
        AssertNoFindings("SAMENAME", AllowedSameNames, each => each.Names,
            "同じ名前の関数が 2 つ以上のファイルにあります。1 つのファイルに置いて、ほかから読み込んでください");
    }

    [Fact]
    public void distとtests_toolsのps1に本体が同じ関数が2つ以上ない()
    {
        AssertNoFindings("SAMEBODY", AllowedSameBodies, KeyOf, "本体が同じ関数が 2 つ以上あります。1 つの関数にまとめてください");
    }

    /// <summary>
    /// 別の関数の中に定義してよい関数と、その理由(キーは関数名)。入れ子の関数は、その関数の中でしか呼べない。
    /// 関数を別の場所へ移したときの置き換えの誤りで、ほかの関数から呼ぶ関数が入れ子になると、呼ぶ側が「認識されないコマンド」で失敗する。
    /// </summary>
    private static readonly Dictionary<string, string> AllowedNestedFunctions = new();

    [Fact]
    public void distとtests_toolsのps1に別の関数の中に定義した関数がない()
    {
        AssertNoFindings("NESTED", AllowedNestedFunctions, each => each.Names,
            "別の関数の中に定義した関数があります。ほかの関数から呼ぶ関数は、スクリプトの直下に定義してください");
    }

    [Fact]
    public void ps1の入れ子の関数の検査は関数の中の関数の定義を見つけ直下の関数とスクリプトブロックの中の定義は除く()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("a.ps1"), "function Outer {\n  param($X)\n  function Inner { return 1 }\n  return Inner\n}\nfunction Top { return 2 }\nfunction Job { Start-Job -ScriptBlock { function InBlock { return 3 }; InBlock } }\n");
        var found = Find(folder.Path).Where(each => each.Kind == "NESTED").ToArray();
        Assert.Equal(new[] { "Inner" }, found.Select(each => each.Names).ToArray());
        Assert.Equal(new[] { "a.ps1:3" }, found.Single().Places);
    }

    // ---------------------------------------------------------------- まだ作っていない変数の [ref]

    /// <summary>
    /// <c>tests/tools/lint/find_ref_without_variable.ps1</c> の出力(「相対パス:行」と変数名)。PowerShell は存在しない変数の [ref] を作れず、
    /// 関数の呼び出しの前で止まる。共通の関数へまとめたときに、呼び出し側で変数を作り忘れて、試験のスクリプトが実行の最初で止まった。
    /// </summary>
    private static string[] RefsWithoutVariable(string? folder = null)
    {
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-File", Path.Combine(RepositoryPaths.Root(), "tests", "tools", "lint", "find_ref_without_variable.ps1") };
        if (folder != null) arguments.AddRange(new[] { "-Folder", folder, "-Root", folder });
        var result = ProcessRunner.RunPowerShell("pwsh", arguments, Timeout);
        Assert.True(result.ExitCode == 0, "find_ref_without_variable.ps1 が失敗しました: " + result.Error);
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void distとtests_toolsのps1にまだ作っていない変数をrefで渡すコマンドの呼び出しがない()
    {
        var found = RefsWithoutVariable();
        Assert.True(found.Length == 0, "まだ作っていない変数に [ref] を付けてコマンドへ渡しています。呼び出しの前で変数を作ってください:\n" + string.Join("\n", found));
    }

    [Fact]
    public void refの検査は作っていない変数を見つけ前に作った変数と引数とforeachの変数とメソッドの呼び出しは除く()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("a.ps1"),
            "function F([ref]$x) { $x.Value = 1 }\n"
            + "F ([ref]$missing)\n"
            + "$made = $null\n"
            + "F ([ref]$made)\n"
            + "function G($p) { F ([ref]$p); F ([ref]$inner) }\n"
            + "foreach ($each in 1..2) { F ([ref]$each) }\n"
            + "[int]::TryParse('1', [ref]$parsed) | Out-Null\n");
        Assert.Equal(new[] { "a.ps1:2\tmissing", "a.ps1:5\tinner" }, RefsWithoutVariable(folder.Path));
    }

    /// <summary>
    /// 同じ文の並びを、別々に持ってよいものと、その理由(キーは場所を「;」でつないだもの)。
    /// 理由を書けないものは許さず、関数にして 1 つのファイルに置き、ほかから読み込む。
    /// 場所は「ファイル:行」なので、行が動いたら(検査が不合格にする)キーを直す。
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSameSequences = new();

    [Fact]
    public void distとtests_toolsのps1に同じ文の並びが2か所以上にない()
    {
        AssertNoFindings("SAMESEQ", AllowedSameSequences, each => string.Join(";", each.Places),
            "同じ文の並びが 2 か所以上にあります。関数にして 1 つのファイルに置き、ほかから読み込んでください(数字は続く文の数)");
    }

    [Fact]
    public void ps1の文の並びの検査は続く同じ文を1組にまとめて見つけ変数名の違いと関数の本体全体の一致は除く()
    {
        using var folder = new TempFolder();
        // 2 文ずつの窓がどれもトークンの最小の数(30)を満たすよう、どの文にも共通の引数を足す。
        const string common = " -Verbose:$false -Debug:$false -InformationAction Ignore -WarningAction Ignore";
        const string sequence = "$a = Join-Path -Path $root -ChildPath 'out' -AdditionalChildPath @('one', 'two')" + common + "\n"
            + "New-Item -ItemType Directory -Force -Path $a -ErrorAction Stop" + common + " | Out-Null\n"
            + "$b = Get-ChildItem -LiteralPath $a -Filter '*.scr' -File -Recurse" + common + "\n"
            + "Set-Content -LiteralPath $b -Value @('one', 'two') -Encoding ASCII" + common + "\n";
        // 前後の文が違う 2 か所に、同じ 4 文が続く(コメントの違いは無視する)。
        File.WriteAllText(folder.File("f.ps1"), "$x = 1\n" + sequence);
        File.WriteAllText(folder.File("g.ps1"), "$y = 2\n" + sequence.Replace("\nSet-Content", "\n# 書く\nSet-Content") + "Write-Host 'done'\n");
        // 変数名が違えば別の並び。
        File.WriteAllText(folder.File("h.ps1"), sequence.Replace("$a", "$c"));
        // 関数の本体全体が同じものは、本体の検査(SAMEBODY)が出すので、並びとしては出さない。
        const string body = "param([int]$A) $total = 0; foreach ($i in 1..$A) { if ($i % 2 -eq 0) { $total += $i } else { $total -= 1 } }; return $total";
        File.WriteAllText(folder.File("i.ps1"), "function Sum-Even {\n  " + body + "\n}\n");
        File.WriteAllText(folder.File("j.ps1"), "function Sum-Other {\n  " + body + "\n}\n");

        var findings = Find(folder.Path);
        var run = Assert.Single(findings, each => each.Kind == "SAMESEQ");
        Assert.Equal("4", run.Names);
        Assert.Equal(new[] { "f.ps1:2", "g.ps1:2" }, run.Places);
        Assert.Single(findings, each => each.Kind == "SAMEBODY");
        Assert.Equal(2, findings.Length);
    }

    [Fact]
    public void ps1の文の並びの検査は最小の文の数の並びが1組だけでも2か所を1行で出す()
    {
        // 同じ並びの組が 1 つだけのとき、組(場所の一覧)がほどけて場所ごとの行にならないこと。2 文は検査の最小の文の数。
        using var folder = new TempFolder();
        const string pair = "$a = Join-Path -Path $root -ChildPath 'out' -AdditionalChildPath @('one', 'two', 'three', 'four')\n"
            + "Set-Content -LiteralPath $a -Value @('one', 'two', 'three') -Encoding ASCII -Force\n";
        File.WriteAllText(folder.File("f.ps1"), "$x = 1\n" + pair + "Write-Host 'f'\n");
        File.WriteAllText(folder.File("g.ps1"), "$y = 2\n" + pair + "Write-Host 'g'\n");

        var run = Assert.Single(Find(folder.Path));
        Assert.Equal("SAMESEQ", run.Kind);
        Assert.Equal("2", run.Names);
        Assert.Equal(new[] { "f.ps1:2", "g.ps1:2" }, run.Places);
    }

    [Fact]
    public void ps1の重複の検査は同じ名前と本体が同じ関数を見つけ別の名前で本体が違う関数とコメントだけの違いと短い関数と仮想環境は区別する()
    {
        using var folder = new TempFolder();
        const string body = "param([int]$A) $total = 0; foreach ($i in 1..$A) { if ($i % 2 -eq 0) { $total += $i } else { $total -= 1 } }; return $total";
        File.WriteAllText(folder.File("a.ps1"), "function Sum-Even {\n  " + body + "\n}\nfunction Same-Name { param($X) return $X + 1 }\n");
        // 別のファイルに、同じ名前の関数(本体は違う)と、名前が違うが本体が同じ関数(コメントと改行の違いは無視する)。
        File.WriteAllText(folder.File("b.ps1"), "function Same-Name { param($X) return $X * 2 }\nfunction Sum-Other {\n  # 偶数の和\n  " + body.Replace("; ", ";\n  # 続き\n  ") + "\n}\n");
        // 変数名が違えば別の本体。
        File.WriteAllText(folder.File("c.ps1"), "function Sum-Different {\n  " + body.Replace("$total", "$sum") + "\n}\n");
        // 短い関数は、本体が同じでも比べない。
        File.WriteAllText(folder.File("d.ps1"), "function Tiny-One { return 1 }\n");
        File.WriteAllText(folder.File("e.ps1"), "function Tiny-Two { return 1 }\n");
        // Python の仮想環境が生成するスクリプトは調べない。
        Directory.CreateDirectory(Path.Combine(folder.Path, ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(folder.Path, ".venv", "Scripts", "activate.ps1"), "function Same-Name { param($X) return $X - 1 }\nfunction Sum-Venv {\n  " + body + "\n}\n");

        var findings = Find(folder.Path);
        var same = Assert.Single(findings, each => each.Kind == "SAMENAME");
        Assert.Equal("Same-Name", same.Names);
        Assert.Equal(new[] { "a.ps1:4", "b.ps1:1" }, same.Places);
        var bodies = Assert.Single(findings, each => each.Kind == "SAMEBODY");
        Assert.Equal(new[] { "Sum-Even", "Sum-Other" }, bodies.Names.Split(',').OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, findings.Length);
    }
}
