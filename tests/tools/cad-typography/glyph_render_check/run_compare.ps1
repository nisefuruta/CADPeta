# SHX 解釈器（CadPeta.Glyphs.Shx）の AutoCAD 実機突き合わせを一括実行する。
# 1) run_ground_truth.scr で accoreconsole からチャート図面を作り正解PDFへプロット
# 2) ShxGlyphProbe でプローブSVG（輪郭化結果）とセルTSVを生成
# 3) compare_glyphs.py で機械比較（見当枠位置合わせ・行/セル比較・並置/差分PNG）
#
# 前提: accoreconsole は PowerShell から起動する（Git Bash は日本語引数が化ける）。
# 使い方: pwsh -File run_compare.ps1 [-OutDir <比較成果物の出力先>] [-SkipAcad]
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$OutDir = "$env:TEMP\claude\glyph_check",
    [switch]$SkipAcad   # 既に glyph_chart_ground_truth.pdf / placement.tsv がある場合、再生成をスキップ
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'run_compare.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$here = $PSScriptRoot
$acc = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"
$dwt = "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt"

if (-not $SkipAcad) {
    $stray = Get-Process AcCoreConsole -ErrorAction SilentlyContinue
    if ($stray) { Write-Warning "accoreconsole の残留プロセスがある（PID: $($stray.Id -join ',')）。手動確認すること" }

    $scr = Join-Path $here "run_ground_truth.scr"
    $log = Join-Path $here "run_ground_truth.log"
    $gtPdfCheck = Join-Path $here "glyph_chart_ground_truth.pdf"

    # 実測した2つの既知の flakiness に対処する:
    #   (a) /i でのテンプレート読み込みが「テンプレートファイルが見つからない」(ErrorStatus=76) で失敗する。
    #       原因は Start-Process -ArgumentList に配列を渡すと、空白を含むパス（"Program Files" の下）が
    #       引用符で囲まれずに分かれること（7.6・5.1 で実測、2026-10-25）。共通の起動は ConvertTo-NativeArgString で
    #       囲むように直し、& と Start-Process で 10 回ずつ起動して 76 が出ないことを確かめた
    #   (b) _.QUIT 後もまれにプロセスが残留する（実害はなく成果物はQUIT前に書き終わっている）
    # (a) の再試行は残している。(b) は時間を超えたら自分が起動したプロセスだけを終了し、成果物のタイムスタンプの更新を
    # 完了の条件にする（ほかのセッションの accoreconsole は終了させない）。
    # SAVEAS の保存先に既存DWG（前回実行の残り）があると「レイアウトへの変更を保存しますか」
    # という余分な確認プロンプトが挟まり、後続の応答列が総崩れになる（実機で再現・実測済み）。
    # 常にファイルが存在しない状態から SAVEAS させることで確認プロンプトの有無を固定する。
    $dwgOut = Join-Path $here "glyph_chart.dwg"
    Remove-Item $dwgOut -ErrorAction SilentlyContinue

    # AutoCAD のプロファイルは、すべての試行の前に 1 回記録し、最後に戻す（CadRegGuard.ps1）。
    # 試行は、時間を超えたら自分が起動した accoreconsole だけを終了する（accore_run.ps1）
    . "$PSScriptRoot\..\..\regression\CadRegGuard.ps1"
    . "$PSScriptRoot\..\..\common\accore_run.ps1"
    $maxAttempts = 3
    $succeeded = $false
    $regGuard = Get-CadRegSnapshot -StageToTrusted @((Join-Path $here "build_glyph_chart.lsp")) -AcadVersion (Get-AcadVersionFromPath $acc)
    $regReport = $null
    $runError = $null
    try {
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            Remove-Item $dwgOut -ErrorAction SilentlyContinue
            $beforeTime = if (Test-Path $gtPdfCheck) { (Get-Item $gtPdfCheck).LastWriteTime } else { $null }
            $accoreProcess = $null
            try { Invoke-AccoreConsoleWithTimeout $acc $dwt $scr $log "$log.err" 90 ([ref]$accoreProcess) }
            catch { Write-Warning "accoreconsole 試行 $attempt/$maxAttempts が失敗した: $($_.Exception.Message)" }
            if (Test-Path $gtPdfCheck) {
                $t = (Get-Item $gtPdfCheck).LastWriteTime
                if ($null -eq $beforeTime -or $t -gt $beforeTime) { $succeeded = $true; break }
            }
            Write-Warning "accoreconsole 試行 $attempt/$maxAttempts で正解PDFができなかった（$log を参照）。再試行する"
            Start-Sleep -Seconds 2
        }
        if (-not $succeeded) {
            throw "accoreconsole が $maxAttempts 回とも正解PDFを生成できなかった。$log を確認すること"
        }
    }
    catch { $runError = $_ }
    finally { Restore-CadRegStateAndReport $regGuard ([ref]$regReport) }
    Assert-CadRunAndRegRestore $runError $regReport
    "accoreconsole: 正解PDF・DWG・配置TSVを生成した（試行${attempt}回目、$log）"
}

$probeExe = Join-Path $here "ShxGlyphProbe\bin\Debug\net10.0-windows\ShxGlyphProbe.exe"
if (-not (Test-Path $probeExe)) {
    dotnet build (Join-Path $here "ShxGlyphProbe\ShxGlyphProbe.csproj") -c Debug
}
$tsv = Join-Path $here "placement.tsv"
$svg = Join-Path $here "glyph_chart_probe.svg"
$cells = Join-Path $here "cells.tsv"
& $probeExe $tsv $svg $cells
if ($LASTEXITCODE -ne 0) { throw "ShxGlyphProbe が異常終了 (exit=$LASTEXITCODE)" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$gtPdf = Join-Path $here "glyph_chart_ground_truth.pdf"
Push-Location $here
uv run python compare_glyphs.py $gtPdf $svg $cells $OutDir 150
Pop-Location
