# run_case_ppt.ps1: 1ケースを別プロセスで起動しタイムアウト監視する外側ラッパー。
# tests/tools/cad-typography/emf_ungroup_threshold/run_case.ps1（Excel 版）の PowerPoint 移植。
# タイムアウト時は ungroup_case_ppt.ps1 が書く PidFile を読んで PowerPoint 実PIDを名指しで
# 強制終了し、ケース実行プロセスも強制終了する。
param(
    [Parameter(Mandatory=$true)][string]$Emf,
    [Parameter(Mandatory=$true)][string]$OutJson,
    [string]$Route = "clipboard",
    [int]$TimeoutSec = 120
)
$repoTools = $PSScriptRoot
$caseScript = Join-Path $repoTools "ungroup_case_ppt.ps1"
$pidFile = "$OutJson.pid"
Remove-Item $OutJson, $pidFile -ErrorAction SilentlyContinue

$argList = @(
    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-File", $caseScript,
    "-Emf", $Emf, "-OutJson", $OutJson, "-Route", $Route
)

# pwsh（PowerShell 7）で統一（BOM なし UTF-8 の日本語を正しく読むため。Excel 版と同一理由）
$proc = Start-Process -FilePath "pwsh.exe" -ArgumentList $argList -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$timedOut = $true
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) { $timedOut = $false; break }
    Start-Sleep -Milliseconds 500
}

if ($timedOut) {
    Write-Warning "タイムアウト（${TimeoutSec}秒）: $Emf の Ungroup がハングした疑い。強制終了する"
    if (Test-Path $pidFile) {
        $pptPid = (Get-Content $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($pptPid) {
            try { Stop-Process -Id ([int]$pptPid) -Force -ErrorAction Stop } catch { }
        }
    }
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
    Start-Sleep -Seconds 1
    $result = [ordered]@{
        emf = $Emf; route = $Route; status = "timeout"
        timeoutSec = $TimeoutSec; error = "ungroup_case_ppt.ps1 がタイムアウト内に終了しなかった（ハングの疑い）"
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
