# 96dpi 多環境確認ハーネス（Phase 0 申し送り#3、報告書_Phase0総括.md §5-3）。
# Office の「EMF デバイス=96dpi」仮定（設計書§6.1）が表示スケール（論理DPI）や
# Office バージョンに依存しないかを、既知寸法 EMF の貼付寸法で判定する。
#
# 判定の原理: 記録側は 96dpi 固定（設計書§6.1）。もし Office の仮定が
#   H1: ハードコード 96dpi        → 貼付寸法は意図どおり（表示スケール非依存）
#   H2: その時の論理DPIに追従     → 貼付寸法は 意図 × 96/論理DPI へ縮む
# のどちらかで、非100%環境（論理DPI≠96）でのみ両説の予言が分かれる。
# 100%/125%/150% の各スケールで本スクリプトを実行し、判定行を突き合わせる。
# 実測済み: 本機 Office 16.0.20026 は 3スケール×3アプリ全9点で H1
# （docs/validation/architecture-decisions.md、2026-07-08）。他バージョン環境での追試にもそのまま使える。
#
# 使い方: pwsh tests/tools/regression/measure_dpi_scale.ps1 [-Emf <既知寸法EMF>] [-IntendedWmm 100 -IntendedHmm 50]
#   -Emf 省略時は testchart から 100×50mm の EMF を CLI で生成する（要 dotnet build）。
#   計測プロセスは SetProcessDPIAware で DPI 仮想化を外し、真の論理DPIを記録する。
#   Shape の寸法は document 単位（pt）で読むため、計測自体は表示スケールの影響を受けない。
param(
    [string]$Emf = "",
    [double]$IntendedWmm = 100.0,
    [double]$IntendedHmm = 50.0,
    [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path

# ---------- 既知寸法 EMF の用意（未指定なら testchart から生成） ----------
if ($Emf -eq "") {
    $cli = "$repo\src\CadPeta.Cli\bin\$Config\net10.0-windows\cadpeta.exe"
    if (-not (Test-Path $cli)) { throw "CLI が未ビルド: $cli（dotnet build CadPeta.sln -c $Config を先に実行するか -Emf で既知寸法EMFを指定すること）" }
    $work = Join-Path $env:TEMP "CadPeta_dpi_scale"
    New-Item -ItemType Directory -Force $work | Out-Null
    $Emf = Join-Path $work "tc_100x50.emf"
    # crop (10,10)-(110,60) = 100×50mm ちょうど（テストチャートはコミット済みコーパス）
    & $cli "$repo\tests\assets\testcharts\TESTCHART2-off.pdf" (Join-Path $work "o.svg") `
        --crop 10 10 110 60 --emf $Emf | Out-Null
    if (-not (Test-Path $Emf)) { throw "EMF 生成に失敗: $Emf" }
    $IntendedWmm = 100.0; $IntendedHmm = 50.0
}

. "$PSScriptRoot\..\common\office_com.ps1"

# ---------- 論理DPI（表示スケール）の記録 ----------
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class DpiProbe {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int idx);
}
"@
[void][DpiProbe]::SetProcessDPIAware()   # DPI 仮想化を外して真の論理DPIを読む
$dc = [DpiProbe]::GetDC([IntPtr]::Zero)
$logdpi = [DpiProbe]::GetDeviceCaps($dc, 88)   # LOGPIXELSX
[void][DpiProbe]::ReleaseDC([IntPtr]::Zero, $dc)
$scale = [Math]::Round($logdpi / 96 * 100)
Write-Output ("=== 論理DPI={0} = 表示スケール {1}% | EMF意図寸法 {2}x{3}mm ===" -f $logdpi, $scale, $IntendedWmm, $IntendedHmm)
$h2w = [Math]::Round($IntendedWmm * 96 / $logdpi, 3); $h2h = [Math]::Round($IntendedHmm * 96 / $logdpi, 3)
Write-Output ("予言: H1(ハードコード96)= {0}x{1}mm / H2(論理DPI追従)= {2}x{3}mm" -f $IntendedWmm, $IntendedHmm, $h2w, $h2h)

function Judge($name, $ver, $wPt, $hPt) {
    $mw = PtToMm $wPt; $mh = PtToMm $hPt
    $verdict = if ([Math]::Abs($mw - $h2w) -lt [Math]::Abs($mw - $IntendedWmm)) { "H2寄り(論理DPI追従)" } else { "H1寄り(ハードコード96)" }
    Write-Output ("{0,-11} v{1,-12}: {2} x {3} mm  => {4}" -f $name, $ver, $mw, $mh, $verdict)
}

# ---------- 3アプリで貼付寸法を実測（ファイル挿入。Word はインライン＝設計書§6.1 方針） ----------
$xl = New-Object -ComObject Excel.Application; $xl.Visible = $false; $xl.DisplayAlerts = $false
try {
    $wb = $xl.Workbooks.Add(); $ws = $wb.Worksheets.Item(1)
    $sh = $ws.Shapes.AddPicture($Emf, $false, $true, 10, 10, -1, -1)
    Judge "Excel" "$($xl.Version).$($xl.Build)" $sh.Width $sh.Height
    $wb.Close($false)
} finally { $xl.Quit(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($xl) }

$pp = New-Object -ComObject PowerPoint.Application
try {
    $pr = $pp.Presentations.Add($false); $sl = $pr.Slides.Add(1, 12)   # ppLayoutBlank
    $sh = $sl.Shapes.AddPicture($Emf, $false, $true, 10, 10, -1, -1)
    Judge "PowerPoint" "$($pp.Version).$($pp.Build)" $sh.Width $sh.Height
    $pr.Close()
} finally { $pp.Quit(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($pp) }

$wd = New-Object -ComObject Word.Application; $wd.Visible = $false
try {
    $doc = $wd.Documents.Add(); $rng = $doc.Content; $rng.Collapse(0)
    $il = $doc.InlineShapes.AddPicture($Emf, $false, $true, $rng)
    # Word の .Build は Excel/PPT と違い「16.0.20026」の版数込み文字列を返すためそのまま使う
    Judge "Word" "$($wd.Build)" $il.Width $il.Height
    $doc.Close($false)
} finally { $wd.Quit(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($wd) }
