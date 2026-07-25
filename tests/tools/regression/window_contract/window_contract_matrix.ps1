# 窓プロットの座標契約を実機で確定する計測ハーネス。
#
# SetPlotWindowArea がどの座標系の値を期待するかは、Autodesk 公式仕様（DCS）と実機挙動が食い違う。
# 本ハーネスは pick 2点から3候補（生値 / UCS→WCS / WCS→DCS）を作り、座標以外の条件を完全に揃えて
# 別々の PDF を発行し、判断材料になる値を全て JSON へ記録する。
#
# 使い方:
#   pwsh -File tests/tools/regression/window_contract/window_contract_matrix.ps1 -Case A
#   pwsh -File tests/tools/regression/window_contract/window_contract_matrix.ps1 -Dwg <実図面> -Case AsIs
#
# 規律（この repo の実機検証の作法。破ると環境が壊れる）:
# - SECURELOAD は既定のまま触らない。プラグインのフォルダだけを TRUSTEDPATHS へ一時追加し、必ず復元する。
# - FILEDIA を脚本に書かない。脚本は必ず _.QUIT で終える（EOF・強制killはレジストリを汚す）。
# - 入力 DWG は作業コピーに対してのみ操作し、原本のハッシュ不変を確認する。
# - 残留プロセスを失敗証跡として報告する（黙って消さない）。
[CmdletBinding()]
param(
    [string]$Case = 'A',
    [string]$Dwg,                                  # 省略時は TESTCHART2.dwg
    [string]$Ver = '2026',
    [string]$OutRoot = "$env:TEMP\CadPeta_window_contract",
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$repo = (Resolve-Path "$PSScriptRoot\..\..\..\..").Path
$acc = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$tfm = switch ($Ver) { '2024' { 'net48' } '2026' { 'net8.0-windows' } '2027' { 'net10.0-windows' } default { throw "未知の版: $Ver" } }
$probeDir = "$PSScriptRoot\WindowContractProbe\bin\Release\net8.0-windows"
$probe = "$probeDir\WindowContractProbe.dll"
if (-not (Test-Path $acc)) { throw "accoreconsole がない: $acc" }
if (-not (Test-Path $probe)) { throw "計測器が未ビルド: $probe" }
if (-not $Dwg) { $Dwg = "$repo\tests\assets\testcharts\TESTCHART2.dwg" }
if (-not (Test-Path $Dwg)) { throw "入力図面がない: $Dwg" }
. "$PSScriptRoot\window_contract_cases.ps1"

$work = Join-Path $OutRoot $Case
if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null
$dwgCopy = Join-Path $work 'target.dwg'
Copy-Item $Dwg $dwgCopy
$srcHash = (Get-FileHash $dwgCopy -Algorithm SHA256).Hash

# 計測条件は環境変数で渡す（プロンプト列は版・ロケール依存で脆いため、行列を直接与える）
$env:CPWIN_OUT = $work
$env:CPWIN_CASE = $Case
$setupLines = ''
if ($Case -eq 'AsIs') {
    # 実図面の UCS・ビューをそのまま使う（合成条件を被せない）
    if (-not $PSBoundParameters.ContainsKey('Dwg')) { throw "AsIs は -Dwg で実図面を指定してください" }
    $p1 = Read-Host '最初のコーナー（現在UCS。例 -50,120）'
    $p2 = Read-Host 'もう一方のコーナー（現在UCS。例 -15,155）'
} else {
    if (-not $CadPetaWindowCases.ContainsKey($Case)) { throw "未知のケース: $Case（共通ケース定義か AsIs）" }
    $cfg = $CadPetaWindowCases[$Case]
    $env:CPWIN_UCS_ORIGIN = $cfg.Ucs[0]; $env:CPWIN_UCS_XDIR = $cfg.Ucs[1]; $env:CPWIN_UCS_YDIR = $cfg.Ucs[2]
    $env:CPWIN_VIEW_TARGET = $cfg.Target; $env:CPWIN_VIEW_DIR = $cfg.Dir
    $env:CPWIN_VIEW_TWIST_DEG = ([string]$cfg.Twist)
    $env:CPWIN_VIEW_CENTER = $cfg.Center
    $env:CPWIN_VIEW_HEIGHT = ([string]$cfg.H); $env:CPWIN_VIEW_WIDTH = ([string]$cfg.W)
    $p1 = $cfg.P1; $p2 = $cfg.P2
    $setupLines = "CPWINSETUCS`r`nCPWINSETVIEW"
}
$env:CPWIN_IN1 = $p1
$env:CPWIN_IN2 = $p2

$pd = $probeDir -replace '\\','/'
$pl = $probe -replace '\\','/'
$transOut = ($work -replace '\\','/') + '/trans.txt'
$lispP1 = ConvertTo-LispPoint $p1
$lispP2 = ConvertTo-LispPoint $p2
# AutoCAD 自身の trans に UCS→WCS・UCS→DCS を答えさせる。行列の向き・ねじれの符号を
# こちらで推測すると、まさに今回の取り違えを繰り返す。CAD の定義を一次情報として採る
# （trans の第2・3引数: 0=WCS, 1=UCS, 2=DCS）。
$transLisp = @"
(defun cpfmt (p) (strcat (rtos (car p) 2 6) "," (rtos (cadr p) 2 6) "," (rtos (caddr p) 2 6)))
(setq cptf (open "$transOut" "w"))
(foreach cpp (list $lispP1 $lispP2)
  (write-line (strcat "UCS=" (cpfmt cpp)
                      " WCS=" (cpfmt (trans cpp 1 0))
                      " DCS=" (cpfmt (trans cpp 1 2))) cptf))
(write-line (strcat "VIEWTWIST=" (rtos (getvar "VIEWTWIST") 2 9)) cptf)
(write-line (strcat "TARGET=" (cpfmt (getvar "TARGET"))) cptf)
(close cptf)
"@
$scr = Join-Path $work 'run.scr'
@"
(setvar "TILEMODE" 1)
(setq cpOldTrusted (getvar "TRUSTEDPATHS"))
(setvar "TRUSTEDPATHS" (if (= cpOldTrusted "") "$pd" (strcat cpOldTrusted ";" "$pd")))
(command "_.NETLOAD" "$pl")
(setvar "TRUSTEDPATHS" cpOldTrusted)
(princ "\nCPWIN_TRUSTED_RESTORED=")(princ (getvar "TRUSTEDPATHS"))(princ)
$setupLines
$transLisp
(setq cpOldOsmode (getvar "OSMODE"))
(setvar "OSMODE" 0)
CPWINPROBE
$p1
$p2
(setvar "OSMODE" cpOldOsmode)
_.QUIT
_Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $work 'accore_stdout.log'
$stderr = Join-Path $work 'accore_stderr.log'
. "$PSScriptRoot\..\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
$runError = $null
$p = $null
try {
    $p = Start-Process -FilePath $acc -ArgumentList @('/i', $dwgCopy, '/s', $scr, '/l', 'ja-JP') `
          -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force; $p.WaitForExit()
        throw "accoreconsole が ${TimeoutSeconds}秒で完了しない（$stdout）"
    }
    if ($p.ExitCode -ne 0) { throw "accoreconsole が異常終了した: exit=$($p.ExitCode)（$stdout）" }
}
catch { $runError = $_ }
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
}
if ($runError -and -not $regReport.ok) {
    throw [AggregateException]::new(
        'CAD実行失敗に加えてレジストリ復元にも失敗した',
        [Exception[]]@($runError.Exception, [InvalidOperationException]::new($regReport.mismatch -join '; ')))
}
if ($runError) { throw $runError }
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
if ((Get-FileHash $dwgCopy -Algorithm SHA256).Hash -ne $srcHash) { throw "入力 DWG が変更された（計測条件が保てない）" }

$json = Join-Path $work "$Case.probe.json"
if (-not (Test-Path $json)) {
    $tail = (Get-Content $stdout -Encoding Unicode -Tail 40 -ErrorAction SilentlyContinue) -join "`n"
    throw "計測記録が出ていない: $json`n--- stdout 末尾 ---`n$tail"
}
$r = Get-Content $json -Raw -Encoding utf8 | ConvertFrom-Json
if ($r.fatal) { throw "計測が失敗した:`n$($r.fatal)" }

"=== ケース $Case（exit=$($p.ExitCode)） ==="
"入力文字列      : $($r.inputString1) / $($r.inputString2)"
"API 戻り値      : $($r.apiPoint1 -join ',') / $($r.apiPoint2 -join ',')"
"UCS原点(WCS)    : $($r.ucsOriginInWcs -join ',')"
"view.Target     : $($r.view.target -join ',')"
"view.ViewDir    : $($r.view.viewDirection -join ',')"
"view.Twist(rad) : $($r.view.viewTwistRad)"
"view.Center(DCS): $($r.view.centerPoint -join ',')"
"sysvar TARGET   : $($r.sysvars.TARGET)"
"sysvar VIEWTWIST: $($r.sysvars.VIEWTWIST)"
"sysvar VIEWDIR  : $($r.sysvars.VIEWDIR)"
"sysvar WORLDUCS : $($r.sysvars.WORLDUCS)"
""
$transFile = Join-Path $work 'trans.txt'
if (Test-Path $transFile) {
    "AutoCAD 自身の trans による変換（一次情報）:"
    Get-Content $transFile | ForEach-Object { "  $_" }
} else {
    Write-Warning "trans の記録が出ていない: $transFile（LISP が失敗した可能性）"
}
""
"候補ごとの窓と成果物:"
foreach ($k in @('raw','ucs2wcs','wcs2dcs')) {
    $e = $r.candidates.$k
    if ($e.error) { "  [$k] 失敗: $(($e.error -split "`n")[0])"; continue }
    "  [{0,-7}] 窓={1} 設定直後={2} 検証後={3} 用紙={4} PDF={5} bytes" -f `
        $k, ($e.window -join ','), ($e.windowAfterSet -join ','), ($e.windowAfterValidate -join ','), $e.mediaUsed, $e.pdfBytes
}
""
"記録: $json"
if ($p -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
    throw "今回起動した accoreconsole が残留している: PID=$($p.Id)"
}
