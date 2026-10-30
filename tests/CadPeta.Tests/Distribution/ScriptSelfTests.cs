using System;
using System.IO;
using Xunit;

namespace CadPeta.Tests.Distribution;

/// <summary>
/// tests/tools と配布物の補助の関数の自己試験（CAD を使わないもの）を実行する。自己試験は手で実行しないと動かず、
/// 補助の関数を直しても壊れたことに気付けないので、単体試験から実行する。自己試験は失敗すると例外で 0 以外の終了コードを返す。
/// </summary>
public sealed class ScriptSelfTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    // Windows PowerShell 5.1 で動く配布物（compat の register.ps1・unregister.ps1）と、それを使う試験の関数は 5.1 で確かめる
    [Theory]
    [InlineData("pwsh", "tests/tools/common/accore_batch_selftest.ps1")]
    [InlineData("pwsh", "tests/tools/regression/cad_reg_guard_selftest.ps1")]
    [InlineData("pwsh", "tests/tools/regression/page_setup_pairwise_selftest.ps1")]
    [InlineData("pwsh", "tests/tools/distribution/dll_path_check_selftest.ps1")]
    [InlineData("pwsh", "tests/tools/distribution/sign_record_selftest.ps1")]
    [InlineData("pwsh", "tests/tools/distribution/sign_dist_resume_check.ps1")]
    [InlineData("pwsh", "tests/tools/lint/trusted_paths_lint.ps1")]
    [InlineData("pwsh", "tests/tools/lint/reg_restore_report_lint.ps1")]
    [InlineData("pwsh", "tests/tools/common/json_lines_selftest.ps1")]
    [InlineData("powershell", "tests/tools/common/json_lines_selftest.ps1")]
    [InlineData("pwsh", "tests/tools/common/e2e_build_record_selftest.ps1")]
    [InlineData("powershell", "tests/tools/common/e2e_build_record_selftest.ps1")]
    [InlineData("powershell", "tests/tools/common/compat_autoload_selftest.ps1")]
    [InlineData("powershell", "tests/tools/distribution/compat_register_selftest.ps1")]
    public void 自己試験が合格する(string shell, string script) => AssertSelfTestPasses(shell, script);

    /// <summary>自己試験のスクリプトを実行し、終了コードが 0 でなければ、その出力(失敗の理由)を付けて不合格にする。</summary>
    internal static void AssertSelfTestPasses(string shell, string script)
    {
        string path = Path.Combine(RepositoryPaths.Root(), script.Replace('/', Path.DirectorySeparatorChar));
        var result = ProcessRunner.RunPowerShell(shell, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }, Timeout);
        Assert.True(result.ExitCode == 0, script + " の終了コード " + result.ExitCode + "\n" + result.Output + result.Error);
    }

    // 自己試験が失敗したときの理由(日本語)を読めるように、PowerShell の出力を正しい文字コードで読む
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public void PowerShellの出力の日本語を読める(string shell)
    {
        var result = ProcessRunner.RunPowerShell(shell, new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output '署名の失敗'; [Console]::Error.WriteLine('復元の失敗')" }, Timeout);
        Assert.Contains("署名の失敗", result.Output);
        Assert.Contains("復元の失敗", result.Error);
    }
}
