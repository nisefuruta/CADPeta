# PETA の O（オブジェクト絞り込み）を、SDK が返す生の PromptStatus / StringResult / 件数まで
# 採取する AutoCAD headless 計測器。成果物の見た目ではなく、プロンプト境界の事実を確定する。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Ver = '2026',
    [string]$OutRoot = "$env:TEMP\CadPeta_object_keyword_probe",
    [int]$TimeoutSeconds = 180,
    [string]$InputDwg = '',
    [string]$Layout = '',
    [ValidateSet('OLE2FRAME','VIEWPORT')]
    [string]$PaperEntityType = 'OLE2FRAME',
    [switch]$ProbeOnly
)
$ErrorActionPreference = 'Stop'
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'object_keyword_probe.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
. "$PSScriptRoot\..\common\test_helpers.ps1"
$tfm = Get-CadPluginTargetFramework $Ver -UnknownVersionLabel "未対応版"
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = (Get-E2ePluginPath Acad $tfm)
$pluginDir = [IO.Path]::GetDirectoryName($plugin)
$paperEntityMode = -not [string]::IsNullOrWhiteSpace($InputDwg)
$source = if ($paperEntityMode) { [IO.Path]::GetFullPath($InputDwg) } else { "$repo\tests\assets\testcharts\TESTCHART2.dwg" }
if ($paperEntityMode -and [string]::IsNullOrWhiteSpace($Layout)) {
    throw 'InputDwg 指定時は Layout も必須'
}

$outRootFull = Resolve-OutRoot $OutRoot
if (-not (Test-Path $source)) { throw "入力図面がない: $source" }
if (-not $ProbeOnly) {
    if (-not (Test-Path $acad)) { throw "accoreconsole がない: $acad" }
}

# 50,40～150,110 は既存の TESTCHART2 窓回帰と同じ領域。_W の真部分集合であることは
# 各ケースの実行時に cpSubsetCount / cpAllCount を出し、PowerShell 側でも再検査する。
$setupCommon = if ($paperEntityMode) {
@"
(setvar "TILEMODE" 0)
(setvar "CTAB" "$Layout")
(command "_.PSPACE")
(setq cpAll (ssget "_X" (list (cons 410 "$Layout"))))
(setq cpCandidates (ssget "_X" (list (cons 0 "$PaperEntityType") (cons 410 "$Layout"))))
(setq cpSubset (ssadd))
(setq cpSubset2 (ssadd))
(setq cpI 0)
(if cpCandidates (while (< cpI (sslength cpCandidates)) (setq cpEntity (ssname cpCandidates cpI)) (setq cpData (entget cpEntity)) (if (or (/= "$PaperEntityType" "VIEWPORT") (> (if (assoc 69 cpData) (cdr (assoc 69 cpData)) 0) 1)) (progn (ssadd cpEntity cpSubset2) (if (= (sslength cpSubset) 0) (ssadd cpEntity cpSubset)))) (setq cpI (1+ cpI))))
(princ (strcat "\nCADPETA_SET_COUNTS all=" (itoa (if cpAll (sslength cpAll) 0)) " subset=" (itoa (if cpSubset (sslength cpSubset) 0)) " subset2=" (itoa (if cpSubset2 (sslength cpSubset2) 0))))(princ)
"@
} else {
@'
(setvar "TILEMODE" 1)
(setq cpAll (ssget "_X" '((410 . "Model"))))
(setq cpSubset (ssget "_W" '(50 40) '(150 110)))
(setq cpSubset2 (ssget "_W" '(60 45) '(140 100)))
(princ (strcat "\nCADPETA_SET_COUNTS all=" (itoa (if cpAll (sslength cpAll) 0)) " subset=" (itoa (if cpSubset (sslength cpSubset) 0)) " subset2=" (itoa (if cpSubset2 (sslength cpSubset2) 0))))(princ)
'@
}

# Accepted=$true は生 Status=Keyword と、その後に選択プロンプトへ進むことを合格条件にする。
# StringResult は SDK の生値なので global 名への正規化前であり、値そのものは証跡へ残す。
# 負例は拒否後に _All を与えるため、再プロンプトから復帰して終了マーカーへ到達できる。
$cases = if ($paperEntityMode) {
    $paperCases = @(
        @{ Id='paper-pickfirst';  Lang='ja'; Selection='pickfirst'; Lines=@('(sssetfirst nil cpSubset)','PETA','') },
        @{ Id='paper-postselect'; Lang='ja'; Selection='postselect'; Lines=@('PETA','O','!cpSubset','','') }
    )
    if ($PaperEntityType -eq 'VIEWPORT') {
        $paperCases += @{
            Id='paper-pickfirst-override-window'; Lang='ja'; Selection='pickfirst-override';
            Setup=@('(setq cpOldOsMode (getvar "OSMODE"))','(setvar "OSMODE" 0)');
            Teardown=@('(setvar "OSMODE" cpOldOsMode)');
            Lines=@('(sssetfirst nil cpSubset)','PETA','O','!cpSubset2','','0,0','420,297')
        }
        $paperCases += @{
            Id='paper-viewport-nonplot-layer'; Lang='ja'; Selection='postselect';
            Setup=@(
                '(setq cpSelectedNonPlot (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") ''(8 . "Defpoints") ''(67 . 1) (cons 410 (getvar "CTAB")) ''(420 . 130687) ''(370 . 100) ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 10.0 10.0) ''(10 40.0 10.0) ''(10 40.0 30.0) ''(10 10.0 30.0))))',
                '(setq cpUnselectedNonPlot (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") ''(8 . "Defpoints") ''(67 . 1) (cons 410 (getvar "CTAB")) ''(420 . 8323582) ''(370 . 100) ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 50.0 10.0) ''(10 80.0 10.0) ''(10 80.0 30.0) ''(10 50.0 30.0))))',
                '(setq cpMixed (ssadd))',
                '(ssadd (ssname cpSubset 0) cpMixed)',
                '(ssadd cpSelectedNonPlot cpMixed)'
            );
            Teardown=@('(princ (strcat "\nCADPETA_NONPLOT_RESTORED layer=" (cdr (assoc 8 (entget cpSelectedNonPlot))) " visible=" (itoa (if (assoc 60 (entget cpSelectedNonPlot)) (cdr (assoc 60 (entget cpSelectedNonPlot))) 0)))) (princ)');
            Lines=@('PETA','O','!cpMixed','','')
            ExpectSvgColor='rgb(1,254,127)'; RejectSvgColor='rgb(127,1,254)'
            ExpectedStdoutPatterns=@('CADPETA_NONPLOT_RESTORED layer=Defpoints visible=0')
        }
        $paperCases += @{
            Id='paper-viewport-vp-freeze'; Lang='ja'; Selection='postselect';
            Setup=@(
                '(setq cpVp (ssname cpSubset 0))',
                '(setq cpVpData (entget cpVp))',
                '(setq cpVpId (cdr (assoc 69 cpVpData)))',
                '(setq cpViewCenter (cdr (assoc 12 cpVpData)))',
                '(setq cpViewHeight (cdr (assoc 45 cpVpData)))',
                '(entmakex ''((0 . "LAYER") (100 . "AcDbSymbolTableRecord") (100 . "AcDbLayerTableRecord") (2 . "CADPETA_VP_VISIBLE") (70 . 0) (62 . 7) (6 . "Continuous")))',
                '(entmakex ''((0 . "LAYER") (100 . "AcDbSymbolTableRecord") (100 . "AcDbLayerTableRecord") (2 . "CADPETA_VP_FROZEN") (70 . 0) (62 . 7) (6 . "Continuous")))',
                '(command "_.MSPACE")',
                '(setvar "CVPORT" cpVpId)',
                '(setq cpMarkerCenter (trans (list (car cpViewCenter) (cadr cpViewCenter) 0.0) 2 0))',
                '(setq cpMarkerRadius (* cpViewHeight 0.04))',
                '(entmakex (list ''(0 . "CIRCLE") ''(100 . "AcDbEntity") ''(8 . "CADPETA_VP_VISIBLE") ''(67 . 0) ''(410 . "Model") ''(420 . 195966) ''(370 . 100) ''(100 . "AcDbCircle") (cons 10 cpMarkerCenter) (cons 40 cpMarkerRadius)))',
                '(entmakex (list ''(0 . "CIRCLE") ''(100 . "AcDbEntity") ''(8 . "CADPETA_VP_FROZEN") ''(67 . 0) ''(410 . "Model") ''(420 . 8258301) ''(370 . 100) ''(100 . "AcDbCircle") (cons 10 cpMarkerCenter) (cons 40 (* cpMarkerRadius 0.6))))',
                '(command "_.VPLAYER" "_Freeze" "CADPETA_VP_FROZEN" "_Current" "")',
                '(command "_.PSPACE")',
                '(setq cpFrozenLayer (tblobjname "LAYER" "CADPETA_VP_FROZEN"))',
                '(princ (strcat "\nCADPETA_VP_FROZEN=" (if (member cpFrozenLayer (mapcar ''cdr (vl-remove-if-not ''(lambda (x) (= (car x) 331)) (entget cpVp)))) "1" "0"))) (princ)'
            );
            Lines=@('PETA','O','!cpSubset','','')
            ExpectSvgColor='rgb(2,253,126)'; RejectSvgColor='rgb(126,2,253)'
            ExpectedStdoutPatterns=@('CADPETA_VP_FROZEN=1')
        }
        $paperCases += @{
            Id='paper-viewport-off'; Lang='ja'; Selection='postselect';
            Setup=@(
                '(setq cpVp (ssname cpSubset 0))',
                '(command "_.MVIEW" "_OFF" cpVp "")',
                '(command "_.REGENALL")',
                '(princ (strcat "\nCADPETA_VP_OFF_STATUS=" (itoa (cdr (assoc 68 (entget cpVp)))))) (princ)'
            );
            Lines=@('PETA','O','!cpSubset','','')
            ExpectedNoArtifact=$true
            ExpectedStdoutPatterns=@(
                'CADPETA_VP_OFF_STATUS=0',
                'オフのビューポートは表示内容がないため選択出力の対象にできない'
            )
        }
        $paperCases += @{
            Id='paper-viewport-inactive-on'; Lang='ja'; Selection='postselect';
            Setup=@(
                '(setq cpOldMaxActVp (getvar "MAXACTVP"))',
                '(setvar "MAXACTVP" 2)',
                '(command "_.MVIEW" "350,250" "400,290")',
                '(command "_.REGENALL")',
                '(setq cpCandidates (ssget "_X" (list ''(0 . "VIEWPORT") (cons 410 (getvar "CTAB")))))',
                '(setq cpInactive (ssadd))',
                '(setq cpI 0)',
                '(while (< cpI (sslength cpCandidates)) (setq cpVp (ssname cpCandidates cpI)) (if (= (cdr (assoc 68 (entget cpVp))) -1) (ssadd cpVp cpInactive)) (setq cpI (1+ cpI)))',
                '(princ (strcat "\nCADPETA_VP_INACTIVE_ON_COUNT=" (itoa (sslength cpInactive)))) (princ)'
            );
            Teardown=@('(setvar "MAXACTVP" cpOldMaxActVp)');
            Lines=@('PETA','O','!cpInactive','','')
            ExpectedPositiveMarker='CADPETA_VP_INACTIVE_ON_COUNT'
        }
    }
    $paperCases
} else { @(
    @{ Id='keyword-o';          Token='O';          Lang='ja'; Accepted=$true;  Lines=@('PETA','O','!cpSubset','','_All') },
    @{ Id='keyword-global';     Token='_Object';    Lang='ja'; Accepted=$true;  Lines=@('PETA','_Object','!cpSubset','','_All') },
    @{ Id='keyword-object';     Token='Object';     Lang='ja'; Accepted=$false; ExpectedReject=$true; Lines=@('PETA','Object') },
    @{ Id='keyword-o-en';       Token='O';          Lang='en'; Accepted=$true;  Lines=@('PETA','O','!cpSubset','','_All') },
    @{ Id='negative-display';   Token='オブジェクト'; Lang='ja'; Accepted=$false; ExpectedReject=$true; Lines=@('PETA','オブジェクト') },
    @{ Id='negative-fullwidth'; Token='Ｏ';         Lang='ja'; Accepted=$false; ExpectedReject=$true; Lines=@('PETA','Ｏ') },
    @{ Id='pickfirst';          Lang='ja'; Selection='pickfirst'; Lines=@('(sssetfirst nil cpSubset)','PETA','') },
    @{ Id='postselect';         Lang='ja'; Selection='postselect'; Lines=@('PETA','O','!cpSubset','','') },
    @{ Id='empty-enter';        Lang='ja'; Selection='empty'; Lines=@('PETA','O','','') },
    @{ Id='reselect';           Lang='ja'; Selection='reselect'; Lines=@('PETA','O','!cpSubset','','O','!cpSubset2','','') },
    @{ Id='side-world';         Lang='ja'; Selection='postselect'; Dump=$true; Diagnostic=$true; Lines=@('PETADIAG','O','!cpSubset','','') },
    @{ Id='side-ucs';           Lang='ja'; Selection='postselect'; Dump=$true; Diagnostic=$true;
       Setup=@('(command "_.UCS" "_O" "123,-45,0")','(command "_.UCS" "_Z" "36.869897645844")');
       Teardown=@('(command "_.UCS" "_W")');
       Lines=@('PETADIAG','O','!cpSubset','','') }
) }

[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$cp932 = [Text.Encoding]::GetEncoding(932, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)

function Write-Cp932Exact([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, $cp932)
    $readBack = [IO.File]::ReadAllText($Path, $cp932)
    if ($readBack -cne $Text) { throw "CP932 の書き込み再読が一致しない: $Path" }
}

function Get-Field($Object, [string[]]$Names) {
    foreach ($name in $Names) {
        $p = $Object.PSObject.Properties[$name]
        if ($p) { return $p.Value }
    }
    $null
}

function Test-IsEndEvent($Event) {
    $kind = [string](Get-Field $Event @('event','operation','kind','type'))
    $kind -match '(?i)(command[-_.]?end|end)'
}

function Get-PromptEvents($Events, [string]$OperationPattern) {
    @($Events | Where-Object {
        ([string](Get-Field $_ @('operation','event','kind','type'))) -match $OperationPattern
    })
}

function New-CaseFiles($Case) {
    $work = Join-Path $outRootFull $Case.Id
    if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
    $dwg = Join-Path $work 'probe.dwg'
    Copy-Item -LiteralPath $source -Destination $dwg
    $sourceHash = (Get-FileHash $dwg -Algorithm SHA256).Hash
    $trace = Join-Path $work 'prompt-trace.jsonl'
    $dumpStem = Join-Path $work 'side-db'
    $end = "CADPETA_CASE_END=$($Case.Id)"
    $pluginScr = (Join-Path $CadPetaTrustedDotnetFolder (Split-Path -Leaf $plugin)) -replace '\\','/'
    $body = New-Object System.Collections.Generic.List[string]
    $body.Add('(setvar "CMDECHO" 1)')
    $body.Add($setupCommon.TrimEnd())
    if ($Case.Setup) { foreach ($line in $Case.Setup) { $body.Add($line) } }
    $body.Add("(command `"_.NETLOAD`" `"$pluginScr`")")
    foreach ($line in $Case.Lines) { $body.Add($line) }
    if ($Case.Teardown) { foreach ($line in $Case.Teardown) { $body.Add($line) } }
    $body.Add("(princ `"\n$end`")(princ)")
    $body.Add('_.CLOSE')
    $body.Add('_Y')
    $body.Add('_.QUIT')
    $text = ($body -join "`r`n") + "`r`n"
    $scr = Join-Path $work 'run.scr'
    Write-Cp932Exact $scr $text
    @{
        Work=$work; Dwg=$dwg; SourceHash=$sourceHash; Trace=$trace; DumpStem=$dumpStem
        End=$end; Scr=$scr; Stdout=(Join-Path $work 'stdout.log'); Stderr=(Join-Path $work 'stderr.log')
    }
}

function Invoke-ObjectKeywordCase($Case, $Files) {
    $fail = New-Object System.Collections.Generic.List[string]
    $runId = "object-keyword-$([guid]::NewGuid().ToString('N'))"
    $childEnv = @{
        CADPETA_PROMPT_TRACE = $Files.Trace
        CADPETA_PROMPT_TRACE_RUN_ID = $runId
        CADPETA_PROMPT_TRACE_CASE_ID = $Case.Id
        CADPETA_DUMP_SIDE_DB = ''
        CADPETA_LANG = $Case.Lang
    }
    if ($Case.Dump) { $childEnv.CADPETA_DUMP_SIDE_DB = $Files.DumpStem }

    # Start-Process は親環境を継承するため、生成の瞬間だけ設定して直後に戻す。
    # 正常・timeout・起動例外の全経路で元値と存在有無を復元し、読み戻して検証する。
    $saved = @{}
    $p = $null
    try {
        foreach ($name in $childEnv.Keys) {
            $old = [Environment]::GetEnvironmentVariable($name, 'Process')
            $saved[$name] = @{ Exists=(Test-Path -LiteralPath "Env:$name"); Value=$old }
            [Environment]::SetEnvironmentVariable($name, [string]$childEnv[$name], 'Process')
        }
        $p = Start-Process -WorkingDirectory (Split-Path -Parent $Files.Scr) -FilePath $acad -ArgumentList (ConvertTo-NativeArgString @('/i', $Files.Dwg, '/s', $Files.Scr, '/l', 'ja-JP')) `
            -RedirectStandardOutput $Files.Stdout -RedirectStandardError $Files.Stderr -PassThru -WindowStyle Hidden
    }
    finally {
        foreach ($name in $childEnv.Keys) {
            $old = $saved[$name]
            if ($old.Exists) {
                [Environment]::SetEnvironmentVariable($name, [string]$old.Value, 'Process')
            } else {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            $actual = [Environment]::GetEnvironmentVariable($name, 'Process')
            $actualExists = Test-Path -LiteralPath "Env:$name"
            if (($old.Exists -and (-not $actualExists -or $actual -cne $old.Value)) -or (-not $old.Exists -and $actualExists)) {
                $fail.Add("親プロセス環境を復元できない: $name")
            }
        }
    }
    if (-not $p) { throw "accoreconsole を起動できなかった: $($Case.Id)" }
    $waitSeconds = $Case.ExpectedReject ? 12 : $TimeoutSeconds
    $timedOut = -not $p.WaitForExit($waitSeconds * 1000)
    if ($timedOut) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        $p.WaitForExit()
        if (-not $Case.ExpectedReject) { $fail.Add("accoreconsole が ${waitSeconds}秒で完了しない") }
    }
    if (-not $p.HasExited) { $fail.Add("所有 accoreconsole が終了していない: PID=$($p.Id)") }
    elseif (-not $timedOut -and $p.ExitCode -ne 0) { $fail.Add("accoreconsole 異常終了: exit=$($p.ExitCode)") }

    $stdoutText = if (Test-Path $Files.Stdout) { Get-Content -LiteralPath $Files.Stdout -Raw -Encoding Unicode } else { '' }
    if (-not $Case.ExpectedReject -and $stdoutText -notmatch [regex]::Escape($Files.End)) { $fail.Add('スクリプト終了マーカーがない') }
    if ($stdoutText -notmatch 'CADPETA_SET_COUNTS all=(\d+) subset=(\d+) subset2=(\d+)') {
        $fail.Add('選択集合件数マーカーがない')
        $counts = $null
    } else {
        $counts = @{ All=[int]$Matches[1]; Subset=[int]$Matches[2]; Subset2=[int]$Matches[3] }
        if ($counts.Subset -le 0 -or $counts.Subset -ge $counts.All) {
            $fail.Add("窓選択が真部分集合でない: subset=$($counts.Subset) all=$($counts.All)")
        }
        if ($counts.Subset2 -le 0 -or $counts.Subset2 -ge $counts.All) {
            $fail.Add("再選択集合が真部分集合でない: subset2=$($counts.Subset2) all=$($counts.All)")
        }
    }
    if ($paperEntityMode) {
        if ($stdoutText -match '\[CadPeta\] エラー|System\.InvalidOperationException|eWrongDatabase') {
            $fail.Add('ペーパー空間選択で例外が発生した')
        }
        $emf = @(Get-ChildItem -LiteralPath $Files.Work -Filter '*.emf' -Recurse -ErrorAction SilentlyContinue)
        if ($Case.ExpectedNoArtifact) {
            $artifacts = @(Get-ChildItem -LiteralPath $Files.Work -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -in @('.emf','.svg','.pdf','.png') })
            if ($artifacts.Count -ne 0) {
                $fail.Add("対象外選択なのに成果物が生成された: $($artifacts.Name -join ', ')")
            }
        } elseif ($PaperEntityType -eq 'VIEWPORT') {
            if ($emf.Count -ne 1 -or $emf[0].Length -le 0) {
                $fail.Add("ペーパー空間選択EMF個数またはサイズが不正: count=$($emf.Count)")
            }
            $svg = @(Get-ChildItem -LiteralPath $Files.Work -Filter '*.svg' -Recurse -ErrorAction SilentlyContinue)
            $pathCount = if ($svg.Count -eq 1) {
                ([regex]::Matches((Get-Content -LiteralPath $svg[0].FullName -Raw -Encoding utf8), '<path\b')).Count
            } else { 0 }
            if ($svg.Count -ne 1 -or $svg[0].Length -le 1000 -or $pathCount -le 2) {
                $fail.Add("Viewport内容が枠を超えて描画されていない: svg=$($svg.Count) bytes=$(if($svg.Count){$svg[0].Length}else{0}) paths=$pathCount")
            }
            if ($Case.ExpectSvgColor -and $svg.Count -eq 1) {
                $svgText = Get-Content -LiteralPath $svg[0].FullName -Raw -Encoding utf8
                if (-not $svgText.Contains([string]$Case.ExpectSvgColor)) {
                    $fail.Add("表示されるべき固有色がSVGにない: $($Case.ExpectSvgColor)")
                }
                if ($Case.RejectSvgColor -and $svgText.Contains([string]$Case.RejectSvgColor)) {
                    $fail.Add("表示されない固有色がSVGへ混入した: $($Case.RejectSvgColor)")
                }
            }
        }
        foreach ($pattern in @($Case.ExpectedStdoutPatterns)) {
            if ($stdoutText -notmatch [regex]::Escape([string]$pattern)) {
                $fail.Add("期待した状態または診断文言がない: $pattern")
            }
        }
        if ($Case.ExpectedPositiveMarker) {
            $positivePattern = [regex]::Escape([string]$Case.ExpectedPositiveMarker) + '=(\d+)'
            if ($stdoutText -notmatch $positivePattern -or [int]$Matches[1] -le 0) {
                $fail.Add("正数であるべき状態マーカーがない: $($Case.ExpectedPositiveMarker)")
            }
        }
    }

    try { $events = @(Get-JsonLines $Files.Trace) }
    catch { $fail.Add($_.Exception.Message); $events = @() }
    if ($events.Count -eq 0) { $fail.Add('生traceがない') }
    if (-not $Case.ExpectedReject -and -not ($events | Where-Object { Test-IsEndEvent $_ })) { $fail.Add('traceのend eventがない') }
    foreach ($event in $events) {
        $eventRun = Get-Field $event @('runId','run_id')
        $eventCase = Get-Field $event @('caseId','case_id')
        if ($null -ne $eventRun -and [string]$eventRun -ne $runId) { $fail.Add("trace run ID不一致: $eventRun") }
        if ($null -ne $eventCase -and [string]$eventCase -ne $Case.Id) { $fail.Add("trace case ID不一致: $eventCase") }
    }

    $windowEvents = @(Get-PromptEvents $events '^PromptWindow\.GetPoint$')
    $selectionEvents = @(Get-PromptEvents $events '^PromptSelection\.GetSelection$')
    $pickfirstEvents = @(Get-PromptEvents $events '^GetPickfirstSelection\.SelectImplied$')
    if ($Case.ContainsKey('Accepted')) {
        if ($Case.Accepted -and $windowEvents.Count -eq 0) { $fail.Add('PromptWindow traceがない') }
        $keyword = @($windowEvents | Where-Object { ([string](Get-Field $_ @('status','rawStatus','raw_status'))) -match '(?i)Keyword' })
        if ($Case.Accepted -and $keyword.Count -eq 0) { $fail.Add("受理必須トークン '$($Case.Token)' がKeywordにならない") }
        if ($Case.Accepted -and -not ($selectionEvents | Where-Object { [int](Get-Field $_ @('count')) -gt 0 })) {
            $fail.Add("受理必須トークン '$($Case.Token)' の後に選択プロンプトへ進まない")
        }
        if (-not $Case.Accepted -and $keyword.Count -gt 0) {
            $badAccepted = @($keyword | Where-Object { [string](Get-Field $_ @('stringResult','string_result','result')) -ne 'All' })
            if ($badAccepted.Count -gt 0) { $fail.Add("負例 '$($Case.Token)' がキーワードとして受理された") }
        }
    }
    $negativeInputVisible = $null
    if ($Case.ContainsKey('Accepted') -and -not $Case.Accepted) {
        $negativeInputVisible = $stdoutText.Contains([string]$Case.Token)
        if (-not $timedOut) { $fail.Add("負例 '$($Case.Token)' の入力待ちが継続しない") }
        if (-not $negativeInputVisible) { $fail.Add("負例 '$($Case.Token)' がconsole logにない") }
        if ($stdoutText -notmatch '点を指定 または オプションのキーワード') {
            $fail.Add("負例 '$($Case.Token)' の拒否プロンプトがない")
        }
    }
    switch ($Case.Selection) {
        'pickfirst' {
            if ($pickfirstEvents.Count -eq 0) { $fail.Add('SelectImplied traceがない') }
            elseif (-not ($pickfirstEvents | Where-Object { [int](Get-Field $_ @('count')) -gt 0 })) { $fail.Add('先選択件数が0') }
        }
        { $_ -in @('postselect','empty','reselect') } {
            if ($selectionEvents.Count -eq 0) { $fail.Add('GetSelection traceがない') }
            if ($_ -eq 'reselect' -and $selectionEvents.Count -lt 2) { $fail.Add('再選択のGetSelection traceが2回未満') }
            if ($_ -in @('postselect','reselect') -and -not ($selectionEvents | Where-Object { [int](Get-Field $_ @('count')) -gt 0 })) {
                $fail.Add('後選択件数が0')
            }
            if ($_ -eq 'empty' -and ($selectionEvents | Where-Object { [int](Get-Field $_ @('count')) -gt 0 })) {
                $fail.Add('空Enterなのに選択件数が0でない')
            }
        }
        'pickfirst-override' {
            if ($pickfirstEvents.Count -eq 0) { $fail.Add('SelectImplied traceがない') }
            elseif ($counts -and -not ($pickfirstEvents | Where-Object { [int](Get-Field $_ @('count')) -eq $counts.Subset })) {
                $fail.Add("先選択件数が旧集合と一致しない: expected=$($counts.Subset)")
            }
            if ($selectionEvents.Count -ne 1) {
                $fail.Add("先選択からOへの切替でGetSelectionが1回ではない: $($selectionEvents.Count)")
            }
            elseif ($counts -and [int](Get-Field $selectionEvents[0] @('count')) -ne $counts.Subset2) {
                $fail.Add("O後の後選択件数が新集合と一致しない: actual=$(Get-Field $selectionEvents[0] @('count')) expected=$($counts.Subset2)")
            }
            if ($counts -and $counts.Subset -eq $counts.Subset2) {
                $fail.Add("旧集合と新集合の件数が同じで切替を識別できない: $($counts.Subset)")
            }
            if ($windowEvents.Count -lt 2) {
                $fail.Add("O後の二点窓プロンプトまで進んでいない: $($windowEvents.Count)")
            }
        }
    }
    $dumpFiles = @()
    $dumpMetadata = @{}
    if ($Case.Dump) {
        foreach ($observation in @('raw','ready')) {
            $dumpPath = "$($Files.DumpStem).$observation.json"
            $dumpFiles += $dumpPath
            if (-not (Test-Path $dumpPath)) { $fail.Add("側DB $observation metadataがない: $dumpPath"); continue }
            try { $dump = Get-Content -LiteralPath $dumpPath -Raw -Encoding utf8 | ConvertFrom-Json -ErrorAction Stop }
            catch { $fail.Add("側DB $observation metadataが不正: $($_.Exception.Message)"); continue }
            if ($dump.observation -cne $observation) { $fail.Add("側DB observation不一致: $($dump.observation) 期待=$observation") }
            if ([string]$dump.creationMethod -notin @('Wblock','clone')) { $fail.Add("側DB creationMethod不正: $($dump.creationMethod)") }
            if ([int]$dump.entityCount -le 0) { $fail.Add("側DB $observation のentityCountが0") }
            if (-not $dump.entityTypes) { $fail.Add("側DB $observation のentityTypesがない") }
            if (-not $dump.extents) { $fail.Add("側DB $observation のextentsがない") }
            if ([int]$dump.extentsUnavailableCount -ne 0) {
                $fail.Add("側DB $observation のextents取得不能がある: $($dump.extentsUnavailableCount)")
            }
            $typeTotal = 0
            foreach ($property in $dump.entityTypes.PSObject.Properties) { $typeTotal += [int]$property.Value }
            if ($typeTotal -ne [int]$dump.entityCount) {
                $fail.Add("側DB $observation の型別件数合計が総数と不一致: types=$typeTotal total=$($dump.entityCount)")
            }
            $dumpMetadata[$observation] = $dump
        }
        if ($dumpMetadata.raw -and $counts -and [int]$dumpMetadata.raw.entityCount -ne $counts.Subset) {
            $fail.Add("raw側DB件数が元窓選択と不一致: raw=$($dumpMetadata.raw.entityCount) source=$($counts.Subset)")
        }
        if ($dumpMetadata.raw -and $dumpMetadata.ready -and
            [int]$dumpMetadata.raw.entityCount -ne [int]$dumpMetadata.ready.entityCount) {
            $fail.Add("raw/readyで側DB件数が変化: raw=$($dumpMetadata.raw.entityCount) ready=$($dumpMetadata.ready.entityCount)")
        }
        $diag = @(Get-ChildItem -LiteralPath $Files.Work -Filter '*.diag.json' -Recurse -ErrorAction SilentlyContinue)
        $stage = @(Get-ChildItem -LiteralPath $Files.Work -Filter '*.stage*.svg' -Recurse -ErrorAction SilentlyContinue)
        $pdf = @(Get-ChildItem -LiteralPath $Files.Work -Filter '*.pdf' -Recurse -ErrorAction SilentlyContinue)
        if ($diag.Count -ne 1) { $fail.Add("PETADIAG diag.json個数不正: $($diag.Count)") }
        if ($stage.Count -lt 1) { $fail.Add('PETADIAG stage SVGがない') }
        if ($pdf.Count -ne 1) { $fail.Add("PETADIAG中間/出力PDF個数不正: $($pdf.Count)") }
    }
    if ((Get-FileHash $Files.Dwg -Algorithm SHA256).Hash -ne $Files.SourceHash) { $fail.Add('入力DWGが変更された') }

    [pscustomobject]@{
        id=$Case.Id; pass=($fail.Count -eq 0); failures=@($fail); pid=$p.Id; runId=$runId; timedOut=$timedOut
        counts=$counts; traceEvents=$events.Count; windowEvents=$windowEvents.Count
        keywordRaw=@($windowEvents | Where-Object { ([string](Get-Field $_ @('status'))) -match '(?i)Keyword' } |
            ForEach-Object { Get-Field $_ @('stringResult') })
        selectionEvents=$selectionEvents.Count; pickfirstEvents=$pickfirstEvents.Count
        negativeInputVisible=$negativeInputVisible; trace=$Files.Trace
        dumpStem=($Case.Dump ? $Files.DumpStem : $null); dumpFiles=$dumpFiles; stdout=$Files.Stdout
    }
}

New-Item -ItemType Directory -Force $outRootFull | Out-Null
$prepared = @()
foreach ($case in $cases) { $prepared += [pscustomobject]@{ Case=$case; Files=(New-CaseFiles $case) } }

if ($ProbeOnly) {
    $manifest = @($prepared | ForEach-Object {
        @{ id=$_.Case.Id; accepted=$_.Case.Accepted; selection=$_.Case.Selection; language=$_.Case.Lang;
           script=$_.Files.Scr; trace=$_.Files.Trace; dumpStem=($_.Case.Dump ? $_.Files.DumpStem : $null) }
    })
    $manifestPath = Join-Path $outRootFull 'probe-manifest.json'
    $manifest | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $manifestPath -Encoding utf8
    "ProbeOnly OK: $($cases.Count) ケースのSCRをCP932往復検証した → $manifestPath"
    exit 0
}

. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$results = @()
$regReport = $null
$runError = $null
try {
    foreach ($item in $prepared) {
        $result = Invoke-ObjectKeywordCase $item.Case $item.Files
        $results += $result
        $mark = $result.pass ? 'PASS' : 'FAIL'
        "[{0,-20}] {1} trace={2} window={3} selection={4} pickfirst={5}" -f `
            $result.id,$mark,$result.traceEvents,$result.windowEvents,$result.selectionEvents,$result.pickfirstEvents
        foreach ($failure in $result.failures) { "  NG: $failure" }
    }
}
catch { $runError = $_ }
finally {
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
# 本体が例外で止まったときは、その例外と復元の失敗を合わせて投げる（ケースの判定は途中までなので、結果は書き出さない）
if ($runError) { Assert-CadRunAndRegRestore $runError $regReport }
if (-not $regReport.ok) {
    $results += [pscustomobject]@{ id='registry-guard'; pass=$false; failures=@("復元検証失敗: " + ($regReport.mismatch -join '; ')) }
}

$resultPath = Join-Path $outRootFull 'results.json'
@{ ver=$Ver; finishedAt=(Get-Date -Format o); regGuard=$regReport; cases=$results } |
    ConvertTo-Json -Depth 12 | Out-File -LiteralPath $resultPath -Encoding utf8
$failed = @($results | Where-Object { -not $_.pass })
if ($failed.Count -gt 0) { throw "Oプロンプト計測が $($failed.Count) ケース失敗した → $resultPath" }
"Oプロンプト計測 $($results.Count) ケース合格 → $resultPath"
