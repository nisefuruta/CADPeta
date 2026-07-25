# FileDialog / SECURELOAD の全数観測（端部整列 受け入れ検証タスク用）。
# accoreconsole 実行の前後で呼び出し、観測値をそのままログへ残す（0 を検知したら即 1 へ復元）。
param([string]$Label = "")
$ErrorActionPreference = "Continue"
$stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Write-Output "----- レジストリ観測 [$Label] $stamp -----"
$anyZero = $false
foreach ($r in @("R24.3", "R25.0", "R25.1", "R26.0")) {
    Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD\$r" -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
        $prof = $_.PSPath
        $profName = $_.PSChildName
        $fd = $null
        try { $fd = (Get-ItemProperty "$prof\FixedProfile\General Configuration" -Name FileDialog -ErrorAction Stop).FileDialog } catch {}
        Write-Output "  $r\$profName  FileDialog=$fd"
        if ($fd -eq 0) {
            $anyZero = $true
            Set-ItemProperty "$prof\FixedProfile\General Configuration" -Name FileDialog -Value 1
            Write-Output "    -> 0 検知、1 へ復元した"
        }
        $profilesKey = "$prof\Profiles"
        if (Test-Path $profilesKey) {
            Get-ChildItem $profilesKey -ErrorAction SilentlyContinue | ForEach-Object {
                $pname = $_.PSChildName
                $sl = $null
                try { $sl = (Get-ItemProperty "$profilesKey\$pname\Variables" -Name SECURELOAD -ErrorAction Stop).SECURELOAD } catch {}
                if ($null -ne $sl) {
                    Write-Output "  $r\$profName\Profiles\$pname  SECURELOAD=$sl"
                    if ($sl -eq 0) {
                        $anyZero = $true
                        Set-ItemProperty "$profilesKey\$pname\Variables" -Name SECURELOAD -Value 1
                        Write-Output "    -> 0 検知、1 へ復元した"
                    }
                }
            }
        }
    }
}
if (-not $anyZero) { Write-Output "  (0 検知なし)" }
Write-Output "----- 観測終了 [$Label] -----"
