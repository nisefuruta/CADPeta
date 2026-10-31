# PETA E2E 総点検の「三点検証」（独立オラクル・可視再現・目視）を駆動する。
#
# 同一 PETA 実行の PDF と、その PDF 由来の EMF/SVG を比べるだけでは範囲・選択の誤りを
# 検出できない（3つとも同じ実装のバグを共有し得る）。本スクリプトは PETA と独立に
# CAD ネイティブ -PLOT で正解 PDF を作り、PETA の PDF／EMF／SVG のレンダと機械比較する。
# 判定は tests/tools/regression/peta_option_matrix/compare_renders.py へ集約し、
# 独自の画素判定はしない。目視判定用のコンタクトシートとチェックリスト空欄も出力する。
#
# 対象は代表8件（peta_option_matrix.ps1 が生成した caseid を固定で参照する）:
#   1 peta-pw-o0m0l0t0f0b0  Paper窓基準
#   2 peta-pw-o0m1l0t0f0b0  Paper窓M5（正解は100x70の四辺へ5mm白を足して110x80と比較）
#   3 peta-pw-o0m0l1t0f0b0  Paper窓L（レイヤ並べ替え。固定ROIのみで比較）
#   4 peta-pw-o0m0l0t1f0b0  Paper窓T（文字アウトライン化）
#   5 peta-mw-o0m0l0t0f0b1  Model窓B（EMF合成下地色。Bモードで期待RGBを算出して比較。
#                           レイアウトは透過フラグオフで B の効果が出ないためモデルで判定）
#   6 peta-ma-o0m0l0t0f0b0  Model全体（範囲=図形範囲。EXTMIN/EXTMAXを独立に読み戻す）
#   7 peta-mw-o0m0l0t0f0b0  Model窓
#   8 peta-pw-o1m0l0t0f0b0  Paper O窓（選択矩形1個だけの oracle DWG を entmake で作る）
#
# 前提資産（流用し、独自の画素判定は書かない）:
#   - peta_option_matrix/compare_renders.py … 横断比較器（Aモード=構造比較、Bモード=ROI色判定）
#   - window_contract/window_contract_oracle.ps1 … accoreconsole の -PLOT 応答列（実機確定済み）。
#     Model窓ケースは -Case AsIs で直接流用する
#   - peta_window_rotation_compat.ps1 … EMF を GDI(System.Drawing) で固定DPI描画する手口
#   - pdf-emf/render_crop.py … 全ブリードA3・1:1の正解PDFから図面mm窓を切り出す換算式
#   - peta_option_matrix.ps1 … 8件の caseid・PaperRectLisp(60,50-140,100)・成果物のディレクトリ構造
#     (<CampaignDir>\<Ver>-<Set>\<caseid>\ に pdf/svg/emf/stdout.log、親に scenarios.json)
#
# 注意（実機未検証・初回実行で崩れたら応答列を採り直すこと）:
#   -PLOT の応答列は window_contract_oracle.ps1 の実機確定済み構造をそのまま流用しているが、
#   本スクリプトは新たに次の代入を加えている（window_contract_oracle.ps1 自体は変更しない）:
#     ・Paper窓/Paper O窓: Window+p1+p2 のブロックはそのまま。スケール応答を "Fit" ではなく
#       "1:1" にしている（PETA_A3 レイアウトのページ設定は既に1:1のため）
#     ・Model全体: Window+p1+p2 の3行を "Extents" の1行に差し替えている（プロンプト行数が
#       2行減るだけで、後続の応答は変更していない）
#   accoreconsole は無効入力でもエラーを出さずに黙って中断する（window_contract_oracle.ps1 の
#   ヘッダ参照）。stdout末尾に格納値読み戻しが出ない・PDFが生成されない場合は応答列がずれている。
#
# 使い方:
#   pwsh -File tests/tools/regression/peta_option_g8_verify.ps1 `
#       -CampaignDir <peta_option_matrix.ps1 の campaignDir> -Ver 2026 `
#       -RoiConfig tests/tools/regression/peta_option_matrix_g8_roi.json
#
# 終了コード: 0=全ケースPASS、1=1件でもFAILまたは未実施（SKIP）がある。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$CampaignDir,
    [ValidateSet('2026', '2027')][string]$Ver = '2026',
    [string]$Config = 'Debug',
    [Parameter(Mandatory)][string]$RoiConfig,
    [string]$OutDir = '',
    [int]$TimeoutSeconds = 300,
    [int]$PythonToolTimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'peta_option_g8_verify.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

# ---------------------------------------------------------------- 前提の確認
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$matrixDir = Join-Path $PSScriptRoot 'peta_option_matrix'
$compareScript = Join-Path $matrixDir 'compare_renders.py'
$helperScript = Join-Path $matrixDir 'g8_helpers.py'
# Model窓オラクルは window_contract_oracle.ps1（座標契約の読み戻し専用・用紙サイズ未指定で
# ANSI A になる）に頼らず、New-PlotScrLines（用紙サイズ明示済み）を自前で使う。そのため
# window_contract_oracle.ps1／WindowContractProbe.dll はこのスクリプトの前提資産ではない
foreach ($f in @($compareScript, $helperScript)) {
    if (-not (Test-Path $f -PathType Leaf)) { throw "前提資産がない: $f" }
}
if (-not (Test-Path $CampaignDir -PathType Container)) { throw "CampaignDir がない: $CampaignDir" }
if (-not $OutDir) { $OutDir = Join-Path $CampaignDir 'g8' }

if (-not (Test-Path $RoiConfig -PathType Leaf)) {
    throw "ROI未確定: -RoiConfig で指定したファイルがない: $RoiConfig（黙って全面比較へ落とさない）"
}
$roiConfigObj = Get-Content $RoiConfig -Raw | ConvertFrom-Json
if (-not $roiConfigObj.PSObject.Properties['layerSafeRois'] -or -not $roiConfigObj.PSObject.Properties['bChecks']) {
    throw "ROI未確定: RoiConfig に layerSafeRois と bChecks の両方が必要: $RoiConfig"
}
$layerSafeRois = @($roiConfigObj.layerSafeRois | ForEach-Object { ($_ -join ',') })
if ($layerSafeRois.Count -eq 0) { throw "ROI未確定: layerSafeRois が空: $RoiConfig" }
$bChecks = @($roiConfigObj.bChecks)
if ($bChecks.Count -eq 0) { throw "ROI未確定: bChecks が空: $RoiConfig" }
foreach ($bc in $bChecks) {
    if (-not $bc.PSObject.Properties['roi'] -or -not $bc.PSObject.Properties['baseColor']) {
        throw "ROI未確定: bChecks の各要素に roi と baseColor が必要: $RoiConfig"
    }
}

$acc = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
if (-not (Test-Path $acc)) { throw "accoreconsole がない: $acc" }
$dwgLayout = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg"
$dwgModel = "$repo\tests\assets\testcharts\TESTCHART2.dwg"
foreach ($f in @($dwgLayout, $dwgModel)) { if (-not (Test-Path $f)) { throw "入力図面がない: $f" } }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$oracleRoot = Join-Path $OutDir 'oracle'
New-Item -ItemType Directory -Force $oracleRoot | Out-Null

# 判定 DPI。150dpi は1画素=0.169mm で、対角1画素ずれ=0.239mm が shift 閾値 0.2mm を超え、
# 位置合わせの量子化だけで偽の不一致になる（実測: Model窓の pdf が shift=0.239mm で境界落ち）。
# 300dpi なら対角1画素=0.12mm となり 0.2mm 閾値が量子化限界の上で意味を持つ
$Dpi = 300
$PaperWinP1 = '50,40'
$PaperWinP2 = '150,110'
# compare_renders.py の既定 --dilate 1 は同一レンダラ内の丸め差を想定した値。本ハーネストは
# PDFium・GDI(EMF)・Chromium(SVG) という特性の異なる3レンダラを跨いで比較するため、輪郭の
# アンチエイリアス幅そのものが数px単位でずれる（実測: O窓や寸法線のような細線・小さいROIで
# dilate=1だとrecall/precisionが僅差でしきい値未満になる。目視では内容は一致している。
# dilate=0.5mm相当で解消し、他ケースを緩めすぎて欠落を見逃す副作用も実測範囲では出ていない）。
# 許容は mm で固定し DPI に応じて画素へ換算する（画素固定だと DPI 変更で物理許容が変わり、
# 150dpi で通るケースが 300dpi で落ちる、を実測）。シフト探索窓も同じ理由で mm 基準（1.35mm）
$CmpDilatePx = [math]::Max(1, [math]::Round(0.5 * $Dpi / 25.4))
$CmpSearchPx = [math]::Max(8, [math]::Round(1.35 * $Dpi / 25.4))
$CmpDilateArgs = @('--dilate', "$CmpDilatePx", '--search', "$CmpSearchPx")

# ---------------------------------------------------------------- Start-Process 引数の組立
# Start-Process -ArgumentList は配列要素を空白区切りで単純連結するだけで自動引用しない
# （実測: "hello world" が2引数に割れる）。CampaignDir/OutDir 等の利用者指定パスは空白を
# 含み得るため、要素ごとに手で引用してから単一文字列として渡す
. "$PSScriptRoot\..\common\test_helpers.ps1"

# ---------------------------------------------------------------- ブラウザ探索（SVG比較用）
function Find-HeadlessBrowser {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe')
        (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe')
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}
$browserExe = Find-HeadlessBrowser
if (-not $browserExe) { "SVG比較: Chromium系ブラウザ(msedge.exe/chrome.exe)が見つからないためスキップする（未実施として記録する）" }

# ---------------------------------------------------------------- uv 経由の Python 呼び出し
$uvCallCount = 0
function Invoke-PyTool([string]$Label, [string]$ScriptPath, [string[]]$PyArgs) {
    $script:uvCallCount++
    $stdoutPath = Join-Path $OutDir ("_uv_{0:d4}_{1}.stdout.log" -f $script:uvCallCount, $Label)
    $stderrPath = Join-Path $OutDir ("_uv_{0:d4}_{1}.stderr.log" -f $script:uvCallCount, $Label)
    $projectArg = [IO.Path]::GetDirectoryName($ScriptPath)
    $argList = @('run', '--project', $projectArg, 'python', $ScriptPath) + $PyArgs
    $p = $null
    try {
        $p = Start-Process -FilePath 'uv' -ArgumentList (ConvertTo-NativeArgString $argList) `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru -WindowStyle Hidden
        $timedOut = -not $p.WaitForExit($PythonToolTimeoutSeconds * 1000)
        if ($timedOut) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
        }
        $text = if (Test-Path $stdoutPath) { (Get-Content $stdoutPath -Raw) } else { '' }
        $errText = if (Test-Path $stderrPath) { (Get-Content $stderrPath -Raw) } else { '' }
        if ($timedOut) {
            return @{ Exit = 2; Json = $null; Raw = $text;
                Err = "[$Label] uvが $PythonToolTimeoutSeconds 秒でタイムアウトした（PID=$($p.Id)）`n$errText";
                Label = $Label }
        }
        $json = $null
        if ($text) { try { $json = $text | ConvertFrom-Json } catch { } }
        @{ Exit = $p.ExitCode; Json = $json; Raw = $text; Err = $errText; Label = $Label }
    }
    finally {
        if ($null -ne $p) { $p.Dispose() }
    }
}
function Invoke-PyToolChecked([string]$Label, [string]$ScriptPath, [string[]]$PyArgs) {
    $r = Invoke-PyTool $Label $ScriptPath $PyArgs
    if ($r.Exit -ne 0) { throw "[$Label] 実行できない (exit=$($r.Exit)): $($r.Err)`n$($r.Raw)" }
    $r
}

# compare_renders.py の exit=2（実行できない）の理由は stderr でなく stdout の JSON "error" に載る
# （欠陥3対応: 正解側が空白のときの判定不能ガード等）。両方を見て空文字を返さないようにする
function Get-CmpSkipReason($Cmp) {
    if ($Cmp.Json -and $Cmp.Json.error) { return [string]$Cmp.Json.error }
    if ($Cmp.Err) { return [string]$Cmp.Err }
    return '(詳細不明)'
}

# ---------------------------------------------------------------- PETA 側成果物の発見
# キャンペーン直下には Trial・PetaEdge 等、同じ caseid を含み得る他セットも並ぶ（実測: 2026-Trial と
# 2026-PetaFull の双方に同一 caseid が存在し「複数一致」例外になった）。総点検の正本は常に
# <Ver>-PetaFull なので、そこだけを探す（ワイルドカードで他セットへ広げない）。
function Find-CaseDir([string]$CaseId) {
    $setDir = Join-Path $CampaignDir "$Ver-PetaFull"
    if (-not (Test-Path $setDir -PathType Container)) { throw "PetaFullセットがない: $setDir" }
    $p = Join-Path $setDir $CaseId
    if (-not (Test-Path $p -PathType Container)) { throw "PETA ケースが見つからない: $CaseId（$setDir 配下）" }
    $p
}

function Get-ScenarioEntry([string]$CaseDir, [string]$CaseId) {
    $scenariosPath = Join-Path (Split-Path -Parent $CaseDir) 'scenarios.json'
    if (-not (Test-Path $scenariosPath)) { throw "scenarios.json がない: $scenariosPath" }
    $all = Get-Content $scenariosPath -Raw | ConvertFrom-Json
    $entry = $all | Where-Object { $_.id -eq $CaseId } | Select-Object -First 1
    if (-not $entry) { throw "scenarios.json に $CaseId がない: $scenariosPath" }
    $entry
}

# R=W のシナリオは cmdLines の末尾2要素が窓の P1/P2（New-PetaCmdLines の実装どおり）
function Get-ScenarioWindowPoints($Scenario) {
    $lines = @($Scenario.cmdLines)
    if ($lines.Count -lt 2) { throw "cmdLines が短すぎて窓点を取れない: $($Scenario.id)" }
    @{ P1 = [string]$lines[-2]; P2 = [string]$lines[-1] }
}

function Get-CaseArtifacts([string]$CaseDir) {
    $pdf = @(Get-ChildItem $CaseDir -Filter '*.pdf' -File)
    $svg = @(Get-ChildItem $CaseDir -Filter '*.svg' -File | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
    $emf = @(Get-ChildItem $CaseDir -Filter '*.emf' -File)
    if ($pdf.Count -ne 1 -or $svg.Count -ne 1 -or $emf.Count -ne 1) {
        throw "成果物が過不足（pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count)）: $CaseDir"
    }
    @{ Pdf = $pdf[0].FullName; Svg = $svg[0].FullName; Emf = $emf[0].FullName }
}

function Get-SvgDimsMm([string]$SvgPath) {
    $head = Get-Content $SvgPath -TotalCount 1 -Encoding UTF8
    if ($head -notmatch 'width="([^"]+)mm" height="([^"]+)mm"') { throw "SVG の width/height(mm) が読めない: $SvgPath" }
    @{ W = [double]$Matches[1]; H = [double]$Matches[2] }
}

# PETA 自身の SVG ヘッダの viewBox は「ページ左上原点・Y下向き・mm」の内容ボックスをそのまま表す
# （実測済みの事実。レイアウトの窓の値は peta_a3_window_expect.ps1 の表、Model窓="0 33 420 231"、
# Model全体="0 0 420.1583 297.0389"）。PETA の PDF は常に用紙全面（A3フルブリード）で、この矩形が
# 内容の載る位置を直接示す一次情報になる。窓プロットの選択点(P1/P2、紙面空間座標)とは別の
# 座標系であり、オフセット換算（render_crop.py の OFF_X/OFF_Y=10,8.5 等）を挟まない。
function Get-SvgViewBoxMm([string]$SvgPath) {
    $head = Get-Content $SvgPath -TotalCount 1 -Encoding UTF8
    if ($head -notmatch 'viewBox="([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)"') { throw "SVG の viewBox が読めない: $SvgPath" }
    $x = [double]$Matches[1]; $y = [double]$Matches[2]; $w = [double]$Matches[3]; $h = [double]$Matches[4]
    @{ X0 = $x; Y0 = $y; X1 = $x + $w; Y1 = $y + $h; W = $w; H = $h }
}

# PDF のページ寸法(mm)。モデル全体ケースの「PETA SVG 寸法 == オラクル用紙寸法」照合に使う
function Get-PdfPageSizeMm([string]$PdfPath) {
    $r = Invoke-PyToolChecked 'page_size' $helperScript @('page-size', '--in', $PdfPath)
    @{ W = [double]$r.Json.wMm; H = [double]$r.Json.hMm }
}

function ConvertTo-PointPair([string]$P1, [string]$P2) {
    $a = $P1 -split ','; $b = $P2 -split ','
    @{ X0 = [double]$a[0]; Y0 = [double]$a[1]; X1 = [double]$b[0]; Y1 = [double]$b[1]
        W = [double]$b[0] - [double]$a[0]; H = [double]$b[1] - [double]$a[1] }
}

# ---------------------------------------------------------------- -PLOT 応答列の組み立て
# モデル空間は window_contract_oracle.ps1 の -PLOT ブロック（実機確定済み）と同型。
# レイアウトはプロンプト列が異なり（AutoCAD 2026 ja 実機で確定: 用紙サイズ既定が pc3 既定の
# ANSI A になるため用紙名を明示し、用紙単位・図面の方向・上下反転・線の太さ尺度変更・
# ペーパー空間を最初に印刷・隠線処理のプロンプトが挟まる）、別テンプレートを使う。
# オラクル 1 つの .scr の行（-PLOT と、ページ設定へ格納された窓の読み戻し）。OSMODE の設定と終了の行は起動の最初と最後に置く
function New-PlotScrLines([string[]]$PreLines, [string[]]$AreaLines, [string]$ScaleLine, [string]$PdfPath, [string]$StoredPathFwd, [string]$Space = 'Model') {
    $body = New-Object System.Collections.Generic.List[string]
    $body.AddRange([string[]]$PreLines)
    if ($Space -eq 'Paper') {
        $body.AddRange([string[]]@(
                '-PLOT'
                'Yes'          # 詳細印刷設定
                ''             # レイアウト名 <PETA_A3>
                ''             # 出力デバイス <AutoCAD PDF (High Quality Print).pc3>（レイアウト保存値）
                'ISO フルブリード A3 (420.00 x 297.00 ミリ)'   # 用紙サイズ（既定は pc3 既定の ANSI A のため明示）
                ''             # 用紙単位 <ミリメートル>
                ''             # 図面の方向 <横>
                ''             # 上下を反転して印刷 <いいえ>
            ))
        $body.AddRange([string[]]$AreaLines)
        $body.AddRange([string[]]@(
                $ScaleLine
                'Center'
                ''             # 印刷スタイルを使用? <はい>
                ''             # 印刷スタイルテーブル名 <monochrome.ctb>
                ''             # 線の太さを使用して印刷? <はい>
                ''             # 印刷尺度で線の太さを尺度変更? <いいえ>
                ''             # ペーパー空間を最初に印刷? <いいえ>
                ''             # ペーパー空間のオブジェクトを隠線処理? <いいえ>
                $PdfPath
                'Yes'
                'Yes'
            ))
    }
    else {
        $body.AddRange([string[]]@(
                '-PLOT'
                'Yes'
                ''
                'AutoCAD PDF (High Quality Print).pc3'
                'ISO フルブリード A3 (420.00 x 297.00 ミリ)'   # 用紙サイズ（Paper分岐と同じ罠。既定は
                # pc3 既定の ANSI A(279.4x215.9mm) になる。実測: 空行のままだとオラクルPDFが
                # ANSI A で生成され、Model窓/Model全体のFit+Center切出しが用紙寸法の思い違いで破綻した）
                ''
                ''
                ''
            ))
        $body.AddRange([string[]]$AreaLines)
        $body.AddRange([string[]]@(
                $ScaleLine
                'Center'
                ''
                ''
                ''
                ''
                $PdfPath
                'Yes'
                'Yes'
            ))
    }
    $body.AddRange([string[]]@(
            '(defun cplastg1 (e / v) (setq v nil) (foreach p e (if (= (car p) 1) (setq v (cdr p)))) v)'
            '(defun cpnum (e k) (if (assoc k e) (rtos (cdr (assoc k e)) 2 6) "<なし>"))'
            "(setq cprf (open `"$StoredPathFwd`" `"w`"))"
            '(setq cpld (cdr (assoc -1 (dictsearch (namedobjdict) "ACAD_LAYOUT"))))'
            '(setq cpe (dictnext cpld T))'
            '(while cpe'
            '  (write-line (strcat "LAYOUT=" (cplastg1 cpe)'
            '                      " 74=" (cpnum cpe 74)'
            '                      " 48=" (cpnum cpe 48) " 49=" (cpnum cpe 49)'
            '                      " 140=" (cpnum cpe 140) " 141=" (cpnum cpe 141)) cprf)'
            '  (setq cpe (dictnext cpld)))'
            '(close cprf)'
        ))
    , @($body)
}

# 起動の最初と最後の行。-PLOT の窓コーナーもスクリプト座標入力の2点目が定常スナップへ吸着する（実測）ため、
# 実行中だけ OSMODE=0 にし、終了の前に元値へ戻す
$oracleHead = @('(setq cpOsmOld (getvar "OSMODE"))', '(setvar "OSMODE" 0)')
$oracleTail = @('(setvar "OSMODE" cpOsmOld)', '_.QUIT', '_Y')

# オラクル 1 つの定義。Kind はオラクルのフォルダの名前、Post は結果へ判定の材料を足す処理（引数は結果の hashtable）
function New-OracleSpec([string]$Kind, [string]$Dir, [string]$Dwg, [string[]]$Pre, [string[]]$Area, [string]$Scale, [string]$Space, [scriptblock]$Post = $null) {
    @{ Kind = $Kind; Dir = $Dir; Dwg = $Dwg; Pre = $Pre; Area = $Area; Scale = $Scale; Space = $Space; Post = $Post }
}

function New-PaperWindowOracleSpec([hashtable]$Win) {
    $pre = @('(setvar "TILEMODE" 0)', '(setvar "CTAB" "PETA_A3")')
    $area = @('Window', $Win.P1, $Win.P2)
    # PETA_A3 レイアウトのページ設定は既に1:1（tests/assets/testcharts/README.md 参照）。
    # window_contract_oracle.ps1 は座標契約の実測目的で "Fit" を使うが、ここは画像比較のため
    # 実際の印刷寸法を1:1で固定する（未検証: 応答列がずれたら "Fit" 相当に戻して切出し側も直すこと）
    New-OracleSpec 'PaperWindow' 'paper-window' $dwgLayout $pre $area '1:1' 'Paper' {
        param($r)
        $line = $r.Readback | Where-Object { $_ -match '^LAYOUT=PETA_A3 ' } | Select-Object -First 1
        $check = @{ found = [bool]$line; matches = $false; detail = $line; expectedP1 = $Win.P1; expectedP2 = $Win.P2 }
        if ($line -and $line -match '48=([-\d.]+) 49=([-\d.]+) 140=([-\d.]+) 141=([-\d.]+)') {
            $pts = ConvertTo-PointPair $Win.P1 $Win.P2
            $x0 = [double]$Matches[1]; $y0 = [double]$Matches[2]; $x1 = [double]$Matches[3]; $y1 = [double]$Matches[4]
            $tol = 0.01
            $check.matches = ([math]::Abs($x0 - $pts.X0) -le $tol) -and ([math]::Abs($y0 - $pts.Y0) -le $tol) -and
            ([math]::Abs($x1 - $pts.X1) -le $tol) -and ([math]::Abs($y1 - $pts.Y1) -le $tol)
            $check.storedLowerLeft = @($x0, $y0); $check.storedUpperRight = @($x1, $y1)
        }
        $r.WindowContractCheck = $check
    }.GetNewClosure()
}

function New-PaperOWindowOracleSpec([hashtable]$Win) {
    # cadpetaRect と同一の矩形（peta_option_matrix.ps1 の $PaperRectLisp・60,50-140,100・
    # LWPOLYLINE・レイヤ"0"）を PETA_A3 の紙面空間へ単独で置き、他の実体（ビューポート含む）を
    # 全て消す。PETA の _Object 選択がこの1実体だけを出力対象にする挙動を、素の -PLOT で
    # 再現するため（座標は peta_option_matrix.ps1 と同期を保つこと）。消した図形はオラクルの後の UNDO で戻る
    $pre = @(
        '(setvar "TILEMODE" 0)'
        '(setvar "CTAB" "PETA_A3")'
        '(command "_.PSPACE")'
        '(command "_.ERASE" "_ALL" "")'
        '(entmakex (list (cons 0 "LWPOLYLINE") (cons 100 "AcDbEntity") (cons 8 "0") (cons 67 1) (cons 410 "PETA_A3") (cons 100 "AcDbPolyline") (cons 90 4) (cons 70 1) (list 10 60.0 50.0) (list 10 140.0 50.0) (list 10 140.0 100.0) (list 10 60.0 100.0)))'
    )
    New-OracleSpec 'PaperOWindow' 'paper-o-window' $dwgLayout $pre @('Window', $Win.P1, $Win.P2) '1:1' 'Paper'
}

function New-ModelFullOracleSpec {
    $pre = @(
        '(setvar "TILEMODE" 1)'
        '(command "_.ZOOM" "_Extents")'
        '(defun cpfmt3 (p) (strcat (rtos (car p) 2 6) "," (rtos (cadr p) 2 6) "," (rtos (caddr p) 2 6)))'
        '(setq cpef (open "{LAUNCHDIR_LISP}/model-full.extents.txt" "w"))'
        '(write-line (strcat "EXTMIN=" (cpfmt3 (getvar "EXTMIN")) " EXTMAX=" (cpfmt3 (getvar "EXTMAX"))) cpef)'
        '(close cpef)'
    )
    New-OracleSpec 'ModelFull' 'model-full' $dwgModel $pre @('Extents') 'Fit' 'Model' {
        param($r)
        $extentsPath = Join-Path $r.WorkDir 'model-full.extents.txt'
        if (Test-Path $extentsPath) {
            $line = Get-Content $extentsPath -Raw
            if ($line -match 'EXTMIN=([-\d.]+),([-\d.]+),[-\d.]+ EXTMAX=([-\d.]+),([-\d.]+),[-\d.]+') {
                $r.ExtentsWMm = [double]$Matches[3] - [double]$Matches[1]
                $r.ExtentsHMm = [double]$Matches[4] - [double]$Matches[2]
            }
        }
        if (-not $r.ContainsKey('ExtentsWMm')) { throw "モデル全体オラクルの図形範囲(EXTMIN/EXTMAX)が読めない: $extentsPath" }
    }
}

function New-ModelWindowOracleSpec([hashtable]$Win) {
    # window_contract_oracle.ps1 の -Case AsIs（図面既定のUCS/ビューのまま Window+Fit+Center で
    # -PLOT する応答列）と同じ考え方だが、あちらは格納値(DXF 48/49/140/141)の読み戻しだけが目的で
    # 画像は使わない。用紙サイズを明示していないため既定の ANSI A (279.4x215.9mm) になり、
    # A3想定で切り出す本ハーネストの画像比較が破綻する（実測）。window_contract_oracle.ps1 自体は
    # 変更せず、ここでは New-PlotScrLines（Model分岐で用紙サイズを明示済み）を自前で使う
    New-OracleSpec 'ModelWindow' 'model-window' $dwgModel @('(setvar "TILEMODE" 1)') @('Window', $Win.P1, $Win.P2) 'Fit' 'Model'
}

# オラクルを図面ごとに 1 回の起動でまとめて作る（tests/tools/common/accore_batch.ps1）。各オラクルの前に UNDO の
# マークを置き、後でマークまで戻す（Paper O 窓の図形の削除と、-PLOT が保存するページ設定を次のオラクルへ残さない）。
# 戻り値: @{ Oracles = Kind → @{ Pdf; Readback; WorkDir; Stdout } と Post が足した判定の材料; Errors = Kind → 作れなかった理由 }。
# 1 つのオラクルの失敗で、ほかのオラクルを捨てない（使うケースだけが不合格になる）
function Invoke-OracleBatch([hashtable[]]$Specs) {
    $oracles = @{}
    $errors = @{}
    New-Item -ItemType Directory -Force $oracleRoot | Out-Null
    foreach ($group in ($Specs | Group-Object { $_.Dwg })) {
        $cases = @($group.Group | ForEach-Object {
            $lines = New-PlotScrLines $_.Pre $_.Area $_.Scale "{LAUNCHDIR}\$($_.Dir).oracle.pdf" "{LAUNCHDIR_LISP}/$($_.Dir).stored.txt" $_.Space
            @{ Id = $_.Dir; Lines = @('(command "_.UNDO" "_M")') + $lines + @('(command "_.UNDO" "_B")'); Spec = $_ }
        })
        try {
            # 日本語（用紙名）を含む scr は CP932 で書く（ASCII だと文字が落ちて応答がずれる）
            $runs = Invoke-AccoreBatch -Acc $acc -DwgSrc $group.Name -Cases $cases -Head $oracleHead -Tail $oracleTail `
                -WorkRoot $oracleRoot -LaunchName ([IO.Path]::GetFileNameWithoutExtension($group.Name)) -TimeoutSeconds $TimeoutSeconds `
                -Encoding ([Text.Encoding]::GetEncoding(932))
        }
        catch {
            foreach ($c in $cases) { $errors[$c.Spec.Kind] = "起動に失敗した: $($_.Exception.Message)" }
            continue
        }
        foreach ($c in $cases) {
            $spec = $c.Spec
            try {
                $run = $runs[$c.Id]
                $stdout = Join-Path $run.CaseDir 'accore_stdout.log'
                [IO.File]::WriteAllText($stdout, $run.Output, [Text.Encoding]::Unicode)
                $tail = "`n--- stdout 末尾 ---`n$(($run.Output -split "`n" | Select-Object -Last 60) -join "`n")"
                $pdf = Join-Path $run.CaseDir "$($spec.Dir).oracle.pdf"
                $stored = Join-Path $run.CaseDir "$($spec.Dir).stored.txt"
                if ($run.TimedOut) { throw "$($run.TimeoutDetail): $stdout" }
                if ($run.ExitCode -ne 0) { throw "accoreconsole が異常終了した（$($run.ExitDetail)）: $stdout" }
                if (@($run.Residual).Count) { throw "accoreconsole が残っている（PID: $(@($run.Residual) -join ',')）" }
                if (-not $run.State.Ok) { throw $run.State.Detail }
                if (-not (Test-Path $pdf)) { throw "PDF が生成されていない（-PLOT の応答列がずれた可能性）:$tail" }
                $readback = @(if (Test-Path $stored) { Get-Content $stored })
                if ($readback.Count -eq 0) { throw "格納値読み戻しが出ていない（-PLOT の応答列がずれた可能性）:$tail" }
                $r = @{ Pdf = $pdf; Readback = $readback; WorkDir = $run.CaseDir; Stdout = $stdout }
                if ($spec.Post) { & $spec.Post $r }
                $oracles[$spec.Kind] = $r
            }
            catch { $errors[$spec.Kind] = $_.Exception.Message }
        }
    }
    @{ Oracles = $oracles; Errors = $errors }
}
# ---------------------------------------------------------------- EMF の GDI 描画（レンダの正規化）
# 実機で確認した事実: EMF のメタファイル再生（Graphics.DrawImage(Metafile,...)）はクラシックGDIの
# 描画命令を経由するため、ホストGraphicsのSmoothingMode=AntiAliasを設定しても無視され、輪郭が
# アンチエイリアスなしの硬いエッジになる（PDFium/Chromiumはどちらも輪郭をアンチエイリアスする）。
# これがEMF側だけ recall が系統的に低く出る主因（線の実ジオメトリは同じで、輪郭の階調が無いだけ）。
# 対策として SuperSample 倍で描いてから高品質縮小し、他レンダラ相当のアンチエイリアス縁を作る
# （実測: 1倍 iou=0.8997/recall=0.917 → 4倍 iou=0.904/recall=0.949。8倍は4倍と同等で頭打ち）
function Render-EmfToPngG8([string]$EmfPath, [string]$PngPath, [int]$Width, [int]$Height, [int]$SuperSample = 4) {
    Add-Type -AssemblyName System.Drawing
    $mf = [System.Drawing.Imaging.Metafile]::new($EmfPath)
    $bigW = $Width * $SuperSample; $bigH = $Height * $SuperSample
    $big = New-Object System.Drawing.Bitmap $bigW, $bigH
    $gBig = [System.Drawing.Graphics]::FromImage($big)
    $gBig.Clear([System.Drawing.Color]::White)
    $gBig.DrawImage($mf, (New-Object System.Drawing.Rectangle 0, 0, $bigW, $bigH))
    $gBig.Dispose()
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($big, (New-Object System.Drawing.Rectangle 0, 0, $Width, $Height))
    $g.Dispose()
    $bmp.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $big.Dispose(); $mf.Dispose()
}

# SVG の Chromium 描画。ヘッドレススクリーンショットの window-size/scale-factor の組は
# tests/tools/data-export/compare_data_print.ps1 で実測済みの手口（CSS 96dpi の自然px寸法へ
# window-size を合わせ、force-device-scale-factor=目標dpi/96 で最終画素寸法を得る）
function Render-SvgViaBrowser([string]$SvgPath, [int]$TargetDpi, [string]$OutPng) {
    $dims = Get-SvgDimsMm $SvgPath
    $cssPxPerMm = 96.0 / 25.4
    $winW = [int][math]::Round($dims.W * $cssPxPerMm)
    $winH = [int][math]::Round($dims.H * $cssPxPerMm)
    $scaleFactor = $TargetDpi / 96.0
    $svgUri = ([Uri]$SvgPath).AbsoluteUri
    $chromeArgs = @(
        '--headless=new', '--disable-gpu', '--hide-scrollbars'
        '--default-background-color=FFFFFFFF'
        "--force-device-scale-factor=$scaleFactor"
        "--window-size=$winW,$winH"
        "--screenshot=$OutPng"
        $svgUri
    )
    # headless ブラウザは稀に終了せず残る（実測2回。screenshot 完了後もプロセスが残留し
    # -Wait が無期限に固まる）。時間上限を置き、超過時は自分が起動した PID だけ停止して
    # 1回だけやり直す。「ファイルが在る」を完了扱いにせず（途中書きがあり得る）、
    # 全画素デコードと期待画素寸法の一致（png-check）まで通って初めて成功とする
    $expectW = [int][math]::Round($winW * $scaleFactor)
    $expectH = [int][math]::Round($winH * $scaleFactor)
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if (Test-Path $OutPng) { Remove-Item $OutPng -Force }
        $p = Start-Process -FilePath $browserExe -ArgumentList (ConvertTo-NativeArgString $chromeArgs) -PassThru -WindowStyle Hidden
        $exited = $p.WaitForExit(90 * 1000)
        if (-not $exited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
        }
        if (Test-Path $OutPng) {
            $chk = Invoke-PyTool 'svg_png_check' $helperScript @('png-check', '--in', $OutPng, '--width', "$expectW", '--height', "$expectH")
            if ($chk.Exit -eq 0) { return }
            "    SVGブラウザ描画の試行 $attempt : PNG 完全性検査に失敗（$($chk.Json.reason)）"
        }
        else {
            "    SVGブラウザ描画の試行 $attempt が失敗（exited=$exited exit=$($p.ExitCode)・PNG なし）"
        }
    }
    throw "SVGのブラウザ描画に失敗（2試行とも完全な PNG が得られない）"
}

# ---------------------------------------------------------------- ケース定義
$Cases = @(
    @{ Id = 'peta-pw-o0m0l0t0f0b0'; Oracle = 'PaperWindow'; Compare = 'Standard' }
    @{ Id = 'peta-pw-o0m1l0t0f0b0'; Oracle = 'PaperWindow'; Compare = 'PadM5'; PadMm = 5.0 }
    @{ Id = 'peta-pw-o0m0l1t0f0b0'; Oracle = 'PaperWindow'; Compare = 'RoiOnly' }
    @{ Id = 'peta-pw-o0m0l0t1f0b0'; Oracle = 'PaperWindow'; Compare = 'Standard' }
    # B（EMF合成下地色）は透過要素があるときだけ効く。TESTCHART2_layout はページ設定の
    # 透過フラグがオフで B の効果が出ない（試走の B対 EMF 一致で実測）ため、B の解析的判定は
    # 透過フラグがオンのモデル空間の窓で行う
    @{ Id = 'peta-mw-o0m0l0t0f0b1'; Oracle = 'ModelWindow'; Compare = 'BMode' }
    @{ Id = 'peta-ma-o0m0l0t0f0b0'; Oracle = 'ModelFull'; Compare = 'Standard' }
    @{ Id = 'peta-mw-o0m0l0t0f0b0'; Oracle = 'ModelWindow'; Compare = 'Standard' }
    @{ Id = 'peta-pw-o1m0l0t0f0b0'; Oracle = 'PaperOWindow'; Compare = 'Standard' }
)

function Get-Px([double]$Mm) { [int][math]::Round($Mm * $Dpi / 25.4) }

# ---------------------------------------------------------------- ケースごとの処理本体
$results = New-Object System.Collections.ArrayList
$overallOk = $true

# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する
. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"
$regGuard = Get-CadRegSnapshot
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
try {
# 4 種類のオラクルを、図面ごとに 1 回の起動でまとめて作る（Paper 窓・Paper O 窓・Model 全体・Model 窓の 4 起動が 2 起動になる）。
# 作れなかったときは、そのオラクルを使うケースを不合格にする
# ケースが使うオラクルだけを作る
$oracleKinds = @($Cases | ForEach-Object Oracle | Sort-Object -Unique)
$paperWin = @{ P1 = $PaperWinP1; P2 = $PaperWinP2 }
$specs = New-Object System.Collections.Generic.List[hashtable]
$specErrors = @{}
foreach ($kind in $oracleKinds) {
    try {
        $specs.Add($(switch ($kind) {
            'PaperWindow' { New-PaperWindowOracleSpec $paperWin }
            'PaperOWindow' { New-PaperOWindowOracleSpec $paperWin }
            'ModelFull' { New-ModelFullOracleSpec }
            'ModelWindow' {
                # Model 窓の範囲は、そのオラクルを使う最初のケースの PETA の窓と同じにする
                $modelWinCase = $Cases | Where-Object Oracle -eq 'ModelWindow' | Select-Object -First 1
                New-ModelWindowOracleSpec (Get-ScenarioWindowPoints (Get-ScenarioEntry (Find-CaseDir $modelWinCase.Id) $modelWinCase.Id))
            }
            default { throw "未知のオラクル種別: $kind" }
        }))
    }
    catch { $specErrors[$kind] = $_.Exception.Message }
}
$batch = if ($specs.Count) { Invoke-OracleBatch $specs.ToArray() } else { @{ Oracles = @{}; Errors = @{} } }
$oracles = $batch.Oracles
$oracleErrors = $batch.Errors + $specErrors
foreach ($kind in $oracleErrors.Keys) { "オラクル[$kind]を作れない: $($oracleErrors[$kind])" }
foreach ($c in $Cases) {
    "=== ケース $($c.Id)（oracle=$($c.Oracle) compare=$($c.Compare)） ==="
    $caseOutDir = Join-Path $OutDir $c.Id
    if (Test-Path $caseOutDir) { Remove-Item -LiteralPath $caseOutDir -Recurse -Force }
    New-Item -ItemType Directory -Force $caseOutDir | Out-Null
    $caseResult = [ordered]@{
        caseId       = $c.Id; oracleKind = $c.Oracle; compareMode = $c.Compare
        targets      = [ordered]@{}; issues = New-Object System.Collections.ArrayList
        pass         = $false
    }
    try {
        $caseDir = Find-CaseDir $c.Id
        $scenario = Get-ScenarioEntry $caseDir $c.Id
        $arts = Get-CaseArtifacts $caseDir
        $caseResult.petaCaseDir = $caseDir

        if (-not $oracles.ContainsKey($c.Oracle)) { throw "オラクル[$($c.Oracle)]が無い: $($oracleErrors[$c.Oracle])" }
        $oracle = $oracles[$c.Oracle]
        $caseResult.oraclePdf = $oracle.Pdf
        if ($oracle.WindowContractCheck) {
            $caseResult.windowContractCheck = $oracle.WindowContractCheck
            if (-not $oracle.WindowContractCheck.matches) {
                [void]$caseResult.issues.Add('ページ設定へ格納された窓(DXF 48/49/140/141)が指定値と一致しない')
            }
        }

        # 内容ボックス(mm)の決定（オラクル切出しとPETA側レンダの目標画素寸法を揃える基準）。
        # PETA 自身の SVG の viewBox を一次情報とする（M5 はパディング込みの110x80がそのまま入る）。
        # 窓の選択点(Win.P1/P2)は -PLOT の Window 応答を駆動するためだけに使い、内容サイズには使わない
        # （紙面空間座標とページmm座標は別系で、Model窓は選択矩形どおりの寸法にもならない実測あり）。
        $viewBox = Get-SvgViewBoxMm $arts.Svg
        $contentW = $viewBox.W; $contentH = $viewBox.H
        $caseResult.svgViewBoxMm = @($viewBox.X0, $viewBox.Y0, $viewBox.X1, $viewBox.Y1)
        $caseResult.contentWMm = $contentW; $caseResult.contentHMm = $contentH

        if ($c.Oracle -eq 'ModelFull') {
            # モデル全体の PETA 出力は「図形範囲を用紙に収めた」用紙寸法になる（利用者向け文書の
            # とおり）。図形範囲(EXTMIN/EXTMAX)そのものと SVG 寸法は一致しないのが正しいため、
            # 独立照合は「PETA の SVG 寸法 == オラクル PDF のページ寸法（同じ用紙合わせ）」で行い、
            # 図形範囲は記録に残す（内容の一致は本体の画素照合が担う）
            $oraclePageMm = Get-PdfPageSizeMm $oracle.Pdf
            $dw = [math]::Abs($oraclePageMm.W - $contentW)
            $dh = [math]::Abs($oraclePageMm.H - $contentH)
            $extPass = ($dw -le 0.5 -and $dh -le 0.5)
            $caseResult.independentExtentsCheck = @{
                extentsWMm = $oracle.ExtentsWMm; extentsHMm = $oracle.ExtentsHMm
                oraclePageWMm = $oraclePageMm.W; oraclePageHMm = $oraclePageMm.H
                petaSvgWMm  = $contentW; petaSvgHMm = $contentH
                diffWMm     = $dw; diffHMm = $dh; pass = $extPass
            }
            if (-not $extPass) { [void]$caseResult.issues.Add('PETAのSVG寸法がオラクルPDFのページ寸法と0.5mm超で不一致') }
        }

        # ------------------------------------------------ 正解PDFの切出し（レンダの正規化・その1）
        # Paper窓/Paper O窓はオラクルも同一レイアウト・1:1・中央の-PLOTなので、PETAのviewBoxと
        # 同じページ座標がそのまま使える（オフセット加算なし）。M5だけは「基準100x70を切り出し、
        # 四辺5mm白パッドで110x80」の既存設計を保つため、viewBoxをPadMm分だけ内側へ縮めた矩形を切り出す
        $oracleCropPng = Join-Path $caseOutDir 'oracle_crop.png'
        switch ($c.Oracle) {
            { $_ -in @('PaperWindow', 'PaperOWindow') } {
                # 注意（実機で踏んだ罠）: @(a + b, c - d) は PowerShell の構文解析でエラーになる
                # （op_Addition が System.Object[] にないと言われる）。@() の中で算術式をカンマ区切り
                # にするときは各要素を丸括弧で包むこと
                $cropRect = if ($c.Compare -eq 'PadM5') {
                    @(($viewBox.X0 + $c.PadMm), ($viewBox.Y0 + $c.PadMm), ($viewBox.X1 - $c.PadMm), ($viewBox.Y1 - $c.PadMm))
                }
                else { @($viewBox.X0, $viewBox.Y0, $viewBox.X1, $viewBox.Y1) }
                Invoke-PyToolChecked "$($c.Id)_oracle" $helperScript @(
                    'paper-window', '--pdf', $oracle.Pdf, '--out', $oracleCropPng, '--dpi', $Dpi,
                    '--window', ($cropRect -join ','), '--off-x', '0', '--off-y', '0'
                ) | Out-Null
            }
            { $_ -in @('ModelFull', 'ModelWindow') } {
                $fitRv = Invoke-PyToolChecked "$($c.Id)_oracle" $helperScript @(
                    'fit-centered', '--pdf', $oracle.Pdf, '--out', $oracleCropPng, '--dpi', $Dpi,
                    '--content-mm', "$contentW,$contentH"
                )
                # オラクルPDFの用紙寸法がA3フルブリード(420.1583x297.0389mm)であることを比較の前に
                # 確認する（実機で踏んだ罠: 用紙サイズを明示し忘れるとpc3既定のANSI Aになり、
                # Fit+Centerの逆算切出しが全く違う矩形を切り出してしまう）
                $oraclePageW = [double]$fitRv.Json.pageWidthMm; $oraclePageH = [double]$fitRv.Json.pageHeightMm
                $caseResult.oraclePageSizeMm = @($oraclePageW, $oraclePageH)
                $pageWOk = [math]::Abs($oraclePageW - 420.1583) -le 0.5
                $pageHOk = [math]::Abs($oraclePageH - 297.0389) -le 0.5
                if (-not ($pageWOk -and $pageHOk)) {
                    [void]$caseResult.issues.Add("オラクルPDFの用紙寸法がA3フルブリードでない（実測 ${oraclePageW}x${oraclePageH}mm）。-PLOTの用紙サイズ応答がずれている可能性")
                }
            }
        }

        $targetWMm = $contentW; $targetHMm = $contentH
        $targetWPx = Get-Px $targetWMm; $targetHPx = Get-Px $targetHMm

        $oracleFinalPng = Join-Path $caseOutDir 'oracle_final.png'
        if ($c.Oracle -in @('ModelFull', 'ModelWindow')) {
            # Fit+Center（1:1でない）でプロットしたオラクルは、切出し直後の画素寸法が「用紙に収まる
            # よう縮小された実寸」になっている（PETA側は常に実寸=1:1で出力）。normalize（白パディング/
            # 切り詰めのみ）では縮尺差を吸収できないため、ここは resize（実際の拡大縮尺）で合わせる
            # （実測で判明した欠陥。旧実装はnormalizeを誤用し、内容が左上へ小さく写り込んだまま比較していた）
            Invoke-PyToolChecked "$($c.Id)_oracle_resize" $helperScript @(
                'resize', '--in', $oracleCropPng, '--out', $oracleFinalPng, '--width', $targetWPx, '--height', $targetHPx
            ) | Out-Null
        }
        else {
            # Paper窓/Paper O窓は1:1プロットなので、normalize（丸め差の白パディング/切り詰めのみ）で足りる
            if ($c.Compare -eq 'PadM5') {
                Invoke-PyToolChecked "$($c.Id)_pad" $helperScript @(
                    'pad', '--in', $oracleCropPng, '--out', $oracleFinalPng, '--dpi', $Dpi, '--pad-mm', $c.PadMm
                ) | Out-Null
            }
            else { Copy-Item $oracleCropPng $oracleFinalPng -Force }
            Invoke-PyToolChecked "$($c.Id)_oracle_norm" $helperScript @(
                'normalize', '--in', $oracleFinalPng, '--out', $oracleFinalPng, '--width', $targetWPx, '--height', $targetHPx
            ) | Out-Null
        }

        # 目視相当ガード: 切出し直後に前景画素数を数え、非空であることを比較の前に確認する
        # （欠陥2対応。空白同士がROI判定で見かけ上PASSしてしまう事態を、比較へ進む前に検出する）
        $oracleFg = Invoke-PyToolChecked "$($c.Id)_oracle_fgcheck" $helperScript @('fg-count', '--in', $oracleFinalPng)
        $caseResult.oracleForegroundPx = $oracleFg.Json.foregroundPx
        if ($oracleFg.Json.foregroundPx -lt 50) {
            [void]$caseResult.issues.Add("正解PDF切出し(oracle_final.png)が空白（前景${($oracleFg.Json.foregroundPx)}px）。クロップ座標が誤っている可能性")
        }

        # ------------------------------------------------ PETA側の正規化（レンダの正規化・その2）
        # PETA自身のPDFも用紙全面(A3フルブリード)で出力される（実測）。オラクルと同じ viewBox 矩形で
        # 切り出してから正規化する（矩形はM5含めviewBoxそのまま。PETAの出力自体がパディング込みで
        # 描画されているため、オラクル側のような内側縮小は不要）
        $petaPdfRaw = Join-Path $caseOutDir 'peta_pdf_raw.png'
        Invoke-PyToolChecked "$($c.Id)_pdf_crop" $helperScript @(
            'paper-window', '--pdf', $arts.Pdf, '--out', $petaPdfRaw, '--dpi', $Dpi,
            '--window', "$($viewBox.X0),$($viewBox.Y0),$($viewBox.X1),$($viewBox.Y1)", '--off-x', '0', '--off-y', '0'
        ) | Out-Null
        $petaPdfPng = Join-Path $caseOutDir 'peta_pdf.png'
        Invoke-PyToolChecked "$($c.Id)_pdf_norm" $helperScript @(
            'normalize', '--in', $petaPdfRaw, '--out', $petaPdfPng, '--width', $targetWPx, '--height', $targetHPx
        ) | Out-Null
        $petaPdfFg = Invoke-PyToolChecked "$($c.Id)_pdf_fgcheck" $helperScript @('fg-count', '--in', $petaPdfPng)
        $caseResult.petaPdfForegroundPx = $petaPdfFg.Json.foregroundPx
        if ($petaPdfFg.Json.foregroundPx -lt 50) {
            [void]$caseResult.issues.Add("PETA PDF切出し(peta_pdf.png)が空白（前景${($petaPdfFg.Json.foregroundPx)}px）。クロップ座標が誤っている可能性")
        }

        $petaEmfPng = Join-Path $caseOutDir 'peta_emf.png'
        Render-EmfToPngG8 $arts.Emf $petaEmfPng $targetWPx $targetHPx

        $petaSvgPng = $null
        if ($browserExe) {
            $rawSvgPng = Join-Path $caseOutDir 'peta_svg_raw.png'
            try {
                Render-SvgViaBrowser $arts.Svg $Dpi $rawSvgPng
                $petaSvgPng = Join-Path $caseOutDir 'peta_svg.png'
                Invoke-PyToolChecked "$($c.Id)_svg_norm" $helperScript @(
                    'normalize', '--in', $rawSvgPng, '--out', $petaSvgPng, '--width', $targetWPx, '--height', $targetHPx
                ) | Out-Null
            }
            catch {
                $caseResult.targets.svg = @{ status = 'SKIP'; reason = "ブラウザ描画に失敗: $($_.Exception.Message)" }
            }
        }
        else {
            $caseResult.targets.svg = @{ status = 'SKIP'; reason = 'Chromium系ブラウザ(msedge.exe/chrome.exe)が見つからない' }
        }

        $targetsToCheck = New-Object System.Collections.ArrayList
        [void]$targetsToCheck.Add(@{ Name = 'pdf'; Png = $petaPdfPng })
        [void]$targetsToCheck.Add(@{ Name = 'emf'; Png = $petaEmfPng })
        if ($petaSvgPng) { [void]$targetsToCheck.Add(@{ Name = 'svg'; Png = $petaSvgPng }) }

        # ------------------------------------------------ 比較（判定は compare_renders.py に集約）
        switch ($c.Compare) {
            { $_ -in @('Standard', 'PadM5') } {
                foreach ($t in $targetsToCheck) {
                    $cmp = Invoke-PyTool "$($c.Id)_cmp_$($t.Name)" $compareScript (@($oracleFinalPng, $t.Png, '--dpi', $Dpi) + $CmpDilateArgs)
                    if ($cmp.Exit -eq 2) { $caseResult.targets[$t.Name] = @{ status = 'SKIP'; reason = "比較器が実行できない: $(Get-CmpSkipReason $cmp)"; metrics = $cmp.Json } }
                    else { $caseResult.targets[$t.Name] = @{ status = ($cmp.Exit -eq 0 ? 'PASS' : 'FAIL'); metrics = $cmp.Json } }
                }
            }
            'RoiOnly' {
                $roiArgs = @()
                foreach ($roi in $layerSafeRois) { $roiArgs += @('--roi', $roi) }
                foreach ($t in $targetsToCheck) {
                    $cmp = Invoke-PyTool "$($c.Id)_cmp_$($t.Name)" $compareScript (@($oracleFinalPng, $t.Png, '--dpi', $Dpi) + $CmpDilateArgs + $roiArgs)
                    if ($cmp.Exit -eq 2) {
                        $caseResult.targets[$t.Name] = @{ status = 'SKIP'; reason = "比較器が実行できない: $(Get-CmpSkipReason $cmp)"; metrics = $cmp.Json }
                    }
                    else {
                        $roiResults = @($cmp.Json.roi)
                        $pass = ($roiResults.Count -gt 0) -and (-not ($roiResults | Where-Object { -not $_.pass }))
                        $caseResult.targets[$t.Name] = @{
                            status = ($pass ? 'PASS' : 'FAIL'); metrics = $cmp.Json
                            note   = 'レイヤ並べ替えで見た目が変わり得る領域を避けるため、固定ROIのみで判定（全面のoverallは無視）'
                        }
                    }
                }
            }
            'BMode' {
                # baseColor に応じてどの成果物を見るかを決める。B(EMF合成下地色)はEMFのみに効くため
                # FFFFFF側はPDF/SVG（Bの影響を受けない）の対照チェック、FF8040側はEMF（実際に合成
                # された成果物）を見る。RoiConfig.bChecks の baseColor はこの2色以外は想定していない
                foreach ($bc in $bChecks) {
                    $baseHex = ([string]$bc.baseColor).TrimStart('#').ToUpperInvariant()
                    $roiCsv = ($bc.roi -join ',')
                    $targetNames = switch ($baseHex) {
                        'FFFFFF' { @('pdf', 'svg') }
                        'FF8040' { @('emf') }
                        default { throw "RoiConfig.bChecks の baseColor が未対応: $($bc.baseColor)（FFFFFF/FF8040のみ対応）" }
                    }
                    $expRv = Invoke-PyToolChecked "$($c.Id)_expcolor_$baseHex" $helperScript @(
                        'expected-b-color', '--in', $oracleFinalPng, '--dpi', $Dpi, '--roi', $roiCsv, '--base', "#$baseHex"
                    )
                    $expHex = $expRv.Json.expectedHex
                    foreach ($tn in $targetNames) {
                        $t = $targetsToCheck | Where-Object { $_.Name -eq $tn } | Select-Object -First 1
                        $key = "$tn@$baseHex"
                        if (-not $t) { $caseResult.targets[$key] = @{ status = 'SKIP'; reason = "対象成果物 $tn が用意できていない" }; continue }
                        $cmp = Invoke-PyTool "$($c.Id)_cmp_$key" $compareScript @($t.Png, '--expect-color', $expHex, '--roi', $roiCsv, '--dpi', $Dpi)
                        if ($cmp.Exit -eq 2) { $caseResult.targets[$key] = @{ status = 'SKIP'; reason = "比較器が実行できない: $(Get-CmpSkipReason $cmp)"; metrics = $cmp.Json } }
                        else { $caseResult.targets[$key] = @{ status = ($cmp.Exit -eq 0 ? 'PASS' : 'FAIL'); metrics = $cmp.Json; expectedColor = $expHex; expectedColorDetail = $expRv.Json } }
                    }
                }
            }
        }

        # ------------------------------------------------ 可視再現・目視の材料
        if ($c.Compare -ne 'BMode') {
            foreach ($t in $targetsToCheck) {
                $diffPath = Join-Path $caseOutDir "diff_$($t.Name).png"
                try {
                    Invoke-PyToolChecked "$($c.Id)_diff_$($t.Name)" $helperScript @('diff', '--a', $oracleFinalPng, '--b', $t.Png, '--out', $diffPath) | Out-Null
                }
                catch { [void]$caseResult.issues.Add("差分画像の生成に失敗($($t.Name)): $($_.Exception.Message)") }
            }
        }
        $manifest = New-Object System.Collections.Generic.List[string]
        [void]$manifest.Add("oracle=$oracleFinalPng")
        [void]$manifest.Add("PETA-PDF=$petaPdfPng")
        [void]$manifest.Add("PETA-EMF=$petaEmfPng")
        [void]$manifest.Add('PETA-SVG=' + ($(if ($petaSvgPng) { $petaSvgPng } else { 'MISSING:' + $caseResult.targets.svg.reason })))
        if ($c.Compare -ne 'BMode') {
            foreach ($t in $targetsToCheck) {
                $diffPath = Join-Path $caseOutDir "diff_$($t.Name).png"
                if (Test-Path $diffPath) { [void]$manifest.Add("diff-$($t.Name)=$diffPath") }
            }
        }
        $contactSheetPath = Join-Path $caseOutDir 'contact_sheet.png'
        $csArgs = @('contact-sheet', '--out', $contactSheetPath, '--title', $c.Id)
        foreach ($m in $manifest) { $csArgs += @('--item', $m) }
        try { Invoke-PyToolChecked "$($c.Id)_sheet" $helperScript $csArgs | Out-Null; $caseResult.contactSheet = $contactSheetPath }
        catch { [void]$caseResult.issues.Add("コンタクトシートの生成に失敗: $($_.Exception.Message)") }

        # 目視判定チェックリストの空欄テンプレート（範囲・欠落・clip・文字・レイヤ・背景合成・寸法）
        $checklist = [ordered]@{
            caseId = $c.Id
            items  = [ordered]@{
                range = $null; missing = $null; clip = $null; text = $null
                layer = $null; backgroundComposite = $null; dimensions = $null
            }
            notes  = ''
        }
        $checklistPath = Join-Path $caseOutDir 'checklist.json'
        $checklist | ConvertTo-Json -Depth 5 | Out-File $checklistPath -Encoding utf8
        $caseResult.checklistTemplate = $checklistPath

        # ------------------------------------------------ ケース判定（未実施=SKIPもFAIL扱い）
        $statuses = @($caseResult.targets.Values | ForEach-Object { $_.status })
        $caseResult.pass = ($statuses.Count -gt 0) -and (-not ($statuses | Where-Object { $_ -ne 'PASS' }))
        if ($caseResult.issues.Count -gt 0) { $caseResult.pass = $false }
    }
    catch {
        $caseResult.pass = $false
        $caseResult.error = $_.Exception.Message
        $caseResult.errorStack = $_.ScriptStackTrace
        "  例外: $($_.Exception.Message)"
        "  スタック: $($_.ScriptStackTrace)"
    }
    if (-not $caseResult.pass) { $overallOk = $false }
    [void]$results.Add($caseResult)
    "  → $($caseResult.pass ? 'PASS' : 'FAIL')"
    foreach ($k in $caseResult.targets.Keys) {
        $t = $caseResult.targets[$k]
        if ($t.status -ne 'PASS') { "     NG $k`: $($t.status) $($t.reason)" }
    }
}
}
catch { $runError = $_ }
finally {
    # finally 内では throw しない（実行中の例外を握りつぶさない）。復元は必ず試みる
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
    if (-not $regReport.ok) { $overallOk = $false; Write-Warning ("レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ')) }
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（ケースの判定は途中までなので、集計は書き出さない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }

# ---------------------------------------------------------------- 集計・保存
$summary = [ordered]@{
    campaignDir = $CampaignDir; ver = $Ver; config = $Config; outDir = $OutDir
    roiConfig   = (Resolve-Path $RoiConfig).Path; generatedAt = (Get-Date -Format 'o')
    dpi         = $Dpi; browser = $browserExe
    total       = $results.Count; passed = @($results | Where-Object { $_.pass }).Count
    pass        = $overallOk
}
$final = [ordered]@{ summary = $summary; cases = @($results) }
$resultPath = Join-Path $OutDir 'g8_results.json'
$final | ConvertTo-Json -Depth 12 | Out-File $resultPath -Encoding utf8

"=== g8総点検: $($summary.passed)/$($summary.total) PASS → $resultPath ==="
if (-not $overallOk) { exit 1 }
exit 0
