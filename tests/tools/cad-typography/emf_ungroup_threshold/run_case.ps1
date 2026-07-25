# run_case.ps1: 1ケース分を「別プロセス」として起動し、タイムアウト監視する外側ラッパー。
# ungroup_case.ps1 自身も Quit() 失敗時の強制終了を持つが、それは Ungroup() 呼び出しが
# 例外で返ってきた後の話であり、Ungroup() 自体が戻ってこない（真のハング）場合は
# ungroup_case.ps1 のプロセスごと固まる。本スクリプトはその外側でプロセス単位の
# ウォッチドッグを掛け、タイムアウト時は ungroup_case.ps1 が書き出す PidFile を読んで
# Excel の実PIDを名指しで強制終了し、ケース実行プロセス自体も強制終了する。
# 実測（2026-07-10, N=50,000）では Ungroup 失敗は例外で数秒〜十数秒以内に返っており、
# ハングは未観測。本スクリプトは「観測されていない事象への保険」として備える。
param(
    [Parameter(Mandatory=$true)][string]$Emf,
    [Parameter(Mandatory=$true)][string]$OutJson,
    [string]$Route = "clipboard",
    [int]$TimeoutSec = 120,
    [switch]$Visible
)
$repoTools = $PSScriptRoot
$caseScript = Join-Path $repoTools "ungroup_case.ps1"
$pidFile = "$OutJson.pid"
Remove-Item $OutJson, $pidFile -ErrorAction SilentlyContinue

$argList = @(
    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-File", $caseScript,
    "-Emf", $Emf, "-OutJson", $OutJson, "-Route", $Route
)
if ($Visible) { $argList += "-Visible" }

# pwsh（PowerShell 7）を使う。Windows PowerShell 5.1（powershell.exe）は BOM なし UTF-8
# スクリプトをシステムのANSIコードページで読み、日本語コメント・文字列の途中でパースが
# 壊れることを実測（2026-07-10）。pwsh は既定で UTF-8 を読むため本ハーネス全体で pwsh に統一する
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
        $excelPid = (Get-Content $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($excelPid) {
            try { Stop-Process -Id ([int]$excelPid) -Force -ErrorAction Stop } catch { }
        }
    }
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
    Start-Sleep -Seconds 1
    $result = [ordered]@{
        emf = $Emf; route = $Route; status = "timeout"
        timeoutSec = $TimeoutSec; error = "ungroup_case.ps1 がタイムアウト内に終了しなかった（ハングの疑い）"
    }
    ($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
}

Remove-Item $pidFile -ErrorAction SilentlyContinue
if (Test-Path $OutJson) {
    $r = Get-Content $OutJson -Raw | ConvertFrom-Json
    return $r
} else {
    $result = [ordered]@{ emf = $Emf; route = $Route; status = "case_error"; error = "OutJson が生成されなかった（exit=$($proc.ExitCode)）" }
    ($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
    return ($result | ConvertTo-Json -Depth 6 | ConvertFrom-Json)
}
