# SHX 不在エラー経路の検証: 実在しない SHX フォント（cadpeta_missing_font.shx）を指す
# スタイルのTEXTを1個だけ持つ図面をLISPで生成し、PETADATA→全選択で
# (a) コマンドが正常完了する (b) 診断ログに「フォントファイルを解決できず近似」が出る
# (c) 出力 SVG では代替書体の text になっていることを確認する。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Ver = "2026",
    [string]$OutDir = "$env:TEMP\cadpeta_p3\shx_missing_font",
    [string]$Repo = "C:\Users\wk\repo\CADPeta"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'shx_missing_font_case.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = $Repo
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
. "$PSScriptRoot\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $tfm)
$lsp = "C:\Users\wk\repo\CADPeta\tests\tools\cad-typography\gen_missing_shx.lsp"
if (-not (Test-Path $lsp)) { throw "LISP がない: $lsp" }
# 保存済み DWG（TESTCHART2.dwg のコピー）を土台にする。テンプレートからの未保存図面は
# 保存先パス確定の追加プロンプトで応答行数がずれるため使わない。
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$lspCopy = Join-Path $OutDir "gen_missing_shx.lsp"
Copy-Item $lsp $lspCopy
$dwg = Join-Path $OutDir "missing_shx.dwg"
Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg

$scr = Join-Path $OutDir "run.scr"
@"
$((Get-AccoreLoadLispLines $lspCopy) -join "`r`n")
GENMISSINGSHX
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
PETADATA
L


_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CAD強制終了でも永続sysvar（SECURELOAD等）を復元するため、実行全体をレジストリガードで包む
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$cadExitCode = $null # [ref] で受け取る変数を先に作る
Invoke-AcadConsoleGuarded $acad $dwg $scr $stdout ([ref]$cadExitCode) -StageToTrusted @($lspCopy, $plugin)
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
