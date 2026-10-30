# AutoCAD 選択絞り込み（範囲＋オブジェクト絞り込み）のヘッドレス回帰。
# PETA は「_Object → 選択 → 範囲（Enter=全体）」の新対話列で駆動する。
#
# -Standard は tests/tools/README.md の標準の 12 シナリオを、図面と環境変数の組み合わせごとに 1 回の起動で
# 続けて実行する（tests/tools/common/accore_batch.ps1。5 回の起動）。各シナリオの前に UNDO のマークを置き、
# 後でマークまで戻す（ペーパー空間の長方形、CurrentLayerObjects の画層と図形を次のシナリオへ残さない）。
# スイッチでシナリオを 1 つ指定したときは、そのシナリオを 1 回の起動で実行する。ページ設定の計測器
# （-AsymmetricPageSettings・-PageSetupCase）を使うシナリオはこの形で実行する。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Ver = "2026",
    [string]$OutRoot = "$env:TEMP\CadPeta_selection_plot",
    [int]$TimeoutSeconds = 120,   # 1 シナリオの上限
    [switch]$Standard,
    [switch]$RequireLayers,
    [switch]$ForceCloneFallback,
    [switch]$NamedPlotStyles,
    # 画層 0 と、元図面の現在の画層にある図形だけを選ぶ（側DBでは片方が現在の画層になる）
    [switch]$CurrentLayerObjects,
    [switch]$AsymmetricPageSettings,
    [string]$PageSetupCase,
    [ValidateSet('PETA','PETADATA','PETADATADIAG')][string]$Command = 'PETA',
    [ValidateSet('Model','Paper','PaperMixed','LayoutModel')][string]$Space = 'Model'
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'selection_plot_matrix.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
. "$PSScriptRoot\..\common\test_helpers.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"
. "$PSScriptRoot\CadRegGuard.ps1"
$tfm = Get-CadPluginTargetFramework $Ver -UnknownVersionLabel "未対応版"
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = (Get-E2ePluginPath Acad $tfm)
if (-not (Test-Path $acad)) { throw "accoreconsole がない: $acad" }
$probe = "$repo\tests\tools\regression\window_contract\WindowContractProbe\bin\Release\net8.0-windows\WindowContractProbe.dll"

# シナリオ 1 つの定義（図面、環境変数、.scr の行、判定に使う条件）を返す。組み合わせの誤りは例外にする
function New-SelectionScenario {
    param(
        [string]$Command = 'PETA', [string]$Space = 'Model',
        [switch]$RequireLayers, [switch]$ForceCloneFallback, [switch]$NamedPlotStyles, [switch]$CurrentLayerObjects,
        [switch]$AsymmetricPageSettings, [string]$PageSetupCase
    )
    if ($Space -eq 'PaperMixed' -and $Command -ne 'PETA') { throw "PaperMixed は PETA の部分出力確認専用です" }
    if ($ForceCloneFallback -and $Command -ne 'PETA') { throw "ForceCloneFallback は側DB絞り込み（PETA）専用です" }
    # ペーパー空間の側DB は元図面全体の Wblock から作り、オブジェクト複製への切り替えを持たない
    if ($ForceCloneFallback -and $Space -ne 'Model') { throw "ForceCloneFallback は Model 空間専用です（ペーパー空間には複製の経路がない）" }
    if ($NamedPlotStyles -and ($Command -ne 'PETA' -or $Space -ne 'Model')) { throw "NamedPlotStyles は PETA の Model 空間専用です" }
    if ($CurrentLayerObjects -and ($Command -ne 'PETA' -or $Space -ne 'Model' -or $NamedPlotStyles)) { throw "CurrentLayerObjects は PETA の Model 空間（CTB 図面）専用です" }
    if ($AsymmetricPageSettings -and $Command -ne 'PETA') { throw "AsymmetricPageSettings は PETA 専用です" }
    if ($AsymmetricPageSettings -and $Ver -ne '2026') { throw "AsymmetricPageSettings の独立計測器は AutoCAD 2026 専用です" }
    $pspCase = $null
    if ($PageSetupCase) {
        if ($Command -ne 'PETA') { throw "PageSetupCase は PETA 専用です" }
        if ($Ver -ne '2026') { throw "PageSetupCase の独立計測器は AutoCAD 2026 専用です" }
        if ($AsymmetricPageSettings -or $NamedPlotStyles -or $ForceCloneFallback -or $RequireLayers) {
            throw "PageSetupCase は他のシナリオスイッチと併用できません"
        }
        if ($Space -notin @('Model', 'Paper', 'LayoutModel')) { throw "PageSetupCase の空間は Model/Paper/LayoutModel のいずれかです" }
        # ケース定義は pairwise ケース表を唯一の情報源とし、因子をここへ複製しない
        . "$PSScriptRoot\page_setup_pairwise_cases.ps1"
        $pspCase = @(Get-CadPetaPageSetupPairwiseCases | Where-Object { $_.Id -eq $PageSetupCase })
        if ($pspCase.Count -ne 1) { throw "未知の pairwise ケース: $PageSetupCase" }
        $pspCase = $pspCase[0]
        if ($pspCase.Space -ne $Space) { throw "ケース $PageSetupCase の空間は $($pspCase.Space) です（指定: $Space）" }
    }
    $pageProbe = $AsymmetricPageSettings -or $PageSetupCase
    $inputName = if ($NamedPlotStyles) { 'STB_RECT.dwg' } elseif ($pageProbe) { 'TESTCHART2_layout.dwg' } elseif ($Space -eq 'Model') { 'TESTCHART2.dwg' } else { 'TESTCHART2_layout.dwg' }
    $id = "$Command-$Space" + $(if ($RequireLayers) { '-layers' } else { '' }) + $(if ($NamedPlotStyles) { '-stb' } else { '' }) + $(if ($CurrentLayerObjects) { '-curlayer' } else { '' }) + $(if ($ForceCloneFallback) { '-fallback' } else { '' }) + $(if ($AsymmetricPageSettings) { '-asymmetric-pages' } else { '' }) + $(if ($PageSetupCase) { "-psp-$($PageSetupCase.ToLowerInvariant())" } else { '' })

    $spaceSetup = switch ($Space) {
        'Model' { @('(setvar "TILEMODE" 1)') }
        'LayoutModel' { @('(setvar "TILEMODE" 0)', '(setvar "CTAB" "PETA_A3")', '_.MSPACE') }
        default { @('(setvar "TILEMODE" 0)', '(setvar "CTAB" "PETA_A3")', '_.PSPACE', '(setvar "CLAYER" "0")', '(setvar "LIMCHECK" 0)') }
    }
    $selectionSetup = if ($Space -in @('Paper', 'PaperMixed')) {
        @('(setq cadpetaRect (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") (cons 8 "0") ''(67 . 1) (cons 410 "PETA_A3") ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 0.0 0.0) ''(10 20.0 0.0) ''(10 20.0 10.0) ''(10 0.0 10.0))))')
    } elseif ($Space -eq 'LayoutModel' -or ($pageProbe -and $Space -eq 'Model')) {
        @('(setq cadpetaModelSel (ssget "_X" ''((67 . 0))))')
    } elseif ($CurrentLayerObjects) {
        # 画層 0 の矩形と、新しく作って現在の画層にした CADPETA_CUR の矩形の 2 つだけを選ぶ
        @(
            '(entmake ''((0 . "LAYER") (100 . "AcDbSymbolTableRecord") (100 . "AcDbLayerTableRecord") (2 . "CADPETA_CUR") (70 . 0) (62 . 1) (6 . "Continuous")))'
            '(setvar "CLAYER" "CADPETA_CUR")'
            '(princ (strcat "\nCADPETA_CURLAYER clayer=" (getvar "CLAYER") " color=" (itoa (cdr (assoc 62 (tblsearch "LAYER" "CADPETA_CUR"))))))'
            '(setq cadpetaCurSel (ssadd))'
            '(ssadd (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") (cons 8 "0") ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 0.0 0.0) ''(10 100.0 0.0) ''(10 100.0 50.0) ''(10 0.0 50.0))) cadpetaCurSel)'
            '(ssadd (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") (cons 8 "CADPETA_CUR") ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 120.0 0.0) ''(10 220.0 0.0) ''(10 220.0 50.0) ''(10 120.0 50.0))) cadpetaCurSel)'
        )
    } else { @() }
    if ($Space -eq 'PaperMixed') {
        $selectionSetup += @(
            '(setq cadpetaSel (ssadd (entlast)))'
            '(setq cadpetaVps (ssget "_X" (list ''(0 . "VIEWPORT") (cons 410 "PETA_A3"))))'
            '(setq cadpetaI 0)'
            '(if cadpetaVps (repeat (sslength cadpetaVps) (ssadd (ssname cadpetaVps cadpetaI) cadpetaSel) (setq cadpetaI (1+ cadpetaI))))'
        )
    }
    $selectionToken = switch ($Space) {
        'Paper' { '!cadpetaRect' }
        'PaperMixed' { '!cadpetaSel' }
        'LayoutModel' { '!cadpetaModelSel' }
        'Model' { if ($pageProbe) { '!cadpetaModelSel' } elseif ($CurrentLayerObjects) { '!cadpetaCurSel' } else { 'all' } }
    }
    $commandLines = if ($Command -eq 'PETA') {
        # _Object → 選択 → 空行（選択終了）→ 範囲オプション（L 等）→ 空行（Enter=全体）。
        # 非対応混在（PaperMixed）は範囲確定後の部分出力確認へ Y を返す
        $petaLines = @('PETA', '_Object', $selectionToken, '')
        if ($RequireLayers) { $petaLines += '_Layer' }
        $petaLines += ''
        if ($Space -eq 'PaperMixed') { $petaLines += 'Y' }
        $petaLines
    } else {
        # PETADATA 系は尺度プロンプト（1:N）を持つ。PETA の範囲プロンプトに尺度はない
        @($Command, $selectionToken, '') + $(if ($RequireLayers) { @('_Layer', '1') } else { @('1') })
    }
    $envVars = @{}
    $pageSetupLines = @()
    if ($pageProbe) {
        if (-not (Test-Path $probe)) { throw "ページ設定計測器が未ビルド: $probe" }
        $pageSetupLines = (Get-AccoreNetloadLines $probe) + @($(if ($PageSetupCase) { 'CPSETPAIRWISE' } else { 'CPSETTESTPAGES' }))
    }
    if ($PageSetupCase) {
        # 計測器 CPSETPAIRWISE への因子受け渡し。用紙探索水準の実デバイス解決は計測器側で行う
        $envVars.CADPETA_PSP_ID = $pspCase.Id
        $envVars.CADPETA_PSP_TARGET = if ($Space -eq 'Paper') { 'PETA_A3' } else { 'Model' }
        $envVars.CADPETA_PSP_UNITS = $pspCase.Units
        $envVars.CADPETA_PSP_SCALEKIND = $pspCase.ScaleKind
        $envVars.CADPETA_PSP_ROTATION = [string]$pspCase.Rotation
        $envVars.CADPETA_PSP_MEDIA = $pspCase.MediaScenario
        if ($pspCase.ScaleKind -eq 'Custom') {
            $envVars.CADPETA_PSP_NUM = $pspCase.CustomScale.Numerator.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
            $envVars.CADPETA_PSP_DEN = $pspCase.CustomScale.Denominator.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        }
    }
    if ($ForceCloneFallback) { $envVars.CADPETA_FORCE_SELECTION_CLONE_FALLBACK = '1' }
    # ページ設定の前後の比較（Layout の DBObject が PETA の前後で変わらない）に使う記録。ケースのフォルダへ移される
    $before = "{LAUNCHDIR_LISP}/$id.layout-before.lspdata"
    $after = "{LAUNCHDIR_LISP}/$id.layout-after.lspdata"
    $lines = @('(command "_.UNDO" "_M")') + $spaceSetup + $selectionSetup + $pageSetupLines +
        @("(cadpeta-layout-snapshot `"$before`")") + $commandLines + @("(cadpeta-layout-snapshot `"$after`")", '(command "_.UNDO" "_B")')
    @{
        Id = $id; Lines = $lines; Env = $envVars; Input = "$repo\tests\assets\testcharts\$inputName"
        Command = $Command; Space = $Space; RequireLayers = [bool]$RequireLayers; ForceCloneFallback = [bool]$ForceCloneFallback
        NamedPlotStyles = [bool]$NamedPlotStyles; CurrentLayerObjects = [bool]$CurrentLayerObjects
        AsymmetricPageSettings = [bool]$AsymmetricPageSettings
        PageSetupCase = $PageSetupCase
        # STB 図面＋複製フォールバックは、側DB既定（CTB）とのモード不一致で中止することを固定する
        ExpectPlotStyleAbort = ($NamedPlotStyles -and $ForceCloneFallback)
    }
}

# 1 シナリオの判定。不合格の理由の一覧を返す（空なら合格）。$r は Invoke-AccoreBatch の結果
function Test-SelectionScenario($sc, $r) {
    $f = New-Object System.Collections.Generic.List[string]
    $out = $r.Output
    $dir = $r.CaseDir
    if ($r.TimedOut) { $f.Add($r.TimeoutDetail); return $f }
    if ($r.ExitCode -ne 0) { $f.Add("accoreconsole が異常終了: $($r.ExitDetail)") }
    if (-not $r.DwgUnchanged) { $f.Add("入力 DWG が変更された") }
    if (-not $r.State.Ok) { $f.Add($r.State.Detail) }
    if ((Find-AccoreBatchMarker $out 'END' $sc.Id) -lt 0) { $f.Add("完了マーカーが無い（入力列とプロンプトの不整合）") }
    $pdf = @(Get-ChildItem $dir -Filter *.pdf -Recurse)
    $svg = @(Get-ChildItem $dir -Filter *.svg -Recurse)
    $emf = @(Get-ChildItem $dir -Filter *.emf -Recurse)
    if ($sc.ForceCloneFallback -and $out -notmatch 'オブジェクト複製で側DBを作る') { $f.Add("複製フォールバック経路の実走を確認できない") }
    if ($sc.AsymmetricPageSettings -and $out -notmatch 'CPSETTESTPAGES_OK') { $f.Add("非対称ページ設定を適用できなかった") }
    if ($sc.PageSetupCase -and $out -notmatch "CPSETPAIRWISE_OK id=$([regex]::Escape($sc.PageSetupCase)) ") { $f.Add("pairwiseページ設定を適用できなかった") }
    if ($sc.AsymmetricPageSettings -or $sc.PageSetupCase) {
        $before = Join-Path $dir "$($sc.Id).layout-before.lspdata"
        $after = Join-Path $dir "$($sc.Id).layout-after.lspdata"
        if (-not (Test-Path $before) -or -not (Test-Path $after)) { $f.Add("ページ設定snapshotが生成されなかった: before=$before after=$after") }
        elseif ((Get-FileHash $before -Algorithm SHA256).Hash -ne (Get-FileHash $after -Algorithm SHA256).Hash) { $f.Add("PETA前後でLayout DBObjectが変化した") }
    }
    if ($sc.ExpectPlotStyleAbort) {
        if ($out -notmatch '側DBの CTB/STB モードが元図面と一致しない') { $f.Add("STB×複製フォールバックのモード不一致中止を確認できない") }
        if ($pdf.Count + $svg.Count + $emf.Count -ne 0) { $f.Add("モード不一致中止後に成果物が生成された: pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count)") }
        return $f
    }
    if ($sc.Command -eq 'PETA' -and ($pdf.Count -ne 1 -or $svg.Count -ne 1 -or $emf.Count -ne 1)) {
        $f.Add("PETA 成果物数が不正: pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count)")
    }
    if ($sc.Command -eq 'PETADATA' -and ($svg.Count -ne 1 -or $pdf.Count -ne 0 -or $emf.Count -ne 0)) {
        $f.Add("PETADATA 成果物数が不正: pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count)")
    }
    if ($sc.Command -eq 'PETADATADIAG') {
        $stageSvg = @($svg | Where-Object { $_.Name -match '\.stage\d+\.' })
        $mainSvg = @($svg | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
        $diagJson = @(Get-ChildItem $dir -Filter *.diag.json -Recurse)
        if ($mainSvg.Count -ne 1 -or $stageSvg.Count -lt 1 -or $diagJson.Count -ne 1 -or $pdf.Count -ne 0 -or $emf.Count -ne 0) {
            $f.Add("PETADATADIAG 成果物数が不正: pdf=$($pdf.Count) svg=$($mainSvg.Count) stage=$($stageSvg.Count) diag=$($diagJson.Count) emf=$($emf.Count)")
        }
    }
    if ($sc.RequireLayers -and ($svg.Count -eq 0 -or -not (Select-String -Path $svg[0].FullName -Pattern 'inkscape:groupmode="layer"' -Quiet))) {
        $f.Add("レイヤ別 SVG が生成されていない")
    }
    if ($sc.Command -eq 'PETA' -and $out -notmatch '絞り込み: 選択\d+件中\d+件を出力対象にする') { $f.Add("絞り込みサマリを確認できない") }
    if ($sc.Space -eq 'PaperMixed') {
        if ($out -notmatch '非対応対象を除外して部分出力する') { $f.Add("部分出力確認プロンプトを確認できない") }
        if ($out -notmatch '絞り込みの除外候補:.*Controller') { $f.Add("制御Viewportの除外記録を確認できない") }
        if ($out -match '非対応対象が含まれるため中止する') { $f.Add("部分出力へ同意した後に絞り込みが中止された") }
    }
    if ($sc.CurrentLayerObjects) {
        if ($out -notmatch 'CADPETA_CURLAYER clayer=CADPETA_CUR color=1(?!\d)') { $f.Add("試験の前提（CADPETA_CUR が現在の画層）を作れていない") }
        if ($out -notmatch '絞り込み: 選択2件中2件を出力対象にする') { $f.Add("画層 0 と現在の画層の 2 図形が出力対象になっていない") }
        # 現在の画層と画層 0 の矩形が両方描かれ、矩形 2 つの線（stroke を持つ path）が出ること
        if ($svg.Count -gt 0) {
            $strokePaths = @(Select-String -Path $svg[0].FullName -Pattern '<path [^>]*stroke=' -AllMatches | ForEach-Object { $_.Matches }).Count
            if ($strokePaths -ne 2) { $f.Add("矩形 2 つの線が SVG に出ていない: path=$strokePaths") }
        }
    }
    $f
}

# if の出力は配列が展開されるので、全体を @() で包む（1 シナリオでも配列にする。ハッシュテーブルのまま数えるとキーの数になる）
$scenarios = @(if ($Standard) {
    @(
        (New-SelectionScenario -Command PETA -Space Model)
        (New-SelectionScenario -Command PETA -Space Paper)
        (New-SelectionScenario -Command PETA -Space PaperMixed)
        (New-SelectionScenario -Command PETA -Space LayoutModel)
        (New-SelectionScenario -Command PETADATA -Space Model)
        (New-SelectionScenario -Command PETA -Space Model -ForceCloneFallback)
        (New-SelectionScenario -Command PETA -Space Model -CurrentLayerObjects)
        (New-SelectionScenario -Command PETA -Space Model -CurrentLayerObjects -ForceCloneFallback)
        (New-SelectionScenario -Command PETA -Space Model -NamedPlotStyles)
        (New-SelectionScenario -Command PETA -Space Model -NamedPlotStyles -ForceCloneFallback)
        (New-SelectionScenario -Command PETADATA -Space Paper)
        (New-SelectionScenario -Command PETADATADIAG -Space Model)
    )
} else {
    @(New-SelectionScenario -Command $Command -Space $Space -RequireLayers:$RequireLayers -ForceCloneFallback:$ForceCloneFallback `
        -NamedPlotStyles:$NamedPlotStyles -CurrentLayerObjects:$CurrentLayerObjects `
        -AsymmetricPageSettings:$AsymmetricPageSettings -PageSetupCase $PageSetupCase)
})
foreach ($sc in $scenarios) { if (-not (Test-Path $sc.Input)) { throw "入力図面がない: $($sc.Input)" } }

# 起動の最初の行: 図面のレイアウトの記録を書く関数と、プラグインの NETLOAD（起動ごとに 1 回）
$head = @(
    '(defun cadpeta-layout-snapshot (path / d e rows f)'
    '  (setq d (cdr (assoc 350 (dictsearch (namedobjdict) "ACAD_LAYOUT"))))'
    '  (setq e (dictnext d T))'
    '  (while e'
    '    (setq rows (cons (list (cdr (assoc 3 e)) (entget (cdr (assoc 350 e)))) rows))'
    '    (setq e (dictnext d)))'
    '  (setq f (open path "w"))'
    '  (prin1 rows f)'
    '  (close f))'
) + (Get-AccoreNetloadLines $plugin)
$tail = @('_.CLOSE', '_Y', '_.QUIT')

$outRootFull = Resolve-OutRoot $OutRoot
New-Item -ItemType Directory -Force $outRootFull | Out-Null
$failures = New-Object System.Collections.Generic.List[string]
# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了（タイムアウトの強制終了で
# FileDialog=0 が永続化する実測がある）を含む finally で必ず復元する
$regGuard = Get-CadRegSnapshot -StageToTrusted (@($plugin) + @(if ($AsymmetricPageSettings -or $PageSetupCase) { $probe })) -AcadVersion $Ver
$regReport = $null
$runError = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
$launches = 0
try {
    # 図面と環境変数（プラグインの動作を変える）が同じシナリオを 1 回の起動にまとめる
    $groups = $scenarios | Group-Object { $_.Input + '|' + (($_.Env.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';') }
    foreach ($group in $groups) {
        $first = $group.Group[0]
        $saved = @{}
        foreach ($name in $first.Env.Keys) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) ; [Environment]::SetEnvironmentVariable($name, $first.Env[$name]) }
        try {
            $launches++
            $runs = Invoke-AccoreBatch -Acc $acad -DwgSrc $first.Input -Cases @($group.Group) -Head $head -Tail $tail `
                -WorkRoot $outRootFull -LaunchName "group$launches" -TimeoutSeconds $TimeoutSeconds
        }
        finally {
            foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }
        foreach ($sc in $group.Group) {
            $r = $runs[$sc.Id]
            # 呼び出し側（page_setup_pairwise_matrix など）が読むので、シナリオの標準出力をケースのフォルダにも置く
            [IO.File]::WriteAllText((Join-Path $r.CaseDir 'stdout.log'), $r.Output, [Text.Encoding]::Unicode)
            $caseFailures = @(Test-SelectionScenario $sc $r)
            if ($caseFailures.Count -eq 0) { "$($sc.Id) OK（$($r.CaseDir)）" }
            else { foreach ($m in $caseFailures) { $failures.Add("[$($sc.Id)] $m（$($r.StdoutPath)）") } ; "$($sc.Id) NG" }
        }
    }
}
catch { $runError = $_ }
finally {
    "所要 {0:n0} 秒（{1} シナリオ、{2} 回の起動）" -f $sw.Elapsed.TotalSeconds, $scenarios.Count, $launches
    # finally 内では throw しない（実行中の例外を握りつぶすため）。復元は必ず試み、復元の失敗はシナリオの失敗の一覧に入れる
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport) $failures
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（シナリオの判定は途中までなので、一覧は表示しない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "NG: $_" -ForegroundColor Red }
    throw "選択絞り込みの回帰が失敗した（$($failures.Count) 件）"
}
"選択絞り込みの回帰 $($scenarios.Count) シナリオ合格"
