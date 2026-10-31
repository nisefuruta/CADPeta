# 図形の多い図面で、構造 SVG 書き出し（PETADATA）が図面から開いたオブジェクトの数を確かめ、時間と accoreconsole のメモリを表示する。
# 図面のトランザクション（src/CadPeta.Acquire.CadCommon/CadTransaction.cs）は開いたオブジェクトを Dispose まで持つので、
# 同じオブジェクトを読み取りで開き直すたびに新しいラッパーを作ると、メモリと時間が図形の数に比例して増える（docs/validation/architecture-decisions.md の 2026-10-31 の節）。
# 開いた数が選択の数の $OpenedPerSelectedLimit 倍を超えたら失敗にする。時間とメモリは版や PC で変わるので、判定せずに表示する。
#
# TESTCHART2.dwg のモデル空間に、20 の画層に分けた線分を -Count 本 entmake してから、同じ起動の中で PETADATA を 2 回実行する。
# 1 回目は起動と図形の作成の時間を含むので、比べるのは 2 回目の時間と、PETADATA の出力の「DB直接取得」の時間にする。
# 時間とメモリを修正の前のビルドと比べるときは、そのコミットで作った CadPeta.Plugin.Acad.dll を -Plugin に渡す（版の TFM と合わせる）。
#
# 使い方: pwsh tests/tools/regression/petadata_size_measure.ps1 -Ver 2026 [-Count 100000] [-Plugin <DLL>] [-OutRoot <出力ルート>]
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$Ver,     # 2024 / 2026 / 2027
    [int]$Count = 100000,
    [string]$Plugin = '',
    [string]$OutRoot = "$env:TEMP\CadPeta_petadata_size_measure"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'petadata_size_measure.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
if (-not $Plugin) { $Plugin = Get-E2ePluginPath Acad (Get-CadPluginTargetFramework $Ver) }
$acc = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$dwgSrc = "$repo\tests\assets\testcharts\TESTCHART2.dwg"

. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"
# PETADATA は図形ごとに画層と線種を開くので、画層を分けて同じ画層を何度も開く形にする
$generate = "(setq cpI 0)(repeat $Count (entmake (list '(0 . `"LINE`") (cons 8 (strcat `"PERF`" (itoa (rem cpI 20)))) (list 10 (* 0.01 cpI) 0.0 0.0) (list 11 (* 0.01 cpI) 10.0 0.0))) (setq cpI (1+ cpI)))(princ)"
$head = (Get-AccoreNetloadLines $Plugin) + @($generate)
# 読み取りのラッパーを使い回せば、開くのは選択した図形とその参照先（画層・線種・ブロック）の 1 回ずつなので、図形の多い図面では
# 選択の数をほとんど超えない。使い回さないと、図形ごとに画層・線種・所有者を開き直して 3 倍以上になる
$OpenedPerSelectedLimit = 2
# 選択（all と確定の Enter）と尺度（Enter で 1:1）
$cases = @(
    @{ Id = 'data1'; Lines = @('PETADATA', 'all', '', '') }
    @{ Id = 'data2'; Lines = @('PETADATA', 'all', '', '') }
)
# ケースの終わり（accoreconsole が次のケースの合図を待っている間）に、起動した accoreconsole のメモリを読む。終了していたら $null
$memory = {
    param($case, $process)
    $process.Refresh()
    if (-not $process.HasExited) { @{ PeakWorkingSet = $process.PeakWorkingSet64; Private = $process.PrivateMemorySize64 } }
}

$regGuard = Get-CadRegSnapshot -StageToTrusted @($Plugin) -AcadVersion $Ver
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
$failures = New-Object System.Collections.ArrayList
try {
    New-Item -ItemType Directory -Force $OutRoot | Out-Null
    $runs = Invoke-AccoreBatch -Acc $acc -DwgSrc $dwgSrc -Cases $cases -Head $head -WorkRoot $OutRoot -LaunchName $Ver `
        -TimeoutSeconds 600 -StallSeconds 300 -OnCaseEnd $memory
    "プラグイン: $Plugin（図形 $Count 本）"
    foreach ($c in $cases) {
        $r = $runs[$c.Id]
        if ($r.TimedOut) { [void]$failures.Add("[$($c.Id)] $($r.TimeoutDetail)"); continue }
        if ($r.Output -notmatch [regex]::Escape('[CadPeta] PETADATA 完了')) { [void]$failures.Add("[$($c.Id)] PETADATA が完了しなかった"); continue }
        if ($r.Output -notmatch 'DB直接取得: (\d+)オブジェクト（(\d+)ms。図面から開いたオブジェクト (\d+)）') { [void]$failures.Add("[$($c.Id)] DB直接取得の行が無い"); continue }
        $selected = [int]$Matches[1]; $acquireMs = $Matches[2]; $opened = [int]$Matches[3]
        if ($opened -gt $selected * $OpenedPerSelectedLimit) {
            [void]$failures.Add("[$($c.Id)] 開いたオブジェクト $opened が、選択 $selected の $OpenedPerSelectedLimit 倍を超えた（同じオブジェクトを開き直している）")
        }
        if ($null -eq $r.After) { [void]$failures.Add("[$($c.Id)] 起動した accoreconsole のメモリを読めなかった（ケースの終わりに終了していた）"); continue }
        "[{0}] ケース {1:n0} ms、DB直接取得 {2} ms、選択 {3:n0}、開いたオブジェクト {4:n0}、ピークのワーキングセット {5:n0} MB、プライベート {6:n0} MB" -f `
            $c.Id, $r.ElapsedMs, $acquireMs, $selected, $opened, ($r.After.PeakWorkingSet / 1MB), ($r.After.Private / 1MB)
    }
}
catch { $runError = $_ }
finally {
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport) $failures
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { $_ }
    throw "版 $Ver の PETADATA の試験が失敗した（$($failures.Count) 件）"
}
