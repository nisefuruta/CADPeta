# unregister.ps1: removes only the CADPeta auto-load registrations written by register.ps1.
# Called by uninstall.bat with -NoPause; can also be run directly from PowerShell.
# Japanese guide: see README-compat.txt
[CmdletBinding()]
param(
    [ValidateSet("ares", "ijcad", "bricscad")][string]$Product,
    [ValidateSet("2020", "2023", "2024", "2027")][string]$AresYear,
    [string]$AppName = "CADPeta",
    [switch]$NoPause,
    [Parameter(DontShow)][string]$RegistryBaseOverride,
    [Parameter(DontShow)][string]$AresTrustedRootOverride,
    [Parameter(DontShow)][string]$AresProfileRootOverride
)
$ErrorActionPreference = "Stop"

# 対応する ARES の版、ARES のフォルダの探し方、終了前の待ち、製品の選択は register.ps1 と共有する
. (Join-Path $PSScriptRoot "compat_common.ps1")

trap {
    Write-Host ""
    Write-Host "[FAILED] Removal did not complete: $($_.Exception.Message)" -ForegroundColor Red
    Wait-CloseKey
    exit 1
}

$Product = Select-Product -Title "CADPeta Compat-CAD Uninstall" -Action "unregister"
$keys = @()

# ARES の対象年式は -AresYear 指定時はその年、無指定なら全対応年式。
# 配置フォルダの DLL が使用中なら、レジストリも profile.xml も変えずに止まる
if ($Product -eq "ares") {
    $years = if ($AresYear) { @($AresYear) } else { $supportedAresYears }
    $payloadDirs = foreach ($year in $years) { $paths = Get-AresPayloadPaths $year; $paths.Dir; $paths.New; $paths.Old }
    Assert-DllsNotInUse @($payloadDirs)
}

switch ($Product) {
    "ares" {
        $bases = if ($RegistryBaseOverride) { @($RegistryBaseOverride) } else { @("HKCU:\SOFTWARE\Graebert GmbH", "HKCU:\SOFTWARE\WOW6432Node\Graebert GmbH") }
        foreach ($base in $bases) {
            if (-not (Test-Path -LiteralPath $base)) { continue }
            foreach ($productKey in (Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "ARES *" })) {
                $year = [regex]::Match($productKey.PSChildName, "$aresYearsPattern(?:\s+x64)?`$").Groups[1].Value
                if ($AresYear -and $year -ne $AresYear) { continue }
                $candidate = Join-Path (Join-Path $productKey.PSPath "Applications") $AppName
                if (Test-Path -LiteralPath $candidate) { $keys += $candidate }
            }
        }
    }
    "bricscad" {
        $bases = if ($RegistryBaseOverride) { @($RegistryBaseOverride) } else { @("HKCU:\SOFTWARE\Bricsys\BricsCAD") }
        foreach ($base in $bases) {
            if (-not (Test-Path -LiteralPath $base)) { continue }
            foreach ($version in (Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue)) {
                foreach ($language in (Get-ChildItem -LiteralPath $version.PSPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '_' })) {
                    $candidate = Join-Path (Join-Path $language.PSPath "Applications") $AppName
                    if (Test-Path -LiteralPath $candidate) { $keys += $candidate }
                }
            }
        }
    }
    "ijcad" {
        $bases = if ($RegistryBaseOverride) { @($RegistryBaseOverride) } else { @("HKCU:\SOFTWARE\IntelliJapan") }
        foreach ($base in $bases) {
            if (-not (Test-Path -LiteralPath $base)) { continue }
            foreach ($edition in (Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "IJCAD*" })) {
                foreach ($release in (Get-ChildItem -LiteralPath $edition.PSPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^R\d' })) {
                    foreach ($language in (Get-ChildItem -LiteralPath $release.PSPath -ErrorAction SilentlyContinue)) {
                        $candidate = Join-Path (Join-Path $language.PSPath "Applications") $AppName
                        if (Test-Path -LiteralPath $candidate) { $keys += $candidate }
                    }
                }
            }
        }
    }
}

$keys = @($keys | Sort-Object -Unique)

$removedRegistrations = 0
foreach ($key in $keys) {
    Write-Host "Removing: $key" -ForegroundColor Cyan
    Remove-Item -LiteralPath $key -Recurse -Force
    if (Test-Path -LiteralPath $key) { throw "Failed to delete: $key" }
    $removedRegistrations++
}

# ARES はプラグイン本体を信頼場所 %AppData%\ARES Standard Edition x64\CADPeta\<年> へ
# コピーして登録するため、そのコピーも撤去する（register.ps1 と対称）。
$removedPayloads = 0
$removedProfiles = 0
if ($Product -eq "ares") {
    $cadpetaRoot = Join-Path (Get-AresTrustedRoot) $AppName
    # 年 < 2024 は profile.xml へ startup LISP を登録しているため、その項目を除去する
    foreach ($year in ($years | Where-Object { [int]$_ -lt $aresRegistryMinYear })) {
        foreach ($profile in (Get-AresProfileXmls $year)) {
            if (Remove-CadPetaFromProfile $profile) {
                Write-Host "Removing startup entry: $profile" -ForegroundColor Cyan
                $removedProfiles++
            }
        }
    }
    # <年> を <年>.old へ改名してから削除する。削除が途中で止まっても、残るのは名前で残骸と分かる <年>.old になる。
    # 前回中断した <年>.new と <年>.old も、中身を見ずに削除する
    foreach ($year in $years) {
        $paths = Get-AresPayloadPaths $year
        $found = $false
        foreach ($residue in @($paths.New, $paths.Old)) {
            if (Test-Path -LiteralPath $residue) {
                Write-Host "Removing leftover folder: $residue" -ForegroundColor Cyan
                Remove-FolderIfExists $residue
                $found = $true
            }
        }
        if (Test-Path -LiteralPath $paths.Dir) {
            Write-Host "Removing payload: $($paths.Dir)" -ForegroundColor Cyan
            Rename-Folder $paths.Dir $paths.Old
            Remove-FolderIfExists $paths.Old
            $found = $true
        }
        if ($found) { $removedPayloads++ }
    }
    # CADPeta フォルダが空になったら畳む（他年式が残るなら残す）
    if ((Test-Path -LiteralPath $cadpetaRoot) -and -not (Get-ChildItem -LiteralPath $cadpetaRoot -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $cadpetaRoot -Force
    }
}

if ($removedRegistrations -eq 0 -and $removedPayloads -eq 0 -and $removedProfiles -eq 0) {
    Write-Host "No CADPeta registration found for $Product (already unregistered)."
    Wait-CloseKey
    exit 0
}

Write-Host "[DONE] Removed $removedRegistrations CADPeta registration(s)." -ForegroundColor Green
Write-Host "If a target CAD is running, the change takes effect after it restarts."
Wait-CloseKey
