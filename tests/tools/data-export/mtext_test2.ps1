# MTEXT＋背景マスクの実機テスト（出力先プロンプトへ明示パスを応答する版）
param([string]$OutDir = "$env:TEMP\cadpeta_p2a\mtext_test\out2")
$ErrorActionPreference = "Stop"
$plugin = "C:\Users\wk\repo\CADPeta\src\CadPeta.Plugin.Acad\bin\Debug\net8.0-windows\CadPeta.Plugin.Acad.dll"
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
(setvar "SECURELOAD" 0)
(load "$(($OutDir -replace "\\", "/"))/mk.lsp")
MKMT
_.ZOOM _E
NETLOAD
$plugin
PETADATA
ALL


$OutDir
_.QUIT _Y
"@ | Out-File "$OutDir\run.scr" -Encoding ascii
# CADが強制終了しても永続sysvar（SECURELOAD等）を利用者プロファイルへ残さないレジストリ保全ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {
    & "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe" /i "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt" /s "$OutDir\run.scr" /l ja-JP 2>&1 | Out-File "$OutDir\accore_stdout.log" -Encoding utf8
    $cadExitCode = $LASTEXITCODE
}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
}
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | Select-Object Name, Length
