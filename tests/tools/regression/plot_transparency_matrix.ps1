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
#   - 実行後、PLOTTRANSPARENCYOVERRIDE が開始値へ復元され、復元コマンドの読み戻しが一致する
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
# 注意:   PLOTTRANSPARENCYOVERRIDE はレジストリ保存のため、実行前の値を記録し必ず復元する。
#         PETA はクリップボードを上書きする（計5回）。
param(
    [string]$AcadRoot = "C:\Program Files\Autodesk\AutoCAD 2026",
    [string]$Tfm = "net8.0-windows",
    [string]$Config = "Debug",
    [switch]$RecordGolden
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\$Config\$Tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグインが未ビルド: $plugin（dotnet build CadPeta.sln を先に実行すること）" }
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

# ---------- 事前 probe: 変数の存在と現在値（復元用） ----------
$probeTxt = ($work -replace '\\', '/') + "/probe0.txt"
@"
(progn (setq f (open "$probeTxt" "w")) (princ (vl-princ-to-string (getvar "PLOTTRANSPARENCYOVERRIDE")) f) (princ "\n" f) (princ (vl-princ-to-string (getvar "PLOTTRANSPARENCY")) f) (princ "\n" f) (close f))
_.QUIT _Y
"@ | Out-File "$work\probe0.scr" -Encoding ascii
& $accore /i "$repo\tests\assets\testcharts\TESTCHART2.dwg" /s "$work\probe0.scr" /l ja-JP 2>&1 |
    Out-File "$work\probe0_stdout.log" -Encoding utf8
$probe = Get-Content "$work\probe0.txt"
$initialOverride = [int]$probe[0]
"PLOTTRANSPARENCYOVERRIDE 初期値=$initialOverride / PLOTTRANSPARENCY=$($probe[1])（nil=変数が存在しない）"

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

$results = @()
$failures = @()
# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する。
# 内側の finally（PLOTTRANSPARENCYOVERRIDE の CAD 経由復元）が先に走り、外側で残差を検証する
. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {
try {
    foreach ($c in $cases) {
        $dir = Join-Path $work $c.id
        New-Item -ItemType Directory -Force $dir | Out-Null
        Get-ChildItem $dir | Remove-Item -Force -ErrorAction SilentlyContinue
        $dwgCopy = Join-Path $dir ([IO.Path]::GetFileName($c.dwg))
        Copy-Item $c.dwg $dwgCopy -Force

        $ctabLine = if ($c.ctab) { "(setvar `"CTAB`" `"$($c.ctab)`")" } else { "" }
        @"
(setvar "SECURELOAD" 0)
(setvar "PLOTTRANSPARENCYOVERRIDE" $($c.ovr))
$ctabLine
NETLOAD
$plugin
PETADIAG

(princ "\nCADPETA_CASE_END=$($c.id)")(princ)
_.QUIT _Y
"@ | Out-File "$dir\run.scr" -Encoding ascii
# ↑ PETADIAG 直後の空行は範囲プロンプトへの Enter=全体出力。
#   PETA はファイル出力(F)を指定しない限り diag.json を書かないため、診断を常に書く
#   PETADIAG で駆動する（旧 PETA 駆動は全ケース diag 不在の -1 を出し、判定が空転していた）。
#   完了マーカーは QUIT 直前に置き、stdout に現れなければ成果物を評価せず FAIL とする

        # accoreconsole の stdout は UTF-16。パイプ経由は PowerShell の再デコードで
        # マーカー文字列が壊れるため、Start-Process のリダイレクトで生のまま受けて Unicode で読む
        $p = Start-Process -FilePath $accore -ArgumentList @('/i', $dwgCopy, '/s', "$dir\run.scr", '/l', 'ja-JP') `
            -RedirectStandardOutput "$dir\stdout.log" -RedirectStandardError "$dir\stderr.log" -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(300 * 1000)) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
        }
        $code = $p.ExitCode
        $stdoutText = if (Test-Path "$dir\stdout.log") { Get-Content "$dir\stdout.log" -Raw -Encoding Unicode } else { "" }
        $markerFound = $stdoutText -match [regex]::Escape("CADPETA_CASE_END=$($c.id)")

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
        if ($code -ne 0) { $caseFail.Add("accoreconsole 異常終了: exit=$code") }
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
        "$($c.id): effective=$effective exit=$code marker=$markerFound 透過件数=$trans pdf=$pdfCount svg=$svgCount emf=$emfCount diag=$diagCount stage=$stageCount -> $judge"
        if ($caseFail.Count -gt 0) {
            $failures += "[$($c.id)] " + ($caseFail -join '; ')
        }
        $results += [pscustomobject]@{
            case = $c.id; ovr = $c.ovr; effective = $effective; exit = $code; marker = $markerFound
            透過件数 = $trans; pdf = $pdfCount; svg = $svgCount; emf = $emfCount; diag = $diagCount; stage = $stageCount
            judge = $judge
        }
    }
}
finally {
    # ---------- 初期値へ復元（レジストリ保存のため必須）。復元コマンドの読み戻しを検証する ----------
    $restoreTxt = ($work -replace '\\', '/') + "/restore.txt"
    @"
(setvar "PLOTTRANSPARENCYOVERRIDE" $initialOverride)
(progn (setq f (open "$restoreTxt" "w")) (princ (vl-princ-to-string (getvar "PLOTTRANSPARENCYOVERRIDE")) f) (close f))
_.QUIT _Y
"@ | Out-File "$work\restore.scr" -Encoding ascii
    & $accore /i "$repo\tests\assets\testcharts\TESTCHART2.dwg" /s "$work\restore.scr" /l ja-JP 2>&1 |
        Out-File "$work\restore_stdout.log" -Encoding utf8
    $restoreExit = $LASTEXITCODE
    $restoreReadback = if (Test-Path $restoreTxt) { (Get-Content $restoreTxt | Select-Object -First 1) } else { $null }
    $restoreOk = ($restoreExit -eq 0) -and ($restoreReadback -eq "$initialOverride")
    "restore: PLOTTRANSPARENCYOVERRIDE=$initialOverride (exit=$restoreExit 読み戻し=$restoreReadback)"
    if (-not $restoreOk) {
        $failures += "復元検証に失敗: exit=$restoreExit 読み戻し=$restoreReadback 期待=$initialOverride"
    }
}
}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
    if (-not $regReport.ok) { $failures += "レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ') }
}

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
