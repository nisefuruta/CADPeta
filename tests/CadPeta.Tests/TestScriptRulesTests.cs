using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests;

// 試験のスクリプト（tests/tools の .ps1）の規則の検査。
// accoreconsole は図面ごとのログを起動時の作業ディレクトリへ書き、その場所をプロファイルの LogFilePath にも残す。
// 起動するときは、作業ディレクトリを試験の作業フォルダにする（リポジトリやシェルの場所に書かせない）。
public class TestScriptRulesTests
{
    // accoreconsole の起動の形（/i 図面 /s スクリプト）。Start-Process の配列の引数と、& での直接の起動の両方
    static readonly Regex AccoreLaunch = new(@"('/s'|""/s""|\s/s\s)", RegexOptions.Compiled);
    static readonly Regex SetsDirectory = new(@"-WorkingDirectory|Push-Location|Set-Location", RegexOptions.Compiled);

    internal static string[] FindLaunchesWithoutWorkingDirectory(string[] lines)
    {
        var found = new System.Collections.Generic.List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            bool isLaunch = AccoreLaunch.IsMatch(line) && (line.Contains("Start-Process") || Regex.IsMatch(line, @"&\s+[""$]"));
            if (!isLaunch) continue;
            bool ok = Enumerable.Range(Math.Max(0, i - 2), i - Math.Max(0, i - 2) + 1).Any(j => SetsDirectory.IsMatch(lines[j]));
            if (!ok) found.Add($"{i + 1}: {line.Trim()}");
        }
        return found.ToArray();
    }

    [Fact]
    public void accoreconsoleの起動は作業ディレクトリを試験の作業フォルダにする()
    {
        string tools = Path.Combine(RepositoryPaths.Root(), "tests", "tools");
        var violations = Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories)
            .SelectMany(f => FindLaunchesWithoutWorkingDirectory(File.ReadAllLines(f))
                .Select(v => f.Substring(tools.Length).TrimStart(Path.DirectorySeparatorChar) + ":" + v))
            .ToArray();
        Assert.True(violations.Length == 0, "作業ディレクトリを指定していない accoreconsole の起動:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("$p = Start-Process -FilePath $acad -ArgumentList @('/i', $dwg, '/s', $scr, '/l', 'ja-JP')")]
    [InlineData("& $acad /i $dwg /s $scr /l ja-JP 2>&1 | Out-File $stdout")]
    [InlineData("& \"$AcadRoot\\accoreconsole.exe\" /i $dwgCopy /s $scr /l ja-JP")]
    public void 作業ディレクトリを指定しない起動を見つける(string launch)
    {
        Assert.Single(FindLaunchesWithoutWorkingDirectory(new[] { "$x = 1", launch }));
        Assert.Empty(FindLaunchesWithoutWorkingDirectory(new[] { "Push-Location -LiteralPath $work", launch }));
    }

    // CAD に読み込ませる製品のプラグインの DLL（ビルドの出力）のパス
    static readonly Regex ProductPluginBuildPath = new(@"CadPeta\.Plugin\.(Acad|Ares|Ijcad|Bricscad)\\bin\\", RegexOptions.Compiled);

    // 製品のプラグインの DLL のパスを書いた .ps1 が、配布物と同じビルドの DLL かの確認（Assert-E2ePluginBuild）を
    // 呼ばない行を返す。確認しないと、開発機のパスが残る普段のビルドの DLL を試験してしまう
    internal static string[] FindUncheckedPluginPaths(string[] lines)
    {
        if (lines.Any(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal) && l.Contains("Assert-E2ePluginBuild")))
            return Array.Empty<string>();
        return lines.Select((line, i) => (line, i))
            .Where(x => !x.line.TrimStart().StartsWith("#", StringComparison.Ordinal) && ProductPluginBuildPath.IsMatch(x.line))
            .Select(x => $"{x.i + 1}: {x.line.Trim()}")
            .ToArray();
    }

    [Fact]
    public void 製品のプラグインのDLLは配布物と同じビルドかを確かめてから使う()
    {
        string tools = Path.Combine(RepositoryPaths.Root(), "tests", "tools");
        var violations = Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories)
            .SelectMany(f => FindUncheckedPluginPaths(File.ReadAllLines(f))
                .Select(v => f.Substring(tools.Length).TrimStart(Path.DirectorySeparatorChar) + ":" + v))
            .ToArray();
        Assert.True(violations.Length == 0,
            "配布物と同じビルドかを確かめずに使うプラグインの DLL（Get-E2ePluginPath か Assert-E2ePluginBuild を使う）:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void 確かめずに使うプラグインのDLLのパスを見つける()
    {
        string path = "$plugin = \"$repo\\src\\CadPeta.Plugin.Acad\\bin\\Debug\\$tfm\\CadPeta.Plugin.Acad.dll\"";
        Assert.Single(FindUncheckedPluginPaths(new[] { path }));
        Assert.Single(FindUncheckedPluginPaths(new[] { path, "# Assert-E2ePluginBuild $plugin" }));
        Assert.Empty(FindUncheckedPluginPaths(new[] { path, "Assert-E2ePluginBuild $plugin" }));
    }

    // SECURELOAD を書き換える行。LISP の setvar（PowerShell の文字列の中の `" を含む）、COM の SetVariable、
    // レジストリの書き込み、.scr でシステム変数の名前をコマンドとして入力する形（SETVAR の次の行を含む）
    static readonly Regex SecureLoadWrite = new(
        @"\(setvar\s+`?""SECURELOAD|SetVariable\(\s*['""]SECURELOAD|\b(Set|New)-ItemProperty\b.*\bSECURELOAD\b|^\s*(_?\.?SETVAR\s+)?_?\.?SECURELOAD(\s+\S+)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // # は PowerShell、; は .scr と LISP のコメント
    internal static string[] FindSecureLoadWrites(string[] lines) =>
        lines.Select((line, i) => (line, i))
            .Where(x => !x.line.TrimStart().StartsWith("#", StringComparison.Ordinal) && !x.line.TrimStart().StartsWith(";", StringComparison.Ordinal)
                && SecureLoadWrite.IsMatch(x.line))
            .Select(x => $"{x.i + 1}: {x.line.Trim()}")
            .ToArray();

    // SECURELOAD を書き換えても利用者の設定を変えないもの（tests/tools からの相対パスと理由）
    static readonly System.Collections.Generic.Dictionary<string, string> SecureLoadWriteAllowed = new()
    {
        ["regression/cad_reg_guard_selftest.ps1"] = "試験用のレジストリのキーに書き、CadRegGuard が戻すことを確かめる",
    };

    // SECURELOAD はプロファイルに保存され、利用者の AutoCAD の安全の設定になる。試験はこれを下げず、
    // 読み込む DLL のフォルダだけを TRUSTEDPATHS へ一時追加して NETLOAD する（Get-AccoreNetloadLines）
    [Fact]
    public void 試験はSECURELOADを変えない()
    {
        string tools = Path.Combine(RepositoryPaths.Root(), "tests", "tools");
        string lint = Path.Combine(tools, "lint") + Path.DirectorySeparatorChar;
        // 手で実行する再現の .scr も対象にする
        var written = Directory.EnumerateFiles(tools, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.StartsWith(lint, StringComparison.OrdinalIgnoreCase))
            .Select(f => (file: f.Substring(tools.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/'), lines: FindSecureLoadWrites(File.ReadAllLines(f))))
            .Where(x => x.lines.Length > 0)
            .ToArray();
        var violations = written.Where(x => !SecureLoadWriteAllowed.ContainsKey(x.file))
            .SelectMany(x => x.lines.Select(v => x.file + ":" + v)).ToArray();
        Assert.True(violations.Length == 0, "SECURELOAD を書き換える試験（Get-AccoreNetloadLines を使う）:\n" + string.Join("\n", violations));
        // 直したファイルを一覧に残さない
        var stale = SecureLoadWriteAllowed.Keys.Where(k => !written.Any(x => x.file == k)).ToArray();
        Assert.True(stale.Length == 0, "SECURELOAD を書き換えなくなったので、除外の一覧から削除する:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void SECURELOADを書き換える行を見つける()
    {
        Assert.Single(FindSecureLoadWrites(new[] { "(setvar \"SECURELOAD\" 0)" }));
        Assert.Single(FindSecureLoadWrites(new[] { "    '(SETVAR \"SECURELOAD\" 0)'," }));
        // PowerShell の二重引用符の文字列の中の LISP、COM、レジストリ、.scr のコマンドの形
        Assert.Single(FindSecureLoadWrites(new[] { "$scr = \"(setvar `\"SECURELOAD`\" 0)\"" }));
        Assert.Single(FindSecureLoadWrites(new[] { "$document.SetVariable(\"SECURELOAD\", [int16]0)" }));
        Assert.Single(FindSecureLoadWrites(new[] { "Set-ItemProperty $key -Name SECURELOAD -Value 1" }));
        Assert.Single(FindSecureLoadWrites(new[] { "_.SETVAR", "SECURELOAD", "0" }));
        Assert.Single(FindSecureLoadWrites(new[] { "SECURELOAD 0" }));
        Assert.Empty(FindSecureLoadWrites(new[] { "(princ (getvar \"SECURELOAD\"))", "# (setvar \"SECURELOAD\" 0)", "; (setvar \"SECURELOAD\" 0)",
            "$old = $document.GetVariable(\"SECURELOAD\")", "Write-Output \"SECURELOAD=$sl\"" }));
    }

#if NET10_0_OR_GREATER
    // まとめた起動の標準出力から 1 ケースの部分を切り出す（tests/tools/common/accore_batch.ps1 の Get-AccoreBatchCaseOutput）。
    // pwsh を起動する ProcessRunner は net10 の試験だけに入っている
    static string CaseOutput(string all, string id)
    {
        var result = ProcessRunner.RunPowerShell("pwsh",
            new[] { "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference = 'Stop'; . $env:CP_BATCH; [Console]::Out.Write((Get-AccoreBatchCaseOutput $env:CP_ALL $env:CP_ID))" },
            TimeSpan.FromSeconds(60),
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["CP_BATCH"] = Path.Combine(RepositoryPaths.Root(), "tests", "tools", "common", "accore_batch.ps1"),
                ["CP_ALL"] = all,
                ["CP_ID"] = id,
            });
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        return result.Output;
    }

    // Start-Process の -ArgumentList 用の文字列（tests/tools/common/test_helpers.ps1 の ConvertTo-NativeArgString）は、
    // 子プロセスで元の引数のとおりに分けられる。空白を含むパス、末尾の \、" と \" を含む引数で確かめる
    [Fact]
    public void 子プロセスへ渡す引数は空白や引用符を含んでも元のとおりに届く()
    {
        string[] expected = { "/i", @"C:\Program Files\a b.dwt", @"C:\dir with space\", "say \"hi\"", @"back\""slash", "" , "plain" };
        using var temp = new TempFolder();
        string script = temp.File("argv.ps1");
        File.WriteAllText(script, "$args | ForEach-Object { '[' + $_ + ']' }\r\n", new System.Text.UTF8Encoding(true));
        string argsFile = temp.File("args.txt");
        File.WriteAllLines(argsFile, expected, new System.Text.UTF8Encoding(false));
        var result = ProcessRunner.RunPowerShell("pwsh",
            new[] { "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference = 'Stop'; . $env:CP_HELPERS; " +
                "$a = @(Get-Content -LiteralPath $env:CP_ARGS -Encoding utf8); " +
                "$p = Start-Process -FilePath pwsh -ArgumentList ('-NoProfile -File ' + (ConvertTo-NativeArgString (@($env:CP_SCRIPT) + $a))) -Wait -NoNewWindow -PassThru -RedirectStandardOutput $env:CP_OUT; " +
                "[Console]::Out.Write([IO.File]::ReadAllText($env:CP_OUT))" },
            TimeSpan.FromSeconds(60),
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["CP_HELPERS"] = Path.Combine(RepositoryPaths.Root(), "tests", "tools", "common", "test_helpers.ps1"),
                ["CP_ARGS"] = argsFile,
                ["CP_SCRIPT"] = script,
                ["CP_OUT"] = temp.File("out.txt"),
            });
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        var received = result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected.Select(e => "[" + e + "]").ToArray(), received);
    }

    // ID が別のケースの ID の頭と同じでも、そのケースの完了の目印を自分の完了と読まない
    [Fact]
    public void ケースの完了の目印はIDが完全に一致するものだけを読む()
    {
        const string setup = "CADPETA_CASE_SETUP=PETA-Model entities=1\n";
        // PETA-Model が途中で止まり、後の PETA-Model-fallback だけが完了した
        string stalled = setup + "a\nCADPETA_CASE_END=PETA-Model-fallback\nb";
        Assert.Equal(stalled, CaseOutput(stalled, "PETA-Model"));
        // accoreconsole は目印の後に改行せずに次のプロンプトを続けることがある
        Assert.Equal(setup + "a\nCADPETA_CASE_END=PETA-Modelコマンド:\n", CaseOutput(setup + "a\nCADPETA_CASE_END=PETA-Modelコマンド:\nb", "PETA-Model"));
    }

    // .scr を 1 つ置いたフォルダに tests/tools/lint/trusted_paths_lint.ps1 を実行し、終了コードを返す。
    // lint は除外の一覧のファイルが無いことも違反にするので、その名前の空のファイルを置く
    static int TrustedPathsLintExitCode(string scrLines)
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "regression"));
        File.WriteAllText(Path.Combine(folder.Path, "regression", "cad_reg_guard_selftest.ps1"), "");
        File.WriteAllText(Path.Combine(folder.Path, "case.scr"), scrLines.Replace("\n", "\r\n") + "\r\n");
        var lint = Path.Combine(RepositoryPaths.Root(), "tests", "tools", "lint", "trusted_paths_lint.ps1");
        return ProcessRunner.RunPowerShell("pwsh", new[] { "-NoProfile", "-NonInteractive", "-File", lint, "-Root", folder.Path }, TimeSpan.FromSeconds(60)).ExitCode;
    }

    // .scr が DLL や LISP を読み込む行は、信頼済みのフォルダのパスを文字どおりに書いた形でなければ違反にする
    // （lint が読み込み先を読めない形は、信頼済みのフォルダの外を読み込んでいても見逃すので、読めない形そのものを違反にする）
    [Theory]
    [InlineData("_.NETLOAD\nC:/work/CadPeta.Plugin.Acad.dll")]
    [InlineData("NETLOAD\n\"C:/Tools/ACAD/dotnet/CadPeta.Plugin.Acad.dll\"")]
    [InlineData("(load (strcat (getenv \"TEMP\") \"/probe.lsp\"))")]
    [InlineData("(command \"_.APPLOAD\" \"C:/work/probe.lsp\")")]
    [InlineData("(arxload \"C:/work/probe.arx\")")]
    [InlineData("(command \"_.NETLOAD\" \"C:/work/CadPeta.Plugin.Acad.dll\")")]
    public void 読み込み先を確かめられない読み込みの行を見つける(string scr) =>
        Assert.NotEqual(0, TrustedPathsLintExitCode(scr));

    [Theory]
    [InlineData("(command \"_.NETLOAD\" \"C:/Tools/ACAD/dotnet/CadPeta.Plugin.Acad.dll\")")]
    [InlineData("(load \"C:/Tools/ACAD/lisp/probe_cmd.lsp\")")]
    [InlineData("(command \"_.NETLOAD\" \"C:/Tools/ACAD/dotnet/CadPeta.Plugin.Acad.dll\" )")]
    [InlineData("(load \"C:/Tools/ACAD/lisp/probe_cmd.lsp\" )")]
    [InlineData("(setq parx 1)")]
    public void 信頼済みのフォルダを文字どおりに読み込む行は違反にしない(string scr) =>
        Assert.Equal(0, TrustedPathsLintExitCode(scr));
#endif

    // Start-Process の -ArgumentList に配列やカンマ区切りの一覧を直接渡す行。Start-Process は要素を空白でつなぐだけで
    // 引用符で囲まないので、"C:\Program Files\..." のような空白を含むパスが子プロセスで分かれる（7.6・5.1 で実測。
    // accoreconsole はテンプレートを見つけられず終了コード 76 で終わった）。ConvertTo-NativeArgString で 1 本の文字列にする
    static readonly Regex ArgumentListArray = new(@"Start-Process\b.*-ArgumentList\s+(@\(|['""][^'""]*['""]\s*,|\$\w+\s*,)", RegexOptions.Compiled);

    internal static string[] FindArgumentListArrays(string[] lines)
    {
        var found = new System.Collections.Generic.List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            // 行の継続（`）でつながった次の行までを 1 つの呼び出しとして読む
            string call = lines[i];
            for (int j = i; j + 1 < lines.Length && lines[j].TrimEnd().EndsWith("`", StringComparison.Ordinal); j++) call += " " + lines[j + 1].Trim();
            if (ArgumentListArray.IsMatch(call)) found.Add($"{i + 1}: {lines[i].Trim()}");
        }
        return found.ToArray();
    }

    [Fact]
    public void Start_Processの引数は引用符で囲んだ1本の文字列で渡す()
    {
        string root = RepositoryPaths.Root();
        var violations = new[] { Path.Combine(root, "tests", "tools"), Path.Combine(root, "dist") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.ps1", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "staging" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar))
            .SelectMany(f => FindArgumentListArrays(File.ReadAllLines(f)).Select(v => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar) + ":" + v))
            .ToArray();
        Assert.True(violations.Length == 0, "Start-Process の -ArgumentList に配列を渡している（ConvertTo-NativeArgString を使う）:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ArgumentListに配列を渡す行を見つける()
    {
        Assert.Single(FindArgumentListArrays(new[] { "$p = Start-Process -FilePath $acc -ArgumentList @('/i', $dwg) -PassThru" }));
        Assert.Single(FindArgumentListArrays(new[] { "$p = Start-Process -FilePath $acc `", "    -ArgumentList @('/i', $dwg) -PassThru" }));
        Assert.Single(FindArgumentListArrays(new[] { "Start-Process pwsh -ArgumentList '-NoProfile', '-File', $script" }));
        Assert.Single(FindArgumentListArrays(new[] { "Start-Process $exe -ArgumentList $a, $b" }));
        Assert.Empty(FindArgumentListArrays(new[] { "Start-Process $exe -ArgumentList (ConvertTo-NativeArgString @('/i', $dwg))", "# Start-Process -ArgumentList @('x')" }));
    }

    // $runner = Join-Path $PSScriptRoot 'x.ps1' の形で別のスクリプトのパスを作る箇所
    static readonly Regex RunnerAssignment = new(@"\$(\w+)\s*=\s*Join-Path\s+\$PSScriptRoot\s+['""]([^'""]+\.ps1)['""]", RegexOptions.Compiled);
    // -File (Join-Path $PSScriptRoot 'x.ps1') の形で直接呼ぶ箇所
    static readonly Regex DirectFileCall = new(@"-File\s+\(Join-Path\s+\$PSScriptRoot\s+['""]([^'""]+\.ps1)['""]\)([^\r\n]*)", RegexOptions.Compiled);
    // 引数の名前（-1 のような負の数の値は名前ではない）
    static readonly Regex ArgumentName = new(@"(?<=\s)-([A-Za-z]\w*)", RegexOptions.Compiled);

    // param( ... ) に宣言した引数の名前（大文字小文字を区別しない）
    internal static System.Collections.Generic.HashSet<string> ParameterNames(string script)
    {
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = Regex.Match(script, @"(?im)^\s*param\s*\(");
        if (!start.Success) return names;
        int depth = 0, i = start.Index + start.Length - 1;
        for (; i < script.Length; i++)
        {
            if (script[i] == '(') depth++;
            else if (script[i] == ')' && --depth == 0) break;
        }
        // 説明のコメント（# 以降）を除いてから読む
        string block = Regex.Replace(script.Substring(start.Index, i - start.Index), @"#[^\r\n]*", "");
        foreach (Match m in Regex.Matches(block, @"\$(\w+)\s*(?:=|,|\)|$)", RegexOptions.Multiline))
            names.Add(m.Groups[1].Value);
        // [CmdletBinding()] のスクリプトは PowerShell の共通の引数を受け付け、SupportsShouldProcess があれば -WhatIf と -Confirm も受け付ける
        string head = script.Substring(0, start.Index);
        if (Regex.IsMatch(head, @"\[CmdletBinding\b", RegexOptions.IgnoreCase))
        {
            foreach (var common in new[] { "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ProgressAction",
                         "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable" })
                names.Add(common);
            if (Regex.IsMatch(head, @"SupportsShouldProcess\s*=\s*\$true", RegexOptions.IgnoreCase)) { names.Add("WhatIf"); names.Add("Confirm"); }
        }
        return names;
    }

    // 呼び出し元が渡す名前付きの引数のうち、呼ばれるスクリプトに無いもの（行の継続の ` を含めて読む）。
    // & $変数、-File $変数、-File (Join-Path $PSScriptRoot 'x.ps1') の呼び出しを読む。splatting（@args）の中身は読まない
    internal static string[] FindUnknownRunnerArguments(string caller, Func<string, string?> readScript)
    {
        var found = new System.Collections.Generic.List<string>();
        string joined = Regex.Replace(caller, @"`\r?\n\s*", " ");
        void Check(string name, string arguments)
        {
            string? target = readScript(name);
            if (target == null) { found.Add($"{name}: 呼ばれるスクリプトが無い"); return; }
            var names = ParameterNames(target);
            foreach (Match arg in ArgumentName.Matches(arguments))
                if (!names.Contains(arg.Groups[1].Value)) found.Add($"{name}: -{arg.Groups[1].Value}");
        }
        foreach (Match assign in RunnerAssignment.Matches(caller))
            foreach (Match call in Regex.Matches(joined, @"(?:&|-File)\s*\$" + Regex.Escape(assign.Groups[1].Value) + @"\b([^\r\n]*)"))
                Check(assign.Groups[2].Value, call.Groups[1].Value);
        foreach (Match call in DirectFileCall.Matches(joined))
            Check(call.Groups[1].Value, call.Groups[2].Value);
        return found.ToArray();
    }

    [Fact]
    public void 別のスクリプトへ渡す引数はそのスクリプトにある()
    {
        string tools = Path.Combine(RepositoryPaths.Root(), "tests", "tools");
        var violations = Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories)
            .SelectMany(f => FindUnknownRunnerArguments(File.ReadAllText(f), name =>
                {
                    string path = Path.Combine(Path.GetDirectoryName(f)!, name);
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                })
                .Select(v => f.Substring(tools.Length).TrimStart(Path.DirectorySeparatorChar) + " → " + v))
            .ToArray();
        Assert.True(violations.Length == 0, "呼ばれるスクリプトに無い引数を渡している:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void 呼ばれるスクリプトに無い引数を見つける()
    {
        string target = "param(\n    [string]$Ver = '2026',\n    [int]$TimeoutSeconds = 120,\n    [switch]$Standard\n)\n";
        string caller = "$runner = Join-Path $PSScriptRoot 'run.ps1'\n& $runner -Ver $Ver -Config $Config `\n    -Standard -Bogus\n";
        var found = FindUnknownRunnerArguments(caller, _ => target);
        Assert.Equal(new[] { "run.ps1: -Config", "run.ps1: -Bogus" }, found);
        Assert.Empty(FindUnknownRunnerArguments("$runner = Join-Path $PSScriptRoot 'run.ps1'\n& $runner -Ver 1 -TimeoutSeconds 2 -Standard\n", _ => target));
        // 二重引用符のパス、pwsh -File での呼び出し
        Assert.Equal(new[] { "run.ps1: -Config" }, FindUnknownRunnerArguments("$run = Join-Path $PSScriptRoot \"run.ps1\"\n& $run -Ver 1 -Config x\n", _ => target));
        Assert.Equal(new[] { "run.ps1: -Config" }, FindUnknownRunnerArguments("$run = Join-Path $PSScriptRoot \"run.ps1\"\n& pwsh -NoProfile -File $run -Ver 1 -Config x\n", _ => target));
        Assert.Equal(new[] { "run.ps1: -Config" }, FindUnknownRunnerArguments("pwsh -NoProfile -File (Join-Path $PSScriptRoot \"run.ps1\") -Config x\n", _ => target));
        // 負の数の値は引数の名前ではない
        Assert.Empty(FindUnknownRunnerArguments("$runner = Join-Path $PSScriptRoot 'run.ps1'\n& $runner -TimeoutSeconds -1\n", _ => target));
        // 説明のコメントの付いた最後の引数も引数の名前として読む
        Assert.Empty(FindUnknownRunnerArguments("$runner = Join-Path $PSScriptRoot 'run.ps1'\n& $runner -Last\n", _ => "param(\n    [switch]$Last # 説明\n)\n"));
    }
}