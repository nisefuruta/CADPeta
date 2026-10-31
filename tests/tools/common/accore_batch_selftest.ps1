#Requires -Version 7
# Invoke-AccoreBatch（accore_batch.ps1）の約束を、AutoCAD を使わずに確かめる。accoreconsole の代わりに、.scr のケースの
# 完了の合図（sync\end_<id>.txt）を書いて次へ進む合図（sync\go_<id>.txt）を待つだけの実行ファイルを作って起動する。
# 使い方: pwsh -NoProfile -File tests\tools\common\accore_batch_selftest.ps1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\accore_batch.ps1"

$work = Join-Path ([IO.Path]::GetTempPath()) ("CadPeta_accore_batch_selftest_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$checked = New-Object System.Collections.Generic.List[string]
$started = New-Object System.Collections.Generic.List[int]
try {
    # 代わりの accoreconsole。環境変数 CADPETA_FAKE_ACC_HANG_TAIL=1 なら、全ケースの後で終了せずに待つ（Tail の CLOSE・QUIT が止まった状態）。
    # 起動したプロセスの番号を CADPETA_FAKE_ACC_PID のファイルへ追記する
    $source = @'
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
public static class FakeAccore {
    public static int Main(string[] args) {
        File.AppendAllText(Environment.GetEnvironmentVariable("CADPETA_FAKE_ACC_PID"), System.Diagnostics.Process.GetCurrentProcess().Id + Environment.NewLine);
        string scr = null;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "/s") scr = args[i + 1];
        string text = File.ReadAllText(scr);
        string sync = Regex.Match(text, "\\(strcat \"([^\"]+)/end_\"").Groups[1].Value;
        foreach (Match m in Regex.Matches(text, "\\(cadpeta-sync \"([A-Za-z0-9_-]+)\"\\)")) {
            string id = m.Groups[1].Value;
            Console.Out.Write("\nCADPETA_CASE_SETUP=" + id + " entities=1\nCADPETA_CASE_END=" + id + "\n");
            Console.Out.Flush();
            File.WriteAllText(Path.Combine(sync, "end_" + id + ".txt"), id);
            while (!File.Exists(Path.Combine(sync, "go_" + id + ".txt"))) Thread.Sleep(50);
        }
        if (Environment.GetEnvironmentVariable("CADPETA_FAKE_ACC_HANG_TAIL") == "1") Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
'@
    $fake = Join-Path $work 'fake_accoreconsole.exe'
    $sourcePath = Join-Path $work 'FakeAccore.cs'
    [IO.File]::WriteAllText($sourcePath, $source)
    # pwsh の Add-Type は実行ファイルを出力できないので、Windows PowerShell 5.1 の Add-Type で作る
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "Add-Type -Path '$sourcePath' -OutputAssembly '$fake' -OutputType ConsoleApplication"
    if (-not (Test-Path -LiteralPath $fake)) { throw "代わりの accoreconsole を作れなかった: $fake" }
    $dwg = Join-Path $work 'input.dwg'
    [IO.File]::WriteAllText($dwg, 'dwg')
    $pidFile = Join-Path $work 'pids.txt'
    $env:CADPETA_FAKE_ACC_PID = $pidFile
    function Get-StartedPids { if (Test-Path -LiteralPath $pidFile) { @(Get-Content -LiteralPath $pidFile | Where-Object { $_ } | ForEach-Object { [int]$_ }) } else { @() } }
    function Assert-NoneRunning([string]$label) {
        foreach ($id in Get-StartedPids) {
            if (Get-Process -Id $id -ErrorAction SilentlyContinue) { throw "${label}: 起動したプロセス $id が残っている" }
        }
    }
    $cases = @(@{ Id = 'one'; Lines = @() }, @{ Id = 'two'; Lines = @() })

    # 1. 正常に終われば、全ケースの ExitCode は 0
    $runs = Invoke-AccoreBatch -Acc $fake -DwgSrc $dwg -Cases $cases -WorkRoot (Join-Path $work 'normal') -LaunchName 'normal' -TimeoutSeconds 30
    foreach ($id in 'one', 'two') {
        if ($runs[$id].ExitCode -ne 0 -or $runs[$id].TimedOut) { throw "1: $id が失敗した: $($runs[$id].ExitDetail)" }
    }
    Assert-NoneRunning '1'
    $checked.Add('正常に終われば、全ケースの ExitCode は 0 で、起動したプロセスは残らない')

    # 2. ケースの後の処理（コールバック・成果物の移動）が例外を投げても、起動したプロセスを終了させてから例外を返す
    Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
    $threw = $false
    try {
        [void](Invoke-AccoreBatch -Acc $fake -DwgSrc $dwg -Cases $cases -WorkRoot (Join-Path $work 'throw') -LaunchName 'throw' -TimeoutSeconds 30 `
            -OnCaseEnd { param($case) throw "注入した例外: $($case.Id)" })
    }
    catch { $threw = $_.Exception.Message -match '注入した例外' }
    if (-not $threw) { throw '2: コールバックの例外を返さなかった' }
    if (@(Get-StartedPids).Count -ne 1) { throw "2: 起動したプロセスの数が 1 でない: $(@(Get-StartedPids).Count)" }
    Assert-NoneRunning '2'
    $checked.Add('ケースの後の処理が例外を投げても、起動したプロセスを終了させてから例外を返す')

    # 3. 全ケースの後の終了（Tail）が止まって強制終了したときは、完了したケースも失敗（ExitCode が 0 でない）として返す
    Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
    $env:CADPETA_FAKE_ACC_HANG_TAIL = '1'
    try { $runs = Invoke-AccoreBatch -Acc $fake -DwgSrc $dwg -Cases $cases -WorkRoot (Join-Path $work 'tail') -LaunchName 'tail' -TimeoutSeconds 3 }
    finally { Remove-Item Env:CADPETA_FAKE_ACC_HANG_TAIL }
    foreach ($id in 'one', 'two') {
        if ($runs[$id].ExitCode -eq 0) { throw "3: 終了が止まったのに $id の ExitCode が 0: $($runs[$id].ExitDetail)" }
        if ($runs[$id].ExitDetail -notmatch '終了') { throw "3: 終了が止まったことを示していない: $($runs[$id].ExitDetail)" }
    }
    Assert-NoneRunning '3'
    $checked.Add('全ケースの後の終了が止まって強制終了したときは、完了したケースも失敗として返し、起動したプロセスは残らない')

    # 4. ケースの後の処理には、起動した accoreconsole のプロセスを渡す（ほかの accoreconsole と取り違えずにメモリなどを読める）
    Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
    $runs = Invoke-AccoreBatch -Acc $fake -DwgSrc $dwg -Cases $cases -WorkRoot (Join-Path $work 'process') -LaunchName 'process' -TimeoutSeconds 30 `
        -OnCaseEnd { param($case, $process) @{ Id = $process.Id; Exited = $process.HasExited } }
    $launched = @(Get-StartedPids)
    foreach ($id in 'one', 'two') {
        if ($runs[$id].After.Id -ne $launched[0]) { throw "4: $id のケースの後の処理に、起動したプロセス $($launched[0]) が渡らなかった: $($runs[$id].After.Id)" }
        if ($runs[$id].After.Exited -ne $false) { throw "4: $id のケースの後の処理の時点で、渡したプロセスが動いていない" }
    }
    $checked.Add('ケースの後の処理には、起動した accoreconsole の動いているプロセスを渡す')

    "accore_batch_selftest OK（確かめた約束 $($checked.Count) 件）"
    $checked | ForEach-Object { "  - $_" }
}
finally {
    # 試験が途中で止まっても、代わりの accoreconsole を残さない
    if (Test-Path -LiteralPath (Join-Path $work 'pids.txt')) {
        foreach ($id in @(Get-Content -LiteralPath (Join-Path $work 'pids.txt') | Where-Object { $_ })) { Stop-Process -Id ([int]$id) -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item Env:CADPETA_FAKE_ACC_PID -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
