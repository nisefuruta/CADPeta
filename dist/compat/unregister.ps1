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

# Supported ARES years. Keep in sync with register.ps1 and the ValidateSet of -AresYear above.
$supportedAresYears = @("2020", "2023", "2024", "2027")
$aresYearsPattern = "(" + ($supportedAresYears -join "|") + ")"
# 年 < 2024 は profile.xml の startup LISP 方式（register.ps1 と対称）
$aresRegistryMinYear = 2024

# ARES の配置ルート（register.ps1 と対称）。既定は信頼済みのユーザーローミングフォルダ
$aresRoamingFolderName = "ARES Standard Edition x64"
function Get-AresTrustedRoot {
    if ($AresTrustedRootOverride) { return $AresTrustedRootOverride }
    return (Join-Path $env:APPDATA $aresRoamingFolderName)
}

# 対象年の内部版フォルダ→全プロファイルの profile.xml（register.ps1 と対称）
function Get-AresVersionFolder([string]$Year) {
    if ($AresProfileRootOverride) { return (Join-Path $AresProfileRootOverride $Year) }
    $exe = "C:\Program Files\Graebert GmbH\ARES Standard $Year\BIN\ARESS.exe"
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    $pv = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    $build = ([regex]::Match($pv, '\d+\.\d+\.\d+\.(\d+)')).Groups[1].Value
    if (-not $build) { return $null }
    $match = Get-ChildItem -LiteralPath (Get-AresTrustedRoot) -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "\.$build`$" } | Select-Object -First 1
    if ($match) { return $match.FullName }
    return $null
}
function Get-AresProfileXmls([string]$Year) {
    $vf = Get-AresVersionFolder -Year $Year
    if (-not $vf -or -not (Test-Path -LiteralPath $vf)) { return @() }
    $profilesDir = Join-Path $vf "Profiles"
    if (-not (Test-Path -LiteralPath $profilesDir)) { return @() }
    return @(Get-ChildItem -LiteralPath $profilesDir -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "profile.xml" } |
        Where-Object { Test-Path -LiteralPath $_ })
}
# profile.xml の applications グループから CADPeta の startup LISP 項目（name="load_cadpeta.lsp" を
# 含む item グループ）を丸ごと除去する。除去したら $true。
function Remove-CadPetaFromProfile([string]$ProfileXml) {
    $lines = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($ProfileXml))
    $removedAny = $false
    # ARES は起動時に profile.xml を書き直し、setting の属性順を並べ替える（type/value/name →
    # name/value/type）。並び順に依存しないよう、load_cadpeta.lsp を含む行を素直に手がかりにする
    # （その行が属する item グループを丸ごと除去する。file 設定・name 設定どちらに当たっても同じ
    # グループなので結果は同じ）。
    while ($true) {
        $hit = -1
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match 'load_cadpeta\.lsp') { $hit = $i; break } }
        if ($hit -lt 0) { break }
        $start = -1
        for ($j = $hit; $j -ge 0; $j--) { if ($lines[$j] -match '<group name="item\d+">') { $start = $j; break } }
        $end = -1
        for ($k = $hit; $k -lt $lines.Count; $k++) { if ($lines[$k].Trim() -eq '</group>') { $end = $k; break } }
        if ($start -lt 0 -or $end -lt $start) { break }
        $lines.RemoveRange($start, $end - $start + 1)
        $removedAny = $true
    }
    if ($removedAny) { [IO.File]::WriteAllLines($ProfileXml, $lines, (New-Object Text.UTF8Encoding($false))) }
    return $removedAny
}

function Wait-CloseKey {
    if ($NoPause) { return }
    Write-Host ""
    Read-Host "Press Enter to close this window" | Out-Null
}

trap {
    Write-Host ""
    Write-Host "[FAILED] Removal did not complete: $($_.Exception.Message)" -ForegroundColor Red
    Wait-CloseKey
    exit 1
}

function Select-Product {
    if ($Product) { return $Product }
    Write-Host "CADPeta Compat-CAD Uninstall" -ForegroundColor Cyan
    Write-Host "  1: ARES Standard $($supportedAresYears -join ' / ')"
    Write-Host "  2: IJCAD"
    Write-Host "  3: BricsCAD"
    $answer = Read-Host "Enter the number of the product to unregister"
    switch ($answer) {
        "1" { return "ares" }
        "2" { return "ijcad" }
        "3" { return "bricscad" }
        default { throw "No product selected. Enter 1, 2, or 3" }
    }
}

$Product = Select-Product
$keys = @()

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
# 対象年式は -AresYear 指定時はその年、無指定なら全対応年式
$removedPayloads = 0
$removedProfiles = 0
if ($Product -eq "ares") {
    $cadpetaRoot = Join-Path (Get-AresTrustedRoot) $AppName
    $years = if ($AresYear) { @($AresYear) } else { $supportedAresYears }
    # 年 < 2024 は profile.xml へ startup LISP を登録しているため、その項目を除去する
    foreach ($year in ($years | Where-Object { [int]$_ -lt $aresRegistryMinYear })) {
        foreach ($profile in (Get-AresProfileXmls $year)) {
            if (Remove-CadPetaFromProfile $profile) {
                Write-Host "Removing startup entry: $profile" -ForegroundColor Cyan
                $removedProfiles++
            }
        }
    }
    foreach ($year in $years) {
        $payloadDir = Join-Path $cadpetaRoot $year
        if (Test-Path -LiteralPath $payloadDir) {
            Write-Host "Removing payload: $payloadDir" -ForegroundColor Cyan
            Remove-Item -LiteralPath $payloadDir -Recurse -Force
            if (Test-Path -LiteralPath $payloadDir) { throw "Failed to delete: $payloadDir" }
            $removedPayloads++
        }
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
