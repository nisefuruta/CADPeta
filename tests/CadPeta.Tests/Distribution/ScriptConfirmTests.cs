using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests.Distribution;

/// <summary>
/// 開発用の PC の環境(AutoCAD のレジストリ・プロファイル・.aws、信頼済みパス、互換 CAD の登録、署名)を変えるスクリプトは、
/// PowerShell の標準の -WhatIf と -Confirm で実行を確かめる。スクリプトごとに、次の 3 つを確かめる。
/// <list type="number">
/// <item>-NonInteractive で -Confirm:$false なしに実行すると、終了コードが 0 以外になり、何も変わらない(確認を出せないので止まる)。</item>
/// <item>-WhatIf で実行すると、終了コード 0 になり、何も残らない。</item>
/// <item>-Confirm:$false を付けると、確認の後まで進む。確認の後の処理が試験のフォルダの中で終わるスクリプト(<see cref="ConfirmedScripts"/>)だけで、
/// 確認の後の処理が出す行で確かめる。AutoCAD・互換 CAD を起動するスクリプトは、確認の後に実際の信頼済みのフォルダへコピーして CAD を起動するので
/// ここでは実行せず、構文木の検査(<see cref="対象のスクリプトはShouldProcessの指定とStopを持ち確認は本体の1か所"/>)と、
/// -Confirm:$false で実行する実機の試験(tests/tools/README.md)で確かめる。</item>
/// </list>
/// 子プロセスの APPDATA・LOCALAPPDATA・TEMP は試験のフォルダへ向け、そのフォルダの全ファイルの内容が前後で同じことを確かめる。
/// </summary>
public sealed class ScriptConfirmTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// -Confirm:$false で確認の後まで進めるスクリプトと、確認の後の処理だけが出す行(正規表現)。戻すだけのスクリプトは、試験のフォルダに置いた
    /// 戻すものの無い記録とバックアップで最後まで進む。sign_dist は、ProgramFiles(x86) を試験のフォルダへ向けているので、
    /// 確認の後に signtool を探すところで止まる。cad_reg_restore は、ほかのセッションの AutoCAD が動いていると戻さずに失敗にするので、
    /// どちらの場合も戻す処理の後に出す「戻した内容」の行で確かめる。compat_autoload_restore も、ARES が動いていると戻さずにバックアップを残すので、
    /// 戻したときの行と戻さなかったときの理由のどちらかで確かめる(単体試験の結果が、ほかのセッションの試験の有無で変わらないように)。
    /// </summary>
    public static readonly TheoryData<string, string, string, string> ConfirmedScripts = new()
    {
        { "tests/tools/regression/cad_reg_restore.ps1", "pwsh", "", "戻した内容: " },
        { "tests/tools/common/compat_autoload_restore.ps1", "powershell", "-Product Ares2020", "自動読み込みの登録を戻しました|戻さずにバックアップを残した" },
        { "dist/sign_dist.ps1", "pwsh", "-Only cli -Thumbprint 0000000000000000000000000000000000000000 -NoTimestamp", "signtool.exe が見つからない" },
    };

    /// <summary>PowerShell で CADPETA_TEST_ で始まる環境変数を読む形($env:、Env: のドライブ、GetEnvironmentVariable)。</summary>
    private static readonly Regex ReadsTestEnvironmentVariable =
        new(@"(\$env:|Env:\\?|GetEnvironmentVariable\(\s*['""])CADPETA_TEST_", RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("if ($env:CADPETA_TEST_STOP_AFTER_CONFIRM -eq '1') { exit 0 }")]
    [InlineData("Get-Item Env:CADPETA_TEST_X")]
    [InlineData("[Environment]::GetEnvironmentVariable('CADPETA_TEST_X')")]
    public void 試験のための環境変数を読む形を見つける(string line) => Assert.Matches(ReadsTestEnvironmentVariable, line);

    [Fact]
    public void 環境変数の名前の先頭を文字列で持つだけの行は見つけない() =>
        Assert.DoesNotMatch(ReadsTestEnvironmentVariable, "$FailureInjectionPrefix = 'CADPETA_TEST_'");

    /// <summary>試験のフォルダの中のパスに置き換える印(引数の中の {box})。</summary>
    private const string BoxToken = "{box}";

    /// <summary>
    /// 対象のスクリプト。Windows PowerShell 5.1 で動くもの(互換 CAD の COM の試験)は "powershell"。引数は必須の引数と、出力先を試験のフォルダへ向けるもの。
    /// 新しく環境を変えるスクリプトを足したら、ここにも足す(<see cref="対象の一覧は環境を変えるスクリプトをすべて含む"/> が確かめる)。
    /// </summary>
    public static readonly TheoryData<string, string, string> Scripts = new()
    {
        { "tests/tools/cad-typography/justify_check/run_all.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/cad-typography/justify_check/run_data_export.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/cad-typography/glyph_render_check/run_compare.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/cad-typography/linetype_symbol_check/run_peta_extract.ps1", "pwsh", "-Dwg {box}\\in.dwg -OutDir {box}\\out" },
        { "tests/tools/cad-typography/obj_shx_toggle.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/cad-typography/obj_smoke_repo.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/cad-typography/obj_text_outline_toggle.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/cad-typography/shx_missing_font_case.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/data-export/mtext_test2.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/data-export/obj_cases.ps1", "pwsh", "-Case basic -OutDir {box}\\out" },
        { "tests/tools/data-export/obj_partial.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/data-export/obj_smoke.ps1", "pwsh", "-OutDir {box}\\out" },
        { "tests/tools/distribution/bundle_sign_verify.ps1", "pwsh", "-BundleContents {box}\\Contents -Label unsigned -OutRoot {box}\\out" },
        { "tests/tools/pdf-emf/gradient_compat_e2e.ps1", "powershell", "-Product Ares2020 -SourceDwg {box}\\in.dwg -OutRoot {box}\\out" },
        { "tests/tools/pdf-emf/gradient_e2e_autocad.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/pdf-emf/gradient_interactive_plot.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/clip_verify.ps1", "pwsh", "" },
        { "tests/tools/regression/crop_regression.ps1", "pwsh", "-Ver 2026 -OutRoot {box}\\out" },
        { "tests/tools/regression/object_keyword_probe.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/office_doc_verify.ps1", "pwsh", "" },
        { "tests/tools/regression/petadata_size_measure.ps1", "pwsh", "-Ver 2026 -OutRoot {box}\\out" },
        { "tests/tools/regression/petaopt_gui_check.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/peta_option_g8_verify.ps1", "pwsh", "-CampaignDir {box}\\campaign -RoiConfig {box}\\roi.json -OutDir {box}\\out" },
        { "tests/tools/regression/peta_option_matrix.ps1", "pwsh", "-Set Smoke -OutRoot {box}\\out" },
        { "tests/tools/regression/peta_option_smoke_compat.ps1", "powershell", "-Product Ares2020 -OutRoot {box}\\out" },
        { "tests/tools/regression/peta_window_rotation_compat.ps1", "powershell", "-Product Ares2020 -OutRoot {box}\\out" },
        { "tests/tools/regression/plot_transparency_matrix.ps1", "pwsh", "" },
        { "tests/tools/regression/plugin_regression_matrix.ps1", "pwsh", "-Ver 2026 -OutRoot {box}\\out" },
        { "tests/tools/regression/selection_plot_matrix.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/page_setup_pairwise_matrix.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/space_pagesetup_matrix.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/window_contract/window_contract_compat.ps1", "powershell", "-Product Ares2024 -OutRoot {box}\\out" },
        { "tests/tools/regression/window_contract/window_contract_matrix.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/window_contract/window_contract_oracle.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/window_fit_scale_matrix.ps1", "pwsh", "-OutRoot {box}\\out" },
        { "tests/tools/regression/window_ucs_regression.ps1", "pwsh", "-Ver 2026 -OutRoot {box}\\out" },
        { "tests/tools/regression/cad_reg_restore.ps1", "pwsh", "" },
        { "tests/tools/common/compat_autoload_restore.ps1", "powershell", "-Product Ares2020" },
        { "dist/sign_dist.ps1", "pwsh", "-Only cli -Thumbprint 0000000000000000000000000000000000000000 -NoTimestamp" },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void 確認なしの非対話の実行は失敗して何も変えずWhatIfは何も残さない(string script, string shell, string arguments)
    {
        using var box = new ConfirmSandbox(script);
        var args = box.Arguments(arguments);

        var before = box.Snapshot();
        var plain = box.Run(shell, args);
        Assert.True(plain.ExitCode != 0, script + " は確認なしの非対話の実行で終了コード 0 を返した:\n" + plain.Text);
        Assert.Equal(before, box.Snapshot());

        var whatIf = box.Run(shell, args.Append("-WhatIf"));
        Assert.True(whatIf.ExitCode == 0, script + " -WhatIf の終了コード " + whatIf.ExitCode + ":\n" + whatIf.Text);
        Assert.Equal(before, box.Snapshot());
    }

    [Theory]
    [MemberData(nameof(ConfirmedScripts))]
    public void ConfirmFalseで確認の後の処理へ進む(string script, string shell, string arguments, string afterConfirm)
    {
        using var box = new ConfirmSandbox(script);

        var confirmed = box.Run(shell, box.Arguments(arguments).Append("-Confirm:$false"));

        Assert.True(Regex.IsMatch(confirmed.Text, afterConfirm),
            script + " -Confirm:$false が確認の後の処理へ進まなかった(終了コード " + confirmed.ExitCode + "):\n" + confirmed.Text);
    }

    /// <summary>
    /// 配布物を作る・署名するスクリプト(dist の .ps1)は、試験のための環境変数(CADPETA_TEST_ で始まるもの)を読まない。
    /// 利用者のシェルにその変数が残っていると、署名をせずに成功の終了コードで終わるといった、試験のための動きになる。
    /// </summary>
    [Fact]
    public void 配布のスクリプトは試験のための環境変数を読まない()
    {
        var dist = Path.Combine(RepositoryPaths.Root(), "dist");
        var found = Directory.GetFiles(dist, "*.ps1", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "staging" + Path.DirectorySeparatorChar)
                           && !path.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar))
            // 環境変数を読む形だけを探す(dll_path_check.ps1 は、配布物の DLL に注入が残っていないかを探すために、名前の先頭を文字列で持つ)
            .Where(path => ReadsTestEnvironmentVariable.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryPaths.Root(), path))
            .ToArray();
        Assert.True(found.Length == 0, "試験のための環境変数を読む配布のスクリプト:\n" + string.Join("\n", found));
    }

    /// <summary>
    /// 先頭が [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')] と param で、最初の ShouldProcess より前に
    /// $ErrorActionPreference = 'Stop' がある(確認の画面を出せない実行で、ShouldProcess の例外がその文だけを止めて変更を続けないように)。
    /// ShouldProcess はスクリプトの本体から 1 回だけ呼ぶ。
    /// </summary>
    [Fact]
    public void 対象のスクリプトはShouldProcessの指定とStopを持ち確認は本体の1か所()
    {
        var paths = Scripts.Select(row => Path.Combine(RepositoryPaths.Root(), ((string)row[0]).Replace('/', Path.DirectorySeparatorChar))).ToArray();
        var lint = Path.Combine(RepositoryPaths.Root(), "tests", "tools", "lint", "should_process_lint.ps1");
        var result = ProcessRunner.RunPowerShell("pwsh", new[] { "-NoProfile", "-NonInteractive", "-File", lint }.Concat(paths), Timeout);
        Assert.True(result.ExitCode == 0, "should_process_lint の終了コード " + result.ExitCode + "\n" + result.Output + result.Error);
    }

    /// <summary>
    /// AutoCAD のプロファイルを記録する関数、互換 CAD の登録を外す関数、記録から戻す関数を呼ぶスクリプトは、すべて対象の一覧にある
    /// (一覧から漏れたスクリプトは、確認なしで環境を変えても試験で見つからない)。
    /// </summary>
    [Fact]
    public void 対象の一覧は環境を変えるスクリプトをすべて含む()
    {
        var root = RepositoryPaths.Root();
        var listed = new HashSet<string>(Scripts.Select(row => ((string)row[0]).Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
        // 共通の関数を定義するファイルと、その自己試験(一時的なキーとフォルダだけを使う)は除く
        var libraries = new[] { "CadRegGuard.ps1", "compat_cad_com.ps1", "accore_run.ps1", "accore_batch.ps1", "test_helpers.ps1" };
        var changers = new[] { "Get-CadRegSnapshot", "Invoke-AcadConsoleGuarded", "Invoke-AccoreConsoleGuardedWithTimeout", "Invoke-AccoreBatch",
            "Suspend-CompatProductAutoload", "Restore-CadRegRecord", "Restore-CompatAutoloadBackup", "Restore-CompatProfileBackup", "accoreconsole.exe" };
        var missing = Directory.GetFiles(Path.Combine(root, "tests", "tools"), "*.ps1", SearchOption.AllDirectories)
            .Where(path => !libraries.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                           && !Path.GetFileName(path).EndsWith("_selftest.ps1", StringComparison.OrdinalIgnoreCase)
                           // lint はファイルを読むだけ(検査する関数の名前を文字列で持つ)
                           && !Path.GetRelativePath(root, path).StartsWith(Path.Combine("tests", "tools", "lint"), StringComparison.OrdinalIgnoreCase))
            .Where(path => changers.Any(name => File.ReadAllText(path).Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(root, path))
            .Where(path => !listed.Contains(path))
            .ToArray();
        Assert.True(missing.Length == 0, "環境を変えるのに確認の試験の一覧に無いスクリプト:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// 1 つのスクリプトの試験のフォルダ。子プロセスの APPDATA・LOCALAPPDATA・TEMP をこの中へ向ける。
    /// 戻すだけのスクリプトと sign_dist には、確認まで進むための記録・バックアップ・staging を用意する。
    /// </summary>
    private sealed class ConfirmSandbox : IDisposable
    {
        private readonly TempFolder folder = new();
        private readonly string scriptPath;

        public string Path => folder.Path;

        public ConfirmSandbox(string script)
        {
            foreach (var name in new[] { "AppData", "LocalAppData", "Temp", "ProgramFilesX86", "out" }) Directory.CreateDirectory(System.IO.Path.Combine(Path, name));
            scriptPath = System.IO.Path.Combine(RepositoryPaths.Root(), script.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (script.EndsWith("cad_reg_restore.ps1", StringComparison.Ordinal))
            {
                // 存在しないキーだけを監視する記録(戻しても何も変わらない)。書式は CadRegGuard の Write-CadRegRecord
                var record = System.IO.Path.Combine(Path, "LocalAppData", "CadPeta-e2e", "reg-backup", "autocad.reg.txt");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(record)!);
                var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(@"HKCU:\Software\CADPeta\Tests\ScriptConfirm_" + Guid.NewGuid().ToString("N")));
                File.WriteAllText(record, "R\t" + key + "\nEND\n", new UTF8Encoding(false));
            }
            else if (script.EndsWith("compat_autoload_restore.ps1", StringComparison.Ordinal))
            {
                // 中身の無い profile.xml のバックアップ(戻すものが無い)
                var backup = System.IO.Path.Combine(Path, "LocalAppData", "CadPeta-e2e", "autoload-backup", "Ares2020.profile");
                Directory.CreateDirectory(backup);
                File.WriteAllText(System.IO.Path.Combine(backup, "paths.txt"), "", new UTF8Encoding(false));
            }
            else if (script == "dist/sign_dist.ps1")
            {
                // sign_dist は自分のフォルダの staging を署名するので、スクリプトを試験のフォルダへ複製し、作ったときの記録のある staging を置く
                var dist = System.IO.Path.Combine(Path, "dist");
                Directory.CreateDirectory(dist);
                foreach (var name in new[] { "sign_dist.ps1", "dist_docs.ps1", "sign_record.ps1", "script_confirm.ps1", "staging_files.ps1" })
                    File.Copy(System.IO.Path.Combine(RepositoryPaths.Root(), "dist", name), System.IO.Path.Combine(dist, name));
                var pkg = System.IO.Path.Combine(dist, "staging", "cli", "pkg");
                Directory.CreateDirectory(pkg);
                File.WriteAllText(System.IO.Path.Combine(pkg, "cadpeta.exe"), "unsigned");
                // sign_dist は zip の名前の版を build_dist が書いた manifest.txt から読む
                Directory.CreateDirectory(System.IO.Path.Combine(dist, "artifacts"));
                File.WriteAllText(System.IO.Path.Combine(dist, "artifacts", "manifest.txt"), "HEAD: 0000000\r\nConfig: Release\r\nVersion: 1.0.1\r\n");
                var write = ProcessRunner.RunPowerShell("pwsh",
                    new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; . $env:CP_SIGN_RECORD; Write-SignRecord $env:CP_STAGING @('cli')" },
                    Timeout,
                    new Dictionary<string, string>
                    {
                        ["CP_SIGN_RECORD"] = System.IO.Path.Combine(dist, "sign_record.ps1"),
                        ["CP_STAGING"] = System.IO.Path.Combine(dist, "staging"),
                    });
                Assert.True(write.ExitCode == 0, "Write-SignRecord の終了コード " + write.ExitCode + "\n" + write.Output + write.Error);
                scriptPath = System.IO.Path.Combine(dist, "sign_dist.ps1");
            }
        }

        /// <summary>試験のフォルダのファイルとフォルダの一覧(名前と SHA256。<see cref="FolderSnapshot"/>)。</summary>
        public string[] Snapshot() => FolderSnapshot.Of(Path);

        /// <summary>
        /// 引数の文字列を空白で分けてから、{box} を試験のフォルダのパスに置き換える(TEMP のパスに空白があっても 1 つの引数のまま渡す)。
        /// </summary>
        public string[] Arguments(string arguments) =>
            arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Replace(BoxToken, Path)).ToArray();

        public sealed record Outcome(int ExitCode, string Text);

        /// <summary>
        /// スクリプトを -NonInteractive で実行する。pwsh は -File、Windows PowerShell 5.1 は -Command で呼ぶ
        /// (5.1 の -File は -Confirm:$false の値を文字列として渡し、SwitchParameter に変換できずに止まる)。
        /// </summary>
        public Outcome Run(string shell, IEnumerable<string> arguments)
        {
            var environment = new Dictionary<string, string>
            {
                ["APPDATA"] = System.IO.Path.Combine(Path, "AppData"),
                ["LOCALAPPDATA"] = System.IO.Path.Combine(Path, "LocalAppData"),
                ["TEMP"] = System.IO.Path.Combine(Path, "Temp"),
                ["TMP"] = System.IO.Path.Combine(Path, "Temp"),
                // Windows SDK(signtool)などの無い開発用の PC と同じにする。確認の前の段階は、署名の道具に頼らない
                ["ProgramFiles(x86)"] = System.IO.Path.Combine(Path, "ProgramFilesX86"),
            };
            ProcessRunner.Result result;
            if (shell == "pwsh")
            {
                result = ProcessRunner.RunPowerShell("pwsh", new[] { "-NoProfile", "-NonInteractive", "-File", scriptPath }.Concat(arguments),
                    Timeout, environment, Path);
            }
            else
            {
                var quoted = string.Join(" ", arguments.Select(a => a.StartsWith('-') ? a : "'" + a.Replace("'", "''") + "'"));
                // スクリプトが例外で止まったときに -Command が終了コード 0 を返さないように、try で受けて 1 にする
                var command = "try { & '" + scriptPath.Replace("'", "''") + "' " + quoted + "; exit $LASTEXITCODE } catch { $_ | Out-String | Write-Output; exit 1 }";
                result = ProcessRunner.RunPowerShell("powershell", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
                    Timeout, environment, Path);
            }
            return new Outcome(result.ExitCode, result.Output + result.Error);
        }

        public void Dispose() => folder.Dispose();
    }
}
