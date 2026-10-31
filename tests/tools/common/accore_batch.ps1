# accoreconsole の 1 回の起動で複数のケースを続けて実行する。呼び出し側スクリプトがドットソースで読み込む。
#
# 各ケースの .scr の行の前後に区切りのマーカー（CADPETA_CASE_SETUP=<id> と CADPETA_CASE_END=<id>）を置き、
# SETUP のマーカーには図面の全図形の数（ssget "_X"）を添える。ケースが図面に図形を残すと、同じ起動の
# 後のケースで数が変わる（ケースの間の影響）ので、起動の最初のケースの数と比べて結果の StateDetail に書く。
# 終わりで LISP の cadpeta-sync が完了のファイル sync\end_<id>.txt を書いて合図のファイル sync\go_<id>.txt を待つ。
# その間にハーネスが成果物をケースのフォルダへ移し、ケースの後の状態を記録してから合図を出す。
# 標準出力が $StallSeconds の間増えないか、1 ケースが $TimeoutSeconds を超えたら止まったと判定し、
# 自分の起動したプロセスだけを止めてそのケースを不合格の材料にし、次のケースから起動し直す。
. "$PSScriptRoot\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）

$accoreBatchHarnessNames = @('run.scr', 'stdout.log', 'stderr.log')

# 標準出力の中のマーカー（CADPETA_CASE_<種類>=<id>）の位置。直後が ID に使える文字なら、別のケースの ID の前方一致なので
# 一致にしない（PETA-Model と PETA-Model-fallback を区別する）。accoreconsole は WriteMessage の後に改行せずに
# 次のプロンプトを続けることがあるので、行末では区切らない。$Last なら最後の一致、そうでなければ $Start 以降の最初の一致。無ければ -1
function Find-AccoreBatchMarker([string]$all, [string]$kind, [string]$id, [int]$Start = 0, [switch]$Last) {
    $pattern = [regex]::Escape("CADPETA_CASE_$kind=$id") + '(?![A-Za-z0-9_-])'
    $found = [regex]::Matches($all.Substring($Start), $pattern)
    if ($found.Count -eq 0) { return -1 }
    $Start + $(if ($Last) { $found[$found.Count - 1].Index } else { $found[0].Index })
}

# 標準出力から 1 ケースの部分（SETUP の行から END の行まで）を切り出す。END が無ければ SETUP 以降を返す
function Get-AccoreBatchCaseOutput([string]$all, [string]$id) {
    $s = Find-AccoreBatchMarker $all 'SETUP' $id -Last
    if ($s -lt 0) { return '' }
    $e = Find-AccoreBatchMarker $all 'END' $id -Start $s
    if ($e -lt 0) { return $all.Substring($s) }
    $lineEnd = $all.IndexOf("`n", $e)
    if ($lineEnd -lt 0) { $lineEnd = $all.Length - 1 }
    $all.Substring($s, $lineEnd - $s + 1)
}

# $WorkRoot の直下に、ケースのフォルダを空の状態で作る
function New-AccoreBatchCaseDir([string]$WorkRoot, [string]$Id) {
    if ($Id -notmatch '^[A-Za-z0-9_-]+$') { throw "ケースの ID が不正: $Id" }
    $root = [IO.Path]::GetFullPath($WorkRoot)
    $caseDir = [IO.Path]::GetFullPath((Join-Path $root $Id))
    if ([IO.Path]::GetDirectoryName($caseDir) -ne $root.TrimEnd('\')) { throw "ケースのフォルダが作業フォルダの直下ではない: $caseDir" }
    if (Test-Path $caseDir) { Remove-Item -LiteralPath $caseDir -Recurse -Force }
    New-Item -ItemType Directory $caseDir | Out-Null
    $caseDir
}

# $Cases の各要素は Id と Lines（ケースの .scr の行）を持つ hashtable。$Head と $Tail は起動の最初と最後の行
# （NETLOAD、後始末、QUIT）。$OnCaseStart と $OnCaseEnd はケースの直前と直後に呼び、引数はケース（$OnCaseEnd は 2 つ目に、
# 起動した accoreconsole の Process）、戻り値は結果の Before と After に入れる（クリップボードやメモリなど、区切りで読む照合の材料）。
# $LaunchName は起動ごとの作業フォルダの名前の頭（_launch_<名前>_<回>）。行の中の {LAUNCHDIR} は、その起動の
# 作業フォルダ（図面の複製と成果物の出力先）の絶対パスに置き換え、{LAUNCHDIR_LISP} は同じパスを LISP の文字列に書ける
# スラッシュの形に置き換える。$DataDir は CADPeta の設定のフォルダ（CADPETA_DATA_DIR として起動する accoreconsole にだけ渡す。
# 既定は起動ごとの作業フォルダの data に Write-E2eSettings の既定の設定を書いたもの）。$KeepPatterns に合うファイル（ケースが
# 保存して開いたままの図面など）はケースのフォルダへ移さない。$Encoding は .scr の文字コード（既定は ASCII）。
# 戻り値: Id → @{ Case; CaseDir; Output; ExitCode; ExitDetail; TimedOut; ElapsedMs; DwgUnchanged; Residual;
#                 StdoutPath; HarnessNames; Before; After; LaunchNo; State = @{ Ok; Detail } }
function Invoke-AccoreBatch {
    param(
        [Parameter(Mandatory)][string]$Acc,
        [Parameter(Mandatory)][string]$DwgSrc,
        [Parameter(Mandatory)][object[]]$Cases,
        [string[]]$Head = @(),
        [string[]]$Tail = @('_.QUIT', '_Y'),
        [Parameter(Mandatory)][string]$WorkRoot,
        [Parameter(Mandatory)][string]$LaunchName,
        [int]$TimeoutSeconds = 240,
        [int]$StallSeconds = 90,
        [scriptblock]$OnCaseStart = { param($case) $null },
        [scriptblock]$OnCaseEnd = { param($case) $null },
        [string[]]$KeepPatterns = @(),
        [string]$DataDir = '',
        [Text.Encoding]$Encoding = [Text.Encoding]::ASCII
    )
    $results = @{}
    $remaining = @($Cases)
    $launchNo = 0
    while ($remaining.Count -gt 0) {
        $launchNo++
        $launchDir = [IO.Path]::GetFullPath((Join-Path $WorkRoot "_launch_${LaunchName}_$launchNo"))
        if (Test-Path $launchDir) { Remove-Item -LiteralPath $launchDir -Recurse -Force }
        New-Item -ItemType Directory $launchDir | Out-Null
        $syncDir = Join-Path $launchDir 'sync'
        New-Item -ItemType Directory $syncDir | Out-Null
        $syncLisp = $syncDir -replace '\\', '/'
        $dwgCopy = Join-Path $launchDir ([IO.Path]::GetFileName($DwgSrc))
        Copy-Item $DwgSrc $dwgCopy
        $dwgHash = (Get-FileHash $dwgCopy -Algorithm SHA256).Hash
        $body = @($Head) + @(
            '(defun cadpeta-sync (id / f ce)'
            "  (setq f (open (strcat `"$syncLisp/end_`" id `".txt`") `"w`"))"
            '  (write-line id f)'
            '  (close f)'
            '  (setq ce (getvar "CMDECHO"))'
            '  (setvar "CMDECHO" 0)'
            "  (while (not (findfile (strcat `"$syncLisp/go_`" id `".txt`"))) (command `"_.DELAY`" 50))"
            '  (setvar "CMDECHO" ce)'
            '  (princ))'
        )
        foreach ($case in $remaining) {
            $body += @("(princ (strcat `"\nCADPETA_CASE_SETUP=$($case.Id) entities=`" (itoa (if (setq cpBatchSs (ssget `"_X`")) (sslength cpBatchSs) 0))))(setq cpBatchSs nil)(princ)") + @($case.Lines) +
                @("(princ `"\nCADPETA_CASE_END=$($case.Id)`")(princ)", "(cadpeta-sync `"$($case.Id)`")")
        }
        $body += $Tail
        $scr = Join-Path $launchDir 'run.scr'
        [IO.File]::WriteAllText($scr, (($body -join "`r`n") + "`r`n").Replace('{LAUNCHDIR_LISP}', ($launchDir -replace '\\', '/')).Replace('{LAUNCHDIR}', $launchDir), $Encoding)
        $harnessNames = @([IO.Path]::GetFileName($dwgCopy)) + $accoreBatchHarnessNames

        $preAcc = @(Get-Process accoreconsole -ErrorAction SilentlyContinue | ForEach-Object Id)
        $stdout = Join-Path $launchDir 'stdout.log'
        $stderr = Join-Path $launchDir 'stderr.log'
        $before = & $OnCaseStart $remaining[0]
        # CADPeta の設定のフォルダは、起動する accoreconsole にだけ渡す（この PowerShell の環境変数は起動の直後に戻す）
        $launchDataDir = if ($DataDir) { $DataDir } else { Write-E2eSettings (Join-Path $launchDir 'data') }
        $p = Invoke-WithProcessEnvironment @{ CADPETA_DATA_DIR = $launchDataDir } {
            Start-Process -WorkingDirectory $launchDir -FilePath $Acc -ArgumentList (ConvertTo-NativeArgString @('/i', $dwgCopy, '/s', $scr, '/l', 'ja-JP')) `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
        }
        $done = @()
        $stalled = $null
        $sw = [Diagnostics.Stopwatch]::StartNew()
        # ケースの後の処理（成果物の移動・コールバック）が例外を投げても、起動したプロセスを残さない
        # （cadpeta-sync の待機のまま残ると、呼び出し側のレジストリの保全がほかの AutoCAD と判定して戻せなくなる）
        $loopCompleted = $false
        try {
        for ($k = 0; $k -lt $remaining.Count; $k++) {
            $case = $remaining[$k]
            $caseDir = New-AccoreBatchCaseDir $WorkRoot $case.Id
            $endFile = Join-Path $syncDir "end_$($case.Id).txt"
            $caseDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
            $lastLength = -1
            $lastGrowth = Get-Date
            $reason = $null
            while (-not (Test-Path -LiteralPath $endFile)) {
                if ($p.HasExited) { $reason = 'AutoCAD が完了のファイルを書かずに終了した'; break }
                $length = if (Test-Path $stdout) { (Get-Item $stdout).Length } else { 0 }
                if ($length -ne $lastLength) { $lastLength = $length; $lastGrowth = Get-Date }
                if (((Get-Date) - $lastGrowth).TotalSeconds -ge $StallSeconds) { $reason = "標準出力が ${StallSeconds}s 増えなかったため自プロセスを停止した"; break }
                if ((Get-Date) -ge $caseDeadline) { $reason = "1 ケースの上限 ${TimeoutSeconds}s を超えたため自プロセスを停止した"; break }
                Start-Sleep -Milliseconds 100
            }
            $elapsed = $sw.ElapsedMilliseconds
            if ($reason) {
                $stalled = @{ Case = $case; CaseDir = $caseDir; Before = $before; ElapsedMs = $elapsed; Reason = $reason }
                break
            }
            # 成果物（ハーネスのファイルと図面ごとのログ以外）をケースのフォルダへ移す
            foreach ($file in @(Get-ChildItem $launchDir -File | Where-Object { $name = $_.Name; $_.Name -notin $harnessNames -and $_.Extension -ne '.log' -and -not ($KeepPatterns | Where-Object { $name -like $_ }) })) {
                Move-Item -LiteralPath $file.FullName -Destination $caseDir
            }
            $after = & $OnCaseEnd $case $p
            $done += @{ Case = $case; CaseDir = $caseDir; Before = $before; After = $after; ElapsedMs = $elapsed }
            $before = if ($k + 1 -lt $remaining.Count) { & $OnCaseStart $remaining[$k + 1] } else { $null }
            [IO.File]::WriteAllText((Join-Path $syncDir "go_$($case.Id).txt"), 'go')
            $sw.Restart()
        }
        $loopCompleted = $true
        }
        finally {
            if (-not $loopCompleted -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit() }
        }
        # 強制終了の理由。後のケースで止まったとき（完了したケースは合格の材料になる）と、全ケースの後の終了（Tail の CLOSE・QUIT）が
        # 止まったとき（終了できなかったので、完了したケースも失敗にする）を分ける
        $killedForStall = $false
        $tailTimedOut = $false
        if ($stalled) {
            if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $killedForStall = $true }
            $p.WaitForExit()
        }
        elseif (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
            $tailTimedOut = $true
        }
        $all = if (Test-Path $stdout) { Get-Content $stdout -Raw -Encoding Unicode } else { '' }
        if ($null -eq $all) { $all = '' }
        $dwgUnchanged = ((Get-FileHash $dwgCopy -Algorithm SHA256).Hash -eq $dwgHash)
        $postAcc = @(Get-Process accoreconsole -ErrorAction SilentlyContinue | ForEach-Object Id)
        $residual = @($postAcc | Where-Object { $_ -notin $preAcc })
        # 起動の最初のケースの図形の数と比べる
        $entities = @{}
        foreach ($m in [regex]::Matches($all, 'CADPETA_CASE_SETUP=([A-Za-z0-9_-]+) entities=(\d+)')) { $entities[$m.Groups[1].Value] = [int]$m.Groups[2].Value }
        $firstId = $remaining[0].Id
        $stateOf = {
            param($id)
            if (-not $entities.ContainsKey($id)) { return @{ Ok = $false; Detail = '図形の数のマーカーがない' } }
            if (-not $entities.ContainsKey($firstId)) { return @{ Ok = $false; Detail = "起動の最初のケース $firstId の図形の数のマーカーがない" } }
            $ok = $entities[$id] -eq $entities[$firstId]
            @{ Ok = $ok; Detail = $(if ($ok) { '' } else { "図形の数 $($entities[$id]) が起動の最初のケース $firstId の $($entities[$firstId]) と違う（前のケースが図面を変えた）" }) }
        }
        $exitDetail = if ($killedForStall) { "起動 $launchNo は後のケースで止まったため強制終了した（このケースは完了済み）" }
            elseif ($tailTimedOut) { "起動 $launchNo は全ケースの後の終了（$($Tail -join ' ')）が ${TimeoutSeconds}s で終わらなかったため強制終了した" }
            else { "exit=$($p.ExitCode)" }
        # 終了できなかった起動の完了したケースは、ExitCode を 0 以外にして失敗にする（強制終了した後の ExitCode は 0 とは限らないので -1 に決める）
        $doneExitCode = if ($killedForStall) { 0 } elseif ($tailTimedOut) { -1 } else { $p.ExitCode }
        foreach ($d in $done) {
            $results[$d.Case.Id] = @{
                Case = $d.Case; CaseDir = $d.CaseDir; Output = (Get-AccoreBatchCaseOutput $all $d.Case.Id)
                ExitCode = $doneExitCode; ExitDetail = $exitDetail; TimedOut = $false; ElapsedMs = $d.ElapsedMs
                DwgUnchanged = $dwgUnchanged; Residual = $residual; StdoutPath = $stdout; HarnessNames = $harnessNames
                Before = $d.Before; After = $d.After; LaunchNo = $launchNo; State = (& $stateOf $d.Case.Id)
            }
        }
        if ($stalled) {
            $results[$stalled.Case.Id] = @{
                Case = $stalled.Case; CaseDir = $stalled.CaseDir; Output = (Get-AccoreBatchCaseOutput $all $stalled.Case.Id)
                ExitCode = $p.ExitCode; ExitDetail = $stalled.Reason; TimedOut = $true; TimeoutDetail = $stalled.Reason; ElapsedMs = $stalled.ElapsedMs
                DwgUnchanged = $dwgUnchanged; Residual = $residual; StdoutPath = $stdout; HarnessNames = $harnessNames
                Before = $stalled.Before; After = (& $OnCaseEnd $stalled.Case $p); LaunchNo = $launchNo; State = (& $stateOf $stalled.Case.Id)
            }
            $idx = [array]::IndexOf(@($remaining | ForEach-Object { $_.Id }), $stalled.Case.Id)
            $remaining = @($remaining | Select-Object -Skip ($idx + 1))
        }
        else { $remaining = @() }
    }
    $results
}
