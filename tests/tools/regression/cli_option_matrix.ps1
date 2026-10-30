# cadpeta.exe（CLI）のオプション結線の全因子検証と、他所産 PDF の忠実性検証。
#
# CAD 側の PETA/PETADATA は peta_option_matrix.ps1 が全因子で検証済みで、変換の中核
# （PDF→IR・変換段・EMF/SVG/PDF 書き出し）は単一実装を共有する。本ハーネスは共有されて
# いない層を検証する:
#   (1) CLI 引数 → 変換オプションへの結線（全因子）
#   (2) PNG 出力（CAD 側の経路に存在しない）
#   (3) 他所産 PDF（CLI/GUI の主用途）に対する忠実性 = 入力 PDF 自身の描画と
#       出力（SVG/EMF/PDF/PNG）の描画の照合（判定は compare_renders.py へ集約）
#
# セット:
#   Wiring  … keepBg(2)×色(4: なし/mono/gray/replace)×layer(2)×textOutline(2)×crop(2)×emfBg(2)
#             = 256 通り。出力は SVG+EMF+PDF+PNG 固定。ケース間ペア照合
#             （emfBg対のSVG一致とEMF差、layer対の描画数不変、keepBg対の除去件数整合）つき
#   Extras  … クリップボード既定動作/併用、--png-dpi、--diag、色×置換の相互作用
#   Errors  … 排他・依存・不明オプション等の非0終了と文言
#   Foreign … 他所産 PDF（合成フィクスチャ4・Word・Excel・Edge・実物図面）の既定変換と
#             入力レンダとの照合
#
# 使い方:
#   pwsh -File tests/tools/regression/cli_option_matrix.ps1 -Set Wiring
#   pwsh -File tests/tools/regression/cli_option_matrix.ps1 -Set Foreign -CorpusDir <Word等の生成先>
param(
    [Parameter(Mandatory)]
    [ValidateSet('Wiring', 'Extras', 'Errors', 'Foreign', 'All')][string]$Set,
    [string]$OutRoot = "$env:TEMP\CadPeta_cli_matrix",
    [string]$CorpusDir = "$env:TEMP\CadPeta_cli_corpus",
    [string]$Config = 'Debug',
    [int]$TimeoutSeconds = 60,
    [string]$CaseFilter = ''
)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing

$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$cli = "$repo\src\CadPeta.Cli\bin\$Config\net10.0-windows\cadpeta.exe"
$inputMain = "$repo\tests\assets\testcharts\TESTCHART2-on.pdf"   # OCG・透過・SMask あり（実測）
$pyProj = Join-Path $PSScriptRoot 'peta_option_matrix'
$compare = Join-Path $pyProj 'compare_renders.py'
$helpers = Join-Path $pyProj 'g8_helpers.py'
foreach ($f in @($cli, $inputMain, $compare, $helpers)) { if (-not (Test-Path $f)) { throw "前提がない: $f" } }

$outRootFull = [IO.Path]::GetFullPath($OutRoot).TrimEnd('\')
if ($outRootFull -eq [IO.Path]::GetPathRoot($outRootFull).TrimEnd('\')) { throw "OutRoot にドライブ直下は不可" }

$Dpi = 150            # PNG 既定出力の照合用（製品既定と同値）
$FidelityDpi = 300    # 忠実性判定のレンダ DPI。150 では対角1画素=0.24mm が shift 閾値 0.2mm を
                      # 超え、細線の AA 差で recall が僅差落ちする（CAD 側 G8 で実測済みの分解能問題）
# クロップ矩形は TrueType 文字行を含む領域（TESTCHART2 のページmm・左上原点。実測で確定）
$CropArgs = @('--crop', '60', '110', '180', '190', '--margin', '5')
$CropW = 130.0; $CropH = 90.0   # (180-60)+2*5 × (190-110)+2*5
$PageW = 420.1583; $PageH = 297.0389
$ReplFrom = '#FF0000'; $ReplTo = '#123456'
$BgHex = '#FF8040'
# 主入力の内容フラグ（実測: OCG あり・透過あり・ACI赤あり・背景矩形あり）。
# クロップ領域は文字行のみで透過50%列と赤列を含まない（試走で確認して固定）
$FullHasTransparency = $true
$CropHasTransparency = $false

function Invoke-Py([string]$label, [string]$script, [string[]]$pyArgs) {
    $so = Join-Path $script:caseDir "_py_$label.out"
    $se = Join-Path $script:caseDir "_py_$label.err"
    $argStr = (@('run', '--project', $pyProj, 'python', $script) + $pyArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p = Start-Process -FilePath 'uv' -ArgumentList $argStr `
        -RedirectStandardOutput $so -RedirectStandardError $se -PassThru -WindowStyle Hidden
    [void]$p.WaitForExit(120000)
    $json = $null
    $raw = if (Test-Path $so) { Get-Content $so -Raw } else { '' }
    try { $json = $raw | ConvertFrom-Json } catch { }
    @{ Exit = $p.ExitCode; Json = $json; Raw = $raw; Err = (Test-Path $se) ? (Get-Content $se -Raw) : '' }
}

# EMF を GDI でスーパーサンプリング描画する（peta_option_g8_verify.ps1 と同じ手口）
function Render-EmfPng([string]$emfPath, [string]$pngPath, [double]$wMm, [double]$hMm, [double]$renderDpi = 150) {
    $w = [int][math]::Round($wMm * $renderDpi / 25.4); $h = [int][math]::Round($hMm * $renderDpi / 25.4)
    $ss = 4
    $mf = [System.Drawing.Imaging.Metafile]::new($emfPath)
    $big = [System.Drawing.Bitmap]::new($w * $ss, $h * $ss)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.Clear([System.Drawing.Color]::White)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($mf, 0, 0, $big.Width, $big.Height)
    $g.Dispose()
    $small = [System.Drawing.Bitmap]::new($w, $h)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.DrawImage($big, 0, 0, $w, $h)
    $g2.Dispose(); $big.Dispose()
    $small.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose(); $mf.Dispose()
}

# SVG を headless ブラウザで描画する（時間上限＋PNG 完全性検査。g8 と同じ受け方）
$browser = @("$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
             "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
function Render-SvgPng([string]$svgPath, [string]$pngPath, [double]$wMm, [double]$hMm, [double]$renderDpi = 150) {
    if (-not $browser) { return $false }
    $cssPxPerMm = 96.0 / 25.4
    $winW = [int][math]::Round($wMm * $cssPxPerMm); $winH = [int][math]::Round($hMm * $cssPxPerMm)
    $scale = $renderDpi / 96.0
    $expectW = [int][math]::Round($winW * $scale); $expectH = [int][math]::Round($winH * $scale)
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if (Test-Path $pngPath) { Remove-Item $pngPath -Force }
        $args = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=FFFFFFFF',
            "--force-device-scale-factor=$scale", "--window-size=$winW,$winH", "--screenshot=$pngPath", ([Uri]$svgPath).AbsoluteUri)
        $p = Start-Process -FilePath $browser -ArgumentList (ConvertTo-NativeArgString $args) -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(90000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit() }
        if (Test-Path $pngPath) {
            $chk = Invoke-Py 'svgchk' $helpers @('png-check', '--in', $pngPath, '--width', "$expectW", '--height', "$expectH")
            if ($chk.Exit -eq 0) { return $true }
        }
    }
    return $false
}

. "$PSScriptRoot\..\common\test_helpers.ps1"

function Get-SvgFacts([string]$path) {
    $head = Get-Content $path -TotalCount 1
    $c = Get-Content $path -Raw
    $facts = @{ sha = (Get-FileHash $path -Algorithm SHA256).Hash }
    if ($head -match 'width="([^"]+)mm" height="([^"]+)mm" viewBox="([^"]+)"') {
        $facts.W = [double]$Matches[1]; $facts.H = [double]$Matches[2]; $facts.VB = $Matches[3]
    }
    $facts.hasLayerGroups = $c.Contains('inkscape:groupmode="layer"')
    $facts.hasText = $c.Contains('<text')
    $facts.drawCount = ([regex]::Matches($c, '<(path|text|image)[\s>]')).Count
    # 色集合。SvgWriter は rgb(R,G,B) 表記で書く（実測）ため #RRGGBB へ正規化して列挙する
    $facts.colors = @([regex]::Matches($c, '(?:fill|stroke)="rgb\((\d+),(\d+),(\d+)\)"') | ForEach-Object {
            '#{0:X2}{1:X2}{2:X2}' -f [int]$_.Groups[1].Value, [int]$_.Groups[2].Value, [int]$_.Groups[3].Value
        } | Sort-Object -Unique)
    $facts
}

# cadpeta.exe を1回実行する。戻り値: exit・stdout/stderr テキスト
function Invoke-Cli([string[]]$cliArgs) {
    $so = Join-Path $script:caseDir 'stdout.log'
    $se = Join-Path $script:caseDir 'stderr.log'
    $argStr = ($cliArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p = Start-Process -FilePath $cli -ArgumentList $argStr `
        -RedirectStandardOutput $so -RedirectStandardError $se -PassThru -WindowStyle Hidden
    $timedOut = -not $p.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit() }
    # cadpeta.exe のコンソール出力は CP932（実測。既定の UTF-8 読みでは文言照合が化けて空振りする）
    $cp932 = [Text.Encoding]::GetEncoding(932)
    @{ Exit = $p.ExitCode; TimedOut = $timedOut
       Out = (Test-Path $so) ? [IO.File]::ReadAllText($so, $cp932) : ''
       Err = (Test-Path $se) ? [IO.File]::ReadAllText($se, $cp932) : '' }
}

# Wiring 1ケースの実行と判定
function Invoke-WiringCase([hashtable]$sc) {
    $script:caseDir = Join-Path $setDir $sc.Id
    if (Test-Path $script:caseDir) { Remove-Item -LiteralPath $script:caseDir -Recurse -Force }
    New-Item -ItemType Directory $script:caseDir | Out-Null
    $svg = Join-Path $script:caseDir 'out.svg'; $emf = Join-Path $script:caseDir 'out.emf'
    $pdf = Join-Path $script:caseDir 'out.pdf'; $png = Join-Path $script:caseDir 'out.png'
    $args = @($inputMain, $svg, '--emf', $emf, '--pdf', $pdf, '--png', $png) + $sc.Extra
    $r = Invoke-Cli $args
    $checks = New-Object System.Collections.ArrayList
    Add-Check $checks 'exit0' ($r.Exit -eq 0 -and -not $r.TimedOut) "exit=$($r.Exit)"
    $result = @{ id = $sc.Id; axes = $sc.Axes; exit = $r.Exit; checks = $checks }
    if ($r.Exit -ne 0) { $result.pass = $false; return $result }

    foreach ($f in @($svg, "$svg.diag.json", $emf, $pdf, $png)) {
        Add-Check $checks "exists:$([IO.Path]::GetFileName($f))" (Test-Path $f)
    }
    if (-not (Test-Path $svg)) { $result.pass = $false; return $result }
    $facts = Get-SvgFacts $svg
    $result.svg = $facts
    $a = $sc.Axes

    # 寸法: クロップは (窓+2×余白)、無クロップはページ寸法
    $expW = $a.Crop ? $CropW : $PageW; $expH = $a.Crop ? $CropH : $PageH
    Add-Check $checks 'svgDims' ([math]::Abs($facts.W - $expW) -le 0.0005 -and [math]::Abs($facts.H - $expH) -le 0.0005) "実 $($facts.W)x$($facts.H) 期待 $expW x $expH"
    # レイヤ（入力に OCG あり）
    Add-Check $checks ($a.Layer ? 'layerGroups' : 'noLayerGroups') ($a.Layer ? $facts.hasLayerGroups : (-not $facts.hasLayerGroups))
    # 文字輪郭（クロップ矩形は文字行を含むよう選定済み）
    Add-Check $checks ($a.Outline ? 'noText' : 'hasText') ($a.Outline ? (-not $facts.hasText) : $facts.hasText)
    # 色モード
    switch ($a.Color) {
        'mono' {
            $bad = @($facts.colors | Where-Object { $_ -notin @('#000000', '#FFFFFF') })
            Add-Check $checks 'monoColors' ($bad.Count -eq 0) (($bad | Select-Object -First 5) -join ',')
        }
        'gray' {
            $bad = @($facts.colors | Where-Object { -not ($_.Substring(1, 2) -eq $_.Substring(3, 2) -and $_.Substring(3, 2) -eq $_.Substring(5, 2)) })
            Add-Check $checks 'grayColors' ($bad.Count -eq 0) (($bad | Select-Object -First 5) -join ',')
        }
        'replace' {
            Add-Check $checks 'replacedFromAbsent' ($facts.colors -notcontains $ReplFrom.ToUpperInvariant())
            if (-not $a.Crop) { Add-Check $checks 'replacedToPresent' ($facts.colors -contains $ReplTo.ToUpperInvariant()) }
        }
        'none' {
            if (-not $a.Crop) { Add-Check $checks 'redPreserved' ($facts.colors -contains $ReplFrom.ToUpperInvariant()) }
        }
    }
    # keepBg: TESTCHART2 系の PDF には除去対象の背景矩形が無い（実測。CLI 出力の「背景除去: 0件」）。
    # 本因子の実効（除去件数と描画数の増減）は Extras の背景矩形つきフィクスチャで確認し、
    # ここでは「背景なし入力では keep-bg が無操作」をペア照合（SVG 一致）で固定する
    $diag = Get-Content "$svg.diag.json" -Raw | ConvertFrom-Json
    $bgRemoved = @($diag | Where-Object { $_.type -like '*背景矩形の除去*' })
    if ($a.KeepBg) { Add-Check $checks 'bgKept' ($bgRemoved.Count -eq 0) }
    $result.bgRemovedCount = ($bgRemoved | Measure-Object -Sum count).Sum ?? 0
    # PNG: 画素寸法と非空白
    $expPxW = [int][math]::Round($expW * $Dpi / 25.4); $expPxH = [int][math]::Round($expH * $Dpi / 25.4)
    $chk = Invoke-Py 'pngchk' $helpers @('png-check', '--in', $png, '--width', "$expPxW", '--height', "$expPxH", '--tolerance', '2')
    Add-Check $checks 'pngDims' ($chk.Exit -eq 0) ($chk.Json.reason ?? '')
    $fg = Invoke-Py 'pngfg' $helpers @('fg-count', '--in', $png)
    Add-Check $checks 'pngNotBlank' ($fg.Exit -eq 0 -and $fg.Json.foregroundPx -gt 100) "前景 $($fg.Json.foregroundPx) px"
    # 出力 PDF のページ寸法
    $ps = Invoke-Py 'pdfsize' $helpers @('page-size', '--in', $pdf)
    Add-Check $checks 'pdfDims' ($ps.Exit -eq 0 -and [math]::Abs($ps.Json.wMm - $expW) -le 0.5 -and [math]::Abs($ps.Json.hMm - $expH) -le 0.5) "実 $($ps.Json.wMm)x$($ps.Json.hMm)"
    $result.emfSha = (Get-FileHash $emf -Algorithm SHA256).Hash
    $result.pass = -not ($checks | Where-Object { -not $_.ok })
    $result
}

# Foreign 1入力の実行と忠実性判定（既定変換＝色変換なし）
function Invoke-ForeignCase([string]$id, [string]$inputPdf) {
    $script:caseDir = Join-Path $setDir $id
    if (Test-Path $script:caseDir) { Remove-Item -LiteralPath $script:caseDir -Recurse -Force }
    New-Item -ItemType Directory $script:caseDir | Out-Null
    $svg = Join-Path $script:caseDir 'out.svg'; $emf = Join-Path $script:caseDir 'out.emf'
    $pdf = Join-Path $script:caseDir 'out.pdf'; $png = Join-Path $script:caseDir 'out.png'
    $r = Invoke-Cli @($inputPdf, $svg, '--emf', $emf, '--pdf', $pdf, '--png', $png)
    $checks = New-Object System.Collections.ArrayList
    Add-Check $checks 'exit0' ($r.Exit -eq 0 -and -not $r.TimedOut) "exit=$($r.Exit) err=$($r.Err)"
    $result = @{ id = $id; input = $inputPdf; exit = $r.Exit; checks = $checks }
    if ($r.Exit -ne 0) { $result.pass = $false; return $result }
    foreach ($f in @($svg, $emf, $pdf, $png)) { Add-Check $checks "exists:$([IO.Path]::GetFileName($f))" (Test-Path $f) }
    $facts = Get-SvgFacts $svg
    $result.svg = $facts

    # 忠実性: 入力 PDF のレンダを正解として、出力 SVG/EMF/PDF のレンダと照合する（300dpi）。
    # 入力が解決できないフォント（Type3 等。Chromium 印刷 PDF で実測）を含む場合、代替書体の
    # 字形差で recall が下がるのは診断つきの文書化済み縮退（対象は CAD 産ベクタ PDF）。
    # その場合は診断記録の実在を必須にした上で、recall の合格下限を回帰床 0.90 へ切り替える
    # （診断なしの劣化は従来どおり FAIL）
    $diagJ = (Test-Path "$svg.diag.json") ? (Get-Content "$svg.diag.json" -Raw | ConvertFrom-Json) : @()
    $fontDegraded = @($diagJ | Where-Object { $_.type -like '*フォント名を解決できず*' -or $_.type -like '*フォント代替*' }).Count -ge 1
    $result.fontDegraded = $fontDegraded
    $fd = $FidelityDpi
    $oracle = Join-Path $script:caseDir 'input_render.png'
    $ro = Invoke-Py 'oracle' $helpers @('render', '--in', $inputPdf, '--out', $oracle, '--dpi', "$fd")
    Add-Check $checks 'oracleRender' ($ro.Exit -eq 0)
    if ($ro.Exit -eq 0) {
        $wMm = $facts.W; $hMm = $facts.H
        $targets = @()
        $emfPng = Join-Path $script:caseDir 'emf_render.png'
        Render-EmfPng $emf $emfPng $wMm $hMm $fd
        $targets += @{ n = 'emf'; p = $emfPng }
        $pdfPng = Join-Path $script:caseDir 'pdf_render.png'
        $rp = Invoke-Py 'pdfrender' $helpers @('render', '--in', $pdf, '--out', $pdfPng, '--dpi', "$fd")
        if ($rp.Exit -eq 0) { $targets += @{ n = 'pdf'; p = $pdfPng } }
        $svgPng = Join-Path $script:caseDir 'svg_render.png'
        if (Render-SvgPng $svg $svgPng $wMm $hMm $fd) { $targets += @{ n = 'svg'; p = $svgPng } }
        else { Add-Check $checks 'svgRender' $false 'ブラウザ描画に失敗' }
        foreach ($t in $targets) {
            # 正解とターゲットを同一画素寸法へ正規化してから比較する
            $targetPx = ([string][int][math]::Round($wMm * $fd / 25.4)) + ',' + ([string][int][math]::Round($hMm * $fd / 25.4))
            $norm = Join-Path $script:caseDir "norm_$($t.n).png"
            $on = Invoke-Py "norm_$($t.n)" $helpers @('render', '--in', $t.p, '--out', $norm, '--dpi', "$fd", '--target', $targetPx)
            $oracleNorm = Join-Path $script:caseDir 'oracle_norm.png'
            $onO = Invoke-Py 'norm_oracle' $helpers @('render', '--in', $oracle, '--out', $oracleNorm, '--dpi', "$fd", '--target', $targetPx)
            $dilate = [math]::Max(1, [math]::Round(0.5 * $fd / 25.4))
            $search = [math]::Max(8, [math]::Round(1.35 * $fd / 25.4))
            $cmpArgs = @($oracleNorm, $norm, '--dpi', "$fd", '--dilate', "$dilate", '--search', "$search")
            if ($fontDegraded) { $cmpArgs += @('--min-recall', '0.90', '--min-iou', '0.88') }
            $cmp = Invoke-Py "cmp_$($t.n)" $compare $cmpArgs
            $m = $cmp.Json
            Add-Check $checks "fidelity:$($t.n)" ($cmp.Exit -eq 0) `
                ("shift=$([math]::Round([double]($m.shift.magnitude_mm ?? -1),3))mm iou=$([math]::Round([double]($m.overall.iou ?? 0),4)) recall=$([math]::Round([double]($m.overall.recall ?? 0),4)) prec=$([math]::Round([double]($m.overall.precision ?? 0),4))" +
                 ($fontDegraded ? '（未解決フォントの診断つき縮退＝回帰床で判定）' : '') + ($m.error ? " err=$($m.error)" : ''))
        }
    }
    $result.pass = -not ($checks | Where-Object { -not $_.ok })
    $result
}

# ---------------------------------------------------------------- セット実行
$results = New-Object System.Collections.ArrayList
$failures = New-Object System.Collections.ArrayList
$setsToRun = if ($Set -eq 'All') { @('Wiring', 'Extras', 'Errors', 'Foreign') } else { @($Set) }

foreach ($currentSet in $setsToRun) {
    $setDir = Join-Path $outRootFull $currentSet
    New-Item -ItemType Directory -Force $setDir | Out-Null
    "===== SET $currentSet ====="
    switch ($currentSet) {
        'Wiring' {
            $cases = @()
            foreach ($kb in 0, 1) { foreach ($col in 'none', 'mono', 'gray', 'replace') {
                foreach ($ly in 0, 1) { foreach ($to in 0, 1) { foreach ($cr in 0, 1) { foreach ($bg in 0, 1) {
                    $extra = @()
                    if ($kb) { $extra += '--keep-bg' }
                    if ($col -eq 'mono') { $extra += '--mono' }
                    if ($col -eq 'gray') { $extra += '--gray' }
                    if ($col -eq 'replace') { $extra += @('--replace-color', $ReplFrom, $ReplTo) }
                    if ($ly) { $extra += '--layer-groups' }
                    if ($to) { $extra += '--text-outline' }
                    if ($cr) { $extra += $CropArgs }
                    if ($bg) { $extra += @('--emf-bg', $BgHex) }
                    $cases += @{ Id = "w-kb$kb-$col-l$ly-t$to-c$cr-b$bg"
                        Axes = @{ KeepBg = [bool]$kb; Color = $col; Layer = [bool]$ly; Outline = [bool]$to; Crop = [bool]$cr; EmfBg = [bool]$bg }
                        Extra = $extra }
                } } } } } }
            if ($CaseFilter) { $cases = @($cases | Where-Object { $_.Id -match $CaseFilter }) }
            $i = 0
            foreach ($sc in $cases) {
                $i++
                $r = Invoke-WiringCase $sc
                [void]$results.Add($r)
                '{0,4}/{1} {2,-28} {3}' -f $i, $cases.Count, $sc.Id, ($r.pass ? 'PASS' : 'FAIL')
                if (-not $r.pass) {
                    foreach ($c in ($r.checks | Where-Object { -not $_.ok })) { "       NG $($c.name): $($c.detail)"; [void]$failures.Add("$($sc.Id): $($c.name)") }
                }
            }
            # ペア照合
            $wire = @($results | Where-Object { $_.id -like 'w-*' })
            foreach ($r in ($wire | Where-Object { $_.axes.EmfBg })) {
                $mate = $wire | Where-Object { -not $_.axes.EmfBg -and $_.axes.KeepBg -eq $r.axes.KeepBg -and $_.axes.Color -eq $r.axes.Color -and $_.axes.Layer -eq $r.axes.Layer -and $_.axes.Outline -eq $r.axes.Outline -and $_.axes.Crop -eq $r.axes.Crop } | Select-Object -First 1
                if ($mate -and $r.svg -and $mate.svg) {
                    if ($r.svg.sha -ne $mate.svg.sha) { [void]$failures.Add("Bpair-svg:$($r.id)") ; "CROSS NG Bpair-svg:$($r.id)" }
                    $expectDiff = $r.axes.Crop ? $CropHasTransparency : $FullHasTransparency
                    if ($expectDiff -and $r.emfSha -eq $mate.emfSha) { [void]$failures.Add("Bpair-emf:$($r.id)"); "CROSS NG Bpair-emf:$($r.id)（透過ありでEMF同一）" }
                }
            }
            foreach ($r in ($wire | Where-Object { $_.axes.KeepBg -and $_.svg })) {
                $mate = $wire | Where-Object { -not $_.axes.KeepBg -and $_.axes.Color -eq $r.axes.Color -and $_.axes.Layer -eq $r.axes.Layer -and $_.axes.Outline -eq $r.axes.Outline -and $_.axes.Crop -eq $r.axes.Crop -and $_.axes.EmfBg -eq $r.axes.EmfBg } | Select-Object -First 1
                if ($mate -and $mate.svg -and $r.svg.sha -ne $mate.svg.sha) { [void]$failures.Add("KBpair:$($r.id)"); "CROSS NG KeepBgペア（背景なし入力で出力が変わった）:$($r.id)" }
            }
            foreach ($r in ($wire | Where-Object { $_.axes.Layer -and $_.svg })) {
                $mate = $wire | Where-Object { -not $_.axes.Layer -and $_.axes.KeepBg -eq $r.axes.KeepBg -and $_.axes.Color -eq $r.axes.Color -and $_.axes.Outline -eq $r.axes.Outline -and $_.axes.Crop -eq $r.axes.Crop -and $_.axes.EmfBg -eq $r.axes.EmfBg } | Select-Object -First 1
                if ($mate -and $mate.svg -and $r.svg.drawCount -ne $mate.svg.drawCount) { [void]$failures.Add("Lpair:$($r.id)"); "CROSS NG Lpair-drawCount:$($r.id) $($r.svg.drawCount) vs $($mate.svg.drawCount)" }
            }
        }
        'Extras' {
            # クリップボード既定動作（SVG 無指定）: 一時 EMF のパス案内と登録
            $script:caseDir = Join-Path $setDir 'clipboard-default'
            New-Item -ItemType Directory -Force $script:caseDir | Out-Null
            $r = Invoke-Cli @($inputMain)
            $tempEmf = Join-Path $env:TEMP 'cadpeta\TESTCHART2-on.emf'
            $ok = ($r.Exit -eq 0) -and (Test-Path $tempEmf) -and ($r.Out -match 'クリップボード登録')
            "clipboard-default: " + ($ok ? 'PASS' : 'FAIL')
            if (-not $ok) { [void]$failures.Add('clipboard-default') }
            [void]$results.Add(@{ id = 'clipboard-default'; pass = $ok })
            # --png-dpi 300: 既定150の2倍の画素寸法
            $script:caseDir = Join-Path $setDir 'png-dpi300'
            New-Item -ItemType Directory -Force $script:caseDir | Out-Null
            $svg3 = Join-Path $script:caseDir 'out.svg'; $png3 = Join-Path $script:caseDir 'out.png'
            $r = Invoke-Cli @($inputMain, $svg3, '--png', $png3, '--png-dpi', '300')
            $expW3 = [int][math]::Round($PageW * 300 / 25.4); $expH3 = [int][math]::Round($PageH * 300 / 25.4)
            $chk = Invoke-Py 'png300' $helpers @('png-check', '--in', $png3, '--width', "$expW3", '--height', "$expH3", '--tolerance', '3')
            $ok = ($r.Exit -eq 0) -and ($chk.Exit -eq 0)
            "png-dpi300: " + ($ok ? 'PASS' : 'FAIL') + " " + ($chk.Json.reason ?? '')
            if (-not $ok) { [void]$failures.Add('png-dpi300') }
            [void]$results.Add(@{ id = 'png-dpi300'; pass = $ok })
            # --diag: 段階ダンプの実在
            $script:caseDir = Join-Path $setDir 'diag'
            New-Item -ItemType Directory -Force $script:caseDir | Out-Null
            $svg4 = Join-Path $script:caseDir 'out.svg'; $dd = Join-Path $script:caseDir 'stages'
            $r = Invoke-Cli @($inputMain, $svg4, '--text-outline', '--layer-groups', '--diag', $dd)
            $stages = @(Get-ChildItem $dd -Filter '*.stage*.svg' -ErrorAction SilentlyContinue)
            $names = ($stages | ForEach-Object Name) -join ';'
            $ok = ($r.Exit -eq 0) -and ($stages.Count -ge 3) -and ($names -match 'text-outlined') -and ($names -match 'layer-grouped')
            "diag: " + ($ok ? 'PASS' : 'FAIL') + "（stage $($stages.Count) 件）"
            if (-not $ok) { [void]$failures.Add('diag') }
            [void]$results.Add(@{ id = 'diag'; pass = $ok })
            # keepBg の実効: 背景矩形つきフィクスチャ（全面白矩形＋図形）で、既定は除去1件・
            # --keep-bg は除去0件かつ描画数が1多いことを確認する
            $bgFixture = "$repo\tests\assets\pdf-fixtures\bg_rect_fixture.pdf"
            if (Test-Path $bgFixture) {
                $facts = @{}
                foreach ($kb in 0, 1) {
                    $script:caseDir = Join-Path $setDir "keepbg-probe-kb$kb"
                    New-Item -ItemType Directory -Force $script:caseDir | Out-Null
                    $svgK = Join-Path $script:caseDir 'out.svg'
                    $extra = $kb ? @('--keep-bg') : @()
                    $r = Invoke-Cli (@($bgFixture, $svgK) + $extra)
                    $diagK = Get-Content "$svgK.diag.json" -Raw | ConvertFrom-Json
                    $removed = (@($diagK | Where-Object { $_.type -like '*背景矩形の除去*' }) | Measure-Object -Sum count).Sum ?? 0
                    $facts[$kb] = @{ exit = $r.Exit; removed = $removed; svg = (Get-SvgFacts $svgK) }
                }
                $ok = ($facts[0].exit -eq 0) -and ($facts[1].exit -eq 0) -and
                      ($facts[0].removed -ge 1) -and ($facts[1].removed -eq 0) -and
                      ($facts[1].svg.drawCount -eq $facts[0].svg.drawCount + $facts[0].removed)
                "keepbg-probe: " + ($ok ? 'PASS' : "FAIL（既定=除去$($facts[0].removed)件 keep=$($facts[1].removed)件 描画 $($facts[0].svg.drawCount)/$($facts[1].svg.drawCount)）")
                if (-not $ok) { [void]$failures.Add('keepbg-probe') }
                [void]$results.Add(@{ id = 'keepbg-probe'; pass = $ok })
            }
            else { "keepbg-probe: FAIL（フィクスチャがない: $bgFixture）"; [void]$failures.Add('keepbg-probe:missing') }
            # 置換×mono / 置換×gray の相互作用（置換が先、モードが後＝文書どおり）
            foreach ($mode in 'mono', 'gray') {
                $script:caseDir = Join-Path $setDir "replace-$mode"
                New-Item -ItemType Directory -Force $script:caseDir | Out-Null
                $svg5 = Join-Path $script:caseDir 'out.svg'
                $r = Invoke-Cli @($inputMain, $svg5, '--replace-color', $ReplFrom, $ReplTo, "--$mode")
                $facts = Get-SvgFacts $svg5
                $bad = if ($mode -eq 'mono') { @($facts.colors | Where-Object { $_ -notin @('#000000', '#FFFFFF') }) }
                       else { @($facts.colors | Where-Object { -not ($_.Substring(1, 2) -eq $_.Substring(3, 2) -and $_.Substring(3, 2) -eq $_.Substring(5, 2)) }) }
                $ok = ($r.Exit -eq 0) -and ($bad.Count -eq 0) -and ($facts.colors -notcontains $ReplTo.ToUpperInvariant())
                "replace-${mode}: " + ($ok ? 'PASS' : 'FAIL')
                if (-not $ok) { [void]$failures.Add("replace-$mode") }
                [void]$results.Add(@{ id = "replace-$mode"; pass = $ok })
            }
        }
        'Errors' {
            $errCases = @(
                @{ Id = 'e-mono-gray';    Args = @($inputMain, 'x.svg', '--mono', '--gray');        Msg = '排他' }
                @{ Id = 'e-margin-alone'; Args = @($inputMain, 'x.svg', '--margin', '5');           Msg = '--margin は --crop と併用' }
                @{ Id = 'e-pngdpi-alone'; Args = @($inputMain, 'x.svg', '--png-dpi', '300');        Msg = '--png-dpi は --png と併用' }
                @{ Id = 'e-emfbg-alone';  Args = @($inputMain, 'x.svg', '--emf-bg', '#102030');     Msg = '--emf-bg は' }
                @{ Id = 'e-unknown';      Args = @($inputMain, 'x.svg', '--nosuch');                Msg = '不明なオプション' }
                @{ Id = 'e-missing-input'; Args = @("$outRootFull\no_such.pdf");                    Msg = '入力 PDF が存在しない' }
                @{ Id = 'e-badcolor';     Args = @($inputMain, 'x.svg', '--replace-color', 'red', '#000000'); Msg = '' }
            )
            foreach ($e in $errCases) {
                $script:caseDir = Join-Path $setDir $e.Id
                New-Item -ItemType Directory -Force $script:caseDir | Out-Null
                $r = Invoke-Cli $e.Args
                $ok = ($r.Exit -ne 0) -and (($e.Msg -eq '') -or (($r.Err + $r.Out) -match [regex]::Escape($e.Msg)))
                "$($e.Id): " + ($ok ? 'PASS' : "FAIL (exit=$($r.Exit))")
                if (-not $ok) { [void]$failures.Add($e.Id) }
                [void]$results.Add(@{ id = $e.Id; pass = $ok; exit = $r.Exit })
            }
        }
        'Foreign' {
            $inputs = [ordered]@{
                'fx-ccitt'     = "$repo\tests\assets\pdf-fixtures\ccitt_g4_indexed1bit.pdf"
                'fx-interleave' = "$repo\tests\assets\pdf-fixtures\interleave_ptx.pdf"
                'fx-form-noimg' = "$repo\tests\assets\pdf-fixtures\subtype_form_noimg.pdf"
                'fx-form-img'  = "$repo\tests\assets\pdf-fixtures\subtype_form_withimg.pdf"
                'real-drawing' = "$repo\tests\assets\testcharts\PDF出力20260706-01.pdf"
                'word'         = (Join-Path $CorpusDir 'word_sample.pdf')
                'excel'        = (Join-Path $CorpusDir 'excel_sample.pdf')
                'edge'         = (Join-Path $CorpusDir 'edge_sample.pdf')
            }
            foreach ($k in $inputs.Keys) {
                if (-not (Test-Path $inputs[$k])) { "SKIP→FAIL $k（入力がない: $($inputs[$k])）"; [void]$failures.Add("$k:missing"); continue }
                $r = Invoke-ForeignCase $k $inputs[$k]
                [void]$results.Add($r)
                "$k : " + ($r.pass ? 'PASS' : 'FAIL')
                if (-not $r.pass) {
                    foreach ($c in ($r.checks | Where-Object { -not $_.ok })) { "       NG $($c.name): $($c.detail)"; [void]$failures.Add("$k : $($c.name)") }
                }
            }
        }
    }
}

@{ finishedAt = (Get-Date -Format 'o'); failures = @($failures); cases = @($results) } |
    ConvertTo-Json -Depth 8 | Out-File (Join-Path $outRootFull 'cli_results.json') -Encoding utf8
"=== CLI 検証: 失敗 $($failures.Count) 件 → $(Join-Path $outRootFull 'cli_results.json') ==="
if ($failures.Count -gt 0) { exit 1 }
exit 0
