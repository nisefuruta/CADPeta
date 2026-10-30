# accoreconsole を .scr 付きで起動するハーネスが共有する関数。呼び出し側スクリプトがドットソースで読み込む。
. "$PSScriptRoot\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）

# accoreconsole を非表示で起動し、$TimeoutSeconds 以内の終了と終了コード 0 を確かめる。
# 起動した Process は直ちに参照 $process へ入れる（例外になっても、呼び出し側が残留の確認に使える）。
# 時間を超えたら自分が起動したプロセスだけを強制終了して例外にする。終了コードが 0 でなければ例外にする。
function Invoke-AccoreConsoleWithTimeout([string]$acc, [string]$dwgCopy, [string]$scr, [string]$stdout, [string]$stderr,
                                         [int]$TimeoutSeconds, [ref]$process) {
    $process.Value = Start-Process -WorkingDirectory (Split-Path -Parent $scr) -FilePath $acc -ArgumentList (ConvertTo-NativeArgString @('/i', $dwgCopy, '/s', $scr, '/l', 'ja-JP')) `
          -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $p = $process.Value
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force; $p.WaitForExit()
        throw "accoreconsole が ${TimeoutSeconds}秒で完了しない（$stdout）"
    }
    if ($p.ExitCode -ne 0) { throw "accoreconsole が異常終了した: exit=$($p.ExitCode)（$stdout）" }
}

# accoreconsole を実行して（Invoke-AccoreConsoleWithTimeout）、その前後でレジストリを保全する。
# CAD の実行の失敗とレジストリ復元の失敗は、両方あれば合流させて投げる（Assert-CadRunAndRegRestore）。
# CadRegGuard.ps1 を呼び出し側がドットソースで読み込んでおくこと。
# $StageToTrusted は、.scr が読み込む DLL と LISP。信頼済みのフォルダへコピーし、終了後に削除する（Get-CadRegSnapshot -StageToTrusted）。
function Invoke-AccoreConsoleGuardedWithTimeout([string]$acc, [string]$dwgCopy, [string]$scr, [string]$stdout, [string]$stderr,
                                                [int]$TimeoutSeconds, [ref]$process, [string[]]$StageToTrusted = @()) {
    $regGuard = Get-CadRegSnapshot -StageToTrusted $StageToTrusted -AcadVersion $(if (@($StageToTrusted).Count) { Get-AcadVersionFromPath $acc } else { '' })
    $runError = $null
    $regReport = $null
    try {
        Invoke-AccoreConsoleWithTimeout $acc $dwgCopy $scr $stdout $stderr $TimeoutSeconds $process
    }
    catch { $runError = $_ }
    finally {
        Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
    }
    Assert-CadRunAndRegRestore $runError $regReport
}
