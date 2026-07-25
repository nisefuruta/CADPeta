# 配布 ZIP を圧縮後に再読し、組立元 staging と内容が一致することを検査する。
# CAD・Office・署名鍵には触れず、ZIP の構造と収録バイトだけを判定する。
param(
    [ValidateSet("all", "bundle", "cli", "gui", "compat")]
    [string]$Only = "all",
    [string]$Version = "1.0.0",
    [string]$StagingRoot,
    [string]$ArtifactsDir,
    [switch]$RequireTrustFiles,
    [switch]$SelfTest
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
if (-not $StagingRoot) { $StagingRoot = Join-Path $repo "dist\staging" }
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $repo "dist\artifacts" }

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-TargetGroups([string]$value) {
    if ($value -eq "all") { return @("bundle", "cli", "gui", "compat") }
    return @($value)
}

function Get-PackageDefinition([string]$group, [string]$staging, [string]$artifacts, [string]$version) {
    switch ($group) {
        "bundle" {
            return @{
                Source = Join-Path $staging "bundle"
                Zip = Join-Path $artifacts "CADPeta-AutoCAD-$version.zip"
                Required = @(
                    "CADPeta.bundle/PackageContents.xml",
                    "CADPeta.bundle/Contents/net48/CadPeta.Plugin.Acad.dll",
                    "CADPeta.bundle/Contents/net48/lang/1041.lang",
                    "CADPeta.bundle/Contents/net48/lang/1033.lang",
                    "CADPeta.bundle/Contents/net8/CadPeta.Plugin.Acad.dll",
                    "CADPeta.bundle/Contents/net8/lang/1041.lang",
                    "CADPeta.bundle/Contents/net8/lang/1033.lang",
                    "CADPeta.bundle/Contents/net10/CadPeta.Plugin.Acad.dll",
                    "CADPeta.bundle/Contents/net10/lang/1041.lang",
                    "CADPeta.bundle/Contents/net10/lang/1033.lang",
                    "install.bat", "uninstall.bat", "README-install.txt",
                    "LICENSE", "THIRD-PARTY-NOTICES.txt"
                )
            }
        }
        "cli" {
            return @{
                Source = Join-Path $staging "cli\pkg"
                Zip = Join-Path $artifacts "CADPeta-CLI-$version.zip"
                Required = @(
                    "cadpeta.exe", "lang/1041.lang", "lang/1033.lang", "README-cli.txt",
                    "LICENSE", "THIRD-PARTY-NOTICES.txt"
                )
            }
        }
        "gui" {
            return @{
                Source = Join-Path $staging "gui\pkg"
                Zip = Join-Path $artifacts "CADPeta-GUI-$version.zip"
                Required = @(
                    "cadpeta-gui.exe", "lang/1041.lang", "lang/1033.lang", "README-gui.txt",
                    "LICENSE", "THIRD-PARTY-NOTICES.txt"
                )
            }
        }
        "compat" {
            return @{
                Source = Join-Path $staging "compat"
                Zip = Join-Path $artifacts "CADPeta-CompatCAD-$version.zip"
                Required = @(
                    "ares/2020/CadPeta.Plugin.Ares.dll", "ares/2020/lang/1041.lang", "ares/2020/lang/1033.lang",
                    "ares/2023/CadPeta.Plugin.Ares.dll", "ares/2023/lang/1041.lang", "ares/2023/lang/1033.lang",
                    "ares/2024/CadPeta.Plugin.Ares.dll", "ares/2024/lang/1041.lang", "ares/2024/lang/1033.lang",
                    "ares/2027/CadPeta.Plugin.Ares.dll", "ares/2027/lang/1041.lang", "ares/2027/lang/1033.lang",
                    "ijcad/CadPeta.Plugin.Ijcad.dll", "ijcad/lang/1041.lang", "ijcad/lang/1033.lang",
                    "bricscad/CadPeta.Plugin.Bricscad.dll", "bricscad/lang/1041.lang", "bricscad/lang/1033.lang",
                    "install.bat", "uninstall.bat",
                    "register.ps1", "unregister.ps1", "README-compat.txt",
                    "LICENSE", "THIRD-PARTY-NOTICES.txt"
                )
            }
        }
        default { throw "未知の配布対象: $group" }
    }
}

function Get-StreamSha256([System.IO.Stream]$stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([Convert]::ToHexString($sha.ComputeHash($stream))) }
    finally { $sha.Dispose() }
}

function Get-FileSha256([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try { return Get-StreamSha256 $stream }
    finally { $stream.Dispose() }
}

function Assert-SafeEntryName([string]$name, [string]$zipPath) {
    $parts = $name -split "/"
    if (-not $name -or $name.StartsWith("/") -or $name -match "^[A-Za-z]:" -or $parts -contains "..") {
        throw "ZIP に安全でないパスがある: $zipPath :: $name"
    }
}

function Assert-BundleManifest([string]$source, [hashtable]$archiveFiles) {
    $manifest = Join-Path $source "CADPeta.bundle\PackageContents.xml"
    [xml]$xml = Get-Content -LiteralPath $manifest -Raw
    foreach ($entry in @($xml.ApplicationPackage.Components.ComponentEntry)) {
        $module = ([string]$entry.ModuleName).Replace("\", "/").TrimStart(".", "/")
        $archivePath = "CADPeta.bundle/$module"
        if (-not $archiveFiles.ContainsKey($archivePath)) {
            throw "PackageContents.xml の ModuleName が ZIP 内にない: $archivePath"
        }
    }
}

function Test-OnePackage([string]$group, [hashtable]$definition, [bool]$requireTrust) {
    $source = [IO.Path]::GetFullPath([string]$definition.Source)
    $zipPath = [IO.Path]::GetFullPath([string]$definition.Zip)
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "[$group] staging がない: $source"
    }
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "[$group] 配布 ZIP がない: $zipPath"
    }

    $archiveFiles = @{}
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace("\", "/").TrimEnd("/")
            if (-not $name) { continue }
            Assert-SafeEntryName $name $zipPath
            if (-not $seen.Add($name)) { throw "[$group] ZIP 内のパスが大小文字違いで重複する: $name" }
            if (-not $entry.Name) { continue }
            if ($entry.Length -eq 0) { throw "[$group] ZIP 内に 0-byte ファイルがある: $name" }
            $stream = $entry.Open()
            try { $archiveFiles[$name] = Get-StreamSha256 $stream }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }

    $stagingFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($source.Length).TrimStart("\", "/").Replace("\", "/")
        $stagingFiles[$relative] = Get-FileSha256 $file.FullName
    }
    $missing = @($stagingFiles.Keys | Where-Object { -not $archiveFiles.ContainsKey($_) } | Sort-Object)
    $extra = @($archiveFiles.Keys | Where-Object { -not $stagingFiles.ContainsKey($_) } | Sort-Object)
    if ($missing.Count -gt 0) { throw "[$group] staging から ZIP に入らなかったファイル: $($missing -join ', ')" }
    if ($extra.Count -gt 0) { throw "[$group] staging にないファイルが ZIP にある: $($extra -join ', ')" }
    foreach ($name in $stagingFiles.Keys) {
        if ($stagingFiles[$name] -ne $archiveFiles[$name]) {
            throw "[$group] staging と ZIP のバイトが一致しない: $name"
        }
    }
    foreach ($required in $definition.Required) {
        if (-not $archiveFiles.ContainsKey($required)) { throw "[$group] 必須ファイルが ZIP にない: $required" }
    }
    if ($requireTrust) {
        $certs = @($archiveFiles.Keys | Where-Object { $_ -notmatch "/" -and $_ -like "*.cer" })
        if ($certs.Count -eq 0) { throw "[$group] 信頼登録用の公開証明書が ZIP 直下にない" }
        if (-not $archiveFiles.ContainsKey("README-signing.txt")) {
            throw "[$group] 信頼登録手順 README-signing.txt が ZIP 直下にない"
        }
    }

    $forbidden = @(
        "accoremgd.dll", "acdbmgd.dll", "acmgd.dll", "autocad.net.dll",
        "td_mgd.dll", "fxcoremgd.dll", "fxproperties.dll", "gcaddmgd.dll", "grxmgd.dll",
        "itjp.ijcad.dll", "teigha.runtime.dll", "teigha.td.dll", "bricscad.dll", "td_dbmgd.dll"
    )
    $hits = @($archiveFiles.Keys | Where-Object { $forbidden -contains ([IO.Path]::GetFileName($_).ToLowerInvariant()) })
    if ($hits.Count -gt 0) { throw "[$group] CAD 本体の参照 DLL が ZIP に混入した: $($hits -join ', ')" }
    if ($group -eq "bundle") { Assert-BundleManifest $source $archiveFiles }
    Write-Host "[package] $group OK: $($archiveFiles.Count) files / $zipPath" -ForegroundColor Green
}

function Invoke-PackageVerification([string]$only, [string]$version, [string]$staging, [string]$artifacts,
                                    [bool]$requireTrust = $false) {
    foreach ($group in Get-TargetGroups $only) {
        Test-OnePackage $group (Get-PackageDefinition $group $staging $artifacts $version) $requireTrust
    }
}

function Write-FixtureFile([string]$root, [string]$relative, [string]$content = "fixture") {
    $path = Join-Path $root $relative
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    Set-Content -LiteralPath $path -Value $content -Encoding UTF8
}

function New-SelfTestFixture([string]$staging, [string]$artifacts, [string]$version) {
    foreach ($group in Get-TargetGroups "all") {
        $def = Get-PackageDefinition $group $staging $artifacts $version
        New-Item -ItemType Directory -Force $def.Source | Out-Null
        foreach ($required in $def.Required) {
            $content = if ($required -eq "CADPeta.bundle/PackageContents.xml") {
                '<ApplicationPackage><Components><ComponentEntry ModuleName="./Contents/net48/CadPeta.Plugin.Acad.dll"/></Components></ApplicationPackage>'
            } else { "fixture:$group/$required" }
            Write-FixtureFile $def.Source $required $content
        }
        Write-FixtureFile $def.Source "README-signing.txt" "trust"
        Write-FixtureFile $def.Source "fixture.cer" "certificate"
        New-Item -ItemType Directory -Force $artifacts | Out-Null
        Compress-Archive -Path (Join-Path $def.Source "*") -DestinationPath $def.Zip
    }
}

function Invoke-SelfTest {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("CadPeta_package_verify_" + [Guid]::NewGuid().ToString("N"))
    $staging = Join-Path $root "staging"
    $artifacts = Join-Path $root "artifacts"
    try {
        New-SelfTestFixture $staging $artifacts "9.9.9"
        Invoke-PackageVerification "all" "9.9.9" $staging $artifacts $true
        Set-Content -LiteralPath (Join-Path $staging "cli\pkg\README-cli.txt") -Value "改変" -Encoding UTF8
        $rejected = $false
        try { Invoke-PackageVerification "cli" "9.9.9" $staging $artifacts $true }
        catch { $rejected = $true; Write-Host "[selftest] 改変検出 OK: $($_.Exception.Message)" -ForegroundColor DarkGray }
        if (-not $rejected) { throw "自己試験失敗: staging と ZIP の不一致を拒否しなかった" }
        Remove-Item -LiteralPath $staging -Recurse -Force
        Remove-Item -LiteralPath $artifacts -Recurse -Force
        New-SelfTestFixture $staging $artifacts "9.9.9"
        Remove-Item -LiteralPath (Join-Path $artifacts "CADPeta-GUI-9.9.9.zip") -Force
        $missingRejected = $false
        try { Invoke-PackageVerification "all" "9.9.9" $staging $artifacts $true }
        catch { $missingRejected = $true; Write-Host "[selftest] all 欠落検出 OK: $($_.Exception.Message)" -ForegroundColor DarkGray }
        if (-not $missingRejected) { throw "自己試験失敗: -Only all の ZIP 欠落を拒否しなかった" }
        Write-Host "[selftest] package_verify PASS" -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}

if ($SelfTest) { Invoke-SelfTest }
else { Invoke-PackageVerification $Only $Version $StagingRoot $ArtifactsDir ([bool]$RequireTrustFiles) }
