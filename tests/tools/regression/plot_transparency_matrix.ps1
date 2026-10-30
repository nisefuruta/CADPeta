# 申し送り#6（推奨プロット設定）の実測ハーネス: PETA の透過保持が「実効の透過プロット設定」に
# 従うことを、ページ設定フラグ × PLOTTRANSPARENCYOVERRIDE の両方向で確認する。
# PETADIAG（診断モード。diag.json とステージ SVG のファイル出力を強制する）を駆動し、
# 期待表つきで合否判定する（件数を記録するだけの観測では回帰を検出できない）。
#
# 期待表（各ケースの effective = (override==2) または (override==1 かつ ページ設定の透過フラグON)）:
#   A_model_ovr1  モデル(ON)  ovr=1 -> effective=true
#   B_model_ovr0  モデル(ON)  ovr=0 -> effective=false
#   C_model_ovr2  モデル(ON)  ovr=2 -> effective=true
#   D_layout_ovr1 レイアウト(OFF) ovr=1 -> effective=false
#   E_layout_ovr2 レイアウト(OFF) ovr=2 -> effective=true
#
# 判定:
#   - effective=true  : diag.json の「透過の不透明化」件数合計が golden 値と厳密一致
#   - effective=false : 同件数が 0
#   - 各ケース共通: exit=0、完了マーカー（CADPETA_CASE_END=<ケース>）が stdout にある、
#     成果物が PETADIAG の期待集合（pdf1・svg1・emf1・diag1・stage1以上）を満たす
#   - 起動の最後に、PLOTTRANSPARENCYOVERRIDE が起動の最初の値へ戻り、読み戻しが一致する
#   1件でも不一致なら非0終了する（Write-Warning だけで続行しない）。
#
# golden 値は同ディレクトリの plot_transparency_golden.json（ケースキー→期待件数）から読む。
# ファイルが無い・キーが無ければ「golden 未採取」として非0終了する（黙って PASS にしない）。
# -RecordGolden を付けると判定を行わず、測定値を golden ファイルへ書き出す
# （採取モード。書き出した内容は人手で確認・承認してからコミットすること）。
#
# 使い方: pwsh -File tests/tools/regression/plot_transparency_matrix.ps1 [-AcadRoot <AutoCADフォルダ>] [-Tfm net8.0-windows]
#         pwsh -File tests/tools/regression/plot_transparency_matrix.ps1 -RecordGolden   # golden 採取
# 出力:   %TEMP%\CadPeta_plot_transparency\ に各ケースの成果物と results.json を残す
# 注意:   PLOTTRANSPARENCYOVERRIDE はレジストリ保存のため、起動の最初の値を記録し、起動の最後に戻して読み戻す。
#         PETA はクリップボードを上書きする（計5回）。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$AcadRoot = "C:\Program Files\Autodesk\AutoCAD 2026",
    [string]$Tfm = "net8.0-windows",
    [switch]$RecordGolden
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'plot_transparency_matrix.ps1' ''
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
. "$PSScriptRoot\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $Tfm)
$accore = "$AcadRoot\accoreconsole.exe"
$work = Join-Path $env:TEMP "CadPeta_plot_transparency"
New-Item -ItemType Directory -Force $work | Out-Null
$goldenPath = Join-Path $PSScriptRoot "plot_transparency_golden.json"

# ---------- golden の読み込み（採取モードでは使わない） ----------
$goldenLoaded = $false
$goldenIn = $null
if (-not $RecordGolden) {
    if (Test-Path $goldenPath) {
        $goldenIn = Get-Content $goldenPath -Raw | ConvertFrom-Json -AsHashtable
        $goldenLoaded = $true
    }
}
$goldenOut = [ordered]@{}

# ---------- ケース定義 ----------
# モデルタブ = ページ設定の透過フラグ「オン」の図面（Phase 0 対話プロットで保存済み）
# レイアウト PETA_A3 = 透過フラグ「オフ」の図面（gen_layout_testchart.scr 生成の既定）
$cases = @(
    @{ id = "A_model_ovr1";  dwg = "$repo\tests\assets\testcharts\TESTCHART2.dwg";        ovr = 1; ctab = $null;      pageOn = $true },
    @{ id = "B_model_ovr0";  dwg = "$repo\tests\assets\testcharts\TESTCHART2.dwg";        ovr = 0; ctab = $null;      pageOn = $true },
    @{ id = "C_model_ovr2";  dwg = "$repo\tests\assets\testcharts\TESTCHART2.dwg";        ovr = 2; ctab = $null;      pageOn = $true },
    @{ id = "D_layout_ovr1"; dwg = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg"; ovr = 1; ctab = "PETA_A3";  pageOn = $false },
    @{ id = "E_layout_ovr2"; dwg = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg"; ovr = 2; ctab = "PETA_A3";  pageOn = $false }
)

# 図面ごとに、その図面のケースを 1 回の起動で続けて実行する（tests/tools/common/accore_batch.ps1）。
# PLOTTRANSPARENCYOVERRIDE はレジストリに保存されるので、起動の最初に値を読み、最後に戻して読み戻す。
# 起動が止まって最後の行が実行されなかったときは、外側のレジストリ保全（CadRegGuard）が戻す。
# SECURELOAD と TRUSTEDPATHS は変えず、信頼済みのフォルダへコピーしたプラグインを NETLOAD する
. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"
$head = @(
    '(setq cpTrOld (getvar "PLOTTRANSPARENCYOVERRIDE"))'
    '(princ (strcat "\nCADPETA_PTO_INITIAL=" (itoa cpTrOld) " PLOTTRANSPARENCY=" (vl-princ-to-string (getvar "PLOTTRANSPARENCY"))))(princ)'
) + (Get-AccoreNetloadLines $plugin)
$tail = @(
    '(setvar "PLOTTRANSPARENCYOVERRIDE" cpTrOld)'
    '(princ (strcat "\nCADPETA_PTO_RESTORED=" (itoa (getvar "PLOTTRANSPARENCYOVERRIDE"))))(princ)'
    '_.QUIT', '_Y'
)

$results = @()
$failures = @()
# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion (Get-AcadVersionFromPath $AcadRoot)
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    foreach ($group in ($cases | Group-Object { $_.dwg })) {
        # PETADIAG 直後の空行は範囲プロンプトへの Enter=全体出力。
        # PETA はファイル出力(F)を指定しない限り diag.json を書かないため、診断を常に書く
        # PETADIAG で駆動する（旧 PETA 駆動は全ケース diag 不在の -1 を出し、判定が空転していた）
        $batchCases = @($group.Group | ForEach-Object {
            $lines = @("(setvar `"PLOTTRANSPARENCYOVERRIDE`" $($_.ovr))")
            if ($_.ctab) { $lines += "(setvar `"CTAB`" `"$($_.ctab)`")" }
            @{ Id = $_.id; Lines = $lines + @('PETADIAG', ''); Def = $_ }
        })
        $launchName = [IO.Path]::GetFileNameWithoutExtension($group.Name)
        $runs = Invoke-AccoreBatch -Acc $accore -DwgSrc $group.Name -Cases $batchCases -Head $head -Tail $tail `
            -WorkRoot $work -LaunchName $launchName -TimeoutSeconds 300

        # 起動の最初の値へ戻ったこと（読み戻し）
        $all = Get-Content $runs[$batchCases[0].Id].StdoutPath -Raw -Encoding Unicode
        $initial = [regex]::Match($all, 'CADPETA_PTO_INITIAL=(\d+) PLOTTRANSPARENCY=(\S+)')
        $restored = [regex]::Match($all, 'CADPETA_PTO_RESTORED=(\d+)')
        if (-not $initial.Success -or -not $restored.Success -or $initial.Groups[1].Value -ne $restored.Groups[1].Value) {
            $failures += "[$launchName] PLOTTRANSPARENCYOVERRIDE の復元を確認できない: 最初=$($initial.Groups[1].Value) 最後=$($restored.Groups[1].Value)"
        }
        "[$launchName] PLOTTRANSPARENCYOVERRIDE 最初=$($initial.Groups[1].Value) 最後=$($restored.Groups[1].Value) / PLOTTRANSPARENCY=$($initial.Groups[2].Value)（nil=変数が存在しない）"

        foreach ($bc in $batchCases) {
            $c = $bc.Def
            $r = $runs[$c.id]
            $dir = $r.CaseDir
            $markerFound = $r.Output.Contains("CADPETA_CASE_END=$($c.id)")

            $diagFile = Get-ChildItem "$dir\*.diag.json" -ErrorAction SilentlyContinue | Select-Object -First 1
            # diag 不在は後段の caseFail（「diag.json 不在」）として集計し、非0終了へ倒す
            $trans = -1
            $pdfCount = 0; $svgCount = 0; $emfCount = 0; $diagCount = 0; $stageCount = 0
            if ($diagFile) {
                $baseName = $diagFile.Name -replace '\.svg\.diag\.json$', ''
                $diag = Get-Content $diagFile.FullName -Raw | ConvertFrom-Json
                $trans = [int](($diag | Where-Object { $_.type -like "*透過の不透明化*" } | Measure-Object -Sum count).Sum ?? 0)
                $pdfCount = @(Get-ChildItem $dir -Filter "$baseName.pdf").Count
                $svgCount = @(Get-ChildItem $dir -Filter "$baseName.svg").Count
                $emfCount = @(Get-ChildItem $dir -Filter "$baseName.emf").Count
                $diagCount = @(Get-ChildItem $dir -Filter "$baseName.svg.diag.json").Count
                $stageCount = @(Get-ChildItem $dir -Filter "$baseName.stage*.svg").Count
            }
            $effective = ($c.ovr -eq 2) -or ($c.ovr -eq 1 -and $c.pageOn)

            $caseFail = New-Object System.Collections.Generic.List[string]
            if ($r.TimedOut) { $caseFail.Add($r.TimeoutDetail) }
            if ($r.ExitCode -ne 0) { $caseFail.Add("accoreconsole 異常終了: $($r.ExitDetail)") }
            if (-not $r.State.Ok) { $caseFail.Add($r.State.Detail) }
            if (-not $markerFound) { $caseFail.Add("完了マーカー未検出(CADPETA_CASE_END=$($c.id))") }
            if (-not $diagFile) { $caseFail.Add("diag.json 不在") }
            else {
                if ($pdfCount -ne 1) { $caseFail.Add("pdf 件数=$pdfCount(期待1)") }
                if ($svgCount -ne 1) { $caseFail.Add("svg 件数=$svgCount(期待1)") }
                if ($emfCount -ne 1) { $caseFail.Add("emf 件数=$emfCount(期待1)") }
                if ($diagCount -ne 1) { $caseFail.Add("diag 件数=$diagCount(期待1)") }
                if ($stageCount -lt 1) { $caseFail.Add("stage svg 件数=$stageCount(期待1以上)") }
            }

            if ($RecordGolden) {
                if ($caseFail.Count -eq 0) { $goldenOut[$c.id] = $trans }
                else { $caseFail.Add("採取不可のため golden へ記録しない") }
            }
            elseif ($caseFail.Count -eq 0) {
                if ($effective) {
                    if (-not $goldenLoaded) { $caseFail.Add("golden 未採取: $goldenPath が無い（-RecordGolden で採取すること）") }
                    elseif (-not $goldenIn.ContainsKey($c.id)) { $caseFail.Add("golden にキー $($c.id) が無い（-RecordGolden で採取すること）") }
                    else {
                        $expect = [int]$goldenIn[$c.id]
                        if ($trans -ne $expect) { $caseFail.Add("透過件数不一致: 実際=$trans golden=$expect") }
                    }
                }
                else {
                    if ($trans -ne 0) { $caseFail.Add("透過件数が0でない(effective=false): 実際=$trans") }
                }
            }

            $judge = if ($caseFail.Count -eq 0) { "PASS" } else { "FAIL" }
            "$($c.id): effective=$effective $($r.ExitDetail) marker=$markerFound 透過件数=$trans pdf=$pdfCount svg=$svgCount emf=$emfCount diag=$diagCount stage=$stageCount -> $judge"
            if ($caseFail.Count -gt 0) {
                $failures += "[$($c.id)] " + ($caseFail -join '; ')
            }
            $results += [pscustomobject]@{
                case = $c.id; ovr = $c.ovr; effective = $effective; exit = $r.ExitCode; marker = $markerFound
                透過件数 = $trans; pdf = $pdfCount; svg = $svgCount; emf = $emfCount; diag = $diagCount; stage = $stageCount
                judge = $judge
            }
        }
    }
}
catch { $runError = $_ }
finally {
    "所要 {0:n0} 秒（図面ごとに 1 回の起動）" -f $sw.Elapsed.TotalSeconds
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
    if (-not $regReport.ok) { $failures += "レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ') }
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（ケースの判定は途中までなので、一覧は表示しない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }
$results | ConvertTo-Json | Out-File "$work\results.json" -Encoding utf8
$results | Format-Table -AutoSize

if ($RecordGolden) {
    if ($failures.Count -gt 0) {
        ""
        "=== 採取失敗（golden は書き出さない） ==="
        $failures | ForEach-Object { $_ }
        exit 1
    }
    $goldenOut | ConvertTo-Json | Out-File $goldenPath -Encoding utf8
    ""
    "採取モード（合否判定なし・要人手承認）: $goldenPath へ書き出した。内容を確認し人手で承認してからコミットすること"
    exit 0
}

if ($failures.Count -gt 0) {
    ""
    "=== 失敗 ==="
    $failures | ForEach-Object { $_ }
    exit 1
}
"5ケース合格（effective=true は golden と一致・effective=false は透過件数0）"
exit 0
