# プラグイン回帰マトリクス: 指定 AutoCAD 版の accoreconsole で
# モデル/レイアウト × PETA/PETADIAG の4ケース（Enter=全体出力）を、図面ごとに 1 回の起動で実行し、
# 各ケースの exit・成果物集合・完了文言・入力DWG不変を assert する合否判定つき回帰。
# 1件でも不一致があれば非0終了する（観測のみで通す旧仕様から厳格化した）。
# 版間・修正前後のバイト比較の実行系（比較自体はハッシュ突き合わせで別途行う）。
# 2027 実機検証（docs/validation/architecture-decisions.md）で 3版×4ケースの基準採取に使用した。
#
# 使い方: pwsh tests/tools/regression/plugin_regression_matrix.ps1 -Ver 2027 [-OutRoot <出力ルート>]
# 前提:   pwsh -File tests/tools/build_e2e.ps1 済み（配布物と同じビルドの DLL）。対話 AutoCAD は終了しておく（DLL のロック回避）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$Ver,     # 2024 / 2026 / 2027
    [string]$OutRoot = "$env:TEMP\CadPeta_plugin_matrix",
    [int]$TimeoutSeconds = 300   # 1 ケースの上限
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'plugin_regression_matrix.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path

. "$PSScriptRoot\..\common\test_helpers.ps1"
$tfm = Get-CadPluginTargetFramework $Ver
$acadRoot = "C:\Program Files\Autodesk\AutoCAD $Ver"
$plugin = (Get-E2ePluginPath Acad $tfm)
if (-not (Test-Path "$acadRoot\accoreconsole.exe")) { throw "accoreconsole がない: $acadRoot" }

$drawings = @(
    @{ Key = "model";  Src = "$repo\tests\assets\testcharts\TESTCHART2.dwg" },
    @{ Key = "layout"; Src = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg" }
)
$commands = @("PETA", "PETADIAG")

$resultLines = @()
$failures = @()

# 図面ごとに PETA と PETADIAG を 1 回の起動で続けて実行する（tests/tools/common/accore_batch.ps1）。
# SECURELOAD と TRUSTEDPATHS は変えず、信頼済みのフォルダへコピーしたプラグインを NETLOAD する（起動ごとに 1 回）。
# .scr は ASCII のみ（日本語を書くなら CP932）
. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"
$head = Get-AccoreNetloadLines $plugin

# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する
# （タイムアウトで accoreconsole を強制終了すると FileDialog=0 が永続化する実測がある）
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    New-Item -ItemType Directory -Force $OutRoot | Out-Null
    foreach ($d in $drawings) {
        # コマンド→空行(Enter=全体)
        $batchCases = @($commands | ForEach-Object { @{ Id = "$($Ver)_$($d.Key)_$_"; Lines = @($_, ''); Command = $_ } })
        $runs = Invoke-AccoreBatch -Acc "$acadRoot\accoreconsole.exe" -DwgSrc $d.Src -Cases $batchCases -Head $head `
            -WorkRoot $OutRoot -LaunchName "$($Ver)_$($d.Key)" -TimeoutSeconds $TimeoutSeconds
        foreach ($bc in $batchCases) {
            $label = $bc.Id
            $cmd = $bc.Command
            $r = $runs[$label]
            $caseFail = New-Object System.Collections.Generic.List[string]
            if ($r.TimedOut) { $caseFail.Add($r.TimeoutDetail) }
            # 1) exit=0
            if ($r.ExitCode -ne 0) { $caseFail.Add("accoreconsole 異常終了 ($($r.ExitDetail))") }
            if (-not $r.State.Ok) { $caseFail.Add($r.State.Detail) }

            # 3) 完了文言（サマリ行・終了マーカー）。サマリ行（RunSummary）は通常モード（PETA）のみが
            # 出力する仕様で、PETADIAG は診断出力（diag.json・stage SVG）の実在で完了を判定する
            if ($cmd -eq 'PETA' -and $r.Output -notmatch 'bytes / \d+図形 → クリップボード登録') {
                $caseFail.Add("完了サマリ行を確認できない: $($r.StdoutPath)")
            }
            $hasEnd = $r.Output.Contains("CADPETA_CASE_END=$label")
            if (-not $hasEnd) { $caseFail.Add("終了マーカー未検出: $($r.StdoutPath)") }

            # 2) 成果物集合（終了マーカーが実在するケースのみ評価。マーカー無しは評価せず FAIL 扱い）
            $pdf = @(); $svg = @(); $emf = @(); $diagJson = @(); $stageSvg = @()
            if ($hasEnd) {
                $pdf = @(Get-ChildItem $r.CaseDir -Filter *.pdf -Recurse)
                $emf = @(Get-ChildItem $r.CaseDir -Filter *.emf -Recurse)
                $allSvg = @(Get-ChildItem $r.CaseDir -Filter *.svg -Recurse)
                $stageSvg = @($allSvg | Where-Object { $_.Name -match '\.stage\d+\.' })
                $svg = @($allSvg | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
                $diagJson = @(Get-ChildItem $r.CaseDir -Filter *.diag.json -Recurse)

                if ($pdf.Count -ne 1) { $caseFail.Add("pdf 個数不正: $($pdf.Count)（期待1）") }
                if ($svg.Count -ne 1) { $caseFail.Add("svg(本体) 個数不正: $($svg.Count)（期待1）") }
                if ($emf.Count -ne 1) { $caseFail.Add("emf 個数不正: $($emf.Count)（期待1）") }
                if ($cmd -eq 'PETA') {
                    if ($diagJson.Count -ne 0) { $caseFail.Add("diag.json が想定外に存在: $($diagJson.Count)（期待0）") }
                    if ($stageSvg.Count -ne 0) { $caseFail.Add("stage SVG が想定外に存在: $($stageSvg.Count)（期待0）") }
                } else {
                    if ($diagJson.Count -ne 1) { $caseFail.Add("diag.json 個数不正: $($diagJson.Count)（期待1）") }
                    if ($stageSvg.Count -lt 1) { $caseFail.Add("stage SVG が存在しない（期待1以上）") }
                }
            }

            # 4) 入力 DWG 不変
            if (-not $r.DwgUnchanged) { $caseFail.Add("入力 DWG が実行前後で変更された") }

            $pass = ($caseFail.Count -eq 0)
            $status = if ($pass) { "PASS" } else { "FAIL" }
            $resultLines += "[$label] $($r.ExitDetail) elapsed=$($r.ElapsedMs)ms pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count) diag=$($diagJson.Count) stage=$($stageSvg.Count) $status"
            if (-not $pass) { $failures += "[$label] " + ($caseFail -join " / ") }
        }
    }
}
catch { $runError = $_ }
finally {
    "所要 {0:n0} 秒（図面ごとに 1 回の起動）" -f $sw.Elapsed.TotalSeconds
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
    if (-not $regReport.ok) { $failures += "レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ') }
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（ケースの判定は途中までなので、一覧は表示しない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }
$resultLines | ForEach-Object { $_ }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "版 $Ver の回帰で $($failures.Count) 件のケースが不一致（詳細は上記）"
}
"版 $Ver の4ケース完了（全PASS）"
