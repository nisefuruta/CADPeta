# PETADATAの追加検証ケース一括実行:
#  case=tfm2024/tfm2027 : 全選択スモーク（キーワード経路）
#  case=pickfirst       : 先選択（sssetfirst）＋尺度 1:2
#  case=emptyir         : POINT のみの図面（全て未対応→中止のエラー経路）
#  case=zeroscale       : 尺度 0 入力の拒否（再入力で 1）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$Case,
    [string]$OutDir = "$env:TEMP\cadpeta_p2a\case_$Case"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'obj_cases.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = "C:\Users\wk\repo\CADPeta"
$Ver = switch ($Case) { "tfm2024" { "2024" } "tfm2027" { "2027" } default { "2026" } }
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
. "$PSScriptRoot\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $tfm)
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue

# CADが強制終了しても永続sysvar（SECURELOAD等）を利用者プロファイルへ残さないレジストリ保全ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
try {

if ($Case -eq "emptyir") {
    # POINT 1個だけの図面を作ってからPETADATA→全選択
    $dwg = Join-Path $OutDir "points.dwg"
    $gen = @"
_.POINT 10,10
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
PETADATA
ALL


_.QUIT _Y
"@
    $gen | Out-File "$OutDir\run.scr" -Encoding ascii
    Push-Location -LiteralPath (Split-Path -Parent "$OutDir\run.scr")  # accoreconsole は図面ごとのログを作業ディレクトリへ書く
    try { & $acad /i "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt" /s "$OutDir\run.scr" /l ja-JP 2>&1 | Out-File "$OutDir\accore_stdout.log" -Encoding utf8 } finally { Pop-Location }
    $cadExitCode = $LASTEXITCODE
}
else {
    $dwg = Join-Path $OutDir "TESTCHART2.dwg"
    Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg
    $body = switch ($Case) {
        "pickfirst" { @"
(setvar "PICKFIRST" 1)
(sssetfirst nil (ssget "_C" (list 48.0 100.0) (list 220.0 140.0)))
PETADATA
2
"@ }
        # AutoCAD は .scr の入力を拒否すると .scr の残りを読まずに標準入力を待つ（2026 で確かめた）。
        # 再入力の 1 と終了は標準入力から渡し、拒否の表示と、1 で出力された SVG を確かめる
        "zeroscale" { @"
PETADATA
ALL

0
"@ }
        default { @"
PETADATA
ALL


"@ }
    }
    @"
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
$body
_.QUIT _Y
"@ | Out-File "$OutDir\run.scr" -Encoding ascii
    # 標準入力は zeroscale だけが使う。ほかのケースは空にして閉じる（.scr が止まっても入力を待たずに終わる）
    $stdinLines = @(if ($Case -eq "zeroscale") { "1"; "_.QUIT _Y" })
    # 拒否の表示を照合するので、accoreconsole の UTF-16LE の出力をそのまま読む
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.Encoding]::Unicode
    Push-Location -LiteralPath (Split-Path -Parent "$OutDir\run.scr")  # accoreconsole は図面ごとのログを作業ディレクトリへ書く
    try { $stdinLines | & $acad /i $dwg /s "$OutDir\run.scr" /l ja-JP 2>&1 | Out-File "$OutDir\accore_stdout.log" -Encoding utf8 }
    finally { Pop-Location; [Console]::OutputEncoding = $previousEncoding }
    $cadExitCode = $LASTEXITCODE
    if ($Case -eq "zeroscale") {
        $log = Get-Content "$OutDir\accore_stdout.log" -Raw -Encoding utf8
        if ($log -notmatch '値は ゼロ以外の正の数でなければなりません') { throw "尺度 0 を拒否した表示が無い（$OutDir\accore_stdout.log）" }
        if (@(Get-ChildItem $OutDir -Filter *.svg).Count -ne 1) { throw "尺度 0 を拒否した後の再入力 1 で SVG が出力されなかった（$OutDir\accore_stdout.log）" }
    }
}

}
catch { $runError = $_ }
finally {
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
Assert-CadRunAndRegRestore $runError $regReport
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
