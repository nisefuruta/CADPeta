# AutoCAD 選択絞り込み（範囲＋オブジェクト絞り込み）のヘッドレス回帰。
# PETA は「_Object → 選択 → 範囲（Enter=全体）」の新対話列で駆動する。
param(
    [string]$Ver = "2026",
    [string]$OutRoot = "$env:TEMP\CadPeta_selection_plot",
    [string]$Config = "Debug",
    [int]$TimeoutSeconds = 120,
    [switch]$RequireLayers,
    [switch]$ForceCloneFallback,
    [switch]$NamedPlotStyles,
    [switch]$AsymmetricPageSettings,
    [string]$PageSetupCase,
    [ValidateSet('PETA','PETADATA','PETADATADIAG')][string]$Command = 'PETA',
    [ValidateSet('Model','Paper','PaperMixed','LayoutModel')][string]$Space = 'Model'
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$tfm = switch ($Ver) { "2024" { "net48" }; "2026" { "net8.0-windows" }; "2027" { "net10.0-windows" }; default { throw "未対応版: $Ver" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\$Config\$tfm\CadPeta.Plugin.Acad.dll"
$inputName = if ($NamedPlotStyles) { 'STB_RECT.dwg' } elseif ($AsymmetricPageSettings -or $PageSetupCase) { 'TESTCHART2_layout.dwg' } elseif ($Space -eq 'Model') { 'TESTCHART2.dwg' } else { 'TESTCHART2_layout.dwg' }
$input = "$repo\tests\assets\testcharts\$inputName"
if ($Space -eq 'PaperMixed' -and $Command -ne 'PETA') { throw "PaperMixed は PETA の部分出力確認専用です" }
if ($ForceCloneFallback -and $Command -ne 'PETA') { throw "ForceCloneFallback は側DB絞り込み（PETA）専用です" }
if ($NamedPlotStyles -and ($Command -ne 'PETA' -or $Space -ne 'Model')) { throw "NamedPlotStyles は PETA の Model 空間専用です" }
if ($AsymmetricPageSettings -and $Command -ne 'PETA') { throw "AsymmetricPageSettings は PETA 専用です" }
if ($AsymmetricPageSettings -and $Ver -ne '2026') { throw "AsymmetricPageSettings の独立計測器は AutoCAD 2026 専用です" }
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
# STB 図面＋複製フォールバックは、側DB既定（CTB）とのモード不一致で中止することを固定する
$expectPlotStyleAbort = $NamedPlotStyles -and $ForceCloneFallback
if (-not (Test-Path $acad)) { throw "accoreconsole がない: $acad" }
if (-not (Test-Path $plugin)) { throw "プラグインがない: $plugin" }
if (-not (Test-Path $input)) { throw "入力図面がない: $input" }

$outRootFull = [IO.Path]::GetFullPath($OutRoot).TrimEnd('\')
$driveRoot = [IO.Path]::GetPathRoot($outRootFull).TrimEnd('\')
if (-not $outRootFull -or $outRootFull -eq $driveRoot) { throw "OutRoot にドライブ直下は指定できません: $outRootFull" }
New-Item -ItemType Directory -Force $outRootFull | Out-Null
$scenarioName = "$Command-$Space" + $(if ($RequireLayers) { '-layers' } else { '' }) + $(if ($NamedPlotStyles) { '-stb' } else { '' }) + $(if ($ForceCloneFallback) { '-fallback' } else { '' }) + $(if ($AsymmetricPageSettings) { '-asymmetric-pages' } else { '' }) + $(if ($PageSetupCase) { "-psp-$($PageSetupCase.ToLowerInvariant())" } else { '' })
$work = [IO.Path]::GetFullPath((Join-Path $outRootFull $scenarioName))
if ([IO.Path]::GetDirectoryName($work) -ne $outRootFull) { throw "作業フォルダが OutRoot の直下ではありません: $work" }
if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

$sourceHash = (Get-FileHash $input -Algorithm SHA256).Hash
$dwg = Join-Path $work "selection.dwg"
Copy-Item $input $dwg
# PETADATA 系は尺度プロンプト（1:N）を持つ。PETA の範囲プロンプトに尺度はない
$scaleInput = if ($RequireLayers) { "_Layer`r`n1" } else { "1" }
$spaceSetup = switch ($Space) {
    'Model' { '(setvar "TILEMODE" 1)' }
    'LayoutModel' { "(setvar `"TILEMODE`" 0)`r`n(setvar `"CTAB`" `"PETA_A3`")`r`n_.MSPACE" }
    default { "(setvar `"TILEMODE`" 0)`r`n(setvar `"CTAB`" `"PETA_A3`")`r`n_.PSPACE`r`n(setvar `"CLAYER`" `"0`")`r`n(setvar `"LIMCHECK`" 0)" }
}
$selectionSetup = if ($Space -in @('Paper','PaperMixed')) {
    @'
(setq cadpetaRect (entmakex (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") (cons 8 "0") '(67 . 1) (cons 410 "PETA_A3") '(100 . "AcDbPolyline") '(90 . 4) '(70 . 1) '(10 0.0 0.0) '(10 20.0 0.0) '(10 20.0 10.0) '(10 0.0 10.0))))
'@
} elseif ($Space -eq 'LayoutModel' -or (($AsymmetricPageSettings -or $PageSetupCase) -and $Space -eq 'Model')) {
    '(setq cadpetaModelSel (ssget "_X" ''((67 . 0))))'
} else { '' }
if ($Space -eq 'PaperMixed') {
    $selectionSetup += @'

(setq cadpetaSel (ssadd (entlast)))
(setq cadpetaVps (ssget "_X" (list '(0 . "VIEWPORT") (cons 410 "PETA_A3"))))
(setq cadpetaI 0)
(if cadpetaVps (repeat (sslength cadpetaVps) (ssadd (ssname cadpetaVps cadpetaI) cadpetaSel) (setq cadpetaI (1+ cadpetaI))))
'@
}
$selectionToken = switch ($Space) {
    'Paper' { '!cadpetaRect' }
    'PaperMixed' { '!cadpetaSel' }
    'LayoutModel' { '!cadpetaModelSel' }
    'Model' { if ($AsymmetricPageSettings -or $PageSetupCase) { '!cadpetaModelSel' } else { 'all' } }
}
$commandInput = if ($Command -eq 'PETA') {
    # _Object → 選択 → 空行（選択終了）→ 範囲オプション（L 等）→ 空行（Enter=全体）。
    # 非対応混在（PaperMixed）は範囲確定後の部分出力確認へ Y を返す。
    # 行配列を join で組み、末尾へ余計な空行（コマンド再実行の Enter）を作らない
    $petaLines = @('PETA', '_Object', $selectionToken, '')
    if ($RequireLayers) { $petaLines += '_Layer' }
    $petaLines += ''
    if ($Space -eq 'PaperMixed') { $petaLines += 'Y' }
    $petaLines -join "`r`n"
} else {
    "$Command`r`n$selectionToken`r`n`r`n$scaleInput"
}
$scr = Join-Path $work "run.scr"
$pageSetupCommands = ''
if ($AsymmetricPageSettings -or $PageSetupCase) {
    $probe = "$repo\tests\tools\regression\window_contract\WindowContractProbe\bin\Release\net8.0-windows\WindowContractProbe.dll"
    if (-not (Test-Path $probe)) { throw "ページ設定計測器が未ビルド: $probe" }
    $probeCommand = if ($PageSetupCase) { 'CPSETPAIRWISE' } else { 'CPSETTESTPAGES' }
    $pageSetupCommands = "NETLOAD`r`n$probe`r`n$probeCommand"
}
if ($PageSetupCase) {
    # 計測器 CPSETPAIRWISE への因子受け渡し。用紙探索水準の実デバイス解決は計測器側で行う
    $env:CADPETA_PSP_ID = $pspCase.Id
    $env:CADPETA_PSP_TARGET = if ($Space -eq 'Paper') { 'PETA_A3' } else { 'Model' }
    $env:CADPETA_PSP_UNITS = $pspCase.Units
    $env:CADPETA_PSP_SCALEKIND = $pspCase.ScaleKind
    $env:CADPETA_PSP_ROTATION = [string]$pspCase.Rotation
    $env:CADPETA_PSP_MEDIA = $pspCase.MediaScenario
    if ($pspCase.ScaleKind -eq 'Custom') {
        $env:CADPETA_PSP_NUM = $pspCase.CustomScale.Numerator.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        $env:CADPETA_PSP_DEN = $pspCase.CustomScale.Denominator.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    } else {
        Remove-Item Env:CADPETA_PSP_NUM, Env:CADPETA_PSP_DEN -ErrorAction SilentlyContinue
    }
}
$layoutBefore = Join-Path $work 'layout-before.lspdata'
$layoutAfter = Join-Path $work 'layout-after.lspdata'
$env:CADPETA_LAYOUT_BEFORE = $layoutBefore
$env:CADPETA_LAYOUT_AFTER = $layoutAfter
# FILEDIA は書かない（ヘッドレスは常に0で走り、書けば汚染の形だけが残る）
@"
(setvar "SECURELOAD" 0)
(defun cadpeta-layout-snapshot (path / d e rows f)
  (setq d (cdr (assoc 350 (dictsearch (namedobjdict) "ACAD_LAYOUT"))))
  (setq e (dictnext d T))
  (while e
    (setq rows (cons (list (cdr (assoc 3 e)) (entget (cdr (assoc 350 e)))) rows))
    (setq e (dictnext d)))
  (setq f (open path "w"))
  (prin1 rows f)
  (close f))
$spaceSetup
$selectionSetup
$pageSetupCommands
(cadpeta-layout-snapshot (getenv "CADPETA_LAYOUT_BEFORE"))
NETLOAD
$plugin
$commandInput
(cadpeta-layout-snapshot (getenv "CADPETA_LAYOUT_AFTER"))
_.CLOSE
_Y
_.QUIT
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $work "stdout.log"
$stderr = Join-Path $work "stderr.log"
if ($ForceCloneFallback) { $env:CADPETA_FORCE_SELECTION_CLONE_FALLBACK = '1' }
else { Remove-Item Env:CADPETA_FORCE_SELECTION_CLONE_FALLBACK -ErrorAction SilentlyContinue }
# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了（タイムアウトの強制終了で
# FileDialog=0 が永続化する実測がある）を含む finally で必ず復元する
. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {
    $p = Start-Process -FilePath $acad -ArgumentList @('/i', $dwg, '/s', $scr, '/l', 'ja-JP') -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit()
        $tail = ((Get-Content $stdout -Encoding Unicode -Tail 40 -ErrorAction SilentlyContinue) + (Get-Content $stderr -Tail 40 -ErrorAction SilentlyContinue)) -join [Environment]::NewLine
        throw "accoreconsole が ${TimeoutSeconds}秒で完了しない`n$tail"
    }
    if ($p.ExitCode -ne 0) {
        $tail = ((Get-Content $stdout -Encoding Unicode -Tail 40 -ErrorAction SilentlyContinue) + (Get-Content $stderr -Tail 40 -ErrorAction SilentlyContinue)) -join [Environment]::NewLine
        throw "accoreconsole が異常終了: exit=$($p.ExitCode)`n$tail"
    }
}
finally {
    # finally 内では throw しない（実行中の例外を握りつぶすため）。復元は必ず試み、検証結果は後段で判定する
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
    if (-not $regReport.ok) { Write-Warning ("レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ')) }
}
if (-not $regReport.ok) { throw ("レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ')) }
if ((Get-FileHash $dwg -Algorithm SHA256).Hash -ne $sourceHash) { throw "入力 DWG が変更された" }

$pdf = @(Get-ChildItem $work -Filter *.pdf -Recurse)
$svg = @(Get-ChildItem $work -Filter *.svg -Recurse)
$emf = @(Get-ChildItem $work -Filter *.emf -Recurse)
if ($ForceCloneFallback -and -not (Select-String -Path $stdout -Encoding Unicode -Pattern 'オブジェクト複製で側DBを作る' -Quiet)) {
    throw "複製フォールバック経路の実走を確認できない: $stdout"
}
if ($AsymmetricPageSettings -and -not (Select-String -Path $stdout -Encoding Unicode -Pattern 'CPSETTESTPAGES_OK' -Quiet)) {
    throw "非対称ページ設定を適用できなかった: $stdout"
}
if ($PageSetupCase -and -not (Select-String -Path $stdout -Encoding Unicode -Pattern "CPSETPAIRWISE_OK id=$PageSetupCase " -Quiet)) {
    throw "pairwiseページ設定を適用できなかった: $stdout"
}
if ($AsymmetricPageSettings -or $PageSetupCase) {
    if (-not (Test-Path $layoutBefore) -or -not (Test-Path $layoutAfter)) {
        throw "ページ設定snapshotが生成されなかった: before=$layoutBefore after=$layoutAfter"
    }
    $beforeHash = (Get-FileHash $layoutBefore -Algorithm SHA256).Hash
    $afterHash = (Get-FileHash $layoutAfter -Algorithm SHA256).Hash
    if ($beforeHash -ne $afterHash) {
        throw "PETA前後でLayout DBObjectが変化した: before=$beforeHash after=$afterHash"
    }
}
if ($expectPlotStyleAbort) {
    if (-not (Select-String -Path $stdout -Encoding Unicode -Pattern '側DBの CTB/STB モードが元図面と一致しない' -Quiet)) {
        throw "STB×複製フォールバックのモード不一致中止を確認できない: $stdout"
    }
    if ($pdf.Count -ne 0 -or $svg.Count -ne 0 -or $emf.Count -ne 0) {
        throw "モード不一致中止後に成果物が生成された: pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count)"
    }
    "$Command/$Space(stb+fallback) OK: モード不一致で中止（成果物なし・入力DWG不変）"
    return
}
if ($Command -eq 'PETA' -and ($pdf.Count -ne 1 -or $svg.Count -ne 1 -or $emf.Count -ne 1)) {
    throw "PETA 成果物数が不正: pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count) ($stdout)"
}
if ($Command -eq 'PETADATA' -and ($svg.Count -ne 1 -or $pdf.Count -ne 0 -or $emf.Count -ne 0)) {
    throw "PETADATA 成果物数が不正: pdf=$($pdf.Count) svg=$($svg.Count) emf=$($emf.Count)"
}
if ($Command -eq 'PETADATADIAG') {
    $stageSvg = @($svg | Where-Object { $_.Name -match '\.stage\d+\.' })
    $mainSvg = @($svg | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
    $diagJson = @(Get-ChildItem $work -Filter *.diag.json -Recurse)
    if ($mainSvg.Count -ne 1 -or $stageSvg.Count -lt 1 -or $diagJson.Count -ne 1 -or $pdf.Count -ne 0 -or $emf.Count -ne 0) {
        throw "PETADATADIAG 成果物数が不正: pdf=$($pdf.Count) svg=$($mainSvg.Count) stage=$($stageSvg.Count) diag=$($diagJson.Count) emf=$($emf.Count)"
    }
}
if ($RequireLayers -and -not (Select-String -Path $svg[0].FullName -Pattern 'inkscape:groupmode="layer"' -Quiet)) {
    throw "レイヤ別 SVG が生成されていない"
}
if ($Command -eq 'PETA') {
    if (-not (Select-String -Path $stdout -Encoding Unicode -Pattern '絞り込み: 選択\d+件中\d+件を出力対象にする' -Quiet)) {
        throw "絞り込みサマリを確認できない: $stdout"
    }
}
if ($Space -eq 'PaperMixed') {
    if (-not (Select-String -Path $stdout -Encoding Unicode -Pattern '非対応対象を除外して部分出力する' -Quiet)) {
        throw "部分出力確認プロンプトを確認できない: $stdout"
    }
    if (-not (Select-String -Path $stdout -Encoding Unicode -Pattern '絞り込みの除外候補:.*Controller' -Quiet)) {
        throw "制御Viewportの除外記録を確認できない: $stdout"
    }
    if (Select-String -Path $stdout -Encoding Unicode -Pattern '非対応対象が含まれるため中止する' -Quiet) {
        throw "部分出力へ同意した後に絞り込みが中止された: $stdout"
    }
}
$flags = $(if ($NamedPlotStyles) { '(stb)' } else { '' }) + $(if ($ForceCloneFallback) { '(fallback)' } else { '' })
"$Command/$Space$flags OK: pdf=$($pdf.Count) svg=$($svg[0].FullName) emf=$($emf.Count)"
