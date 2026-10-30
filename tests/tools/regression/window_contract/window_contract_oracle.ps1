# 窓プロットの座標契約のオラクル（正解の定義）。
#
# CAD 自身の -PLOT に窓の2点を与え、AutoCAD がその2点を「どの座標系へ写して」ページ設定へ
# 格納するかを DXF 群 48/49/140/141 から直接読み出す。利用者が同じ2点を指定したときに CAD が
# 実際に行う解釈そのものであり、レンダ比較より曖昧さがない。
#
# 使い方:
#   pwsh -File tests/tools/regression/window_contract/window_contract_oracle.ps1 -Case B
#
# -PLOT の応答列は版・ロケール依存で壊れやすい（AutoCAD 2026 ja-JP・PDF プロッタで実機確定した）。
# 無効入力を与えると -PLOT はエラーを出さずに黙って中断し、後続行が別コマンドとして誤実行される。
# プロンプト数が変わったら stdout を読み直して応答列を取り直すこと。
#
# -Case AsIs は図面自身の UCS・ビューをそのまま使う（合成条件を被せない）。実物図面の再現に使う。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Case = 'B',
    [string]$Dwg,
    [string]$P1,                      # AsIs のとき必須。最初のコーナー（現在UCS、"x,y"）
    [string]$P2,                      # AsIs のとき必須。もう一方のコーナー
    [string]$Ver = '2026',
    [string]$OutRoot = "$env:TEMP\CadPeta_window_oracle",
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'window_contract_oracle.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
. "$PSScriptRoot\window_contract_cases.ps1"

$repo = (Resolve-Path "$PSScriptRoot\..\..\..\..").Path
$acc = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$probeDir = "$PSScriptRoot\WindowContractProbe\bin\Release\net8.0-windows"
$probe = "$probeDir\WindowContractProbe.dll"
if (-not (Test-Path $acc)) { throw "accoreconsole がない: $acc" }
if (-not (Test-Path $probe)) { throw "計測器が未ビルド: $probe" }
if (-not $Dwg) { $Dwg = "$repo\tests\assets\testcharts\TESTCHART2.dwg" }
if (-not (Test-Path $Dwg)) { throw "入力図面がない: $Dwg" }
$asIs = $Case -eq 'AsIs'
if (-not $asIs -and -not $CadPetaWindowCases.ContainsKey($Case)) { throw "未知のケース: $Case（共通ケース定義か AsIs）" }
if ($asIs -and (-not $P1 -or -not $P2)) { throw "AsIs は -P1 / -P2（現在UCSの2点）が要る" }

$work = Join-Path $OutRoot $Case
if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null
$dwgCopy = Join-Path $work 'target.dwg'
Copy-Item $Dwg $dwgCopy

if ($asIs) {
    # 図面自身の UCS・ビューを使うため、条件の設定行は入れない
    $setupLines = ''
    $p1 = $P1; $p2 = $P2
} else {
    $cfg = $CadPetaWindowCases[$Case]
    $setupLines = Set-CadPetaWindowCaseEnv $cfg
    $p1 = if ($P1) { $P1 } else { $cfg.P1 }
    $p2 = if ($P2) { $P2 } else { $cfg.P2 }
}
$lispP1 = ConvertTo-LispPoint $p1
$lispP2 = ConvertTo-LispPoint $p2
$pd = $probeDir -replace '\\','/'
$pl = $probe -replace '\\','/'
$workFwd = $work -replace '\\','/'
$pdfPath = "$work\oracle.pdf"

# -PLOT の応答列（AutoCAD 2026 ja-JP・PDF プロッタで実機確定）。空行は Enter＝既定値。
# 「印刷スタイルを使用して印刷しますか」「印刷スタイル テーブル名」「線の太さを使用して印刷しますか」の
# 3プロンプトが「印刷オフセット」と「シェーディング印刷の設定」の間に挟まる。ここを落とすと
# 以降の応答が1つずつずれ、-PLOT が黙って中断する（実機で2回再現した）。
$scr = Join-Path $work 'oracle.scr'
@"
(setvar "TILEMODE" 1)
(setq cpOsmOld (getvar "OSMODE"))
(setvar "OSMODE" 0)
$((Get-AccoreNetloadLines $probe) -join "`r`n")
$setupLines
(defun cpfmt (p) (strcat (rtos (car p) 2 6) "," (rtos (cadr p) 2 6) "," (rtos (caddr p) 2 6)))
(setq cptf (open "$workFwd/oracle_trans.txt" "w"))
(foreach cpp (list $lispP1 $lispP2)
  (write-line (strcat "UCS=" (cpfmt cpp) " WCS=" (cpfmt (trans cpp 1 0)) " DCS=" (cpfmt (trans cpp 1 2))) cptf))
(close cptf)
-PLOT
Yes

AutoCAD PDF (High Quality Print).pc3
ISO_full_bleed_A3_(420.00_x_297.00_MM)



Window
$p1
$p2
Fit
Center




$pdfPath
Yes
Yes
(defun cplastg1 (e / v) (setq v nil) (foreach p e (if (= (car p) 1) (setq v (cdr p)))) v)
(defun cpnum (e k) (if (assoc k e) (rtos (cdr (assoc k e)) 2 6) "<なし>"))
(setq cprf (open "$workFwd/oracle_stored.txt" "w"))
(setq cpld (cdr (assoc -1 (dictsearch (namedobjdict) "ACAD_LAYOUT"))))
(setq cpe (dictnext cpld T))
(while cpe
  (write-line (strcat "LAYOUT=" (cplastg1 cpe)
                      " 74=" (cpnum cpe 74)
                      " 48=" (cpnum cpe 48) " 49=" (cpnum cpe 49)
                      " 140=" (cpnum cpe 140) " 141=" (cpnum cpe 141)) cprf)
  (setq cpe (dictnext cpld)))
(close cprf)
(setvar "OSMODE" cpOsmOld)
_.QUIT
_Y
"@ | Out-File $scr -Encoding ascii
# ↑ OSMODE はスクリプト座標入力の2点目が定常スナップへ吸着するため実行中だけ 0 にする
#   （AutoCAD 2026 実測。プロファイル保存のため QUIT 前に必ず元値へ戻す）

$stdout = Join-Path $work 'accore_stdout.log'
$stderr = Join-Path $work 'accore_stderr.log'
. "$PSScriptRoot\..\CadRegGuard.ps1"
. "$PSScriptRoot\..\..\common\accore_run.ps1"
$p = $null
Invoke-AccoreConsoleGuardedWithTimeout $acc $dwgCopy $scr $stdout $stderr $TimeoutSeconds ([ref]$p) -StageToTrusted @($probe)

$storedFile = Join-Path $work 'oracle_stored.txt'
if (-not (Test-Path $storedFile)) {
    $tail = (Get-Content $stdout -Encoding Unicode -Tail 60 -ErrorAction SilentlyContinue) -join "`n"
    throw "格納値の読み戻しが出ていない（-PLOT の応答列がずれた可能性）:`n--- stdout 末尾 ---`n$tail"
}
if (-not (Test-Path $pdfPath)) {
    $tail = (Get-Content $stdout -Encoding Unicode -Tail 60 -ErrorAction SilentlyContinue) -join "`n"
    throw "オラクル PDF が生成されていない（-PLOT が中断した可能性）:`n--- stdout 末尾 ---`n$tail"
}

"=== オラクル ケース $Case（exit=$($p.ExitCode)） ==="
"与えた2点（現在UCS）: $p1 / $p2"
""
"AutoCAD 自身の trans（一次情報）:"
Get-Content (Join-Path $work 'oracle_trans.txt') | ForEach-Object { "  $_" }
""
"-PLOT がページ設定へ格納した窓（74=4 が窓プロット。48/49=左下、140/141=右上）:"
Get-Content $storedFile | ForEach-Object { "  $_" }
""
"オラクル PDF: $pdfPath ($((Get-Item $pdfPath).Length) bytes)"
if ($p -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
    throw "今回起動した accoreconsole が残留している: PID=$($p.Id)"
}
