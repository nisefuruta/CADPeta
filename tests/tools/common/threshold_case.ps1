# 「図形に変換」（Ungroup）の閾値を調べる Excel／PowerPoint のハーネスが共有する関数。
# 呼び出し側スクリプトがドットソースで読み込む。
. "$PSScriptRoot\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）

# 1 ケース分のケース実行スクリプト（ungroup_case.ps1／ungroup_case_ppt.ps1）を別プロセスで起動し、
# タイムアウトを監視する。タイムアウト時は、ケース実行スクリプトが書く PidFile を読んで Office の実 PID を
# 名指しで強制終了し、ケース実行プロセス自体も強制終了する。結果の JSON を読んだオブジェクトを返す。
# $ExtraArguments はケース実行スクリプトへ渡す追加の引数（例: -Visible）。
function Invoke-UngroupCaseProcess([string]$CaseScript, [string]$Emf, [string]$OutJson, [string]$Route,
                                   [int]$TimeoutSec, [string[]]$ExtraArguments = @()) {
    $pidFile = "$OutJson.pid"
    Remove-Item $OutJson, $pidFile -ErrorAction SilentlyContinue

    $argList = @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", $CaseScript,
        "-Emf", $Emf, "-OutJson", $OutJson, "-Route", $Route
    ) + $ExtraArguments

    # pwsh（PowerShell 7）を使う。Windows PowerShell 5.1（powershell.exe）は BOM なし UTF-8
    # スクリプトをシステムのANSIコードページで読み、日本語コメント・文字列の途中でパースが
    # 壊れることを実測（2026-07-10）。pwsh は既定で UTF-8 を読むため本ハーネス全体で pwsh に統一する
    $proc = Start-Process -FilePath "pwsh.exe" -ArgumentList (ConvertTo-NativeArgString $argList) -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $timedOut = $true
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { $timedOut = $false; break }
        Start-Sleep -Milliseconds 500
    }

    if ($timedOut) {
        Write-Warning "タイムアウト（${TimeoutSec}秒）: $Emf の Ungroup がハングした疑い。強制終了する"
        if (Test-Path $pidFile) {
            $officePid = (Get-Content $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
            if ($officePid) {
                try { Stop-Process -Id ([int]$officePid) -Force -ErrorAction Stop } catch { }
            }
        }
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
        Start-Sleep -Seconds 1
        $result = [ordered]@{
            emf = $Emf; route = $Route; status = "timeout"
            timeoutSec = $TimeoutSec; error = "$(Split-Path $CaseScript -Leaf) がタイムアウト内に終了しなかった（ハングの疑い）"
        }
        ($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
    }

    Remove-Item $pidFile -ErrorAction SilentlyContinue
    if (Test-Path $OutJson) {
        return (Get-Content $OutJson -Raw | ConvertFrom-Json)
    } else {
        $result = [ordered]@{ emf = $Emf; route = $Route; status = "case_error"; error = "OutJson が生成されなかった（exit=$($proc.ExitCode)）" }
        ($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
        return ($result | ConvertTo-Json -Depth 6 | ConvertFrom-Json)
    }
}

# 合成コーパス（PDF）を gen_corpus.py で作る。失敗したら例外を投げる。$Cell が 0 以下なら --cell を渡さない。
function New-CorpusPdf([string]$GenScript, [int]$N, [int]$Seg, [double]$SegLen, [double]$Cell, [string]$Pdf) {
    $genArgs = @("--n", $N, "--seg", $Seg, "--len", $SegLen, "--out", $Pdf)
    if ($Cell -gt 0) { $genArgs += @("--cell", $Cell) }
    $genOut = & uv run --no-project python $GenScript @genArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gen_corpus.py 失敗: $genOut" }
}

# cadpeta.exe で PDF を SVG と EMF へ変換し、IR と EMF のパス数、EMF の大きさを返す（IrPaths／EmfPaths／EmfBytes）。
# 失敗したら例外を投げる。標準出力にパス数の行が無ければ、そのパス数は $null。
function Convert-PdfWithCadPetaCli([string]$Exe, [string]$Pdf, [string]$Svg, [string]$Emf) {
    $cliOut = & $Exe $Pdf $Svg --emf $Emf 2>&1
    $cliExit = $LASTEXITCODE
    $cliText = ($cliOut -join "`n")
    if ($cliExit -ne 0) { throw "cadpeta.exe 失敗(exit=$cliExit): $cliText" }
    if ($cliText -match "IR: paths=(\d+)") { $irPaths = [int]$Matches[1] } else { $irPaths = $null }
    if ($cliText -match "EMF: paths=(\d+)") { $emfPaths = [int]$Matches[1] } else { $emfPaths = $null }
    return @{ IrPaths = $irPaths; EmfPaths = $emfPaths; EmfBytes = (Get-Item $Emf).Length }
}

# ケースの EMF を読み込んでバイト列を返す。ファイルが無ければ、$result へ case_error を入れて JSON に書き、$null を返す
# （呼び出し側が exit 1 で終わる）。
function Read-CaseEmf([string]$Emf, $result, [string]$OutJson) {
    if (-not (Test-Path $Emf)) {
        $result.status = "case_error"; $result.error = "EMF が存在しない: $Emf"
        ($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
        return $null
    }
    return ,[IO.File]::ReadAllBytes($Emf)
}
