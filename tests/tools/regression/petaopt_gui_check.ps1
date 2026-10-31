# PETAOPT のダイアログとリボンを、画面のある AutoCAD（acad.exe）の 1 回の起動で確かめる。
#  - 読み込みの後、リボンの「アドイン」タブに CADPeta のパネルが 1 つ置かれる（試験の記録の Ribbon.Place）
#  - PETAOPT のダイアログを UI オートメーションで操作し（PNG をオン、PNG の解像度を 150、ファイル名に {comment}、OK）、設定のファイルに保存される
#  - 続く PETA が、範囲の指定の後にコメントのダイアログを開く。ダイアログは入力欄にフォーカスを置いて AutoCAD のウィンドウの中央に開き、
#    使えない文字では理由を出して OK を押せず、使えるコメントではファイル名を表示し、Enter で確定する
#  - その PETA が、保存した形式（PDF・SVG・EMF・PNG）を、入力したコメントを入れた名前で図面のフォルダに出す
#  - スクリプトの後、リボンの PETA のボタンを UI オートメーションで押す。先に選んだオブジェクトがあれば PETA がそれを受け取り（選択を解除しない）、
#    コマンドの実行中（LINE の途中）に押せば、そのコマンドを取り消して PETA が始まる。PETA への入力と終了は、AutoCAD のメイン ウィンドウへ
#    WM_COPYDATA で文字列を送る（コマンド ラインへの入力と同じ。キーボードとフォーカスを使わない）。
#    この 2 つの PETA はコメントのダイアログを [キャンセル] と Esc で閉じ、何も出力せずに終わる
#  - リボンの「設定」のボタンを押すと PETAOPT のダイアログが開き、[キャンセル] で PETAOPT が終わる。ボタンにマウスを重ねると、
#    ツールチップに題名「設定」とコマンド名 PETAOPT が出る（マウスはその間だけ動かして元へ戻す）
# ダイアログの画像を作業フォルダに残す（配色と部品の配置を目で確かめるため）。画面のロック中は画像とクリップボードが使えないので実行しない。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('2024', '2026', '2027')][string]$Ver = '2027',
    [string]$OutRoot = "$env:TEMP\CadPeta_petaopt_gui",
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
# AutoCAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'petaopt_gui_check.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
. "$PSScriptRoot\CadRegGuard.ps1"
. "$PSScriptRoot\..\common\cad_dialog_ui.ps1"

$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$plugin = Get-E2ePluginPath Acad (Get-CadPluginTargetFramework $Ver)
Assert-E2ePluginBuild $plugin
$acadExe = "C:\Program Files\Autodesk\AutoCAD $Ver\acad.exe"
if (-not (Test-Path -LiteralPath $acadExe)) { throw "acad.exe がない: $acadExe" }
if (@(Get-CadRegRunningCad).Count) { throw 'AutoCAD が動いている。終わってから実行する' }
# インストール済みの CADPeta があると、acad.exe が起動時にその DLL を読み込み、試験の DLL を NETLOAD できない（同じ名前のアセンブリ）
$installed = @(Get-InstalledCadPetaBundles)
if ($installed.Count) { throw "インストール済みの CADPeta を acad.exe が自動で読み込むので、試験の DLL を確かめられない。アンインストールしてから実行する: $($installed -join ', ')" }

$work = Join-Path (Resolve-OutRoot $OutRoot) (Get-Date -Format 'yyyyMMdd_HHmmss')
New-Item -ItemType Directory -Force $work | Out-Null
$dwg = Join-Path $work 'TESTCHART2.dwg'
Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg
$dataDir = Write-E2eSettings (Join-Path $work 'cadpeta-data')
$settingsPath = Join-Path $dataDir 'settings.json'
$trace = Join-Path $work 'prompt-trace.jsonl'
$done = Join-Path $work 'done.txt'
$doneLisp = $done -replace '\\', '/'
$scr = Join-Path $work 'run.scr'
$lines = @(Get-AccoreNetloadLines $plugin) + @(
    'PETAOPT'
    'PETA'
    ''
    # スクリプトの後のリボンの試験のため、図面の全部のオブジェクトを先に選んだ状態にしてから、終わりの合図を書く
    '(sssetfirst nil (ssget "_X"))(princ)'
    "(progn (setq cpF (open `"$doneLisp`" `"w`")) (write-line `"done`" cpF) (close cpF))(princ)"
)
[IO.File]::WriteAllText($scr, ($lines -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)

$checks = New-Object System.Collections.Generic.List[object]
function Get-AcadMainWindow($proc) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "起動した AutoCAD（PID $($proc.Id)）のメイン ウィンドウが無い" }
    $proc.MainWindowHandle
}

# リボンの表示名が name のボタンを押す。押せたら空、押せなければ理由
function Invoke-RibbonButton($proc, [string]$name) {
    $reason = ''
    $button = Find-RibbonButton (Get-AcadMainWindow $proc) $name ([ref]$reason)
    if (-not $button) { return $reason }
    Invoke-Element $button
    ''
}

# 試験の記録の from 行目より後に、条件に合う行が出るまで待つ（出なければ null）
function Wait-TraceLine([int]$from, [scriptblock]$match, [datetime]$deadline) {
    while ((Get-Date) -lt $deadline) {
        $lines = @(if (Test-Path -LiteralPath $trace) { Get-JsonLines $trace })
        $found = $lines | Select-Object -Skip $from | Where-Object $match | Select-Object -First 1
        if ($found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    $null
}

function Get-TraceCount { @(if (Test-Path -LiteralPath $trace) { Get-JsonLines $trace }).Count }

# リボンから始めた PETA の範囲の問い合わせに Enter（全体）で答え、コメントのダイアログを how（Cancel か Escape）で閉じて、
# Enter が答えとして受け付けられ（問い合わせの結果が None）、ダイアログがキャンセルで閉じ、PETA が終わったかを判定の name に記録する。
# 答えは問い合わせが入力を待ち始めてから送る（試験の記録の .Waiting の行。待つ前に送ると答えにならず、問い合わせが取り消しで終わって、
# 後で PETA がもう一度始まった）
function Complete-RibbonPeta($proc, [int]$from, [datetime]$deadline, [string]$name, [string]$how) {
    $waiting = Wait-TraceLine $from { $_.command -eq 'PETA' -and $_.operation -eq 'PromptWindow.GetPoint.Waiting' } $deadline
    if (-not $waiting) { $checks.Add(@{ Name = $name; Ok = $false; Detail = '範囲の問い合わせが入力を待たなかった' }); return }
    [void][CadPeta.Gui.Win]::SendCommandLine((Get-AcadMainWindow $proc), ' ')
    $answer = Wait-TraceLine $from { $_.command -eq 'PETA' -and $_.operation -eq 'PromptWindow.GetPoint' } $deadline
    $reason = Close-CommentDialog $proc.Id $how $deadline
    $closed = Wait-TraceLine $from { $_.command -eq 'PETA' -and $_.operation -eq 'ShowCommentDialog' } $deadline
    $end = Wait-TraceLine $from { $_.command -eq 'PETA' -and $_.operation -eq 'Command.End' } $deadline
    $checks.Add(@{ Name = $name; Ok = ($answer.status -eq 'None' -and $reason -eq '' -and $closed.status -eq 'Cancel' -and [bool]$end)
        Detail = "範囲の問い合わせの結果: $($answer.status)、コメントのダイアログ: $($closed.status) $reason" })
}

# スクリプトの後のリボンの 4 ケース。PETA の 2 ケースは範囲の Enter（全体）で終え、設定のケースはダイアログを [キャンセル] で閉じ、
# 最後に設定のボタンのツールチップを確かめる
function Invoke-RibbonCases($proc, [datetime]$deadline) {
    $caseDeadline = [datetime]::Now.AddSeconds(60)
    if ($caseDeadline -gt $deadline) { $caseDeadline = $deadline }

    # 1. 先に選んだオブジェクトがある状態で押す: PETA が先選択を受け取る。ボタンを押した時点の先選択の件数（試験の記録の Ribbon.Execute）も
    #    確かめ、押す前に選択が無くなっていた（画面の操作など）のか、押した後に PETA へ渡らなかったのかを分ける
    $from = Get-TraceCount
    $reason = Invoke-RibbonButton $proc 'PETA'
    $checks.Add(@{ Name = 'ribbonPressedIdle'; Ok = ($reason -eq ''); Detail = $reason })
    if ($reason) { return }
    $pressed = Wait-TraceLine $from { $_.operation -eq 'Ribbon.Execute' -and $_.stringResult -eq 'PETA' } $caseDeadline
    $checks.Add(@{ Name = 'pickfirstWhenPressed'; Ok = ($pressed -and $pressed.count -gt 0); Detail = ($pressed | ConvertTo-Json -Compress) })
    $pick = Wait-TraceLine $from { $_.command -eq 'PETA' -and $_.operation -eq 'GetPickfirstSelection.SelectImplied' } $caseDeadline
    $checks.Add(@{ Name = 'ribbonKeepsPickfirst'; Ok = ($pick -and $pick.count -gt 0); Detail = ($pick | ConvertTo-Json -Compress) })
    Complete-RibbonPeta $proc $from $caseDeadline 'ribbonPetaFinishedIdle' 'Cancel'

    # 2. LINE の途中で押す: LINE を取り消して PETA が始まる（取り消さなければ、送った文字列が LINE の点の入力になり PETA は始まらない）
    $from = Get-TraceCount
    $sent = [CadPeta.Gui.Win]::SendCommandLine((Get-AcadMainWindow $proc), '_.LINE _non 0,0 ')
    Start-Sleep -Seconds 2
    $reason = if ($sent) { Invoke-RibbonButton $proc 'PETA' } else { 'LINE を送れなかった' }
    $checks.Add(@{ Name = 'ribbonPressedDuringLine'; Ok = ($reason -eq ''); Detail = $reason })
    if ($reason) { return }
    $start = Wait-TraceLine $from { $_.command -eq 'PETA' } $caseDeadline
    $checks.Add(@{ Name = 'ribbonCancelsLine'; Ok = [bool]$start; Detail = ($start | ConvertTo-Json -Compress) })
    Complete-RibbonPeta $proc $from $caseDeadline 'ribbonPetaFinishedAfterLine' 'Escape'

    # 3. 「設定」のボタンを押す: PETAOPT のダイアログが開き、[キャンセル] で PETAOPT が終わる
    $from = Get-TraceCount
    $reason = Invoke-RibbonButton $proc '設定'
    $checks.Add(@{ Name = 'ribbonSettingsPressed'; Ok = ($reason -eq ''); Detail = $reason })
    if ($reason) { return }
    $settingsDialog = Find-CadDialog $proc.Id 'CadPeta.Settings.PngDpi' $caseDeadline
    $checks.Add(@{ Name = 'ribbonSettingsOpensDialog'; Ok = [bool]$settingsDialog })
    if (-not $settingsDialog) { return }
    $missing = @()
    $parts = Get-DialogParts $settingsDialog @('CadPeta.Settings.Cancel') $caseDeadline ([ref]$missing)
    if ($missing.Count) { $checks.Add(@{ Name = 'ribbonSettingsCancel'; Ok = $false; Detail = "部品が無い: $($missing -join ', ')" }); return }
    Invoke-Element $parts['CadPeta.Settings.Cancel']
    $end = Wait-TraceLine $from { $_.command -eq 'PETAOPT' -and $_.operation -eq 'Command.End' } $caseDeadline
    $checks.Add(@{ Name = 'ribbonSettingsCancel'; Ok = [bool]$end })

    # 4. 「設定」のボタンのツールチップに、題名「設定」とコマンド名 PETAOPT が出る
    $reason = Test-RibbonTooltip $proc.Id (Get-AcadMainWindow $proc) '設定' 'PETAOPT' (Join-Path $work 'tooltip-PETAOPT.png') $caseDeadline
    $checks.Add(@{ Name = 'ribbonSettingsTooltip'; Ok = ($reason -eq ''); Detail = $reason })
}

$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$regReport = $null
$runError = $null
$p = $null
try {
    # CADPeta の設定のフォルダと試験の記録は、起動する AutoCAD にだけ渡す
    $childEnv = @{ CADPETA_DATA_DIR = $dataDir; CADPETA_PROMPT_TRACE = $trace; CADPETA_PROMPT_TRACE_RUN_ID = "petaopt-gui-$Ver"; CADPETA_PROMPT_TRACE_CASE_ID = 'gui' }
    $p = Invoke-WithProcessEnvironment $childEnv {
        Start-Process -FilePath $acadExe -WorkingDirectory $work -ArgumentList (ConvertTo-NativeArgString @($dwg, '/b', $scr, '/nologo')) -PassThru
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    # スクリプトの PETAOPT のダイアログを設定し、続く PETA のコメントのダイアログを確かめて Enter で確定する
    $settingsChecks = @(Invoke-SettingsDialogChecks $p.Id $settingsPath $work $deadline)
    foreach ($c in $settingsChecks) { $checks.Add($c) }
    if (-not @($settingsChecks | Where-Object { -not $_.Ok }).Count) {
        foreach ($c in Invoke-CommentDialogChecks $p.Id 'TESTCHART2' (Join-Path $work 'comment-dialog.png') $deadline) { $checks.Add($c) }
        $result = Wait-TraceLine 0 { $_.operation -eq 'ShowCommentDialog' } $deadline
        $checks.Add(@{ Name = 'commentEnterAccepts'; Ok = ($result.status -eq 'OK' -and $result.stringResult -eq $CadPetaComment); Detail = ($result | ConvertTo-Json -Compress) })
    }
    while (-not (Test-Path -LiteralPath $done) -and (Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Milliseconds 500 }
    $checks.Add(@{ Name = 'scriptFinished'; Ok = (Test-Path -LiteralPath $done) })
    if ((Test-Path -LiteralPath $done) -and -not $p.HasExited) {
        Invoke-RibbonCases $p $deadline
        # 終了: 変更を保存せずに終わる（スクリプトではないので、QUIT の確認には LISP の command で答える）。
        # ケースが途中で止まってコマンド（LINE など）が残っていると QUIT が実行されないので、先に Ctrl+C を 2 つ送って取り消す
        $sent = [CadPeta.Gui.Win]::SendCommandLine((Get-AcadMainWindow $p), "`u{3}`u{3}(command `"_.QUIT`" `"_Y`")`n")
        $checks.Add(@{ Name = 'quitSent'; Ok = $sent })
    }
    if (-not $p.WaitForExit([Math]::Max(1000, [int]($deadline - (Get-Date)).TotalMilliseconds))) {
        # 終わらなかった理由（残ったダイアログ、コマンド ラインの問い合わせ）を確かめるため、止める前に画面と、開いているウィンドウの
        # 題名とクラスを残す（画面の画像は、ほかのアプリのウィンドウが前にあると AutoCAD を写さない）
        $open = foreach ($h in [CadPeta.Gui.Win]::TopWindows([uint32]$p.Id)) {
            $window = [Windows.Automation.AutomationElement]::FromHandle($h)
            Save-ElementImage $window (Join-Path $work "timeout-$h.png")
            "'$($window.Current.Name)'($($window.Current.ClassName))"
        }
        Stop-Process -Id $p.Id -Force; $p.WaitForExit()
        $checks.Add(@{ Name = 'acadExited'; Ok = $false; Detail = "制限時間を超えたので自分の起動したプロセスを止めた。開いていたウィンドウ: $($open -join ', ')" })
    }
    Wait-CadRegChildProcesses $p.Id 30
}
catch { $runError = $_ }
finally { Restore-CadRegStateAndReport $regGuard ([ref]$regReport) }

# 判定は AutoCAD を終了し、レジストリを戻した後に、残ったファイルから作る
$traceLines = @(if (Test-Path -LiteralPath $trace) { Get-JsonLines $trace })
# 読み込まれた DLL が、信頼済みのフォルダへコピーした試験の DLL であること（インストール済みの DLL を試験しない）
$loaded = @($traceLines | Where-Object { $_.operation -eq 'Plugin.Load' } | ForEach-Object stringResult)
$expectedDll = Join-Path $CadPetaTrustedDotnetFolder (Split-Path -Leaf $plugin)
$checks.Add(@{ Name = 'loadedTestDll'; Ok = ($loaded.Count -eq 1 -and $loaded[0] -eq $expectedDll); Detail = ($loaded -join ', ') })
$ribbon = @($traceLines | Where-Object { $_.operation -eq 'Ribbon.Place' })
$last = $ribbon | Select-Object -Last 1
$checks.Add(@{ Name = 'ribbonOnAddinsTab'; Ok = ($last -and $last.status -match '(^|\.)RBN_00012112$' -and $last.count -eq 1); Detail = ($last | ConvertTo-Json -Compress) })
# 始まった PETA（最初に先選択を調べる）がすべて終わった（試験が送った入力で、予定しない PETA が始まって残っていない）
$petaStarts = @($traceLines | Where-Object { $_.command -eq 'PETA' -and $_.operation -eq 'GetPickfirstSelection.SelectImplied' }).Count
$petaEnds = @($traceLines | Where-Object { $_.command -eq 'PETA' -and $_.operation -eq 'Command.End' }).Count
$checks.Add(@{ Name = 'everyPetaFinished'; Ok = ($petaStarts -gt 0 -and $petaStarts -eq $petaEnds); Detail = "始まった $petaStarts、終わった $petaEnds" })
$settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else { $null }
$checks.Add(@{ Name = 'settingsSaved'; Ok = ($settings -and (@($settings.formats) -join ',') -eq 'pdf,svg,emf,png' -and $settings.pngDpi -eq 150 -and $settings.fileName -eq $CadPetaCommentFileName); Detail = ($settings | ConvertTo-Json -Compress) })
# 出力はスクリプトの PETA の 4 形式だけで、名前の最後が入力したコメント（リボンの PETA はコメントのダイアログをキャンセルしたので出力しない）
$outputs = @(Get-ChildItem -LiteralPath $work -File | Where-Object { $_.Name -like 'TESTCHART2_*' } | Sort-Object Extension)
$checks.Add(@{ Name = 'petaOutputs'; Ok = ((($outputs | ForEach-Object Extension) -join ',') -eq '.emf,.pdf,.png,.svg' -and
    -not @($outputs | Where-Object { $_.BaseName -notlike "*_$CadPetaComment" }).Count); Detail = (($outputs | ForEach-Object Name) -join ',') })

foreach ($c in $checks) { "{0} {1} {2}" -f ($c.Ok ? 'PASS' : 'FAIL'), $c.Name, $c.Detail }
"作業フォルダ: $work"
Assert-CadRunAndRegRestore $runError $regReport
if (@($checks | Where-Object { -not $_.Ok }).Count) { exit 1 }
