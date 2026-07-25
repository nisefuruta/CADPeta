# CadRegGuard の純粋な復元契約を、AutoCAD の実プロファイルへ触れず一時HKCUキーで故障注入する。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\CadRegGuard.ps1"

$testRoot = "HKCU:\Software\CADPeta\Tests\CadRegGuard_$PID"
$testProfileKey = Join-Path $testRoot 'Profile\General Configuration'

try {
    New-Item -Path $testProfileKey -Force | Out-Null
    New-ItemProperty -Path $testProfileKey -Name FileDialog -Value 0 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $testProfileKey -Name OSMODE -Value 4133 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $testProfileKey -Name TRUSTEDPATHS -Value 'before' -PropertyType String | Out-Null

    $before = Get-CadRegSnapshot $testRoot

    # 既存値の変更、既存値の削除、開始時に無かった値の新設を同時に注入する。
    Set-ItemProperty -Path $testProfileKey -Name FileDialog -Value 0
    Set-ItemProperty -Path $testProfileKey -Name OSMODE -Value 0
    Remove-ItemProperty -Path $testProfileKey -Name TRUSTEDPATHS
    New-ItemProperty -Path $testProfileKey -Name SECURELOAD -Value 0 -PropertyType DWord | Out-Null

    $report = Restore-CadRegState $before $testRoot
    if (-not $report.ok) { throw "復元レポートが失敗: $($report.mismatch -join '; ')" }

    $after = Get-CadRegSnapshot $testRoot
    $prefix = (Get-Item $testProfileKey).Name
    $expected = @{
        "${prefix}::FileDialog" = 1       # 基本安全値は開始値0でも1へ矯正する
        "${prefix}::OSMODE" = 4133
        "${prefix}::TRUSTEDPATHS" = 'before'
    }
    foreach ($id in $expected.Keys) {
        if (-not $after.ContainsKey($id) -or "$($after[$id].Value)" -ne "$($expected[$id])") {
            throw "復元値が不一致: ${id} 期待=$($expected[$id]) 実=$($after[$id].Value)"
        }
    }
    if ($after.ContainsKey("${prefix}::SECURELOAD")) { throw '開始時に無かった SECURELOAD が残留した' }
    if ($after["${prefix}::FileDialog"].Kind -ne 'DWord' -or $after["${prefix}::OSMODE"].Kind -ne 'DWord') {
        throw '数値レジストリ値の型が維持されなかった'
    }

    # 子プロセスが値を変更した直後に強制終了する、CADタイムアウト相当の経路を注入する。
    $timeoutBefore = Get-CadRegSnapshot $testRoot
    $marker = Join-Path $env:TEMP "CadRegGuard_$PID.ready"
    Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    $childSource = @"
Set-ItemProperty -LiteralPath '$testProfileKey' -Name FileDialog -Value 0
Set-ItemProperty -LiteralPath '$testProfileKey' -Name OSMODE -Value 0
[IO.File]::WriteAllText('$marker', 'ready')
Start-Sleep -Seconds 300
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childSource))
    $child = Start-Process pwsh -ArgumentList '-NoProfile', '-EncodedCommand', $encoded -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path -LiteralPath $marker) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $marker)) {
        Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
        throw '故障注入子プロセスが状態変更まで到達しなかった'
    }
    Stop-Process -Id $child.Id -Force
    $child.WaitForExit()
    $timeoutReport = Restore-CadRegState $timeoutBefore $testRoot
    if (-not $timeoutReport.ok) { throw "強制終了後の復元に失敗: $($timeoutReport.mismatch -join '; ')" }
    $timeoutAfter = Get-CadRegSnapshot $testRoot
    if ($timeoutAfter["${prefix}::FileDialog"].Value -ne 1 -or $timeoutAfter["${prefix}::OSMODE"].Value -ne 4133) {
        throw '強制終了後に FileDialog=1／OSMODE=4133 へ戻らなかった'
    }
    Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue

    "CadRegGuard自己試験 OK: 通常復元=$($report.actions.Count)件 強制終了復元=$($timeoutReport.actions.Count)件 FileDialog=1 OSMODE=4133"
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
