# クリップボード登録の E2E 検証ハーネス（設計書§6.4、2026-07-07 承認の検証方式(b)）。
# accoreconsole で NETLOAD→PETA を実行し、別プロセス（本スクリプト）から
# CF_ENHMETAFILE を読み戻して、PETA が出力した .emf とのバイト一致を検査する。
# クリップボードはマシン全域の共有状態のため dotnet test には含めない（実行は手動起動）。
#
# 使い方: pwsh tests/tools/regression/clip_verify.ps1 [-Dwg <図面>] [-AcadRoot <AutoCADフォルダ>] [-Tfm net8.0-windows]
# 出力:   作業フォルダ（%TEMP%\CadPeta_clip_verify）に成果物と accore_stdout.log を残す
param(
    [string]$Dwg = "$PSScriptRoot\..\..\assets\testcharts\TESTCHART2_layout.dwg",
    [string]$AcadRoot = "C:\Program Files\Autodesk\AutoCAD 2026",
    [string]$Tfm = "net8.0-windows",
    [string]$Config = "Debug"
)
$ErrorActionPreference = "Stop"

$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\$Config\$Tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグインが未ビルド: $plugin（dotnet build CadPeta.sln を先に実行すること）" }
if (-not (Test-Path $Dwg)) { throw "検証図面がない: $Dwg" }

# 作業フォルダ（図面を複製し、成果物は図面の隣に出るためリポジトリを汚さない）
$work = Join-Path $env:TEMP "CadPeta_clip_verify"
New-Item -ItemType Directory -Force $work | Out-Null
Remove-Item "$work\*" -Force -ErrorAction SilentlyContinue
$dwgCopy = Join-Path $work ([IO.Path]::GetFileName($Dwg))
Copy-Item $Dwg $dwgCopy -Force

# NETLOAD→PETA の .scr（パスは ASCII のみ。日本語を書くなら CP932 が必要）
$scr = Join-Path $work "peta_clip.scr"
@"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
PETA

_.QUIT _Y
"@ | Out-File $scr -Encoding ascii
# ↑ PETA 直後の空行は範囲プロンプト（FR-4 対話結線、2026-07-09）への Enter=全体出力

# クリップボードを既知状態（テキスト）にしてから実行し、登録が本当に PETA 由来であることを保証する
Set-Clipboard -Value "clip_verify sentinel $(Get-Date -Format o)"

$stdout = Join-Path $work "accore_stdout.log"
. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {
    & "$AcadRoot\accoreconsole.exe" /i $dwgCopy /s $scr /l ja-JP 2>&1 | Out-File $stdout -Encoding utf8
    $cadExitCode = $LASTEXITCODE
}
finally {
    $regReport = Restore-CadRegState $regGuard
}
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
if ($cadExitCode -ne 0) { throw "accoreconsole が異常終了 (exit=$cadExitCode)。$stdout を確認すること" }

$emf = [IO.Path]::ChangeExtension($dwgCopy, $null).TrimEnd('.') + "_PETA_A3.emf"
$emfFile = Get-ChildItem "$work\*.emf" | Select-Object -First 1
if (-not $emfFile) { throw "PETA の EMF 出力が見つからない。$stdout を確認すること" }

# 読み戻し（生 Win32。WinForms Clipboard は STA 必須のため使わない）
Add-Type -Name Clip -Namespace CadPetaVerify -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
[DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint f);
[DllImport("gdi32.dll")] public static extern uint GetEnhMetaFileBits(IntPtr h, uint c, byte[] b);
'@
$CF_ENHMETAFILE = 14
if (-not [CadPetaVerify.Clip]::IsClipboardFormatAvailable($CF_ENHMETAFILE)) {
    throw "クリップボードに CF_ENHMETAFILE がない（PETA の登録が成立していない）。$stdout を確認すること"
}
if (-not [CadPetaVerify.Clip]::OpenClipboard([IntPtr]::Zero)) { throw "OpenClipboard 失敗" }
try {
    $h = [CadPetaVerify.Clip]::GetClipboardData($CF_ENHMETAFILE)
    if ($h -eq [IntPtr]::Zero) { throw "GetClipboardData 失敗" }
    $size = [CadPetaVerify.Clip]::GetEnhMetaFileBits($h, 0, $null)
    $got = New-Object byte[] $size
    $null = [CadPetaVerify.Clip]::GetEnhMetaFileBits($h, $size, $got)
} finally { $null = [CadPetaVerify.Clip]::CloseClipboard() }

$fileBytes = [IO.File]::ReadAllBytes($emfFile.FullName)
$equal = [Linq.Enumerable]::SequenceEqual($got, $fileBytes)
"EMF ファイル : $($emfFile.FullName) ($($fileBytes.Length) bytes)"
"クリップボード: CF_ENHMETAFILE ($size bytes)"
if ($equal) {
    "判定: 一致（クリップボード登録 E2E 合格）"
    exit 0
} else {
    "判定: 不一致（登録内容が出力 EMF と異なる）"
    exit 1
}
