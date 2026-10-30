# compat_common.ps1: register.ps1 and unregister.ps1 share these definitions (dot-sourced by both).
# Japanese guide: see README-compat.txt
# 登録と解除が同じ規則で ARES のフォルダを探すよう、定義をこの 1 か所に置く。関数は読み込んだ側の
# パラメーター（$Product・$NoPause・$AresTrustedRootOverride・$AresProfileRootOverride）を参照する。

# Supported ARES years. Each year has its own payload folder ares\<year> because the SDK
# assembly names differ per year. Keep in sync with the ValidateSet of -AresYear in register.ps1 and unregister.ps1.
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

# ARES <year> の実行ファイル（既定のインストール先）
function Get-AresExe([string]$Year) {
    return "C:\Program Files\Graebert GmbH\ARES Standard $Year\BIN\ARESS.exe"
}

# ARES <year> の内部版フォルダ（%AppData%\ARES Standard Edition x64\<major.minor.build>）を、
# インストール先 ARESS.exe の ProductVersion 末尾ビルド番号で突き合わせて特定する。
# 初回起動前は存在しない。テスト時は -AresProfileRootOverride\<year> を直接使う。
function Get-AresVersionFolder([string]$Year) {
    if ($AresProfileRootOverride) { return (Join-Path $AresProfileRootOverride $Year) }
    $exe = Get-AresExe $Year
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    $pv = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    $build = ([regex]::Match($pv, '\d+\.\d+\.\d+\.(\d+)')).Groups[1].Value
    if (-not $build) { return $null }
    $match = Get-ChildItem -LiteralPath (Get-AresTrustedRoot) -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "\.$build`$" } | Select-Object -First 1
    if ($match) { return $match.FullName }
    return $null
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

# ARES の配置フォルダ <年> と、更新・解除の途中で止まったときに残る <年>.new・<年>.old。
# .new と .old は登録と解除だけが作るので、名前だけで残骸と判定し、中身を見ずにフォルダごと削除する
function Get-AresPayloadPaths([string]$Year) {
    $dir = Join-Path (Join-Path (Get-AresTrustedRoot) $AppName) $Year
    return @{ Dir = $dir; New = "$dir.new"; Old = "$dir.old" }
}

# CAD は終了してから数秒 DLL を開いたままにし、ウイルス対策ソフトなどは書き込んだ直後のフォルダを
# 数秒開いていることがある。DLL の確認(全 DLL を 1 回)とフォルダの操作は、約 1 秒の間隔で最大 10 回やり直す
$retryCount = 10
$retryIntervalMilliseconds = 1000

# $Dirs の下の DLL のうち、追記で開けない(CAD が読み込んでいる)ものの一覧を返す。開くだけで何も書かない
function Get-LockedDlls([string[]]$Dirs) {
    for ($attempt = 1; ; $attempt++) {
        $locked = @()
        foreach ($dir in $Dirs) {
            if (-not (Test-Path -LiteralPath $dir)) { continue }
            foreach ($dll in (Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.dll)) {
                try { [IO.File]::Open($dll.FullName, 'Append', 'Write', 'None').Dispose() }
                catch [IO.IOException] { $locked += $dll.FullName }
            }
        }
        if ($locked.Count -eq 0 -or $attempt -ge $retryCount) { return $locked }
        if ($attempt -eq 1) { Write-Host "Waiting for the CAD to release the DLL files..." }
        Start-Sleep -Milliseconds $retryIntervalMilliseconds
    }
}

# 変更を始める前に呼ぶ。使用中の DLL があれば、何も変えずに失敗する
function Assert-DllsNotInUse([string[]]$Dirs) {
    $locked = @(Get-LockedDlls $Dirs)
    if ($locked.Count -gt 0) {
        throw "These DLL files are in use. Close ARES, then run this again. Nothing was changed: $($locked -join ', ')"
    }
}

function Invoke-FolderOperation([string]$Description, [scriptblock]$Operation) {
    for ($attempt = 1; ; $attempt++) {
        try { & $Operation; return }
        catch {
            if ($attempt -ge $retryCount) { throw "$Description failed: $($_.Exception.Message)" }
            if ($attempt -eq 1) { Write-Host "Waiting for another program to release the folder: $Description" }
            Start-Sleep -Milliseconds $retryIntervalMilliseconds
        }
    }
}

function Remove-FolderIfExists([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Invoke-FolderOperation "Delete $Path" { Remove-Item -LiteralPath $Path -Recurse -Force }.GetNewClosure()
}

# 移動先が既にあれば、その中へ移さずに失敗する
function Rename-Folder([string]$Path, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Cannot rename $Path. The destination already exists: $Destination" }
    Invoke-FolderOperation "Rename $Path to $Destination" {
        if (Test-Path -LiteralPath $Destination) { throw "The destination already exists: $Destination" }
        Move-Item -LiteralPath $Path -Destination $Destination
    }.GetNewClosure()
}

function Wait-CloseKey {
    if ($NoPause) { return }
    Write-Host ""
    Read-Host "Press Enter to close this window" | Out-Null
}

# -Product が無いときは番号で製品を選ばせる。$Title は見出し、$Action は "register" か "unregister"。
function Select-Product([string]$Title, [string]$Action) {
    if ($Product) { return $Product }
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "  1: ARES Standard $($supportedAresYears -join ' / ')"
    Write-Host "  2: IJCAD"
    Write-Host "  3: BricsCAD"
    $answer = Read-Host "Enter the number of the product to $Action"
    switch ($answer) {
        "1" { return "ares" }
        "2" { return "ijcad" }
        "3" { return "bricscad" }
        default { throw "No product selected. Enter 1, 2, or 3" }
    }
}