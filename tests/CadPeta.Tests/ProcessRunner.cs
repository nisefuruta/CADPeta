using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CadPeta.Tests;

/// <summary>試験から外部のプログラム(pwsh、cmd)を起動し、終了を待って、出力と終了コードを返す。</summary>
internal static class ProcessRunner
{
    internal sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>
    /// <paramref name="fileName"/> を起動する。<paramref name="environment"/> は子プロセスにだけ渡し、試験のプロセスの環境変数は変えない。
    /// <paramref name="input"/> を標準入力に書いて閉じる(pause などの入力待ちで止まらないように)。
    /// <paramref name="timeout"/> までに終わらなければ子プロセスを終了させて失敗にする。
    /// </summary>
    public static Result Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout, Encoding outputEncoding,
                             IDictionary<string, string>? environment = null, string? workingDirectory = null, string input = "")
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment != null)
        {
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException(fileName + " を起動できませんでした。");
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(fileName + " が " + timeout.TotalSeconds + " 秒で終わりませんでした。出力:\n" + output.Result);
        }
        process.WaitForExit();
        return new Result(process.ExitCode, output.Result, error.Result);
    }

    /// <summary>
    /// PowerShell を起動する。<paramref name="shell"/> は "pwsh"(PowerShell 7)か "powershell"(Windows PowerShell 5.1)。
    /// 実行ファイルと出力の文字コードはここで決める(呼び出し元ごとに決めない)。
    /// 試験のプロセスにはコンソールが無いので、子の PowerShell は新しいコンソールを作り、その既定のコードページ(OEM)で出力する
    /// (pwsh 7 と 5.1 の両方。2026-10-26 に確かめた)。そのため OEM のコードページで読む。起動するスクリプトは、外部のプログラムの
    /// 出力を読む間だけ [Console]::OutputEncoding を変えてよく、自分が出力を書く前に元へ戻す。
    /// </summary>
    public static Result RunPowerShell(string shell, IEnumerable<string> arguments, TimeSpan timeout,
                                       IDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        string exe = shell switch
        {
            "pwsh" => "pwsh",
            "powershell" => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            _ => throw new ArgumentException("PowerShell の種類は pwsh か powershell: " + shell, nameof(shell)),
        };
        return Run(exe, arguments, timeout, OemEncoding(), environment, workingDirectory);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>コンソールの OEM のコードページ。cmd と Windows PowerShell 5.1 は、出力をリダイレクトするとこのコードページで書く。</summary>
    public static Encoding OemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetOEMCP());
    }
}
