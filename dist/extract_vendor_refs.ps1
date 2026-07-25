# extract_vendor_refs.ps1: 互換CAD（ARES/IJCAD/BricsCAD）の compile 専用参照 DLL を、
# インストール済み本体から lib/vendor-refs/<cad>/ へ抽出し、別端末へ運ぶ zip に固める。
#
# 目的: 本体をアンインストール／別マシンでも互換プラグインをビルド（＝配布物の compat を
# 生成）できるようにする。src/Directory.Build.props は lib/vendor-refs/ があれば本体より
# 優先して参照するため、抽出後は本体無しでビルドが通る。
#
# これは compile 専用の参照 DLL（Private=false・実行時はホストが提供）であり、各社 SDK の
# ライセンス下にある。lib/vendor-refs/ と本 zip は .gitignore 済みで、公開リポ（GitHub）へは
# 絶対に含めない。自分の開発端末間で私的に運ぶ用途に限ること。
#
# 実行は pwsh（PowerShell 7）。powershell.exe 5.1 は本スクリプトの日本語コメントを誤読する。
#   pwsh -NoProfile -ExecutionPolicy Bypass -File dist/extract_vendor_refs.ps1
#
#   -NoZip                 lib/vendor-refs/ への抽出のみ行い zip は作らない
#   -BricscadBinDir <dir>  本体インストール先の上書き（既定は各社の標準パス）
#   -IjcadBinDir <dir>
#   -AresBinDirNet48 <dir>
#   -AresBinDirNet8 <dir>
param(
    [switch]$NoZip,
    [string]$BricscadBinDir = "C:\Program Files\Bricsys\BricsCAD V26 ja_JP",
    [string]$IjcadBinDir    = "C:\Program Files\ITJP\IJCAD 2026 PRO",
    [string]$AresBinDirNet48 = "C:\Program Files\Graebert GmbH\ARES Standard 2024\BIN",
    [string]$AresBinDirNet8  = "C:\Program Files\Graebert GmbH\ARES Standard 2027\BIN"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot          # リポジトリルート（dist/ の親）
$cache = Join-Path $root "lib\vendor-refs"
$zipPath = Join-Path $root "lib\CADPeta-vendor-refs.zip"

# 抽出対象は各 csproj の <Reference><HintPath> と厳密に一致させる（src/CadPeta.Plugin.* /
# src/CadPeta.Acquire.* の互換CAD参照）。System.Drawing 等の in-box フレームワーク参照は
# どの端末にもあるため含めない。
$vendors = @(
    @{ Sub = "bricscad";  Src = $BricscadBinDir;  Files = @("TD_Mgd.dll", "BrxMgd.dll") },
    @{ Sub = "ijcad";     Src = $IjcadBinDir;     Files = @("GcDbMgd.dll", "GcCoreMgd.dll", "GcMgd.dll") },
    @{ Sub = "ares\2024"; Src = $AresBinDirNet48; Files = @("TD_Mgd_23.1_16.dll", "FxCoreMgd_23.1_16.dll") },
    @{ Sub = "ares\2027"; Src = $AresBinDirNet8;  Files = @("TD_Mgd_Net8_26.8_17.dll", "FxCoreMgd_Net8_26.8_17.dll") }
)

if (Test-Path $cache) { Remove-Item $cache -Recurse -Force }
New-Item -ItemType Directory -Force $cache | Out-Null

$manifestLines = @("CADPeta vendor-refs（互換CAD compile 専用参照 DLL。私的転送用・GitHub 禁止）", "")
$total = 0
foreach ($v in $vendors) {
    if (-not (Test-Path $v.Src)) {
        throw "本体が見つからない: $($v.Src)（$($v.Sub)。未インストールか、パスを -*BinDir で指定すること）"
    }
    $dst = Join-Path $cache $v.Sub
    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($f in $v.Files) {
        $srcFile = Join-Path $v.Src $f
        if (-not (Test-Path $srcFile)) {
            throw "参照 DLL が本体に無い: $srcFile（版が違う可能性。csproj の HintPath と照合すること）"
        }
        Copy-Item $srcFile (Join-Path $dst $f) -Force
        $hash = (Get-FileHash $srcFile -Algorithm SHA256).Hash
        $manifestLines += ("{0}/{1}  SHA256:{2}" -f $v.Sub, $f, $hash)
        $total++
        Write-Host "  抽出: $($v.Sub)/$f" -ForegroundColor DarkGray
    }
}
Set-Content -Path (Join-Path $cache "MANIFEST.txt") -Value $manifestLines -Encoding UTF8
Write-Host "[extract] $total 件を $cache へ抽出" -ForegroundColor Green

if ($NoZip) { Write-Host "[extract] -NoZip: zip は作らない" -ForegroundColor Yellow; return }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $cache "*") -DestinationPath $zipPath
$mb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "[extract] パッケージ: $zipPath ($mb MB)" -ForegroundColor Green
Write-Host "[extract] 別端末での展開先: <repo>\lib\vendor-refs\  （展開すれば本体無しで互換ビルド可）" -ForegroundColor Cyan
