# MTEXT＋背景マスクの実機テスト（出力先プロンプトへ明示パスを応答する版）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param([string]$OutDir = "$env:TEMP\cadpeta_p2a\mtext_test\out2")
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'mtext_test2.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$plugin = (Get-E2ePluginPath Acad 'net8.0-windows')
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*.svg", "$OutDir\*.emf", "$OutDir\*.json" -Force -ErrorAction SilentlyContinue
$lsp = @'
(defun c:MKMT ()
  (command "_.LINE" (list 20.0 28.0 0.0) (list 100.0 42.0 0.0) "")
  (command "_.LINE" (list 20.0 42.0 0.0) (list 100.0 28.0 0.0) "")
  (entmake (list (cons 0 "MTEXT") (cons 100 "AcDbEntity") (cons 8 "0")
                 (cons 100 "AcDbMText") (list 10 30.0 40.0 0.0) (cons 40 5.0)
                 (cons 41 70.0) (cons 71 1)
                 (cons 1 "背景あり {\\C1;MTEXT} テスト")
                 (cons 90 1) (cons 63 2) (cons 45 1.5)))
  (princ))
'@
[IO.File]::WriteAllText("$OutDir\mk.lsp", $lsp, [Text.Encoding]::UTF8)
@"
$((Get-AccoreLoadLispLines (Join-Path $OutDir "mk.lsp")) -join "`r`n")
MKMT
_.ZOOM _E
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
PETADATA
ALL


$OutDir
_.QUIT _Y
"@ | Out-File "$OutDir\run.scr" -Encoding ascii
# CADが強制終了しても永続sysvar（SECURELOAD等）を利用者プロファイルへ残さないレジストリ保全ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @((Join-Path $OutDir "mk.lsp"), $plugin) -AcadVersion '2026'
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
try {
    Push-Location -LiteralPath (Split-Path -Parent "$OutDir\run.scr")  # accoreconsole は図面ごとのログを作業ディレクトリへ書く
    try { & "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe" /i "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt" /s "$OutDir\run.scr" /l ja-JP 2>&1 | Out-File "$OutDir\accore_stdout.log" -Encoding utf8 } finally { Pop-Location }
    $cadExitCode = $LASTEXITCODE
}
catch { $runError = $_ }
finally {
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
Assert-CadRunAndRegRestore $runError $regReport
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | Select-Object Name, Length
