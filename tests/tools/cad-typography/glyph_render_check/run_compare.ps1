# SHX 解釈器（CadPeta.Glyphs.Shx）の AutoCAD 実機突き合わせを一括実行する。
# 1) run_ground_truth.scr で accoreconsole からチャート図面を作り正解PDFへプロット
# 2) ShxGlyphProbe でプローブSVG（輪郭化結果）とセルTSVを生成
# 3) compare_glyphs.py で機械比較（見当枠位置合わせ・行/セル比較・並置/差分PNG）
#
# 前提: accoreconsole は PowerShell から起動する（Git Bash は日本語引数が化ける）。
# 使い方: pwsh -File run_compare.ps1 [-OutDir <比較成果物の出力先>] [-SkipAcad]
param(
    [string]$OutDir = "$env:TEMP\claude\glyph_check",
    [switch]$SkipAcad   # 既に glyph_chart_ground_truth.pdf / placement.tsv がある場合、再生成をスキップ
)
$ErrorActionPreference = "Stop"
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
    #   (a) 稀に /i でのテンプレート読み込みが「テンプレートファイルが見つからない」
    #       (ErrorStatus=76) で即失敗する（原因不明・再実行すれば通ることを確認済み）。
    #       Start-Process -ArgumentList は空白を含むパス（"Program Files"配下）の引用符が
    #       壊れて再現することを実機で確認したため使わない。呼出演算子(&)をジョブ内で使う
    #   (b) _.QUIT 後もまれにプロセスが残留する（実害はなく成果物はQUIT前に書き終わっている）
    # (a) はリトライで、(b) はジョブ完了を待たず成果物のタイムスタンプ更新を
    # 完了条件にしタイムアウトしたらジョブごと強制終了することで、それぞれ吸収する。
    # SAVEAS の保存先に既存DWG（前回実行の残り）があると「レイアウトへの変更を保存しますか」
    # という余分な確認プロンプトが挟まり、後続の応答列が総崩れになる（実機で再現・実測済み）。
    # 常にファイルが存在しない状態から SAVEAS させることで確認プロンプトの有無を固定する。
    $dwgOut = Join-Path $here "glyph_chart.dwg"
    Remove-Item $dwgOut -ErrorAction SilentlyContinue

    $maxAttempts = 3
    $succeeded = $false
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        Remove-Item $dwgOut -ErrorAction SilentlyContinue
        $beforeTime = if (Test-Path $gtPdfCheck) { (Get-Item $gtPdfCheck).LastWriteTime } else { $null }
        $job = Start-Job -ScriptBlock {
            param($acc, $dwt, $scr, $log)
            & $acc /i $dwt /s $scr /l ja-JP *> $log
        } -ArgumentList $acc, $dwt, $scr, $log
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            if (Test-Path $gtPdfCheck) {
                $t = (Get-Item $gtPdfCheck).LastWriteTime
                if ($null -eq $beforeTime -or $t -gt $beforeTime) { $succeeded = $true; break }
            }
            if ($job.State -ne "Running") { break }
            Start-Sleep -Milliseconds 500
        }
        if ($job.State -eq "Running") {
            if ($succeeded) {
                Write-Warning "accoreconsole ジョブが成果物生成後も残留しているため強制終了する"
            }
            Stop-Job $job
        }
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        Get-Process AcCoreConsole -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        if ($succeeded) { break }
        Write-Warning "accoreconsole 試行 $attempt/$maxAttempts が失敗した（$log を参照）。再試行する"
        Start-Sleep -Seconds 2
    }
    if (-not $succeeded) {
        throw "accoreconsole が $maxAttempts 回とも正解PDFを生成できなかった。$log を確認すること"
    }
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
