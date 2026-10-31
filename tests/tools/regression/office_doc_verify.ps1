# 利用者向け文書（docs/04_利用者向け文書.md）の記載どおりに全手順を再現する検証ハーネス。
# accoreconsole で NETLOAD→PETA（文書§3）を実行してから、Office 3アプリを可視起動して
# 文書の操作を COM で再現し、各段階のスクリーンショットを残す。
# 対応箇所: §3(Ctrl+V 実寸)・§5(PPT 自動縮小、Word 図(拡張メタファイル)+サイズ再設定)・
#           §6(グループ解除・文字編集・線幅の 0.75pt 量子化)
#
# 使い方: pwsh tests/tools/regression/office_doc_verify.ps1 [-Dwg <図面>] [-AcadRoot <AutoCADフォルダ>]
# 出力:   %TEMP%\CadPeta_doc_verify\ に成果物・office_doc_verify.json・ss1〜ss5 の PNG
# 注意:   クリップボードを上書きし、Excel/PowerPoint/Word を可視起動する（保存はしない）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Dwg = "$PSScriptRoot\..\..\assets\testcharts\TESTCHART2_layout.dwg",
    [string]$Ctab = "PETA_A3",
    [double]$IntendWmm = 420.16,
    [double]$IntendHmm = 297.04,
    [string]$AcadRoot = "C:\Program Files\Autodesk\AutoCAD 2026",
    [string]$Tfm = "net8.0-windows"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'office_doc_verify.ps1' ''
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
. "$PSScriptRoot\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $Tfm)
$work = Join-Path $env:TEMP "CadPeta_doc_verify"
New-Item -ItemType Directory -Force $work | Out-Null
Get-ChildItem $work | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------- 文書§3: NETLOAD→PETA（accoreconsole） ----------
$dwgCopy = Join-Path $work ([IO.Path]::GetFileName($Dwg))
Copy-Item $Dwg $dwgCopy -Force
@"
(setvar "CTAB" "$Ctab")
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
PETA

_.QUIT _Y
"@ | Out-File "$work\peta.scr" -Encoding ascii
# ↑ PETA 直後の空行は範囲プロンプト（FR-4 対話結線、2026-07-09）への Enter=全体出力
# accoreconsole のタイムアウト・強制終了でも永続 sysvar（SECURELOAD 等）を利用者プロファイルへ
# 残さない包括ガード（対象は accoreconsole 実行区間のみ。後続の Office COM 区間は CAD レジストリに触れない）
. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion (Get-AcadVersionFromPath $AcadRoot)
$runError = $null
try {
    Push-Location -LiteralPath (Split-Path -Parent "$work\peta.scr")  # accoreconsole は図面ごとのログを作業ディレクトリへ書く
    try { & "$AcadRoot\accoreconsole.exe" /i $dwgCopy /s "$work\peta.scr" /l ja-JP 2>&1 | Out-File "$work\accore_stdout.log" -Encoding utf8 } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "accoreconsole が異常終了 (exit=$LASTEXITCODE)" }
}
catch { $runError = $_ }
finally {
    $regReport = $null # [ref] で受け取る変数を先に作る
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
Assert-CadRunAndRegRestore $runError $regReport
# PETA は <図面名>_<レイアウト名>_<日時>.<拡張子> の PDF・SVG・EMF を出力する（diag.json は PETADIAG だけが出力する）
$outputs = @("pdf", "svg", "emf") | ForEach-Object {
    $pattern = [IO.Path]::GetFileNameWithoutExtension($dwgCopy) + "_${Ctab}_*.$_"
    $found = @(Get-ChildItem -LiteralPath $work -Filter $pattern)
    if ($found.Count -ne 1) { throw "文書§3 の出力ファイルが 1 つではない: $pattern（$($found.Count) 個）" }
    $found[0].FullName
}
"PETA 出力3点を確認: $(($outputs | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ', ')"

$PtPerMm = 72.0 / 25.4
function MM([double]$pt) { [math]::Round($pt / $PtPerMm, 2) }
Add-Type -AssemblyName System.Windows.Forms
. "$PSScriptRoot\..\common\screen_capture.ps1"
# 主モニターの全体を path に保存する（Office の描画が終わるのを少し待つ）
function Screenshot([string]$path) {
    Start-Sleep -Milliseconds 800
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Save-ScreenImage $b.X $b.Y $b.Width $b.Height $path
}

$result = [ordered]@{}

# ---------- Excel: Ctrl+V 実寸(§3・§5) → グループ解除(§6) ----------
$xl = New-Object -ComObject Excel.Application
$xl.Visible = $true
$wb = $xl.Workbooks.Add()
$ws = $wb.ActiveSheet
$ws.Paste()
$shape = $ws.Shapes.Item(1)
$result.excel_paste_mm = "$(MM $shape.Width) x $(MM $shape.Height)"
$xl.ActiveWindow.Zoom = 40
Screenshot "$work\ss1_excel_paste.png"

# 文書§6「右クリック→グループ化→グループ解除」の COM 等価（再帰）
$null = $shape.Ungroup()
do {
    $groups = @()
    foreach ($s in $ws.Shapes) { if ($s.Type -eq 6) { $groups += $s } }  # msoGroup=6
    foreach ($g in $groups) { $null = $g.Ungroup() }
} while ($groups.Count -gt 0)
$texts = 0; $weights = @{}
foreach ($s in $ws.Shapes) {
    try { if ($s.TextFrame2.HasText -eq -1) { $texts++ } } catch {}
    try {
        if ($s.Line.Visible -eq -1) {
            $w = [math]::Round($s.Line.Weight, 2)
            $weights[$w] = 1 + ($weights[$w] ?? 0)
        }
    } catch {}
}
$result.excel_ungroup_leaves = $ws.Shapes.Count
$result.excel_text_shapes = $texts
$result.excel_line_weights_pt = ($weights.GetEnumerator() | Sort-Object Name |
    ForEach-Object { "$($_.Name)pt x$($_.Value)" }) -join ", "

# 文書§6: 文字はフォントを保ったまま書き換えられる（書換→読戻し→復元）
foreach ($s in $ws.Shapes) {
    $isText = $false
    try { $isText = ($s.TextFrame2.HasText -eq -1) } catch {}
    if ($isText) {
        $tr = $s.TextFrame2.TextRange
        $result.excel_text_font = $tr.Font.Name
        $orig = $tr.Text
        $tr.Text = "編集テストOK"
        $result.excel_text_rewrite = ($tr.Text -eq "編集テストOK") ? "一致" : "不一致($($tr.Text))"
        $tr.Text = $orig
        break
    }
}
Screenshot "$work\ss2_excel_ungrouped.png"

# ---------- PowerPoint: Ctrl+V → スライド超過の自動縮小(§5) ----------
$pp = New-Object -ComObject PowerPoint.Application
$pp.Visible = $true
$pres = $pp.Presentations.Add($true)
$slide = $pres.Slides.Add(1, 12)  # ppLayoutBlank=12
$ps = $slide.Shapes.Paste().Item(1)
$result.ppt_paste_mm = "$(MM $ps.Width) x $(MM $ps.Height)"
$result.ppt_slide_mm = "$(MM $pres.PageSetup.SlideWidth) x $(MM $pres.PageSetup.SlideHeight)"
Screenshot "$work\ss3_ppt_paste.png"

# ---------- Word: 図(拡張メタファイル)貼り付け → 本文幅縮小 → サイズ再設定(§5) ----------
$wd = New-Object -ComObject Word.Application
$wd.Visible = $true
$docw = $wd.Documents.Add()
$wd.Selection.PasteSpecial([Type]::Missing, $false, 0, $false, 9)  # wdPasteEnhancedMetafile=9
$wshape = if ($docw.InlineShapes.Count -gt 0) { $docw.InlineShapes.Item(1) } else { $docw.Shapes.Item(1) }
$result.word_kind = ($docw.InlineShapes.Count -gt 0) ? "InlineShape" : "Shape"
$result.word_paste_mm = "$(MM $wshape.Width) x $(MM $wshape.Height)"
Screenshot "$work\ss4_word_paste.png"
# 文書§5「貼り付けた後に図のサイズを数値で指定し直す」→ 意図寸法へ復元
$wshape.Width = $IntendWmm * $PtPerMm
$wshape.Height = $IntendHmm * $PtPerMm
$result.word_resized_mm = "$(MM $wshape.Width) x $(MM $wshape.Height)"
Screenshot "$work\ss5_word_resized.png"

# ---------- 後始末（保存しない） ----------
$wb.Saved = $true; $wb.Close($false); $xl.Quit()
$pres.Saved = $true; $pres.Close(); $pp.Quit()
$docw.Saved = $true; $docw.Close($false); $wd.Quit()

$result | ConvertTo-Json | Out-File "$work\office_doc_verify.json" -Encoding utf8
$result | Format-Table -AutoSize
