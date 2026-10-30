# クロップ経路の回帰: 2点窓・余白(M)・ゼロ面積窓の聞き直し の3ケースを
# 指定 AutoCAD 版の accoreconsole で実行する（図面=TESTCHART2_layout、PETA_A3、A3 1:1。
# 窓は検証記録_クロップ結線.md §5 と同値。期待: 窓=viewBox "160 113.5 100 70"）。
# 2027 実機検証（docs/validation/architecture-decisions.md）で 2026 との版間バイト比較に使用した。
#
# 使い方: pwsh tests/tools/regression/crop_regression.ps1 -Ver 2027 [-OutRoot <出力ルート>]
# 注意:   プロンプト応答は正しい値のみ与える（無効入力は AutoCAD の作法どおり
#         スクリプト自体を停止させる。検証記録_クロップ結線.md §6）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$Ver,     # 2024 / 2026 / 2027
    [string]$OutRoot = "$env:TEMP\CadPeta_crop_regression"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'crop_regression.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$tfm = Get-CadPluginTargetFramework $Ver
$acadRoot = "C:\Program Files\Autodesk\AutoCAD $Ver"
$plugin = (Get-E2ePluginPath Acad $tfm)
$dwgSrc = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg"

# ケース定義: PETA の範囲プロンプトへの応答列と、SVG に期待する寸法・viewBox。
# 期待値は実機で確定した正解であり、一致しなければ失敗として非0終了する
# （表示するだけでは回帰を検出できない）。
$cases = @(
    # 2点窓
    @{ Key = "window";  Lines = @("50,40", "150,110")
       Expect = @{ Width = "100"; Height = "70"; ViewBox = "160 113.5 100 70" } }
    # 余白5mm→2点窓（四辺へ5mmずつ広がる）
    @{ Key = "margin";  Lines = @("M", "5", "50,40", "150,110")
       Expect = @{ Width = "110"; Height = "80"; ViewBox = "155 108.5 110 80" } }
    # ゼロ面積→聞き直し→A=全体（レイアウト全面。用紙実寸由来の端数を含む）
    @{ Key = "zerowin"; Lines = @("50,40", "50,40", "A")
       Expect = @{ Width = "420.1583"; Height = "297.0389"; ViewBox = "0 0 420.1583 297.0389" } }
)

# 3 ケースを 1 回の起動で続けて実行する（tests/tools/common/accore_batch.ps1）。前のケースの余白(M)などの
# オプションが次のケースへ残れば、期待の寸法と合わずに不合格になる。
# 定常オブジェクトスナップは実行中だけ OSMODE=0 にする。accoreconsole のスクリプト座標入力は
# 2点目（GetCorner／-PLOT の右上コーナー）が近傍ジオメトリへ吸着し、窓が入力値からずれる
# （AutoCAD 2026 実測: (150,110) が (167,111) に化けた。ネイティブ -PLOT の格納値も同値で、
# 製品でなく入力経路の性質。BricsCAD でも同じ実測がある）。OSMODE はプロファイル保存のため
# QUIT 前に必ず元値へ戻す。SECURELOAD と TRUSTEDPATHS は変えず、プラグインを信頼済みのフォルダへコピーして読み込む
. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"
$head = @(
    '(setq cpOsmOld (getvar "OSMODE"))'
    '(setvar "OSMODE" 0)'
) + (Get-AccoreNetloadLines $plugin)
$tail = @('(setvar "OSMODE" cpOsmOld)', '_.QUIT', '_Y')
$batchCases = @($cases | ForEach-Object { @{ Id = $_.Key; Lines = @('PETA') + $_.Lines; Def = $_ } })

# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
$failures = New-Object System.Collections.ArrayList
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    New-Item -ItemType Directory -Force $OutRoot | Out-Null
    $runs = Invoke-AccoreBatch -Acc "$acadRoot\accoreconsole.exe" -DwgSrc $dwgSrc -Cases $batchCases -Head $head -Tail $tail `
        -WorkRoot $OutRoot -LaunchName $Ver
    foreach ($bc in $batchCases) {
        $c = $bc.Def
        $r = $runs[$c.Key]
        $label = "$($Ver)_$($c.Key)"
        if ($r.TimedOut) { [void]$failures.Add("[$label] $($r.TimeoutDetail)"); continue }
        if ($r.ExitCode -ne 0) { [void]$failures.Add("[$label] accoreconsole が異常終了: $($r.ExitDetail)"); continue }
        if (-not $r.State.Ok) { [void]$failures.Add("[$label] $($r.State.Detail)"); continue }

        # SVG の寸法と viewBox（ASCII のため stdout の文字化けと無関係に判定できる）
        $svg = Get-ChildItem $r.CaseDir -Filter *.svg | Select-Object -First 1
        if (-not $svg) { [void]$failures.Add("[$label] SVG が生成されていない"); continue }
        $headLine = (Get-Content $svg.FullName -TotalCount 1)
        if ($headLine -notmatch 'width="([^"]+)mm" height="([^"]+)mm" viewBox="([^"]+)"') {
            [void]$failures.Add("[$label] SVG の寸法・viewBox を読み取れない: $headLine")
            continue
        }
        $got = @{ Width = $Matches[1]; Height = $Matches[2]; ViewBox = $Matches[3] }
        "[$label] width=$($got.Width) height=$($got.Height) viewBox=$($got.ViewBox)"
        foreach ($k in @("Width", "Height", "ViewBox")) {
            if ($got[$k] -ne $c.Expect[$k]) {
                [void]$failures.Add("[$label] $k が期待と違う: 期待=$($c.Expect[$k]) 実際=$($got[$k])")
            }
        }
    }
}
catch { $runError = $_ }
finally {
    "所要 {0:n0} 秒（1 回の起動）" -f $sw.Elapsed.TotalSeconds
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport) $failures
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（ケースの判定は途中までなので、一覧は表示しない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }
if ($failures.Count -gt 0) {
    ""
    "=== 失敗 ==="
    $failures | ForEach-Object { $_ }
    throw "版 $Ver のクロップ回帰が失敗した（$($failures.Count) 件）"
}
"版 $Ver のクロップ3ケース合格（期待値と一致）"
