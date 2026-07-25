# プラグイン回帰マトリクス: 指定 AutoCAD 版の accoreconsole で
# モデル/レイアウト × PETA/PETADIAG の4ケース（Enter=全体出力）を実行し、
# 各ケースの exit・成果物集合・完了文言・入力DWG不変を assert する合否判定つき回帰。
# 1件でも不一致があれば非0終了する（観測のみで通す旧仕様から厳格化した）。
# 版間・修正前後のバイト比較の実行系（比較自体はハッシュ突き合わせで別途行う）。
# 2027 実機検証（docs/validation/architecture-decisions.md）で 3版×4ケースの基準採取に使用した。
#
# 使い方: pwsh tests/tools/regression/plugin_regression_matrix.ps1 -Ver 2027 [-OutRoot <出力ルート>]
# 前提:   dotnet build CadPeta.sln 済み。対話 AutoCAD は終了しておく（Debug DLL のロック回避）
param(
    [Parameter(Mandatory)][string]$Ver,     # 2024 / 2026 / 2027
    [string]$OutRoot = "$env:TEMP\CadPeta_plugin_matrix",
    [string]$Config = "Debug",
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path

# 版→TFM の対応は src/Directory.Build.props の割当てに一致させる
$tfm = switch ($Ver) {
    "2024" { "net48" }
    "2026" { "net8.0-windows" }
    "2027" { "net10.0-windows" }
    default { throw "未知の版: $Ver" }
}
$acadRoot = "C:\Program Files\Autodesk\AutoCAD $Ver"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\$Config\$tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグイン未ビルド: $plugin" }
if (-not (Test-Path "$acadRoot\accoreconsole.exe")) { throw "accoreconsole がない: $acadRoot" }

$drawings = @(
    @{ Key = "model";  Src = "$repo\tests\assets\testcharts\TESTCHART2.dwg" },
    @{ Key = "layout"; Src = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg" }
)
$commands = @("PETA", "PETADIAG")

$resultLines = @()
$failures = @()

# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する
# （タイムアウトで accoreconsole を強制終了すると FileDialog=0 が永続化する実測がある）
. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot

try {
foreach ($d in $drawings) {
    foreach ($cmd in $commands) {
        $label = "$($Ver)_$($d.Key)_$cmd"
        $work = Join-Path $OutRoot $label
        New-Item -ItemType Directory -Force $work | Out-Null
        Remove-Item "$work\*" -Force -Recurse -ErrorAction SilentlyContinue
        $dwgCopy = Join-Path $work ([IO.Path]::GetFileName($d.Src))
        Copy-Item $d.Src $dwgCopy -Force
        $sourceHash = (Get-FileHash $dwgCopy -Algorithm SHA256).Hash

        # NETLOAD→コマンド→空行(Enter=全体)→終了マーカー→QUIT の .scr（ASCII のみ。日本語を書くなら CP932）
        # 終了マーカーは QUIT 直前に置き、成果物評価の前提（実在しないケースは成果物を評価せず FAIL）にする
        $scr = Join-Path $work "run.scr"
        @"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
$cmd

(princ "\nCADPETA_CASE_END=$label")(princ)
_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

        $caseFail = New-Object System.Collections.Generic.List[string]
        $stdout = Join-Path $work "accore_stdout.log"
        $stderr = Join-Path $work "accore_stderr.log"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath "$acadRoot\accoreconsole.exe" -ArgumentList @('/i', $dwgCopy, '/s', $scr, '/l', 'ja-JP') -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
            $caseFail.Add("accoreconsole が ${TimeoutSeconds}秒で完了しない")
        }
        $sw.Stop()
        $exit = $p.ExitCode

        # 1) exit=0
        if ($exit -ne 0) { $caseFail.Add("accoreconsole 異常終了 (exit=$exit)") }

        # 3) 完了文言（サマリ行・終了マーカー）。サマリ行（RunSummary）は通常モード（PETA）のみが
        # 出力する仕様で、PETADIAG は診断出力（diag.json・stage SVG）の実在で完了を判定する
        if ($cmd -eq 'PETA') {
            $hasSummary = (Test-Path $stdout) -and (Select-String -Path $stdout -Encoding Unicode -Pattern 'bytes / \d+図形 → クリップボード登録' -Quiet)
            if (-not $hasSummary) { $caseFail.Add("完了サマリ行を確認できない: $stdout") }
        }
        $endPattern = [regex]::Escape("CADPETA_CASE_END=$label")
        $hasEnd = (Test-Path $stdout) -and (Select-String -Path $stdout -Encoding Unicode -Pattern $endPattern -Quiet)
        if (-not $hasEnd) { $caseFail.Add("終了マーカー未検出: $stdout") }

        # 2) 成果物集合（終了マーカーが実在するケースのみ評価。マーカー無しは評価せず FAIL 扱い）
        $pdf = @(); $svg = @(); $emf = @(); $diagJson = @(); $stageSvg = @()
        if ($hasEnd) {
            $pdf = @(Get-ChildItem $work -Filter *.pdf -Recurse)
            $emf = @(Get-ChildItem $work -Filter *.emf -Recurse)
            $allSvg = @(Get-ChildItem $work -Filter *.svg -Recurse)
            $stageSvg = @($allSvg | Where-Object { $_.Name -match '\.stage\d+\.' })
            $svg = @($allSvg | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
            $diagJson = @(Get-ChildItem $work -Filter *.diag.json -Recurse)

            if ($cmd -eq 'PETA') {
                if ($pdf.Count -ne 1) { $caseFail.Add("pdf 個数不正: $($pdf.Count)（期待1）") }
                if ($svg.Count -ne 1) { $caseFail.Add("svg 個数不正: $($svg.Count)（期待1）") }
                if ($emf.Count -ne 1) { $caseFail.Add("emf 個数不正: $($emf.Count)（期待1）") }
                if ($diagJson.Count -ne 0) { $caseFail.Add("diag.json が想定外に存在: $($diagJson.Count)（期待0）") }
                if ($stageSvg.Count -ne 0) { $caseFail.Add("stage SVG が想定外に存在: $($stageSvg.Count)（期待0）") }
            } else {
                if ($pdf.Count -ne 1) { $caseFail.Add("pdf 個数不正: $($pdf.Count)（期待1）") }
                if ($svg.Count -ne 1) { $caseFail.Add("svg(本体) 個数不正: $($svg.Count)（期待1）") }
                if ($emf.Count -ne 1) { $caseFail.Add("emf 個数不正: $($emf.Count)（期待1）") }
                if ($diagJson.Count -ne 1) { $caseFail.Add("diag.json 個数不正: $($diagJson.Count)（期待1）") }
                if ($stageSvg.Count -lt 1) { $caseFail.Add("stage SVG が存在しない（期待1以上）") }
            }
        }

        # 4) 入力 DWG 不変
        if ((Get-FileHash $dwgCopy -Algorithm SHA256).Hash -ne $sourceHash) { $caseFail.Add("入力 DWG が実行前後で変更された") }

        $pass = ($caseFail.Count -eq 0)
        $status = if ($pass) { "PASS" } else { "FAIL" }
        $resultLines += "[$label] exit=$exit elapsed=$($sw.ElapsedMilliseconds)ms pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count) diag=$($diagJson.Count) stage=$($stageSvg.Count) $status"
        if (-not $pass) { $failures += "[$label] " + ($caseFail -join " / ") }
    }
}
}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
    if (-not $regReport.ok) { $failures += "レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ') }
}

$resultLines | ForEach-Object { $_ }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "版 $Ver の回帰で $($failures.Count) 件のケースが不一致（詳細は上記）"
}
"版 $Ver の4ケース完了（全PASS）"
