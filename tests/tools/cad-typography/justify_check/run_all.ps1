# TEXT Justify 全種（左/中央/右/中心/整列/フィット/TL~BR＋回転3種＋SHX＋日本語TT）の
# 構造SVG書き出し（AutoCAD DB 直接取得）/PDF印刷経路（-PLOT PDF→CLI変換）位置ずれ実測を一括実行する。
#
# 処理の流れ:
#  1) run_generate.scr → accoreconsole がチャートDWGを生成し、entget ダンプ
#     （entget_dump.tsv: group 10/11/40/41/50/72/73 の実値）・アンカーTSV
#     （anchors.tsv）を書き、見当枠窓（CAD 10,10-410,287）を正解PDFへプロットする
#  2) run_data_export.ps1 → PETADATAのオブジェクト選択（ALL→尺度Enter）で
#     構造SVG書き出しのSVGを取得する（要: CadPeta.Plugin.Acad の net8.0-windows ビルド済み）
#  3) CLI（cadpeta.exe）→ 正解PDFをPDF印刷経路のSVGへ変換する（要: net10.0-windows ビルド済み）
#  4) compare_justify.py → Chrome ヘッドレスで両 SVG をレンダし、見当枠でアフィン
#     位置合わせ後、行ごとに異画素率とインクbbox の mm ずれを表にする
#
# 前提・規律:
#  - accoreconsole は PowerShell から起動する（Git Bash は日本語引数が化ける）
#  - .scr は CP932・.lsp は UTF-8 BOM 付き。脚本は必ず _.QUIT _Y で終える
#  - SECURELOAD と TRUSTEDPATHS は変えず、読み込む .lsp を信頼済みのフォルダ（C:\Tools\ACAD\lisp）へコピーして読み込み、終了後に削除する
#  - 既知の環境制約: accoreconsole は TTC（msgothic.ttc 等）のフォントを解決できず、
#    正解PDF側の日本語TrueType行が「?」になる（行19 は render 比較の対象外。
#    justify 位置の裏取りは entget_dump.tsv の group 10/11 で行う）
#
# 使い方: pwsh -File run_all.ps1 [-OutDir <出力先>] [-SkipAcad]
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$OutDir = "$env:TEMP\cadpeta_p3b_justify",
    [switch]$SkipAcad   # DWG/PDF/TSV が生成済みならチャート生成を飛ばす
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'run_all.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$here = $PSScriptRoot
$acc = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"
$dwt = "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt"
$cli = "C:\Users\wk\repo\CADPeta\src\CadPeta.Cli\bin\Debug\net10.0-windows\cadpeta.exe"
if (-not (Test-Path $cli)) { throw "CLI 未ビルド: $cli（本ハーネスは再ビルドしない）" }

if (-not $SkipAcad) {
    $stray = Get-Process AcCoreConsole -ErrorAction SilentlyContinue
    if ($stray) { throw "accoreconsole の残留プロセスがある（PID: $($stray.Id -join ',')）。先に確認すること" }

    # SAVEAS の保存先に既存 DWG があると確認プロンプトが挟まり応答列が崩れるため、
    # 常にファイルが存在しない状態から始める（glyph_render_check で実測済みの流儀）
    foreach ($f in "justify_chart.dwg", "justify_chart_ground_truth.pdf", "anchors.tsv", "entget_dump.tsv") {
        Remove-Item (Join-Path $here $f) -ErrorAction SilentlyContinue
    }
    $scr = Join-Path $here "run_generate.scr"
    $log = Join-Path $here "run_generate_stdout.log"
    $pdf = Join-Path $here "justify_chart_ground_truth.pdf"
    # AutoCAD のプロファイルを記録して戻し、時間を超えたら自分が起動した accoreconsole だけを終了する（accore_run.ps1）
    . "$PSScriptRoot\..\..\regression\CadRegGuard.ps1"
    . "$PSScriptRoot\..\..\common\accore_run.ps1"
    $accoreProcess = $null
    Invoke-AccoreConsoleGuardedWithTimeout $acc $dwt $scr $log "$log.err" 120 ([ref]$accoreProcess) -StageToTrusted @((Join-Path $here "build_justify_chart.lsp"))
    if (-not (Test-Path $pdf)) { throw "チャート生成が失敗した。$log を確認すること" }
    "チャート生成: DWG・正解PDF・anchors.tsv・entget_dump.tsv を生成した"
}

# 構造SVG書き出し（PETADATAオブジェクト選択）
& (Join-Path $here "run_data_export.ps1") -OutDir "$OutDir\data-export" -Confirm:$false

# PDF印刷経路（正解PDF → CLI 変換）
New-Item -ItemType Directory -Force "$OutDir\print-output" | Out-Null
& $cli (Join-Path $here "justify_chart_ground_truth.pdf") "$OutDir\print-output\justify_print.svg"
if ($LASTEXITCODE -ne 0) { throw "CLI 変換が異常終了 (exit=$LASTEXITCODE)" }

# 突き合わせ（Chrome レンダ＋行別計測）
Push-Location $here
uv run python compare_justify.py "$OutDir\data-export\justify_chart_Model_data.svg" `
    "$OutDir\print-output\justify_print.svg" anchors.tsv "$OutDir\compare" 150
Pop-Location
