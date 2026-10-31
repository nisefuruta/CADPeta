# PETA/PETADATA オプション全組み合わせのシナリオ駆動 E2E ランナー。
#
# コマンドのプロンプト処理は4製品共通の単一実装（RangePrompt/PetaRunner）であり、本ランナーは
# AutoCAD の accoreconsole でオプションの全因子組み合わせを実機駆動し、成果物と表示文言を
# 機械判定する。シナリオと判定条件は scenarios.json へ書き出し、結果は results へ集約する。
#
# セット:
#   Trial    … Space×Range×O の8組（B対付き）と PETADATA 代表4組ほかを試走し、内容フラグ
#              （has_text/has_transparency）と golden 数値の候補を trial_report.json へ出す。
#              合否判定はせず、フラグ確定（人手承認 → peta_option_matrix_flags.json）の材料を作る
#   PetaFull … PETA 全因子 2^8=256（Space×Range×O×M×L×T×F×B）
#   PetaEdge … PETA エッジ10件（A=Enter一致、縮退窓、B不正、Enter維持、トグル偶奇、O解除、先選択）
#   PetaData … PETADATA 全因子 2^5=32 ＋ グラデーションDPI 2 ＋ 空選択 1
#   DiagSmoke… PETADIAG / PETADATADIAG 各1（診断出力の実在確認）
#   Smoke    … 固定12件（-Ver 2027 の版差スモーク用）
#
# 使い方:
#   pwsh -File tests/tools/regression/peta_option_matrix.ps1 -Set Trial
#   pwsh -File tests/tools/regression/peta_option_matrix.ps1 -Set PetaFull [-CaseFilter '...']
#   pwsh -File tests/tools/regression/peta_option_matrix.ps1 -Set Smoke -Ver 2027 -CampaignId <既存ID>
#
# 前提: pwsh -File tests/tools/build_e2e.ps1 済み（配布物と同じビルドの DLL）。PetaFull/PetaData は peta_option_matrix_flags.json
# （試走から人手承認で固定した内容フラグと golden）が無いと実行を拒否する。
#
# 環境保全: scr に FILEDIA / SECURELOAD / TRUSTEDPATHS を書かない。プラグインは信頼済みのフォルダへ
# コピーして読み込み、終了後に削除する。さらに実行全体を HKCU の AutoCAD ハイブ対象値
# （FileDialog/SECURELOAD/TRUSTEDPATHS/PLOTTRANSPARENCYOVERRIDE）のスナップショット→
# 復元検査で包む（タイムアウトで自プロセスを停止した後も含む）。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('2026', '2027')][string]$Ver = '2026',
    [Parameter(Mandatory)]
    [ValidateSet('Trial', 'PetaFull', 'PetaEdge', 'PetaData', 'DiagSmoke', 'Smoke')][string]$Set,
    [string]$CaseFilter = '',
    [string]$OutRoot = "$env:TEMP\CadPeta_option_matrix",
    [string]$CampaignId = '',
    [int]$TimeoutSeconds = 240,
    [int]$PdfRenderComparisonTimeoutSeconds = 120,
    [switch]$ProbeOnly,
    # 1 ケース 1 起動で実行する（既定は図面の種類ごとに 1 回の起動で続けて実行する）
    [switch]$OneLaunchPerCase
)
$ErrorActionPreference = 'Stop'
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'peta_option_matrix.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

# ---------------------------------------------------------------- 環境と定数
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$tfm = switch ($Ver) { '2026' { 'net8.0-windows' } '2027' { 'net10.0-windows' } }
$acc = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
. "$PSScriptRoot\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $tfm)
$dwgByKind = @{
    model    = "$repo\tests\assets\testcharts\TESTCHART2.dwg"
    layout   = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg"
    gradient = "$repo\tests\assets\testcharts\gradient\gradient_e2e.dwg"
}
$flagsPath = Join-Path $PSScriptRoot 'peta_option_matrix_flags.json'
if (-not (Test-Path $acc)) { throw "accoreconsole がない: $acc" }
$dwgByKind.Values | ForEach-Object { if (-not (Test-Path $_)) { throw "入力図面がない: $_" } }

# レイアウトの窓の既知正解（peta_a3_window_expect.ps1 の表。accoreconsole の PDF のページの高さは 842pt に丸められる）
. "$PSScriptRoot\peta_a3_window_expect.ps1"
$PaperWin = @{ P1 = $PetaA3Window.P1; P2 = $PetaA3Window.P2 }
$ExpectPaperWinM0 = $PetaA3WindowExpect.PageHeight842pt.M0
$ExpectPaperWinM5 = $PetaA3WindowExpect.PageHeight842pt.M5
$ExpectPaperFull = $PetaA3WindowExpect.PageHeight842pt.Full
# Paper の O 用に entmake する矩形（標準窓の内側に置く）
$PaperRectLisp = '(setq cadpetaRect (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") (cons 8 "0") ''(67 . 1) (cons 410 "PETA_A3") ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 60.0 50.0) ''(10 140.0 50.0) ''(10 140.0 100.0) ''(10 60.0 100.0))))'
$BgColor = '#FF8040'

# stdout 照合の文言（出典: src/CadPeta.UseCases/lang/1041.lang）。リテラルは Escape して使う
$Msg = @{
    # summary.line（正規表現）。「クリップボード登録失敗」を「登録」として通さない否定先読み付き
    SummaryLine    = 'bytes / \d+図形 → クリップボード登録(?!失敗)'
    # summary.no_files（F=1。設定の形式を 0 件にしてファイルを出さない）
    NoFiles        = [regex]::Escape('ファイル出力なし。出力する形式は PETAOPT で設定')
    LayerOn        = [regex]::Escape('レイヤ別グループ化: オン')
    LayerOff       = [regex]::Escape('レイヤ別グループ化: オフ')
    OutlineOn      = [regex]::Escape('文字アウトライン化: オン')
    BgState        = [regex]::Escape("EMF合成下地色: $BgColor")
    BgInvalid      = [regex]::Escape('EMF背景色は #RRGGBB 形式で指定すること')
    Degenerate     = [regex]::Escape('窓の幅または高さが 0 のため指定し直すこと')
    FilterSet      = [regex]::Escape('出力対象: 選択オブジェクトへ絞り込む')
    FilterCleared  = [regex]::Escape('出力対象: 先選択を含む絞り込みを解除（全オブジェクト）')
    FilterSummary  = '絞り込み: 選択\d+件中\d+件を出力対象にする'
    NoOcg          = [regex]::Escape('レイヤ情報(OCG)がないためレイヤ別グループ化を行わずに出力する')
    DataDone       = [regex]::Escape('[CadPeta] PETADATA 完了')
    DataEmpty      = [regex]::Escape('オブジェクトが選択されなかったため PETADATA を中止する')
    DiagHeader     = [regex]::Escape('---- 診断ログ（件数と対象） ----')
}

# 内容フラグと golden（試走から人手承認で固定する）。Trial 以外の PETA/PETADATA 系は必須
$flags = $null
if (Test-Path $flagsPath) { $flags = Get-Content $flagsPath -Raw | ConvertFrom-Json }
$needsFlags = $Set -in @('PetaFull', 'PetaEdge', 'PetaData', 'Smoke')
if ($needsFlags) {
    if (-not $flags) { throw "内容フラグ未確定: $flagsPath がない。先に -Set Trial を流して承認・作成すること" }
    foreach ($space in 'Model', 'Paper') {
        foreach ($r in 'A', 'W') {
            foreach ($o in 0, 1) {
                $k = "$space|$r|O$o"
                if (-not $flags.contentFlags.PSObject.Properties[$k]) { throw "内容フラグ未確定の組がある: $k（全量実行へ進まない）" }
            }
        }
    }
}
$ModelWin = if ($flags -and $flags.modelWindow) { @{ P1 = $flags.modelWindow.p1; P2 = $flags.modelWindow.p2 } }
            else { @{ P1 = '120,90'; P2 = '320,200' } }  # Trial の既定候補（文字行と透過50%列を含む見込みの2点）

# Model の O は、モデルの窓の内側に完全に入る図形（全図形の真部分集合）を選ぶ。
# 全体選択を使うと O が無視されても全体出力と一致し、偽 PASS になる。
# 窓と別の範囲から選ぶと、窓と O を組み合わせたケースで選んだ図形が範囲に入らず、出力が無くなる
$ModelSubsetLisp = '(setq cadpetaAll (ssget "_X" ''((410 . "Model")))) (setq cadpetaSubset (ssget "_W" ''(' + ($ModelWin.P1 -replace ',', ' ') + ') ''(' + ($ModelWin.P2 -replace ',', ' ') + '))) (princ (strcat "\nCADPETA_MODEL_SELECTION all=" (itoa (if cadpetaAll (sslength cadpetaAll) 0)) " subset=" (itoa (if cadpetaSubset (sslength cadpetaSubset) 0)))) (princ)'

function Get-ContentFlag([string]$space, [string]$r, [int]$o) {
    if (-not $flags) { return $null }
    $p = $flags.contentFlags.PSObject.Properties["$space|$r|O$o"]
    if ($p) { $p.Value } else { $null }
}

# ---------------------------------------------------------------- Win32（クリップボード読み戻し）
if (-not ('CadPeta.E2E.Clip' -as [type])) {
    Add-Type -Namespace CadPeta.E2E -Name Clip -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
[DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint fmt);
[DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
[DllImport("gdi32.dll")] public static extern uint GetEnhMetaFileBits(IntPtr hemf, uint cb, byte[] buf);
'@
}

# クリップボードの CF_ENHMETAFILE をバイト列で読む。ハンドルはクリップボード所有のため解放しない。
# 戻り値: @{ ok = 開閉に成功したか; bytes = byte[]（EMF なしは $null） }
function Read-ClipboardEmf {
    for ($i = 0; $i -lt 10; $i++) {
        if ([CadPeta.E2E.Clip]::OpenClipboard([IntPtr]::Zero)) {
            try {
                $h = [CadPeta.E2E.Clip]::GetClipboardData(14)  # CF_ENHMETAFILE
                if ($h -eq [IntPtr]::Zero) { return @{ ok = $true; bytes = $null } }
                $size = [CadPeta.E2E.Clip]::GetEnhMetaFileBits($h, 0, $null)
                if ($size -eq 0) { return @{ ok = $true; bytes = $null } }
                $buf = New-Object byte[] $size
                [void][CadPeta.E2E.Clip]::GetEnhMetaFileBits($h, $size, $buf)
                return @{ ok = $true; bytes = $buf }
            }
            finally { [void][CadPeta.E2E.Clip]::CloseClipboard() }
        }
        Start-Sleep -Milliseconds 200
    }
    return @{ ok = $false; bytes = $null }
}

function Get-BytesSha256([byte[]]$bytes) {
    if (-not $bytes) { return $null }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '') } finally { $sha.Dispose() }
}
function Get-StringSha256([string]$s) { Get-BytesSha256 ([Text.Encoding]::UTF8.GetBytes($s)) }

# PDF は生成時刻などの非描画情報を持ち得るため、ファイルハッシュではなく既存の
# compare_renders.py で描画内容を比較する。負の対照は通常の合格閾値ではなく、前景マスクが
# 完全一致していないことを JSON 指標で判定する。exit=2 は比較不能なので同一・相違とも FAIL にする。

function Compare-PdfRender([string]$a, [string]$b) {
    if (-not $a -or -not $b -or -not (Test-Path $a) -or -not (Test-Path $b)) {
        return @{ Exit = 2; Detail = "PDF がない: a='$a' b='$b'" }
    }
    $compareScript = Join-Path $PSScriptRoot 'peta_option_matrix\compare_renders.py'
    $projectDir = Split-Path -Parent $compareScript
    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()
    $p = $null
    try {
        $argList = @('run', '--project', $projectDir, 'python', $compareScript, $a, $b, '--dpi', '150')
        $p = Start-Process -FilePath 'uv' -ArgumentList (ConvertTo-NativeArgString $argList) `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru -WindowStyle Hidden
        $timedOut = -not $p.WaitForExit($PdfRenderComparisonTimeoutSeconds * 1000)
        if ($timedOut) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
        }
        $stdoutText = [string](Get-Content $stdoutPath -Raw)
        $stderrText = [string](Get-Content $stderrPath -Raw)
        if ($timedOut) {
            return @{ Exit = 2; Json = $null; Different = $false;
                Detail = "PDF描画比較が $PdfRenderComparisonTimeoutSeconds 秒でタイムアウトした（PID=$($p.Id)）" }
        }
        $json = $null
        if ($stdoutText) { try { $json = $stdoutText | ConvertFrom-Json } catch { } }
        $different = $false
        if ($p.ExitCode -in 0, 1 -and $json -and $json.overall) {
            $different = ([double]$json.overall.recall -lt 0.999999 -or
                          [double]$json.overall.precision -lt 0.999999 -or
                          [double]$json.overall.iou -lt 0.999999)
        }
        @{ Exit = $p.ExitCode; Json = $json; Different = $different; Detail = ($stdoutText + $stderrText).Trim() }
    }
    finally {
        if ($null -ne $p) { $p.Dispose() }
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- レジストリ保全
# 開始時に記録し、正常終了・エラー終了を問わず finally で復元・検証する（CadRegGuard.ps1）
. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\accore_batch.ps1"

# ---------------------------------------------------------------- キャンペーン記録
if (-not $CampaignId) { $CampaignId = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + ([guid]::NewGuid().ToString('N').Substring(0, 8)) }
if ($CampaignId -notmatch '^[A-Za-z0-9_-]+$') { throw "CampaignId は英数字・-・_ のみ: $CampaignId" }
$outRootFull = Resolve-OutRoot $OutRoot
$campaignDir = Join-Path $outRootFull $CampaignId
New-Item -ItemType Directory -Force (Join-Path $campaignDir 'results') | Out-Null

$campaignJson = Join-Path $campaignDir 'campaign.json'
if (-not (Test-Path $campaignJson)) {
    # 実行時 HEAD・差分・untracked を記録し、どの実装で測った結果かを一意にする
    $head = (git -C $repo rev-parse HEAD).Trim()
    $diffText = (git -C $repo diff HEAD --binary | Out-String)
    $untracked = @(git -C $repo -c core.quotepath=false ls-files --others --exclude-standard) | ForEach-Object {
        $p = Join-Path $repo $_
        @{ path = $_; sha256 = if (Test-Path $p -PathType Leaf) { (Get-FileHash $p -Algorithm SHA256).Hash } else { $null } }
    }
    @{
        campaignId = $CampaignId
        createdAt  = (Get-Date -Format 'o')
        head       = $head
        diffSha256 = Get-StringSha256 $diffText
        untracked  = $untracked
        harness    = @(Get-ChildItem $PSScriptRoot -Filter 'peta_option_matrix*' -File | ForEach-Object {
                @{ path = $_.Name; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } })
    } | ConvertTo-Json -Depth 6 | Out-File $campaignJson -Encoding utf8
}

# ---------------------------------------------------------------- シナリオ構築
# 軸の値: O/M/L/T/B は 0=触らない（既定）、1=オプションを入力。R は A=全体(Enter)、W=窓。
# F は 0=設定の形式が PDF・SVG・EMF、1=設定の形式が 0 件（ファイルを出さない）。ケースの前に設定のファイルを書き替える（Get-CaseSettingsFormats）
function New-PetaCmdLines([string]$space, [string]$r, [int]$o, [int]$m, [int]$l, [int]$t, [int]$f, [int]$b, [string]$cmd = 'PETA') {
    $lines = @($cmd)
    if ($o) {
        $lines += '_Object'
        $lines += if ($space -eq 'Paper') { '!cadpetaRect' } else { '!cadpetaSubset' }
        $lines += ''
    }
    if ($m) { $lines += @('_Margin', '5') }
    if ($l) { $lines += '_Layer' }
    if ($t) { $lines += '_Textoutline' }
    if ($b) { $lines += @('_Background', $BgColor) }
    if ($r -eq 'W') {
        $win = if ($space -eq 'Paper') { $PaperWin } else { $ModelWin }
        $lines += @($win.P1, $win.P2)
    }
    else { $lines += '' }
    $lines
}

function New-SpaceSetup([string]$space) {
    if ($space -eq 'Model') { @('(setvar "TILEMODE" 1)') }
    else {
        @('(setvar "TILEMODE" 0)', '(setvar "CTAB" "PETA_A3")', '_.PSPACE',
          '(setvar "CLAYER" "0")', '(setvar "LIMCHECK" 0)')
    }
}

function New-PetaScenario([string]$space, [string]$r, [int]$o, [int]$m, [int]$l, [int]$t, [int]$f, [int]$b) {
    $id = ('peta-{0}{1}-o{2}m{3}l{4}t{5}f{6}b{7}' -f $space.Substring(0, 1).ToLower(), $r.ToLower(), $o, $m, $l, $t, $f, $b)
    @{
        Id = $id; Kind = 'peta'; Dwg = if ($space -eq 'Paper') { 'layout' } else { 'model' }
        Space = $space; Axes = @{ R = $r; O = $o; M = $m; L = $l; T = $t; F = $f; B = $b }
        SpaceSetup = New-SpaceSetup $space
        SelSetup = if (-not $o) { @() }
                   elseif ($space -eq 'Paper') { @($PaperRectLisp) }
                   else { @($ModelSubsetLisp) }
        CmdLines = New-PetaCmdLines $space $r $o $m $l $t $f $b
    }
}

function New-PetaDataCmdLines([int]$scale2, [int]$m, [int]$l, [int]$t, [int]$s, [string]$cmd = 'PETADATA') {
    $lines = @($cmd, 'all', '')
    if ($m) { $lines += @('_Margin', '5') }
    if ($l) { $lines += '_Layer' }
    if ($t) { $lines += '_Textoutline' }
    if ($s) { $lines += '_Shx' }
    $lines += if ($scale2) { '2' } else { '' }
    $lines
}

# 既存シナリオへ Id 等を上書きする（hashtable の + は重複キーで失敗するため代入で行う）
function Set-ScenarioId([hashtable]$sc, [string]$id, [bool]$baseline = $false) {
    $sc.Id = $id
    if ($baseline) { $sc.Baseline = $true }
    $sc
}
# 軸 hashtable を複製して1軸だけ差し替える
function Copy-AxesWith([hashtable]$a, [string]$key, $val) {
    $c = @{} + $a
    $c[$key] = $val
    $c
}

function Get-ScenarioList {
    $list = New-Object System.Collections.ArrayList
    switch ($Set) {
        'PetaFull' {
            foreach ($space in 'Model', 'Paper') {
                foreach ($r in 'A', 'W') {
                    foreach ($o in 0, 1) { foreach ($m in 0, 1) { foreach ($l in 0, 1) {
                        foreach ($t in 0, 1) { foreach ($f in 0, 1) { foreach ($b in 0, 1) {
                            [void]$list.Add((New-PetaScenario $space $r $o $m $l $t $f $b))
                        } } } } } }
                }
            }
        }
        'Trial' {
            # Space×Range×O の8組 × B(0/1)。has_text と has_transparency の実測材料
            foreach ($space in 'Model', 'Paper') {
                foreach ($r in 'A', 'W') {
                    foreach ($o in 0, 1) { foreach ($b in 0, 1) {
                        [void]$list.Add((New-PetaScenario $space $r $o 0 0 0 0 $b))
                    } }
                }
            }
            # PETADATA の golden 材料（S×T、尺度1・余白0）
            foreach ($s in 0, 1) { foreach ($t in 0, 1) {
                [void]$list.Add(@{ Id = "trial-data-s$s-t$t"; Kind = 'petadata'; Dwg = 'model'
                        Axes = @{ Scale2 = 0; M = 0; L = 0; T = $t; S = $s }
                        SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                        CmdLines = New-PetaDataCmdLines 0 0 0 $t $s })
            } }
            # L=on の診断カテゴリ材料（レイヤ別グループ化のカテゴリ名と件数）
            [void]$list.Add(@{ Id = 'trial-data-l1'; Kind = 'petadata'; Dwg = 'model'
                    Axes = @{ Scale2 = 0; M = 0; L = 1; T = 0; S = 0 }
                    SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                    CmdLines = New-PetaDataCmdLines 0 0 1 0 0 })
            # グラデーション既定 DPI の材料
            [void]$list.Add(@{ Id = 'trial-grad-default'; Kind = 'petadata'; Dwg = 'gradient'
                    Axes = @{ Scale2 = 0; M = 0; L = 0; T = 0; S = 0; G = 0 }
                    SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                    CmdLines = New-PetaDataCmdLines 0 0 0 0 0 })
        }
        'PetaEdge' {
            # 比較基準（母数に数えない）: Paper 全体、Paper 窓、Model 全体、Model 窓、Model O 真部分集合
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'A' 0 0 0 0 0 0) 'edge-base-full' $true))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'W' 0 0 0 0 0 0) 'edge-base-win' $true))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Model' 'A' 0 0 0 0 0 0) 'edge-base-model-full' $true))
            # Model 窓は O の印刷より前に置く（edge11 の比較基準。O の後の印刷が O の前と同じになることを確かめる）
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Model' 'W' 0 0 0 0 0 0) 'edge-base-model-win' $true))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Model' 'A' 1 0 0 0 0 0) 'edge-base-osel' $true))
            $edges = @(
                @{ Id = 'edge01-all-keyword'; Cmd = @('PETA', '_All'); Expect = @{ svgEquals = 'edge-base-full' } }
                @{ Id = 'edge02-degenerate';  Cmd = @('PETA', '50,40', '50,40', '_All')
                   Expect = @{ msg = @($Msg.Degenerate); svgEquals = 'edge-base-full' } }
                @{ Id = 'edge03-bg-invalid';  Cmd = @('PETA', '_Background', 'red', $PaperWin.P1, $PaperWin.P2)
                   Expect = @{ msg = @($Msg.BgInvalid); emfEquals = 'edge-base-win' } }
                @{ Id = 'edge04-bg-enter';    Cmd = @('PETA', '_Background', '', $PaperWin.P1, $PaperWin.P2)
                   Expect = @{ emfEquals = 'edge-base-win' } }
                @{ Id = 'edge05-margin-enter'; Cmd = @('PETA', '_Margin', '', $PaperWin.P1, $PaperWin.P2)
                   Expect = @{ svgEquals = 'edge-base-win' } }
                @{ Id = 'edge06-layer-twice'; Cmd = @('PETA', '_Layer', '_Layer', $PaperWin.P1, $PaperWin.P2)
                   Expect = @{ msg = @($Msg.LayerOn, $Msg.LayerOff); svgEquals = 'edge-base-win' } }
                @{ Id = 'edge07-outline-twice'; Cmd = @('PETA', '_Textoutline', '_Textoutline', $PaperWin.P1, $PaperWin.P2)
                   Expect = @{ msg = @($Msg.OutlineOn); svgEquals = 'edge-base-win' } }
                @{ Id = 'edge09-object-clear'; Cmd = @('PETA', '_Object', '', '')
                   Expect = @{ msg = @($Msg.FilterCleared); svgEquals = 'edge-base-full' } }
            )
            foreach ($e in $edges) {
                [void]$list.Add(@{ Id = $e.Id; Kind = 'peta'; Dwg = 'layout'; Space = 'Paper'
                        Axes = @{ R = ($e.Id -in @('edge01-all-keyword', 'edge02-degenerate', 'edge09-object-clear') ? 'A' : 'W'); O = 0; M = 0; L = 0; T = 0; F = 0; B = 0 }
                        Edge = $e.Expect
                        SpaceSetup = New-SpaceSetup 'Paper'; SelSetup = @(); CmdLines = $e.Cmd })
            }
            # 先選択（sssetfirst）。明示 O と同じ真部分集合を選び、Model 全体との差も要求する。
            [void]$list.Add(@{ Id = 'edge10-pickfirst'; Kind = 'peta'; Dwg = 'model'; Space = 'Model'
                    Axes = @{ R = 'A'; O = 1; M = 0; L = 0; T = 0; F = 0; B = 0 }
                    Edge = @{ msg = @($Msg.FilterSummary)
                              svgEquals = 'edge-base-osel'; emfEquals = 'edge-base-osel'; pdfRenderEquals = 'edge-base-osel'
                              svgNotEquals = 'edge-base-model-full'; emfNotEquals = 'edge-base-model-full'; pdfRenderNotEquals = 'edge-base-model-full' }
                    SpaceSetup = New-SpaceSetup 'Model'
                    SelSetup = @($ModelSubsetLisp, '(sssetfirst nil cadpetaSubset)')
                    CmdLines = @('PETA', '') })
            # O の印刷（側DB）の直後の Model 窓が、O の前の Model 窓と同じ出力になる。O の印刷で破棄した側DB の
            # 文字のフォントの情報が、元の図面の次の印刷に残らないことを確かめる（1 回の起動で続けて実行するときだけ確かめられる）
            [void]$list.Add(@{ Id = 'edge11-window-after-object'; Kind = 'peta'; Dwg = 'model'; Space = 'Model'
                    Axes = @{ R = 'W'; O = 0; M = 0; L = 0; T = 0; F = 0; B = 0 }
                    Edge = @{ svgEquals = 'edge-base-model-win'; emfEquals = 'edge-base-model-win'; pdfRenderEquals = 'edge-base-model-win' }
                    SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                    CmdLines = New-PetaCmdLines 'Model' 'W' 0 0 0 0 0 0 })
        }
        'PetaData' {
            foreach ($sc2 in 0, 1) { foreach ($m in 0, 1) { foreach ($l in 0, 1) {
                foreach ($t in 0, 1) { foreach ($s in 0, 1) {
                    [void]$list.Add(@{ Id = ('pdata-s{0}m{1}l{2}t{3}x{4}' -f $sc2, $m, $l, $t, $s)
                            Kind = 'petadata'; Dwg = 'model'
                            Axes = @{ Scale2 = $sc2; M = $m; L = $l; T = $t; S = $s }
                            SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                            CmdLines = New-PetaDataCmdLines $sc2 $m $l $t $s })
                } } } } }
            # グラデーション DPI の既定は 150（実測）。軸は既定と半分の 75 で画素寸法差を見る
            foreach ($g in 0, 1) {
                $lines = @('PETADATA', 'all', '')
                if ($g) { $lines += @('_Gradient', '75') }
                $lines += ''
                [void]$list.Add(@{ Id = "pdata-grad-g$g"; Kind = 'petadata'; Dwg = 'gradient'
                        Axes = @{ Scale2 = 0; M = 0; L = 0; T = 0; S = 0; G = $g }
                        SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @(); CmdLines = $lines })
            }
            # 空選択（コマンド後選択で何も選ばない）→ 中止文言・成果物0
            [void]$list.Add(@{ Id = 'pdata-empty'; Kind = 'petadata-empty'; Dwg = 'model'
                    Axes = @{}; SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                    CmdLines = @('PETADATA', '') })
        }
        'DiagSmoke' {
            [void]$list.Add(@{ Id = 'diag-peta'; Kind = 'diag'; Dwg = 'layout'; Space = 'Paper'
                    Axes = @{ R = 'W'; O = 0; M = 0; L = 1; T = 1; F = 1; B = 1 }
                    SpaceSetup = New-SpaceSetup 'Paper'; SelSetup = @()
                    CmdLines = New-PetaCmdLines 'Paper' 'W' 0 0 1 1 1 1 'PETADIAG' })
            [void]$list.Add(@{ Id = 'diag-petadata'; Kind = 'datadiag'; Dwg = 'model'
                    Axes = @{ Scale2 = 0; M = 0; L = 1; T = 1; S = 0 }
                    SpaceSetup = New-SpaceSetup 'Model'; SelSetup = @()
                    CmdLines = New-PetaDataCmdLines 0 0 1 1 0 'PETADATADIAG' })
        }
        'Smoke' {
            # 版差スモーク固定14件（s11 は Enter/A の2実行で1件。s13 は -PETAOPT の後の PETA、s14 はファイル名のコメント）
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Model' 'A' 0 0 0 0 0 0) 'smoke01-model-full'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'A' 0 0 0 0 0 0) 'smoke02-paper-full'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'W' 0 0 0 0 0 0) 'smoke03-paper-win'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Model' 'A' 1 0 0 0 0 0) 'smoke04-model-osel'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'W' 0 1 0 0 0 0) 'smoke05-paper-win-m5'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'A' 0 0 1 0 0 0) 'smoke06-paper-layer'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'A' 0 0 0 1 0 0) 'smoke07-paper-outline'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'A' 0 0 0 0 1 0) 'smoke08-paper-fileoff'))
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'W' 0 0 0 0 0 1) 'smoke09-paper-win-bg'))
            [void]$list.Add(@{ Id = 'smoke10-degenerate'; Kind = 'peta'; Dwg = 'layout'; Space = 'Paper'
                    Axes = @{ R = 'A'; O = 0; M = 0; L = 0; T = 0; F = 0; B = 0 }
                    Edge = @{ msg = @($Msg.Degenerate); svgEquals = 'smoke02-paper-full' }
                    SpaceSetup = New-SpaceSetup 'Paper'; SelSetup = @()
                    CmdLines = @('PETA', '50,40', '50,40', '_All') })
            [void]$list.Add(@{ Id = 'smoke11-all-keyword'; Kind = 'peta'; Dwg = 'layout'; Space = 'Paper'
                    Axes = @{ R = 'A'; O = 0; M = 0; L = 0; T = 0; F = 0; B = 0 }
                    Edge = @{ svgEquals = 'smoke02-paper-full' }
                    SpaceSetup = New-SpaceSetup 'Paper'; SelSetup = @()
                    CmdLines = @('PETA', '_All') })
            [void]$list.Add((Set-ScenarioId (New-PetaScenario 'Paper' 'W' 0 1 1 1 0 1) 'smoke12-composite'))
            # -PETAOPT で形式を値の合計 13（PDF=1 + EMF=4 + PNG=8）にして保存し、続く PETA がその 3 形式だけを出す
            [void]$list.Add(@{ Id = 'smoke13-petaopt'; Kind = 'opt'; Dwg = 'layout'; Space = 'Paper'
                    Axes = @{}; SpaceSetup = New-SpaceSetup 'Paper'; SelSetup = @()
                    CmdLines = @('-PETAOPT', '_Formats', '13', '', 'PETA', '') })
            # ファイル名の形を {drawing}_{comment} にし、PETA の範囲の後（変換を背景で進めている間）に聞くコメントが名前に入る
            [void]$list.Add(@{ Id = 'smoke14-comment'; Kind = 'comment'; Dwg = 'layout'; Space = 'Paper'
                    Axes = @{}; SpaceSetup = New-SpaceSetup 'Paper'; SelSetup = @()
                    CmdLines = @('-PETAOPT', '_Name', '{drawing}_{comment}', '', 'PETA', '', 'elev') })
        }
    }
    if ($CaseFilter) { $list = @($list | Where-Object { $_.Id -match $CaseFilter }) }
    @($list)
}

# ---------------------------------------------------------------- ケース実行
$setDir = Join-Path $campaignDir "$Ver-$Set"
New-Item -ItemType Directory -Force $setDir | Out-Null
$tempCadPeta = Join-Path $env:TEMP 'CadPeta'


function Get-SvgInfo([string]$path) {
    $head = Get-Content $path -TotalCount 1
    $content = Get-Content $path -Raw
    $info = @{ sha = (Get-FileHash $path -Algorithm SHA256).Hash }
    if ($head -match 'width="([^"]+)mm" height="([^"]+)mm" viewBox="([^"]+)"') {
        $info.W = $Matches[1]; $info.H = $Matches[2]; $info.VB = $Matches[3]
    }
    $info.hasLayerGroups = $content.Contains('inkscape:groupmode="layer"')
    $info.hasText = $content.Contains('<text')
    $info.drawCount = ([regex]::Matches($content, '<(path|text|image)[\s>]')).Count
    $info
}

# 診断サマリを辞書化する。PETADATA は「PETADATA 完了」行の直後に2スペース字下げの
# 「<カテゴリ>: N件」（diag.summary_line）が並ぶ（ヘッダ行なし。実測）。CASE_END までの
# 範囲で字下げ行だけを拾う（プロンプト等の他行と混同しない）
function Get-DiagSummary([string]$out) {
    $dict = [ordered]@{}
    $endIdx = $out.IndexOf('CADPETA_CASE_END=')
    $scanText = if ($endIdx -ge 0) { $out.Substring(0, $endIdx) } else { $out }
    foreach ($line in ($scanText -split "`r?`n")) {
        if ($line -match '^\s{2}(\S.*?): (\d+)件\s*$') { $dict[$Matches[1]] = [int]$Matches[2] }
    }
    $dict
}

# 埋込グラデーション PNG の枚数・画素寸法・非単色を PIL（svg_gradient_probe.py）で実測する。
# PowerShell での IHDR 手読みは実 PNG との不一致を起こした（実測）ため使わない
function Get-SvgGradientProbe([string]$path) {
    $probe = Join-Path $PSScriptRoot 'peta_option_matrix\svg_gradient_probe.py'
    $json = & uv run --project (Join-Path $PSScriptRoot 'peta_option_matrix') python $probe $path 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
    $json | ConvertFrom-Json
}

# 1 ケースの .scr の行（区切りのマーカーは Invoke-AccoreBatch が前後に置く）。先選択を毎回解除し、前のケースの
# 選択を持ち越さない。ペーパー空間の O のために作った長方形はケースの終わりで削除する（残すと後のケースの出力に入る）
function Get-OptionMatrixCaseLines([hashtable]$sc) {
    @('(sssetfirst nil nil)') + $sc.SpaceSetup + $sc.SelSetup + @(
        "(princ `"\nCADPETA_CASE_BEGIN=$($sc.Id)`")(princ)"
    ) + $sc.CmdLines + @(
        '(if cadpetaRect (progn (entdel cadpetaRect) (setq cadpetaRect nil)))(princ)'
    )
}

# 起動の最初と最後の .scr の行。OSMODE は実行中だけ 0 にする（accoreconsole のスクリプト座標入力は2点目が
# 定常スナップで近傍ジオメトリへ吸着し、窓が入力値からずれる。AutoCAD 2026 実測）。プロファイル保存のため
# 最後に元値へ戻す（タイムアウト等で戻らない場合は外側のレジストリ保全が復元する）。NETLOAD は起動ごとに 1 回
function Get-OptionMatrixLaunchHead {
    @(
        '(setq cpOsmOld (getvar "OSMODE"))'
        '(setvar "OSMODE" 0)'
    ) + (Get-AccoreNetloadLines $plugin)
}
function Get-OptionMatrixLaunchTail { @('(setvar "OSMODE" cpOsmOld)', '_.CLOSE', '_Y', '_.QUIT') }

# ケースの区切りで記録する照合の材料。クリップボードは共有資源なので、ケースの直前と直後の内容を区切りで読む
# （PETADATA 系は「クリップボードを更新しない」ことを EMF 内容で判定する。シーケンス番号は対話デスクトップ上の
# 他プロセスのコピー操作でも進み、判定が偶発で壊れる。実測）
function Get-OptionMatrixBeforeState([hashtable]$sc) {
    @{
        TempBefore    = @(if (Test-Path $tempCadPeta) { (Get-ChildItem $tempCadPeta -Force).Name })
        SeqBefore     = [CadPeta.E2E.Clip]::GetClipboardSequenceNumber()
        ClipEmfBefore = if ($sc.Kind -like 'petadata*') { (Read-ClipboardEmf).bytes } else { $null }
    }
}
function Get-OptionMatrixAfterState {
    @{
        TempAfter = @(if (Test-Path $tempCadPeta) { (Get-ChildItem $tempCadPeta -Force).Name })
        SeqAfter  = [CadPeta.E2E.Clip]::GetClipboardSequenceNumber()
        ClipAfter = Read-ClipboardEmf
    }
}

# 同じ図面の種類のケースを 1 回の起動で続けて実行し（Invoke-AccoreBatch）、ケースごとに判定する。
# 1 ケースだけを渡すと 1 ケース 1 起動になる（-OneLaunchPerCase と、まとめた実行でだけ不合格になったケースの
# 確かめ直しに使う）。戻り値: id → 結果の hashtable
# ケースの設定の形式。F=1 は 0 件（ファイルを出さず、クリップボードだけ）、それ以外は今までの試験と同じ PDF・SVG・EMF
function Get-CaseSettingsFormats($scenario) {
    if ($scenario.Axes -and $scenario.Axes.F -eq 1) { return , @() }
    @('pdf', 'svg', 'emf')
}

function Invoke-OptionMatrixBatch([object[]]$cases, [string]$kind, [string]$launchName = $kind) {
    $batchCases = @($cases | ForEach-Object { @{ Id = $_.Id; Lines = (Get-OptionMatrixCaseLines $_); Scenario = $_ } })
    # 設定のフォルダは起動の間で共有し、ケースの直前に F の軸の設定を書く（プラグインはファイルが変われば次の PETA で読み直す）
    $dataDir = Join-Path $setDir "_data_$launchName"
    $runs = Invoke-AccoreBatch -Acc $acc -DwgSrc $dwgByKind[$kind] -Cases $batchCases `
        -Head (Get-OptionMatrixLaunchHead) -Tail (Get-OptionMatrixLaunchTail) -WorkRoot $setDir -LaunchName $launchName `
        -TimeoutSeconds $TimeoutSeconds -DataDir (Write-E2eSettings $dataDir) `
        -OnCaseStart { param($case) [void](Write-E2eSettings $dataDir -Formats (Get-CaseSettingsFormats $case.Scenario)); Get-OptionMatrixBeforeState $case.Scenario } `
        -OnCaseEnd { param($case) Get-OptionMatrixAfterState }
    $results = @{}
    foreach ($id in $runs.Keys) {
        $r = $runs[$id]
        # ケースの標準出力をケースのフォルダにも置く（g8 の検証などがケースのフォルダを読む）
        [IO.File]::WriteAllText((Join-Path $r.CaseDir 'stdout.log'), $r.Output, [Text.Encoding]::Unicode)
        $run = $r.Before + $r.After + @{
            ExitCode = $r.ExitCode; ExitDetail = $r.ExitDetail; TimedOut = $r.TimedOut; TimeoutDetail = $r.TimeoutDetail
            ElapsedMs = $r.ElapsedMs; DwgUnchanged = $r.DwgUnchanged; Residual = $r.Residual; StdoutPath = $r.StdoutPath
            HarnessNames = $r.HarnessNames; State = $r.State
        }
        $results[$id] = Complete-OptionMatrixCase $r.Case.Scenario $r.CaseDir $r.Output $run
    }
    $results
}
# 起動の作業フォルダはケースごとに分ける（まとめた実行の起動の標準出力を上書きしない）
function Invoke-OptionMatrixCase([hashtable]$sc) { (Invoke-OptionMatrixBatch @($sc) $sc.Dwg "$($sc.Dwg)_$($sc.Id)")[$sc.Id] }

# 実行の結果（$run）と、そのケースの標準出力（$out）とケースのフォルダの成果物から判定する
function Complete-OptionMatrixCase([hashtable]$sc, [string]$caseDir, [string]$out, [hashtable]$run) {
    $checks = New-Object System.Collections.ArrayList
    $timeoutDetail = if ($run.TimeoutDetail) { $run.TimeoutDetail } else { "${TimeoutSeconds}s 超過で自プロセスを停止した" }
    Add-Check $checks 'timeout' (-not $run.TimedOut) ($run.TimedOut ? $timeoutDetail : '')
    Add-Check $checks 'exit0' ($run.ExitCode -eq 0) $run.ExitDetail
    $endMarker = $out.Contains("CADPETA_CASE_END=$($sc.Id)")
    Add-Check $checks 'endMarker' $endMarker ($endMarker ? '' : '完了マーカーなし（入力列とプロンプトの不整合）')
    Add-Check $checks 'dwgUnchanged' $run.DwgUnchanged
    Add-Check $checks 'noResidualProcess' ($run.Residual.Count -eq 0) (($run.Residual -join ','))
    Add-Check $checks 'drawingStateUnchanged' $run.State.Ok $run.State.Detail

    # Model の O 系は、入力集合そのものが非空の真部分集合であることを各ケースで証明する。
    # 成果物比較だけでは、O と比較基準の両方が誤って全体出力になった場合を区別できない。
    if ($sc.Space -eq 'Model' -and $sc.Axes.O -eq 1) {
        $selectionMatch = [regex]::Match($out, 'CADPETA_MODEL_SELECTION all=(\d+) subset=(\d+)')
        Add-Check $checks 'modelSelectionCounts' $selectionMatch.Success ($selectionMatch.Success ? '' : '選択集合の件数マーカーがない')
        if ($selectionMatch.Success) {
            $allCount = [int]$selectionMatch.Groups[1].Value
            $subsetCount = [int]$selectionMatch.Groups[2].Value
            Add-Check $checks 'modelSelectionProperSubset' ($subsetCount -gt 0 -and $subsetCount -lt $allCount) `
                "all=$allCount subset=$subsetCount"
        }
    }

    $result = @{
        id = $sc.Id; kind = $sc.Kind; axes = $sc.Axes; space = $sc.Space
        exit = $run.ExitCode; elapsedMs = $run.ElapsedMs; timedOut = $run.TimedOut
        checks = $checks; stdoutPath = $run.StdoutPath
    }

    # 成果物の分類（ケースディレクトリ内のみ・ハーネス自身のファイルと図面ごとのログは除外）
    if ($endMarker) {
        $files = @(Get-ChildItem $caseDir -File -Recurse | Where-Object { $_.Name -notin $run.HarnessNames -and $_.Extension -ne '.log' })
        $arts = @{
            pdf   = @($files | Where-Object Extension -eq '.pdf')
            emf   = @($files | Where-Object Extension -eq '.emf')
            diag  = @($files | Where-Object Name -like '*.diag.json')
            stage = @($files | Where-Object { $_.Extension -eq '.svg' -and $_.Name -match '\.stage\d+\.' })
            svg   = @($files | Where-Object { $_.Extension -eq '.svg' -and $_.Name -notmatch '\.stage\d+\.' })
            png   = @($files | Where-Object Extension -eq '.png')
        }
        $result.artifacts = @($files | ForEach-Object { @{ name = $_.Name; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash; size = $_.Length } })

        switch -Regex ($sc.Kind) {
            '^peta$'          { Test-PetaCase $sc $out $arts $checks $result $caseDir $run }
            '^petadata$'      { Test-PetaDataCase $sc $out $arts $checks $result $run }
            '^petadata-empty$' {
                Add-Check $checks 'abortMsg' ($out -match $Msg.DataEmpty)
                Add-Check $checks 'noArtifacts' (($arts.pdf.Count + $arts.emf.Count + $arts.svg.Count + $arts.diag.Count) -eq 0) `
                    "pdf=$($arts.pdf.Count) emf=$($arts.emf.Count) svg=$($arts.svg.Count)"
            }
            '^diag$'          { Test-DiagCase $sc $out $arts $checks $result }
            '^datadiag$'      { Test-DataDiagCase $sc $out $arts $checks $result }
            '^opt$'           { Test-OptCase $out $arts $checks }
            '^comment$'       { Test-CommentCase $out $arts $checks }
        }
    }
    $result.pass = -not ($checks | Where-Object { -not $_.ok })
    $result
}

# PETA の判定。共通文言・成果物集合・SVG 痕跡・F=off のクリップボード読み戻し
function Test-PetaCase($sc, $out, $arts, $checks, $result, $caseDir, $run) {
    $a = $sc.Axes
    $fileOn = ($a.F -eq 0)
    # サマリ行（F=1 はファイルを出していないことと PETAOPT の案内）
    Add-Check $checks 'summaryLine' ($out -match $Msg.SummaryLine)
    if (-not $fileOn) { Add-Check $checks 'summaryNoFiles' ($out -match $Msg.NoFiles) }
    # 入力したオプションの状態文言
    if ($a.L) { Add-Check $checks 'msgLayerOn' ($out -match $Msg.LayerOn) }
    if ($a.T) { Add-Check $checks 'msgOutlineOn' ($out -match $Msg.OutlineOn) }
    if ($a.B) { Add-Check $checks 'msgBgState' ($out -match $Msg.BgState) }
    if ($a.O -and -not $sc.Edge) {
        Add-Check $checks 'msgFilterSet' ($out -match $Msg.FilterSet -or $out -match [regex]::Escape('選択済みのオブジェクトへ出力対象を絞り込む'))
        Add-Check $checks 'msgFilterSummary' ($out -match $Msg.FilterSummary)
    }
    $flag = Get-ContentFlag $sc.Space $a.R $a.O

    if ($fileOn) {
        Add-Check $checks 'artifactSet' ($arts.pdf.Count -eq 1 -and $arts.svg.Count -eq 1 -and $arts.emf.Count -eq 1 -and $arts.diag.Count -eq 0 -and $arts.stage.Count -eq 0) `
            "pdf=$($arts.pdf.Count) svg=$($arts.svg.Count) emf=$($arts.emf.Count) diag=$($arts.diag.Count) stage=$($arts.stage.Count)"
        if ($arts.svg.Count -eq 1) {
            $nameOk = $arts.svg[0].Name -match '^(TESTCHART2|TESTCHART2_layout|gradient_e2e)_.+_\d{8}_\d{6}(_\d+)?\.svg$'
            Add-Check $checks 'artifactName' $nameOk $arts.svg[0].Name
            $svg = Get-SvgInfo $arts.svg[0].FullName
            $result.svg = $svg
            # 寸法（レイアウトは既知正解と厳密一致。モデルは flags かクロスチェックで判定）
            if ($sc.Space -eq 'Paper') {
                $exp = if ($a.R -eq 'W') { if ($a.M) { $ExpectPaperWinM5 } else { $ExpectPaperWinM0 } } else { $ExpectPaperFull }
                Add-Check $checks 'svgDims' ($svg.W -eq $exp.W -and $svg.H -eq $exp.H -and $svg.VB -eq $exp.VB) `
                    "実 $($svg.W)x$($svg.H) [$($svg.VB)] 期待 $($exp.W)x$($exp.H) [$($exp.VB)]"
            }
            elseif ($flags -and $flags.modelExpect) {
                $key = if ($a.R -eq 'W') { "W-M$($a.M)" } else { 'A' }
                $exp = $flags.modelExpect.PSObject.Properties[$key]
                if ($exp) {
                    Add-Check $checks 'svgDims' ($svg.W -eq $exp.Value.w -and $svg.H -eq $exp.Value.h -and $svg.VB -eq $exp.Value.vb) `
                        "実 $($svg.W)x$($svg.H) [$($svg.VB)] 期待 $($exp.Value.w)x$($exp.Value.h) [$($exp.Value.vb)]"
                }
            }
            # レイヤ別グループ化（OCG 欠如は仕様どおりの警告付き縮退 → WARN 記録・アサートはスキップ）
            $degraded = $out -match $Msg.NoOcg
            $result.degradedOcg = [bool]$degraded
            if ($a.L) {
                if ($degraded) { Add-Check $checks 'layerGroupsDegraded(WARN)' $true 'OCG欠如の警告付き縮退' }
                else { Add-Check $checks 'layerGroups' $svg.hasLayerGroups }
            }
            else { Add-Check $checks 'noLayerGroups' (-not $svg.hasLayerGroups) }
            # 文字輪郭（has_text はシナリオの範囲∩選択に文字が入るかのフラグ）
            if ($a.T) { Add-Check $checks 'noTextElement' (-not $svg.hasText) }
            elseif ($flag -and $flag.hasText) { Add-Check $checks 'textElement' $svg.hasText }
        }
        if ($arts.emf.Count -eq 1) { $result.emfSha = (Get-FileHash $arts.emf[0].FullName -Algorithm SHA256).Hash }
        if ($arts.pdf.Count -eq 1) { $result.pdfPath = $arts.pdf[0].FullName }
    }
    else {
        Add-Check $checks 'noArtifacts' (($arts.pdf.Count + $arts.svg.Count + $arts.emf.Count + $arts.diag.Count + $arts.stage.Count) -eq 0) `
            "pdf=$($arts.pdf.Count) svg=$($arts.svg.Count) emf=$($arts.emf.Count)"
        $newTemp = @($run.TempAfter | Where-Object { $_ -notin $run.TempBefore })
        Add-Check $checks 'noTempResidue' ($newTemp.Count -eq 0) ($newTemp -join ',')
        # クリップボード読み戻し（ケースの区切り、または終了後に読んだ内容。F=on の EMF とバイト一致することを
        # 実測済み）。EMF の内容は F=on の相方とクロスチェックで比較する
        $clipPost = $run.ClipAfter
        if (-not $clipPost.ok) { Add-Check $checks 'clipboardReadable' $false 'OpenClipboard がリトライ後も失敗（判定不能=FAIL）' }
        $bytes = $clipPost.bytes
        Add-Check $checks 'clipboardEmf' ($null -ne $bytes) ($null -eq $bytes ? 'CF_ENHMETAFILE なし' : "$($bytes.Length) bytes")
        if ($bytes) {
            $result.clipEmfSha = Get-BytesSha256 $bytes
            $result.clipEmfBytes = $bytes.Length
            $hd = Join-Path $caseDir '_harness'
            New-Item -ItemType Directory -Force $hd | Out-Null
            [IO.File]::WriteAllBytes((Join-Path $hd 'clipboard_readback.emf'), $bytes)
        }
    }
    # エッジの単発期待（文言）
    if ($sc.Edge -and $sc.Edge.msg) {
        foreach ($m in $sc.Edge.msg) { Add-Check $checks "edgeMsg:$m" ($out -match $m) }
    }
}

# PETADATA の判定。SVG のみ・寸法 golden・診断カテゴリ・クリップボード不変
function Test-PetaDataCase($sc, $out, $arts, $checks, $result, $run) {
    $a = $sc.Axes
    Add-Check $checks 'dataDone' ($out -match $Msg.DataDone)
    Add-Check $checks 'artifactSet' ($arts.svg.Count -eq 1 -and $arts.pdf.Count -eq 0 -and $arts.emf.Count -eq 0 -and $arts.diag.Count -eq 0 -and $arts.stage.Count -eq 0) `
        "svg=$($arts.svg.Count) pdf=$($arts.pdf.Count) emf=$($arts.emf.Count) diag=$($arts.diag.Count)"
    # クリップボード不変は CF_ENHMETAFILE の内容で判定する。「新しい EMF が現れた／別内容へ
    # 変わった」＝ PETADATA が書いた証拠のみ FAIL。形式の消失・open 失敗は対話デスクトップの
    # 他プロセス由来があり得るため WARN 記録（seq 差分も添える）
    $seqBefore = $run.SeqBefore
    $seqAfter = $run.SeqAfter
    $clipAfter = $run.ClipAfter
    $shaBefore = Get-BytesSha256 $run.ClipEmfBefore
    $shaAfter = Get-BytesSha256 $clipAfter.bytes
    if ($shaAfter -eq $shaBefore) {
        Add-Check $checks 'clipboardUntouched' $true "seq $seqBefore → $seqAfter（EMF 内容不変）"
    }
    elseif ($null -ne $clipAfter.bytes) {
        Add-Check $checks 'clipboardUntouched' $false "EMF が書き換わった: $shaBefore → $shaAfter"
    }
    else {
        Add-Check $checks 'clipboardUntouched(WARN)' $true "EMF 形式が消失（外部のコピー操作の形。seq $seqBefore → $seqAfter）"
    }
    if ($arts.svg.Count -ne 1) { return }
    Add-Check $checks 'artifactName' ($arts.svg[0].Name -match '_data_\d{8}_\d{6}(_\d+)?\.svg$') $arts.svg[0].Name
    $svg = Get-SvgInfo $arts.svg[0].FullName
    $result.svg = $svg
    $result.diagSummary = Get-DiagSummary $out

    if ($sc.Dwg -eq 'gradient') {
        $probe = Get-SvgGradientProbe $arts.svg[0].FullName
        if (-not $probe) { Add-Check $checks 'gradientProbe' $false 'svg_gradient_probe.py の実行に失敗' ; return }
        $result.gradient = $probe
        # gradient_e2e.dwg は9形状（tests/assets/testcharts/gradient/README）。寸法だけ同じ空画像を
        # PASS にしないため非単色も要求する
        Add-Check $checks 'gradientImages' ($probe.count -eq 9) "画像 $($probe.count) 件（期待9）"
        $uni = @($probe.images | Where-Object { $_.unicolor })
        Add-Check $checks 'gradientNotUnicolor' ($uni.Count -eq 0) "単色画像 $($uni.Count) 件"
        return  # DPI 差の判定はクロスチェック（G0/G1 比較）で行う
    }
    # 寸法 golden（S×尺度の実測表 + 2×余白）。範囲は線幅半分込みの保守的 bbox で、線幅は
    # 出力mm（尺度に乗らない）のため「内容/尺度＋固定パッド」になる。式で導出せず尺度別の
    # 実測値を golden に持つ
    if ($flags -and $flags.petadata -and $flags.petadata.dims) {
        $dim = $flags.petadata.dims.PSObject.Properties["S$($a.S)"]
        $scaleKey = if ($a.Scale2) { 'scale2' } else { 'scale1' }
        $dimS = if ($dim) { $dim.Value.PSObject.Properties[$scaleKey] } else { $null }
        if ($dimS) {
            $margin = if ($a.M) { 5.0 } else { 0.0 }
            $expW = [double]$dimS.Value.w + 2 * $margin
            $expH = [double]$dimS.Value.h + 2 * $margin
            $okW = [math]::Abs([double]$svg.W - $expW) -le 0.0005
            $okH = [math]::Abs([double]$svg.H - $expH) -le 0.0005
            Add-Check $checks 'dataDims' ($okW -and $okH) "実 $($svg.W)x$($svg.H) 期待 $expW x $expH"
        }
        else { Add-Check $checks 'dataDims' $false "golden 未確定: S$($a.S)/$scaleKey" }
    }
    elseif ($Set -ne 'Trial') { Add-Check $checks 'dataDims' $false 'petadata golden がない' }
    # レイヤ・文字
    if ($a.L) { Add-Check $checks 'layerGroups' $svg.hasLayerGroups } else { Add-Check $checks 'noLayerGroups' (-not $svg.hasLayerGroups) }
    if ($a.T) { Add-Check $checks 'noTextElement' (-not $svg.hasText) } else { Add-Check $checks 'textElement' $svg.hasText }
    # SHX 代替: S=1 は代替診断カテゴリあり。S=0 は「利用者設定による近似」なし
    $subst = @($result.diagSummary.Keys | Where-Object { $_ -like '*SHX文字のフォント代替*' })
    if ($a.S) { Add-Check $checks 'shxSubstDiag' ($subst.Count -ge 1) (($subst -join '; ')) }
    else { Add-Check $checks 'noShxSubstDiag' (-not ($subst | Where-Object { $_ -like '*利用者設定による近似*' })) (($subst -join '; ')) }
    # 診断カテゴリの golden 照合（未知カテゴリを黙って許可しない）。L はレイヤ別グループ化の
    # カテゴリを加えるため golden のキーに含める
    if ($flags -and $flags.petadata -and $flags.petadata.diag) {
        $gk = "S$($a.S)|T$($a.T)|L$($a.L)"
        $gold = $flags.petadata.diag.PSObject.Properties[$gk]
        if ($gold) {
            $exp = @{}
            foreach ($p in $gold.Value.PSObject.Properties) { $exp[$p.Name] = [int]$p.Value }
            $obs = $result.diagSummary
            $bad = New-Object System.Collections.ArrayList
            foreach ($k in $obs.Keys) { if (-not $exp.ContainsKey($k)) { [void]$bad.Add("未知: $k=$($obs[$k])") } elseif ($exp[$k] -ne $obs[$k]) { [void]$bad.Add("件数差: $k 実$($obs[$k]) 期待$($exp[$k])") } }
            foreach ($k in $exp.Keys) { if (-not $obs.Contains($k)) { [void]$bad.Add("欠落: $k 期待$($exp[$k])") } }
            Add-Check $checks 'diagCategories' ($bad.Count -eq 0) (($bad -join '; '))
        }
        elseif ($Set -ne 'Trial') { Add-Check $checks 'diagCategories' $false "golden 未確定: $gk" }
    }
    elseif ($Set -ne 'Trial') { Add-Check $checks 'diagCategories' $false 'petadata 診断 golden がない' }
}

# -PETAOPT の判定。今の形式を名前と値で表示し、値の合計で入力した形式を保存して、続く PETA がその形式だけを出す
function Test-OptCase($out, $arts, $checks) {
    Add-Check $checks 'optShowsCurrent' ($out -match [regex]::Escape('出力する形式: PDF,SVG,EMF (7)'))
    Add-Check $checks 'optSaved' ($out -match [regex]::Escape('設定を保存しました: '))
    Add-Check $checks 'summaryLine' ($out -match $Msg.SummaryLine)
    Add-Check $checks 'artifactSet' ($arts.pdf.Count -eq 1 -and $arts.emf.Count -eq 1 -and $arts.png.Count -eq 1 -and $arts.svg.Count -eq 0) `
        "pdf=$($arts.pdf.Count) emf=$($arts.emf.Count) png=$($arts.png.Count) svg=$($arts.svg.Count)"
}

# コメントの判定。コメントを聞き、入力した文字列がファイル名に入り、設定の形式（PDF・SVG・EMF）を出す
function Test-CommentCase($out, $arts, $checks) {
    Add-Check $checks 'commentPrompted' ($out -match [regex]::Escape('ファイル名に入れるコメントを入力'))
    Add-Check $checks 'summaryLine' ($out -match $Msg.SummaryLine)
    $names = @($arts.pdf + $arts.svg + $arts.emf | ForEach-Object Name | Sort-Object)
    Add-Check $checks 'commentInName' (($names -join ',') -eq 'TESTCHART2_layout_elev.emf,TESTCHART2_layout_elev.pdf,TESTCHART2_layout_elev.svg') ($names -join ',')
}

function Test-DiagCase($sc, $out, $arts, $checks, $result) {
    # DiagSmoke は F=1（設定の形式が 0 件）。PETADIAG は設定にかかわらず全部の形式と診断のファイルを出す
    Add-Check $checks 'artifactSet' ($arts.pdf.Count -eq 1 -and $arts.svg.Count -eq 1 -and $arts.emf.Count -eq 1 -and $arts.diag.Count -eq 1 -and $arts.stage.Count -ge 1) `
        "pdf=$($arts.pdf.Count) svg=$($arts.svg.Count) emf=$($arts.emf.Count) diag=$($arts.diag.Count) stage=$($arts.stage.Count)"
    Test-DiagJsonAndStages $sc $arts $checks $result @('text-outlined', 'layer-grouped', 'cropped')
}

function Test-DataDiagCase($sc, $out, $arts, $checks, $result) {
    Add-Check $checks 'artifactSet' ($arts.svg.Count -eq 1 -and $arts.diag.Count -eq 1 -and $arts.stage.Count -ge 1 -and $arts.pdf.Count -eq 0 -and $arts.emf.Count -eq 0) `
        "svg=$($arts.svg.Count) diag=$($arts.diag.Count) stage=$($arts.stage.Count) pdf=$($arts.pdf.Count) emf=$($arts.emf.Count)"
    Test-DiagJsonAndStages $sc $arts $checks $result @('text-outlined', 'layer-grouped')
}

function Test-DiagJsonAndStages($sc, $arts, $checks, $result, [string[]]$expectStages) {
    if ($arts.diag.Count -eq 1) {
        try {
            $dj = Get-Content $arts.diag[0].FullName -Raw | ConvertFrom-Json
            $shapeOk = $true
            foreach ($e in $dj) {
                if (-not ($e.PSObject.Properties['type'] -and $e.PSObject.Properties['count'] -and $e.PSObject.Properties['targets'])) { $shapeOk = $false }
            }
            Add-Check $checks 'diagJsonShape' $shapeOk "entries=$(@($dj).Count)"
        }
        catch { Add-Check $checks 'diagJsonShape' $false $_.Exception.Message }
    }
    $stageNames = @($arts.stage | ForEach-Object Name)
    foreach ($s in $expectStages) {
        Add-Check $checks "stage:$s" (($stageNames -match [regex]::Escape($s)).Count -ge 1)
    }
    $result.stages = $stageNames
}

# ---------------------------------------------------------------- クロスチェック
function Find-Result($results, [hashtable]$axes, [string]$space) {
    $results | Where-Object {
        $_.kind -eq 'peta' -and $_.space -eq $space -and -not $_.timedOut -and -not $_.edge -and
        $_.axes.R -eq $axes.R -and $_.axes.O -eq $axes.O -and $_.axes.M -eq $axes.M -and
        $_.axes.L -eq $axes.L -and $_.axes.T -eq $axes.T -and $_.axes.F -eq $axes.F -and $_.axes.B -eq $axes.B
    } | Select-Object -First 1
}

function Invoke-PetaCross($results) {
    $cross = New-Object System.Collections.ArrayList
    $petaCases = @($results | Where-Object { $_.kind -eq 'peta' -and -not $_.Baseline })
    foreach ($r in $petaCases) {
        $a = $r.axes
        $flag = Get-ContentFlag $r.space $a.R $a.O
        # B ペア（B だけ違う）: SVG 一致（B は EMF のみに効く）。透過ありなら EMF は不一致
        if ($a.B -eq 1) {
            $mate = Find-Result $results (Copy-AxesWith $a 'B' 0) $r.space
            if ($mate) {
                if ($a.F -eq 0) {
                    if ($r.svg -and $mate.svg) { Add-Check $cross "Bpair-svg:$($r.id)" ($r.svg.sha -eq $mate.svg.sha) }
                    if ($flag -and $flag.hasTransparency -and $r.emfSha -and $mate.emfSha) {
                        Add-Check $cross "Bpair-emfDiff:$($r.id)" ($r.emfSha -ne $mate.emfSha) '透過ありのため下地色差が出るはず'
                    }
                }
                elseif ($flag -and $flag.hasTransparency -and $r.clipEmfSha -and $mate.clipEmfSha) {
                    Add-Check $cross "Bpair-clipDiff:$($r.id)" ($r.clipEmfSha -ne $mate.clipEmfSha)
                }
            }
        }
        # F ペア（F だけ違う）: クリップボード読み戻し EMF と F=on の EMF ファイルの内容一致
        if ($a.F -eq 1 -and $r.clipEmfSha) {
            $mate = Find-Result $results (Copy-AxesWith $a 'F' 0) $r.space
            if ($mate -and $mate.emfSha) {
                Add-Check $cross "Fpair-emf:$($r.id)" ($r.clipEmfSha -eq $mate.emfSha) `
                    ($r.clipEmfSha -eq $mate.emfSha ? '' : "clip=$($r.clipEmfSha.Substring(0,12)) file=$($mate.emfSha.Substring(0,12))")
            }
        }
        # L ペア（L だけ違う）: 描画要素数（<g を数えない）と寸法が不変
        if ($a.L -eq 1 -and $r.svg) {
            $mate = Find-Result $results (Copy-AxesWith $a 'L' 0) $r.space
            if ($mate -and $mate.svg) {
                Add-Check $cross "Lpair-drawCount:$($r.id)" ($r.svg.drawCount -eq $mate.svg.drawCount) "$($r.svg.drawCount) vs $($mate.svg.drawCount)"
                Add-Check $cross "Lpair-dims:$($r.id)" ($r.svg.W -eq $mate.svg.W -and $r.svg.H -eq $mate.svg.H)
            }
        }
    }
    # Model O の代表組は全体出力と三形式すべてで異なることを要求する。
    # O が無視された場合や、選択集合が誤って全体になった場合はここで必ず不合格になる。
    $modelFullAxes = @{ R = 'A'; O = 0; M = 0; L = 0; T = 0; F = 0; B = 0 }
    $modelOAxes = Copy-AxesWith $modelFullAxes 'O' 1
    $modelFull = Find-Result $results $modelFullAxes 'Model'
    $modelO = Find-Result $results $modelOAxes 'Model'
    if ($modelFull -and $modelO) {
        $svgDiff = $modelFull.svg -and $modelO.svg -and ($modelFull.svg.sha -ne $modelO.svg.sha)
        Add-Check $cross 'ModelObjectSubset-svgDiff' ([bool]$svgDiff) ($svgDiff ? '' : 'O 出力が Model 全体と同一')
        $emfDiff = $modelFull.emfSha -and $modelO.emfSha -and ($modelFull.emfSha -ne $modelO.emfSha)
        Add-Check $cross 'ModelObjectSubset-emfDiff' ([bool]$emfDiff) ($emfDiff ? '' : 'O 出力が Model 全体と同一')
        $pdfCmp = Compare-PdfRender $modelFull.pdfPath $modelO.pdfPath
        Add-Check $cross 'ModelObjectSubset-pdfRenderDiff' ([bool]$pdfCmp.Different) $pdfCmp.Detail
    }
    elseif (-not $CaseFilter) {
        Add-Check $cross 'ModelObjectSubset-coverage' $false 'Model 全体または Model O の代表ケースがない'
    }
    # Model の寸法一貫性: (R,O,M) が同じ組は同一ヘッダ
    $modelGroups = $petaCases | Where-Object { $_.space -eq 'Model' -and $_.svg } |
        Group-Object { "$($_.axes.R)|$($_.axes.O)|$($_.axes.M)" }
    foreach ($g in $modelGroups) {
        $heads = @($g.Group | ForEach-Object { "$($_.svg.W)|$($_.svg.H)|$($_.svg.VB)" } | Sort-Object -Unique)
        Add-Check $cross "ModelDims:$($g.Name)" ($heads.Count -eq 1) (($heads -join ' / '))
    }
    # Model 窓×余白の算術: M=5 の寸法は M=0 の寸法＋四辺5mm（=各軸+10mm）。クロップは
    # 出力mm座標で効くため、用紙合わせ後の寸法に対しても加算で成立する
    foreach ($o in 0, 1) {
        $m0 = $petaCases | Where-Object { $_.space -eq 'Model' -and $_.axes.R -eq 'W' -and $_.axes.O -eq $o -and $_.axes.M -eq 0 -and $_.svg -and $_.svg.W } | Select-Object -First 1
        $m1 = $petaCases | Where-Object { $_.space -eq 'Model' -and $_.axes.R -eq 'W' -and $_.axes.O -eq $o -and $_.axes.M -eq 1 -and $_.svg -and $_.svg.W } | Select-Object -First 1
        if ($m0 -and $m1) {
            $okW = [math]::Abs([double]$m1.svg.W - ([double]$m0.svg.W + 10)) -le 0.0005
            $okH = [math]::Abs([double]$m1.svg.H - ([double]$m0.svg.H + 10)) -le 0.0005
            Add-Check $cross "ModelMarginArith:O$o" ($okW -and $okH) "M0=$($m0.svg.W)x$($m0.svg.H) M5=$($m1.svg.W)x$($m1.svg.H)"
        }
    }
    # L 軸のカバレッジ: L=1 かつファイル出力ありのケースを持つ空間ごとに、縮退せず
    # レイヤ群を確認できたケースが最低1件（L ケース自体が無い空間は対象外。Smoke 等）。
    # CaseFilter で絞った部分実行では母集団が欠けるため判定しない（全量実行時のみ）
    $lSpaces = if ($CaseFilter) { @() } else {
        @($petaCases | Where-Object { $_.axes.L -eq 1 -and $_.axes.F -eq 0 } | ForEach-Object space | Sort-Object -Unique)
    }
    foreach ($space in $lSpaces) {
        $confirmed = @($petaCases | Where-Object { $_.space -eq $space -and $_.axes.L -eq 1 -and $_.svg -and $_.svg.hasLayerGroups -and -not $_.degradedOcg })
        Add-Check $cross "LayerAxisCoverage:$space" ($confirmed.Count -ge 1) ($confirmed.Count -eq 0 ? '全ケースが OCG 欠如縮退 → L 軸未検証' : "$($confirmed.Count) 件で確認")
    }
    @($cross)
}

# PETADATA のクロスチェック: T/L だけ違うペアの寸法・描画数の不変、グラデーション G0/G1
function Invoke-PetaDataCross($results) {
    $cross = New-Object System.Collections.ArrayList
    $cases = @($results | Where-Object { $_.kind -eq 'petadata' -and $_.svg -and $_.id -like 'pdata-s*' })
    $groups = $cases | Group-Object { "$($_.axes.Scale2)|$($_.axes.M)|$($_.axes.S)" }
    foreach ($g in $groups) {
        $dims = @($g.Group | ForEach-Object { "$($_.svg.W)|$($_.svg.H)" } | Sort-Object -Unique)
        Add-Check $cross "DataDims-invariant:$($g.Name)" ($dims.Count -eq 1) (($dims -join ' / '))
    }
    foreach ($g in ($cases | Group-Object { "$($_.axes.Scale2)|$($_.axes.M)|$($_.axes.S)|$($_.axes.T)" })) {
        $counts = @($g.Group | ForEach-Object { $_.svg.drawCount } | Sort-Object -Unique)
        Add-Check $cross "DataDrawCount-Lpair:$($g.Name)" ($counts.Count -eq 1) (($counts -join ' / '))
    }
    $g0 = $results | Where-Object id -eq 'pdata-grad-g0' | Select-Object -First 1
    $g1 = $results | Where-Object id -eq 'pdata-grad-g1' | Select-Object -First 1
    if ($g0 -and $g1 -and $g0.gradient -and $g1.gradient) {
        Add-Check $cross 'Gradient-imageCount' ($g0.gradient.count -eq $g1.gradient.count) "$($g0.gradient.count) vs $($g1.gradient.count)"
        # DPI 150→75 で埋込 PNG の画素寸法は各画像とも縮む（拡大する画像が1枚でもあれば失敗）
        $shrunk = 0; $grew = 0
        for ($i = 0; $i -lt [math]::Min($g0.gradient.count, $g1.gradient.count); $i++) {
            if ($g1.gradient.images[$i].w -lt $g0.gradient.images[$i].w) { $shrunk++ }
            elseif ($g1.gradient.images[$i].w -gt $g0.gradient.images[$i].w) { $grew++ }
        }
        Add-Check $cross 'Gradient-dpiShrinks' ($shrunk -ge 1 -and $grew -eq 0) "縮小 $shrunk 件 / 拡大 $grew 件"
    }
    @($cross)
}

# エッジ・スモークの比較期待。
function Invoke-EdgeCross($results) {
    $cross = New-Object System.Collections.ArrayList
    $byId = @{}
    foreach ($r in $results) { $byId[$r.id] = $r }
    foreach ($r in $results) {
        $e = $r.edge
        if (-not $e) { continue }
        if ($e.svgEquals) {
            $base = $byId[$e.svgEquals]
            $ok = $base -and $r.svg -and $base.svg -and ($r.svg.sha -eq $base.svg.sha)
            Add-Check $cross "svgEquals:$($r.id)=$($e.svgEquals)" ([bool]$ok) ($ok ? '' : "実 $($r.svg.sha) 基準 $($base.svg.sha)")
        }
        if ($e.emfEquals) {
            $base = $byId[$e.emfEquals]
            $ok = $base -and $r.emfSha -and $base.emfSha -and ($r.emfSha -eq $base.emfSha)
            Add-Check $cross "emfEquals:$($r.id)=$($e.emfEquals)" ([bool]$ok)
        }
        if ($e.svgNotEquals) {
            $base = $byId[$e.svgNotEquals]
            $ok = $base -and $r.svg -and $base.svg -and ($r.svg.sha -ne $base.svg.sha)
            Add-Check $cross "svgNotEquals:$($r.id)!=$($e.svgNotEquals)" ([bool]$ok) `
                ($ok ? '' : 'O 出力が Model 全体と同一（O 無視の疑い）')
        }
        if ($e.emfNotEquals) {
            $base = $byId[$e.emfNotEquals]
            $ok = $base -and $r.emfSha -and $base.emfSha -and ($r.emfSha -ne $base.emfSha)
            Add-Check $cross "emfNotEquals:$($r.id)!=$($e.emfNotEquals)" ([bool]$ok) `
                ($ok ? '' : 'O 出力が Model 全体と同一（O 無視の疑い）')
        }
        if ($e.pdfRenderEquals) {
            $base = $byId[$e.pdfRenderEquals]
            $cmp = if ($base) { Compare-PdfRender $r.pdfPath $base.pdfPath } else { @{ Exit = 2; Detail = '比較基準がない' } }
            Add-Check $cross "pdfRenderEquals:$($r.id)=$($e.pdfRenderEquals)" ($cmp.Exit -eq 0) $cmp.Detail
        }
        if ($e.pdfRenderNotEquals) {
            $base = $byId[$e.pdfRenderNotEquals]
            $cmp = if ($base) { Compare-PdfRender $r.pdfPath $base.pdfPath } else { @{ Exit = 2; Detail = '比較基準がない' } }
            Add-Check $cross "pdfRenderNotEquals:$($r.id)!=$($e.pdfRenderNotEquals)" ([bool]$cmp.Different) $cmp.Detail
        }
    }
    @($cross)
}

# ---------------------------------------------------------------- 実行本体
# 1件のときの配列アンロール（hashtable の Count がキー数になる）を防ぐため必ず配列で受ける
$scenarios = @(Get-ScenarioList)
if ($scenarios.Count -eq 0) { throw "実行対象のシナリオがない（Set=$Set CaseFilter='$CaseFilter'）" }
@($scenarios | ForEach-Object { @{ id = $_.Id; kind = $_.Kind; dwg = $_.Dwg; axes = $_.Axes; cmdLines = $_.CmdLines } }) |
    ConvertTo-Json -Depth 6 | Out-File (Join-Path $setDir 'scenarios.json') -Encoding utf8

if ($ProbeOnly) {
    $modelO = @($scenarios | Where-Object { $_.Space -eq 'Model' -and $_.Axes.O -eq 1 })
    foreach ($sc in $modelO) {
        if (-not ($sc.SelSetup -match 'CADPETA_MODEL_SELECTION')) {
            throw "Model O ケースに真部分集合の件数オラクルがない: $($sc.Id)"
        }
        if ($sc.CmdLines -contains 'all') {
            throw "Model O ケースが全体選択を入力している: $($sc.Id)"
        }
    }
    if ($Set -eq 'PetaEdge') {
        $edge10 = $scenarios | Where-Object Id -eq 'edge10-pickfirst' | Select-Object -First 1
        $modelFull = $scenarios | Where-Object Id -eq 'edge-base-model-full' | Select-Object -First 1
        if (-not $edge10 -or -not $modelFull -or
            -not $edge10.Edge.svgNotEquals -or -not $edge10.Edge.emfNotEquals -or -not $edge10.Edge.pdfRenderNotEquals) {
            throw 'edge10 の Model 全体に対する三形式の負の対照が不足している'
        }
    }
    "ProbeOnly: $Set の $($scenarios.Count) ケースを生成し、Model O $($modelO.Count) ケースの真部分集合契約を確認した"
    exit 0
}

"=== campaign=$CampaignId ver=$Ver set=$Set cases=$($scenarios.Count) ==="
# FILEDIA/OSMODE 等は開始時に必ず記録し、正常・エラー終了の両方で finally が復元する
$regBefore = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
"レジストリ保全: AutoCAD のプロファイルの値 $($regBefore.Values.Count) 件と .aws $($regBefore.Aws.Count) 件を記録"

$results = New-Object System.Collections.ArrayList
$regReport = $null
$runError = $null
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # 既定は図面の種類ごとに 1 回の起動で続けて実行する。-OneLaunchPerCase は 1 ケース 1 起動（従来の形）
    $byId = @{}
    if (-not $OneLaunchPerCase) {
        foreach ($kind in @($scenarios | ForEach-Object { $_.Dwg } | Select-Object -Unique)) {
            $group = @($scenarios | Where-Object { $_.Dwg -eq $kind })
            "--- 図面 $kind の $($group.Count) ケースを 1 回の起動で実行する"
            $batch = Invoke-OptionMatrixBatch $group $kind
            foreach ($id in $batch.Keys) { $byId[$id] = $batch[$id] }
        }
    }
    $i = 0
    foreach ($sc in $scenarios) {
        $i++
        $r = if ($OneLaunchPerCase) { Invoke-OptionMatrixCase $sc } else { $byId[$sc.Id] }
        # クリップボードは共有資源で、利用者の作業（コピー操作）と競合し得る。競合起因の
        # 読み戻し失敗だけは1回だけ自動再実行する。それ以外の失敗、再実行後の失敗はそのまま FAIL。
        # clipboardUntouched も対象に含める（Office 系のコピーは CF_ENHMETAFILE を載せるため
        # 「別内容の EMF へ書き換わった」が外部コピーでも成立する。実測: 作業中の文書コピーで
        # 5,948 bytes の文面 EMF が載った。製品起因なら再実行でも決定的に再失敗する）
        $clipOnly = @($r.checks | Where-Object { -not $_.ok } | ForEach-Object name)
        if (-not $r.pass -and $clipOnly.Count -gt 0 -and -not ($clipOnly | Where-Object { $_ -notin @('clipboardEmf', 'clipboardReadable', 'clipboardUntouched') })) {
            "    ↻ {0}: クリップボード読み戻しのみ失敗のため1回だけ 1 ケース 1 起動で再実行する" -f $sc.Id
            $r = Invoke-OptionMatrixCase $sc
            $r.retried = $true
        }
        elseif (-not $r.pass -and -not $OneLaunchPerCase) {
            # まとめた実行でだけ不合格かを、1 ケース 1 起動で確かめる。合格でも、ケースの間の影響を見逃さないため不合格のままにする
            "    ↻ {0}: まとめた実行で不合格のため 1 ケース 1 起動で確かめる" -f $sc.Id
            # まとめた実行のケースのフォルダを残すため、別の名前のフォルダで実行する
            $scSingle = @{} + $sc
            $scSingle.Id = "$($sc.Id)-single"
            $single = Invoke-OptionMatrixCase $scSingle
            if ($single.pass) {
                Add-Check $r.checks 'batchOnlyFailure' $false '1 ケース 1 起動では合格した（まとめた実行でだけ不合格）'
            }
        }
        if ($sc.Baseline) { $r.Baseline = $true }
        if ($sc.Edge) { $r.edge = $sc.Edge }
        [void]$results.Add($r)
        $mark = if ($r.pass) { 'PASS' } else { 'FAIL' }
        '{0,4}/{1} {2,-28} {3} ({4:n1}s)' -f $i, $scenarios.Count, $sc.Id, $mark, ($r.elapsedMs / 1000)
        if (-not $r.pass) {
            foreach ($c in ($r.checks | Where-Object { -not $_.ok })) { "       NG {0}: {1}" -f $c.name, $c.detail }
        }
    }
}
catch { $runError = $_ }
finally {
    "所要 {0:n0} 秒（{1}）" -f $sw.Elapsed.TotalSeconds, $(if ($OneLaunchPerCase) { '1 ケース 1 起動' } else { '図面の種類ごとに 1 回の起動' })
    if ($regBefore) {
        Restore-CadRegStateAndReport $regBefore ([ref]$regReport)
        if (-not $regReport.ok) { "レジストリ復元の検証に失敗: " + ($regReport.mismatch -join '; ') }
    }
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（ケースの判定は途中までなので、集計は書き出さない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }

# クロスチェック
$cross = @()
switch ($Set) {
    'PetaFull' { $cross = Invoke-PetaCross $results }
    'PetaEdge' { $cross = Invoke-EdgeCross $results }
    'PetaData' { $cross = Invoke-PetaDataCross $results }
    'Smoke'    { $cross = @(Invoke-PetaCross $results) + @(Invoke-EdgeCross $results) }
}
foreach ($c in ($cross | Where-Object { -not $_.ok })) { "CROSS NG {0}: {1}" -f $c.name, $c.detail }

# 集計・保存
$asserted = @($results | Where-Object { -not $_.Baseline })
$failCases = @($asserted | Where-Object { -not $_.pass })
$failCross = @($cross | Where-Object { -not $_.ok })
$regOk = (-not $regBefore) -or $regReport.ok
$pass = ($failCases.Count -eq 0 -and $failCross.Count -eq 0 -and $regOk)
$summary = @{
    campaignId = $CampaignId; ver = $Ver; set = $Set; finishedAt = (Get-Date -Format 'o')
    total = $asserted.Count; passed = ($asserted.Count - $failCases.Count)
    failedCases = @($failCases | ForEach-Object id)
    crossTotal = @($cross).Count; crossFailed = @($failCross | ForEach-Object name)
    regGuard = $regReport
    pass = $pass
}
$resultPath = Join-Path $campaignDir "results\$Ver-$Set.json"
@{ summary = $summary; cases = @($results); cross = @($cross) } | ConvertTo-Json -Depth 10 | Out-File $resultPath -Encoding utf8

"=== $Set 結果: ケース {0}/{1} PASS / クロス {2}/{3} PASS / レジストリ保全 {4} → {5} ===" -f `
    ($asserted.Count - $failCases.Count), $asserted.Count, (@($cross).Count - $failCross.Count), @($cross).Count, `
    ($regOk ? 'OK' : 'NG'), $resultPath
if (-not $pass) { exit 1 }
exit 0
