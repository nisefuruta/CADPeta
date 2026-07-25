# register.ps1: registers CADPeta plugin auto-load for compat CADs (ARES / IJCAD / BricsCAD).
# Called by install.bat with -NoPause; can also be run directly from PowerShell.
# Japanese guide: see README-compat.txt
[CmdletBinding()]
param(
    [ValidateSet("ares", "ijcad", "bricscad")][string]$Product,
    [string]$DllPath,
    [ValidateSet("2020", "2023", "2024", "2027")][string]$AresYear,
    [string]$AppName = "CADPeta",
    [switch]$NoPause,
    [Parameter(DontShow)][string]$RegistryBaseOverride,
    [Parameter(DontShow)][string]$RegistryMachineBaseOverride,
    [Parameter(DontShow)][string]$AresTrustedRootOverride,
    [Parameter(DontShow)][string]$AresProfileRootOverride,
    [Parameter(DontShow)][int]$FailAfterRegistrationCount
)
$ErrorActionPreference = "Stop"

# Supported ARES years. Each year has its own payload folder ares\<year> because the SDK
# assembly names differ per year. Keep in sync with the ValidateSet of -AresYear above.
$supportedAresYears = @("2020", "2023", "2024", "2027")
$aresYearsPattern = "(" + ($supportedAresYears -join "|") + ")"

# ARES の自動読込方式は版で分ける（実機で確定, 2026-07-21）:
#   - 年 < 2024（2020/2023 等）: HKCU レジストリの Applications では .NET が発火しない。ARES 本来の
#     startup 機構（profile.xml の applications グループ＝OPTIONS の Add-Ins）へ、信頼場所の DLL を
#     読み込むローダ LISP を1項目登録する。
#   - 年 >= 2024（2024/2027 等）: HKCU レジストリの Applications（LOADCTRLS=2, MANAGED=1）が発火する。
# セットアップの区分は「2023 以前」「2024 以降」の版境界で恒久的に分ける（.NET 世代とは独立）。
$aresRegistryMinYear = 2024

# ARES Standard trusts, by default, its per-user roaming folder %AppData%\ARES Standard Edition x64
# and the folders under it. Installing the plugin there lets ARES auto-load it without the
# "not in a trusted folder" prompt and without administrator rights (2026-07-21 実機確認)。
# 全ARES版で同じ信頼ルートを使い、版ごとのサブフォルダに分ける（SDKアセンブリ名が版で異なるため）。
$aresRoamingFolderName = "ARES Standard Edition x64"
function Get-AresTrustedRoot {
    if ($AresTrustedRootOverride) { return $AresTrustedRootOverride }
    return (Join-Path $env:APPDATA $aresRoamingFolderName)
}
# この実行で作成・変更したものの、登録失敗時のロールバック記録
$aresPayloadRollback = @()   # @{ Dir; Existed }        配置フォルダ（信頼場所コピー）
$aresLispRollback = @()      # @{ Path; Existed }       ローダ LISP ファイル
$aresProfileRollback = @()   # @{ Path; Content }        編集前の profile.xml 全文

# 年 < 2024 か（＝LISPフック方式か）
function Test-AresUsesLispHook([string]$Year) {
    return ($Product -eq "ares" -and $Year -and [int]$Year -lt $aresRegistryMinYear)
}

# ARES <year> の内部版フォルダ（%AppData%\ARES Standard Edition x64\<major.minor.build>）を、
# インストール先 ARESS.exe の ProductVersion 末尾ビルド番号で突き合わせて特定する。
# 初回起動前は存在しない。テスト時は -AresProfileRootOverride\<year> を直接使う。
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

# 対象年の全プロファイルの profile.xml を返す
function Get-AresProfileXmls([string]$Year) {
    $vf = Get-AresVersionFolder -Year $Year
    if (-not $vf -or -not (Test-Path -LiteralPath $vf)) { return @() }
    $profilesDir = Join-Path $vf "Profiles"
    if (-not (Test-Path -LiteralPath $profilesDir)) { return @() }
    return @(Get-ChildItem -LiteralPath $profilesDir -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "profile.xml" } |
        Where-Object { Test-Path -LiteralPath $_ })
}

# 信頼場所へ置いた DLL を読み込むローダ LISP を、その配置フォルダに作る（作成をロールバック記録）
function New-CadPetaLoaderLisp([string]$DllFullPath) {
    $lispPath = Join-Path (Split-Path -Parent $DllFullPath) "load_cadpeta.lsp"
    $dllForLisp = $DllFullPath -replace '\\', '/'
    $lisp = @(
        ";; CADPeta startup loader (ARES 2023 以前用). profile.xml の Add-Ins から起動時に実行され、"
        ";; 信頼フォルダ内の .NET プラグインを読み込む。DLL は信頼フォルダにあり確認ダイアログは出ない。"
        "(defun cadpeta-startup-load ( / old)"
        "  (setq old (getvar `"FILEDIA`"))"
        "  (setvar `"FILEDIA`" 0)"
        "  (command `"._LOADDOTNETAPPLICATION`" `"$dllForLisp`")"
        "  (if old (setvar `"FILEDIA`" old))"
        "  (princ)"
        ")"
        "(cadpeta-startup-load)"
        "(princ)"
    )
    $script:aresLispRollback += @{ Path = $lispPath; Existed = (Test-Path -LiteralPath $lispPath) }
    Set-Content -LiteralPath $lispPath -Value $lisp -Encoding UTF8
    return $lispPath
}

# profile.xml の applications グループへ CADPeta ローダ LISP を1項目（次の itemN）挿入する。
# 既に登録済みなら何もしない。編集前の全文をロールバック記録する。挿入したら $true を返す。
function Add-CadPetaToProfile([string]$ProfileXml, [string]$LispPath) {
    $orig = [IO.File]::ReadAllText($ProfileXml)
    if ($orig -match 'load_cadpeta\.lsp') { return $false }
    $lines = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($ProfileXml))
    $openIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '<group name="applications">') { $openIdx = $i; break } }
    if ($openIdx -lt 0) { throw "applications group not found: $ProfileXml" }
    $indent = ($lines[$openIdx] -replace '\S.*$', '')
    $closeIdx = -1
    $maxItem = 0
    for ($i = $openIdx + 1; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], '<group name="item(\d+)">')
        if ($m.Success) { $n = [int]$m.Groups[1].Value; if ($n -gt $maxItem) { $maxItem = $n } }
        if ($lines[$i] -eq ($indent + "</group>")) { $closeIdx = $i; break }
    }
    if ($closeIdx -lt 0) { throw "applications group close not found: $ProfileXml" }
    $itemName = "item" + ($maxItem + 1)
    $item = @(
        "$indent    <group name=`"$itemName`">"
        "$indent        <setting type=`"QString`" value=`"`" name=`"description`"/>"
        "$indent        <setting type=`"QString`" value=`"$LispPath`" name=`"file`"/>"
        "$indent        <setting type=`"bool`" value=`"false`" name=`"managed`"/>"
        "$indent        <setting type=`"QString`" value=`"load_cadpeta.lsp`" name=`"name`"/>"
        "$indent        <setting type=`"int`" value=`"2`" name=`"reasons`"/>"
        "$indent        <setting type=`"QString`" value=`"LISP`" name=`"type`"/>"
        "$indent    </group>"
    )
    $script:aresProfileRollback += @{ Path = $ProfileXml; Content = $orig }
    $lines.InsertRange($closeIdx, [string[]]$item)
    [IO.File]::WriteAllLines($ProfileXml, $lines, (New-Object Text.UTF8Encoding($false)))
    return $true
}

function Wait-CloseKey {
    if ($NoPause) { return }
    Write-Host ""
    Read-Host "Press Enter to close this window" | Out-Null
}

trap {
    Write-Host ""
    Write-Host "[FAILED] Registration did not complete: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "For troubleshooting, see README-compat.txt (Japanese)."
    Wait-CloseKey
    exit 1
}

function Select-Product {
    if ($Product) { return $Product }

    Write-Host "CADPeta Compat-CAD Setup" -ForegroundColor Cyan
    Write-Host "  1: ARES Standard $($supportedAresYears -join ' / ')"
    Write-Host "  2: IJCAD"
    Write-Host "  3: BricsCAD"
    $answer = Read-Host "Enter the number of the product to register"
    switch ($answer) {
        "1" { return "ares" }
        "2" { return "ijcad" }
        "3" { return "bricscad" }
        default { throw "No product selected. Enter 1, 2, or 3" }
    }
}

$Product = Select-Product

if ($Product -eq "ares" -and $DllPath -and -not $AresYear) {
    throw "When using -DllPath with ARES, also specify -AresYear $($supportedAresYears -join '|') so the DLL matches the registration target"
}

# Copy one year's ARES payload from the extracted zip into the trusted roaming location and
# return the installed DLL path. The whole ares\<year> folder is copied so the plugin's
# dependencies and lang catalog sit next to the DLL. Records the created folder for rollback.
function Install-AresPayload([string]$Year) {
    $srcDir = Join-Path $PSScriptRoot "ares\$Year"
    $srcDll = Join-Path $srcDir "CadPeta.Plugin.Ares.dll"
    if (-not (Test-Path -LiteralPath $srcDll -PathType Leaf)) {
        throw "Target DLL not found: $srcDll. Run this script inside the extracted zip folder"
    }
    $destDir = Join-Path (Join-Path (Get-AresTrustedRoot) $AppName) $Year
    $script:aresPayloadRollback += @{ Dir = $destDir; Existed = (Test-Path -LiteralPath $destDir) }
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item (Join-Path $srcDir '*') $destDir -Recurse -Force
    $destDll = Join-Path $destDir "CadPeta.Plugin.Ares.dll"
    if (-not (Test-Path -LiteralPath $destDll -PathType Leaf)) { throw "Failed to install payload to trusted location: $destDll" }
    return (Resolve-Path -LiteralPath $destDll).Path
}

function Resolve-Dll([string]$Year) {
    if ($DllPath) {
        if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) { throw "Specified DLL not found: $DllPath" }
        return (Resolve-Path -LiteralPath $DllPath).Path
    }

    if ($Product -eq "ares") {
        # ARES は信頼フォルダからしか読み込まないため、配置先を信頼済みの場所へ移して登録する
        return (Install-AresPayload -Year $Year)
    }

    $dllName = if ($Product -eq "ijcad") { "CadPeta.Plugin.Ijcad.dll" } else { "CadPeta.Plugin.Bricscad.dll" }
    $candidate = Join-Path $PSScriptRoot "$Product\$dllName"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Target DLL not found: $candidate. Run this script inside the extracted zip folder"
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Normalize-RegistryIdPart([string]$Value) {
    return $Value.Replace('_', '-').ToLowerInvariant()
}

function Get-AresTargets {
    $userBase = if ($RegistryBaseOverride) { $RegistryBaseOverride } else { "HKCU:\SOFTWARE\Graebert GmbH" }
    $machineBase = if ($RegistryMachineBaseOverride) { $RegistryMachineBaseOverride } elseif ($RegistryBaseOverride) { $null } else { "HKLM:\SOFTWARE\Graebert GmbH" }
    $bases = @($userBase, $machineBase) | Where-Object { $_ }

    $targets = @()
    $seenYears = @{}
    foreach ($base in $bases) {
        if (-not (Test-Path -LiteralPath $base)) { continue }
        $products = Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match "^ARES Standard $aresYearsPattern`$" } |
            Sort-Object PSChildName
        foreach ($productKey in $products) {
            $year = [regex]::Match($productKey.PSChildName, "$aresYearsPattern`$").Groups[1].Value
            if ($AresYear -and $year -ne $AresYear) { continue }
            if ($seenYears.ContainsKey($year)) { continue }
            $seenYears[$year] = $true
            $targets += @{
                DisplayName = $productKey.PSChildName
                Path = Join-Path (Join-Path $userBase $productKey.PSChildName) "Applications"
                Year = $year
            }
        }
    }

    if ($targets.Count -eq 0) {
        $suffix = if ($AresYear) { " $AresYear" } else { " $($supportedAresYears -join '/')" }
        throw "No registration target found for ARES Standard$suffix. Start the target CAD once, then run this script again"
    }
    return @($targets)
}

function Get-ProductTargets {
    switch ($Product) {
        "bricscad" {
            $userBase = if ($RegistryBaseOverride) { $RegistryBaseOverride } else { "HKCU:\SOFTWARE\Bricsys\BricsCAD" }
            $machineBase = if ($RegistryMachineBaseOverride) { $RegistryMachineBaseOverride } elseif ($RegistryBaseOverride) { $null } else { "HKLM:\SOFTWARE\Bricsys\BricsCAD" }
            $bases = @($userBase, $machineBase) | Where-Object { $_ }
            $targets = @()
            $seen = @{}
            foreach ($base in $bases) {
                if (-not (Test-Path -LiteralPath $base)) { continue }
                foreach ($version in (Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue | Sort-Object PSChildName)) {
                    foreach ($language in (Get-ChildItem -LiteralPath $version.PSPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '_' } | Sort-Object PSChildName)) {
                        $id = "$(Normalize-RegistryIdPart $version.PSChildName)|$(Normalize-RegistryIdPart $language.PSChildName)"
                        if ($seen.ContainsKey($id)) { continue }
                        $seen[$id] = $true
                        $targetPath = Join-Path (Join-Path (Join-Path $userBase $version.PSChildName) $language.PSChildName) "Applications"
                        $targets += @{ DisplayName = "BricsCAD $($version.PSChildName) $($language.PSChildName)"; Path = $targetPath; Year = $null }
                    }
                }
            }
            if ($targets.Count -gt 0) { return @($targets) }
            throw "No registration target found for BricsCAD. Start BricsCAD once, then run this script again"
        }
        "ijcad" {
            $userBase = if ($RegistryBaseOverride) { $RegistryBaseOverride } else { "HKCU:\SOFTWARE\IntelliJapan" }
            $machineBase = if ($RegistryMachineBaseOverride) { $RegistryMachineBaseOverride } elseif ($RegistryBaseOverride) { $null } else { "HKLM:\SOFTWARE\IntelliJapan" }
            $bases = @($userBase, $machineBase) | Where-Object { $_ }
            $targets = @()
            $seen = @{}
            foreach ($base in $bases) {
                if (-not (Test-Path -LiteralPath $base)) { continue }
                foreach ($edition in (Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "IJCAD*" } | Sort-Object PSChildName)) {
                    foreach ($release in (Get-ChildItem -LiteralPath $edition.PSPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^R\d' } | Sort-Object PSChildName)) {
                        foreach ($language in (Get-ChildItem -LiteralPath $release.PSPath -ErrorAction SilentlyContinue | Sort-Object PSChildName)) {
                            $id = "$(Normalize-RegistryIdPart $edition.PSChildName)|$(Normalize-RegistryIdPart $release.PSChildName)|$(Normalize-RegistryIdPart $language.PSChildName)"
                            if ($seen.ContainsKey($id)) { continue }
                            $seen[$id] = $true
                            $targetPath = Join-Path (Join-Path (Join-Path (Join-Path $userBase $edition.PSChildName) $release.PSChildName) $language.PSChildName) "Applications"
                            $targets += @{ DisplayName = "$($edition.PSChildName) $($release.PSChildName) $($language.PSChildName)"; Path = $targetPath; Year = $null }
                        }
                    }
                }
            }
            if ($targets.Count -gt 0) { return @($targets) }
            throw "No registration target found for IJCAD. Start IJCAD once, then run this script again"
        }
    }
}

$targets = if ($Product -eq "ares") { @(Get-AresTargets) } else { @(Get-ProductTargets) }

$managedValueNames = @("LOADER", "LOADCTRLS", "MANAGED", "DESCRIPTION")
$snapshots = @()
$results = @()

try {
    # 配置（信頼場所へのコピー）とキーパス解決。ここでの失敗も下の catch で
    # 新規作成した配置フォルダをロールバックできるよう、try 内で行う
    $prepared = @()
    foreach ($target in $targets) {
        $prepared += @{
            DisplayName = $target.DisplayName
            Year = $target.Year
            Dll = Resolve-Dll -Year $target.Year
            ParentPath = $target.Path
            KeyPath = Join-Path $target.Path $AppName
        }
    }

    foreach ($target in $prepared) {
        Write-Host ""
        Write-Host "Target  : $($target.DisplayName)" -ForegroundColor Cyan
        Write-Host "DLL     : $($target.Dll)"

        if (Test-AresUsesLispHook $target.Year) {
            # 年 <= 2023: profile.xml の Add-Ins へローダ LISP を登録（レジストリは使わない）
            $profiles = @(Get-AresProfileXmls $target.Year)
            if ($profiles.Count -eq 0) {
                throw "ARES $($target.Year) のプロファイルが見つかりません。ARES $($target.Year) を一度起動してから再実行してください"
            }
            $lispPath = New-CadPetaLoaderLisp $target.Dll
            Write-Host "Loader  : $lispPath"
            foreach ($profile in $profiles) {
                $added = Add-CadPetaToProfile $profile $lispPath
                Write-Host ("Profile : {0} {1}" -f $profile, $(if ($added) { "(登録)" } else { "(登録済み)" }))
            }
            $results += $target
            if ($FailAfterRegistrationCount -gt 0 -and $results.Count -ge $FailAfterRegistrationCount) {
                throw "Self-test failure injected after registration"
            }
            Write-Host "Verified: startup LISP registered in profile.xml" -ForegroundColor Green
            continue
        }

        # 年 >= 2024（および IJCAD/BricsCAD）: レジストリ Applications へ登録
        Write-Host "Key     : $($target.KeyPath)"
        $snapshot = @{ KeyPath = $target.KeyPath; ParentPath = $target.ParentPath; KeyExisted = (Test-Path -LiteralPath $target.KeyPath); ParentExisted = (Test-Path -LiteralPath $target.ParentPath); Values = @{} }
        if ($snapshot.KeyExisted) {
            $registryKey = Get-Item -LiteralPath $target.KeyPath
            foreach ($name in $managedValueNames) {
                if ($registryKey.GetValueNames() -contains $name) {
                    $snapshot.Values[$name] = @{ Value = $registryKey.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); Kind = $registryKey.GetValueKind($name).ToString() }
                }
            }
        }
        $snapshots += $snapshot

        if (-not $snapshot.ParentExisted) { New-Item -Path $target.ParentPath -Force | Out-Null }
        if (-not $snapshot.KeyExisted) { New-Item -Path $target.KeyPath -Force | Out-Null }
        New-ItemProperty -LiteralPath $target.KeyPath -Name "LOADER" -Value $target.Dll -PropertyType String -Force | Out-Null
        New-ItemProperty -LiteralPath $target.KeyPath -Name "LOADCTRLS" -Value 2 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -LiteralPath $target.KeyPath -Name "MANAGED" -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -LiteralPath $target.KeyPath -Name "DESCRIPTION" -Value "CADPeta" -PropertyType String -Force | Out-Null

        $readBack = Get-ItemProperty -LiteralPath $target.KeyPath
        if ($readBack.LOADER -ne $target.Dll -or $readBack.LOADCTRLS -ne 2 -or $readBack.MANAGED -ne 1 -or $readBack.DESCRIPTION -ne "CADPeta") {
            throw "Read-back verification failed: $($target.KeyPath)"
        }
        $results += $target
        if ($FailAfterRegistrationCount -gt 0 -and $results.Count -ge $FailAfterRegistrationCount) {
            throw "Self-test failure injected after registration"
        }
        Write-Host "Verified: LOADER, LOADCTRLS=2, MANAGED=1, DESCRIPTION read back OK" -ForegroundColor Green
    }
}
catch {
    $originalError = $_.Exception.Message
    $rollbackFailures = @()
    for ($index = $snapshots.Count - 1; $index -ge 0; $index--) {
        $snapshot = $snapshots[$index]
        try {
            if (-not $snapshot.KeyExisted) {
                if (Test-Path -LiteralPath $snapshot.KeyPath) { Remove-Item -LiteralPath $snapshot.KeyPath -Recurse -Force }
            }
            else {
                foreach ($name in $managedValueNames) {
                    Remove-ItemProperty -LiteralPath $snapshot.KeyPath -Name $name -ErrorAction SilentlyContinue
                    if ($snapshot.Values.ContainsKey($name)) {
                        $old = $snapshot.Values[$name]
                        New-ItemProperty -LiteralPath $snapshot.KeyPath -Name $name -Value $old.Value -PropertyType $old.Kind -Force | Out-Null
                    }
                }
            }
        }
        catch { $rollbackFailures += "$($snapshot.KeyPath): $($_.Exception.Message)" }
    }
    # profile.xml の編集を、記録した編集前全文へ戻す（LISPフック方式のロールバック）
    for ($xIndex = $aresProfileRollback.Count - 1; $xIndex -ge 0; $xIndex--) {
        $pr = $aresProfileRollback[$xIndex]
        try { [IO.File]::WriteAllText($pr.Path, $pr.Content, (New-Object Text.UTF8Encoding($false))) }
        catch { $rollbackFailures += "$($pr.Path): $($_.Exception.Message)" }
    }
    # この実行で新規作成したローダ LISP を撤去する（既存は残す）
    for ($lIndex = $aresLispRollback.Count - 1; $lIndex -ge 0; $lIndex--) {
        $lp = $aresLispRollback[$lIndex]
        if (-not $lp.Existed) {
            try { if (Test-Path -LiteralPath $lp.Path) { Remove-Item -LiteralPath $lp.Path -Force } }
            catch { $rollbackFailures += "$($lp.Path): $($_.Exception.Message)" }
        }
    }
    # レジストリ復元に続き、この実行で新規作成した配置フォルダ（信頼場所コピー）も撤去する。
    # 既存フォルダ（再登録時）は消さない
    for ($pIndex = $aresPayloadRollback.Count - 1; $pIndex -ge 0; $pIndex--) {
        $payload = $aresPayloadRollback[$pIndex]
        if (-not $payload.Existed) {
            try {
                if (Test-Path -LiteralPath $payload.Dir) { Remove-Item -LiteralPath $payload.Dir -Recurse -Force }
            }
            catch { $rollbackFailures += "$($payload.Dir): $($_.Exception.Message)" }
        }
    }
    if ($rollbackFailures.Count -gt 0) { throw "$originalError. Restoring the previous state also failed: $($rollbackFailures -join ' / ')" }
    throw "$originalError. All changes were rolled back to the previous state"
}

Write-Host ""
Write-Host "[DONE] Auto-load registration and read-back verification succeeded ($($results.Count) entries)." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. If a target CAD is running, close it first."
Write-Host "  2. Start: $($results.DisplayName -join ', ')"
Write-Host "  3. Type PETA at the command line and confirm the command starts."
Write-Host "This script verified the registry settings only; step 3 completes the in-CAD check."
Write-Host "If PETA is an unknown command, see README-compat.txt (Japanese)."
Write-Host "Uninstall: double-click uninstall.bat, or run: powershell -File unregister.ps1 -Product $Product"
Wait-CloseKey
