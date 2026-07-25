# モデル空間×窓×用紙合わせ（Fit）の実効尺度・クロップ絶対サイズの実機回帰。
#
# 既存回帰の空白を埋める: window_ucs_regression.ps1 はハッシュの相対一致のみ、
# peta_window_rotation_compat.ps1 は固定尺度 1:1 のみで、Fit 尺度の絶対値は
# どちらも実機と突き合わせていなかった（回転90の窓フィットが印刷可能領域の
# 縦横入れ替えで印字される契約は 2026-07-17 に実測確定。テストチャートが条件の
# 隅に寄っていると誤りが何度でも通る、という既知の穴の同型）。
#
# 各ケース: 新規図面へ窓と同一矩形＋対角線を描き、-PLOT（オラクル・ページ設定保存）→
# PETADIAG を同一セッションで流す。判定は3点:
#  1) AutoCAD 自身の「有効な印刷領域」（-PLOT と PETA の PlotEngine 双方）が
#     窓×PETA算出尺度と一致する（回転が奇数四半なら用紙フレームで縦横が入れ替わる）
#  2) 最終 SVG の viewBox 寸法 = 窓×尺度（表示フレーム）
#  3) 最終 SVG の内容 bbox が viewBox を満たす（窓の縁に描いた矩形が四辺とも残る =
#     クロップが実際の印字内容と一致する絶対サイズ検証）
param(
    [string]$Ver = "2026",
    [string]$OutRoot = "$env:TEMP\CadPeta_window_fit_scale",
    [string]$Config = "Debug",
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = "Stop"
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$cp932 = [Text.Encoding]::GetEncoding(932)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$tfm = switch ($Ver) { "2024" { "net48" }; "2026" { "net8.0-windows" }; "2027" { "net10.0-windows" }; default { throw "未対応版: $Ver" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\$Config\$tfm\CadPeta.Plugin.Acad.dll"
$template = "C:\Program Files\Autodesk\AutoCAD $Ver\UserDataCache\ja-jp\Template\acadiso.dwt"
if (-not (Test-Path $acad)) { throw "accoreconsole がない: $acad" }
if (-not (Test-Path $plugin)) { throw "プラグインが未ビルド: $plugin" }
if (-not (Test-Path $template)) { throw "テンプレートがない: $template" }

# 用紙は表示名（-PLOT の用紙プロンプトは表示名を要求する）。余白付き（200×263）と
# フルブリード（210×297）× 縦横 × 窓縦横比で、フィット式の全分岐（幅基準・高さ基準・
# 回転あり・なし）を通す。
$paperMargin = "ISO A4 (210.00 x 297.00 ミリ)"
$paperFull   = "ISO フルブリード A4 (210.00 x 297.00 ミリ)"
$cases = @(
    @{ id = "margin_P_wide"; paper = $paperMargin; orient = "P"; w = 92.028; h = 62.445 },
    @{ id = "margin_L_wide"; paper = $paperMargin; orient = "L"; w = 92.028; h = 62.445 },
    @{ id = "full_P_wide";   paper = $paperFull;   orient = "P"; w = 92.028; h = 62.445 },
    @{ id = "full_L_wide";   paper = $paperFull;   orient = "L"; w = 92.028; h = 62.445 },
    @{ id = "margin_L_tall"; paper = $paperMargin; orient = "L"; w = 30.0;   h = 100.0 }
)
# 「有効な印刷領域」は小数2桁表示、SVG 内容は線幅（0.254mm）とクリップの丸めを含む
$EffectiveAreaTolMm = 0.02
$ContentBboxTolMm = 1.0

# 終了時保全: accoreconsole は _.QUIT を経ない終了でセッション値をレジストリへ永続化し得るため、
# 実行前スナップショット→全ケース終了後の復元検証（CadRegGuard）で利用者プロファイルを守る。
# 従来の Restore-AcadRegistryDefaults（trap 併用）は基本値 1 へ矯正するだけで、開始値が
# 基本値以外の環境ではガードの開始値復元と矛盾するため、包括ガードへ一本化した
. "$PSScriptRoot\CadRegGuard.ps1"

$failures = @()
New-Item -ItemType Directory -Force $OutRoot | Out-Null
# スナップショットは最初のCAD起動より前、復元は全ケースのCAD終了後に1回だけ（ループ全体を1つのtry/finallyで包む）
$regGuard = Get-CadRegSnapshot
$runError = $null
try {
    foreach ($c in $cases) {
        $work = Join-Path $OutRoot $c.id
        if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
        New-Item -ItemType Directory $work | Out-Null
        $seed = Join-Path $work "seed.dwt"
        Copy-Item $template $seed
        $dwg = Join-Path $work "case.dwg"
        $oraclePdf = Join-Path $work "oracle.pdf"
        $x1 = 50.0; $y1 = 40.0
        $x2 = [math]::Round($x1 + $c.w, 3); $y2 = [math]::Round($y1 + $c.h, 3)
        $p1 = "$x1,$y1"; $p2 = "$x2,$y2"

        # -PLOT の応答列は AutoCAD 2026 ja-JP 実機確定（window_contract_oracle.ps1 と同型）。
        # 方向はローカルキーワード英字（P/L）で応答する（「縦」等の全角語は黙って中断する。実測）
        $scr = Join-Path $work "run.scr"
        # FILEDIA は書かない（accoreconsole ではダイアログが出ず不要）。基本値は 1 であり、
        # _.QUIT を経ない異常終了で 0 が永続した場合も、終了時の Restore-CadRegState が開始値へ戻す
        $text = @"
(setvar "SECURELOAD" 0)
(command "_.PLINE" "$p1" "$x2,$y1" "$p2" "$x1,$y2" "_C")
(command "_.LINE" "$p1" "$p2" "")
(command "_.LINE" "$x1,$y2" "$x2,$y1" "")
_.SAVEAS
2018
$dwg
-PLOT
Yes

AutoCAD PDF (High Quality Print).pc3
$($c.paper)

$($c.orient)

Window
$p1
$p2
Fit
Center




$oraclePdf
Yes
Yes
NETLOAD
$plugin
PETADIAG
$p1
$p2
_.CLOSE
_Y
_.QUIT
"@
        [IO.File]::WriteAllText($scr, $text, $cp932)
        $stdout = Join-Path $work "stdout.log"
        $stderr = Join-Path $work "stderr.log"
        $p = Start-Process -FilePath $acad -ArgumentList @('/i', $seed, '/s', $scr, '/l', 'ja-JP') -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $p.Id -Force; $p.WaitForExit()
            $failures += "$($c.id): accoreconsole が ${TimeoutSeconds}秒で完了しない"
            continue
        }
        if ($p.ExitCode -ne 0) { $failures += "$($c.id): exit=$($p.ExitCode)"; continue }

        $lines = Get-Content $stdout -Encoding Unicode
        $effective = @($lines | Select-String '有効な印刷領域:\s*幅=\s*([0-9.]+)\s*高さ=\s*([0-9.]+)')
        $scaleLine = @($lines | Select-String '窓プロット:.*尺度=([0-9.]+)mm/単位、回転=(\d+)度')
        if ($effective.Count -lt 2 -or $scaleLine.Count -lt 1) {
            $failures += "$($c.id): 実効領域または尺度の診断行が取れない（stdout=$stdout）"
            continue
        }
        $s = [double]$scaleLine[0].Matches[0].Groups[1].Value
        $rot = [int]$scaleLine[0].Matches[0].Groups[2].Value
        # 用紙フレームの期待寸法: 回転が奇数四半なら窓の縦横が入れ替わって印字される
        $expW = if (($rot / 90) % 2 -eq 1) { $c.h * $s } else { $c.w * $s }
        $expH = if (($rot / 90) % 2 -eq 1) { $c.w * $s } else { $c.h * $s }
        foreach ($i in 0..1) {
            $src = if ($i -eq 0) { "-PLOTオラクル" } else { "PETA(PlotEngine)" }
            $aw = [double]$effective[$i].Matches[0].Groups[1].Value
            $ah = [double]$effective[$i].Matches[0].Groups[2].Value
            if ([math]::Abs($aw - $expW) -gt $EffectiveAreaTolMm -or [math]::Abs($ah - $expH) -gt $EffectiveAreaTolMm) {
                $failures += "$($c.id): $src の実効領域 ${aw}x${ah} が期待 $([math]::Round($expW,2))x$([math]::Round($expH,2)) と不一致（尺度=$s 回転=$rot）"
            }
        }

        $svg = @(Get-ChildItem $work -Filter *.svg | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
        if ($svg.Count -ne 1) { $failures += "$($c.id): 最終SVGが1つでない（$($svg.Count)）"; continue }
        $svgText = Get-Content $svg[0].FullName -Raw
        $vb = [regex]::Match($svgText, 'viewBox="([-0-9.]+) ([-0-9.]+) ([0-9.]+) ([0-9.]+)"')
        if (-not $vb.Success) { $failures += "$($c.id): viewBox が読めない"; continue }
        $vbX = [double]$vb.Groups[1].Value; $vbY = [double]$vb.Groups[2].Value
        $vbW = [double]$vb.Groups[3].Value; $vbH = [double]$vb.Groups[4].Value
        # 表示フレームの期待寸法: 内容は表示フレームで窓×尺度のまま（回転で入れ替わらない）
        if ([math]::Abs($vbW - $c.w * $s) -gt $EffectiveAreaTolMm -or [math]::Abs($vbH - $c.h * $s) -gt $EffectiveAreaTolMm) {
            $failures += "$($c.id): viewBox ${vbW}x${vbH} が 窓×尺度 $([math]::Round($c.w*$s,2))x$([math]::Round($c.h*$s,2)) と不一致"
        }
        # 内容 bbox（clipPath 定義を除く描画パスの座標全部）が viewBox の四辺まで届く
        $minX = [double]::MaxValue; $minY = [double]::MaxValue
        $maxX = [double]::MinValue; $maxY = [double]::MinValue
        $body = [regex]::Replace($svgText, '<defs>.*?</defs>', '', 'Singleline')
        foreach ($m in [regex]::Matches($body, 'd="([^"]+)"')) {
            foreach ($pt in [regex]::Matches($m.Groups[1].Value, '(-?[0-9.]+) (-?[0-9.]+)')) {
                $px = [double]$pt.Groups[1].Value; $py = [double]$pt.Groups[2].Value
                if ($px -lt $minX) { $minX = $px }; if ($px -gt $maxX) { $maxX = $px }
                if ($py -lt $minY) { $minY = $py }; if ($py -gt $maxY) { $maxY = $py }
            }
        }
        $edges = @(
            @{ n = "左"; d = $minX - $vbX }, @{ n = "上"; d = $minY - $vbY },
            @{ n = "右"; d = ($vbX + $vbW) - $maxX }, @{ n = "下"; d = ($vbY + $vbH) - $maxY })
        foreach ($e in $edges) {
            if ([math]::Abs($e.d) -gt $ContentBboxTolMm) {
                $failures += "$($c.id): 内容が viewBox の$($e.n)辺へ届かない（差 $([math]::Round($e.d,3))mm = 窓の縁が欠落）"
            }
        }
        "$($c.id) OK: 尺度=$s 回転=$rot 実効領域=$([math]::Round($expW,2))x$([math]::Round($expH,2)) viewBox=${vbW}x${vbH}"
    }
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

$residual = (Get-Process AcCoreConsole -ErrorAction SilentlyContinue | Measure-Object).Count
if ($residual -gt 0) { $failures += "AcCoreConsole が $residual 件残留" }
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "NG: $_" -ForegroundColor Red }
    throw "window_fit_scale_matrix: $($failures.Count) 件失敗"
}
"全ケース合格（成果物: $OutRoot）"
