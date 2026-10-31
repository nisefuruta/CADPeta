# 用紙の寸法対応付けとプロット回転のクロップを互換CAD（ARES／IJCAD／BricsCAD）で実測する。
#
# 検査すること:
# 1. 用紙の寸法対応付け（依頼: SelectMedia／RestorePaperUnitsAndScale の実機確認）
#    レイアウト（PETA_A3・A3・1:1）で 100×70mm の窓を指定すると、出力 SVG の寸法が
#    100×70mm になる。用紙が既定用紙へ落ちて用紙単位が変わると 1:1 の意味が壊れ、
#    この寸法にならない（AutoCAD 実測: 259.86×181.9mm に化けた）。
# 2. PlotRotation → QuarterTurnsCw の対応（依頼: 回転の実測）
#    同じ窓を PlotRotation 0/90/180/270 で出力すると、クロップ後の内容は同一になる
#    （回転はプロット像が用紙上のどこへ載るかだけを変え、窓の中身は変えない）。
#    対応付けが誤っていればクロップ矩形が内容を外し、出力が別物になる。
#    判定は EMF のバイト一致を第一とし、不一致なら EMF を GDI で描画して画素比較する。
# 3. ペーパー空間判定の CVPORT 値（棚卸しの実測）
#    真のペーパー空間で CVPORT=1、ビューポート内（MSPACE）で CVPORT≠1 が
#    AutoCAD 以外でも成り立つかを読み取って記録する。
# 4. モデル空間 × 窓 × 用紙合わせ（Fit）× プロット回転の実効尺度（-SkipModelFit で省く）
#    window_fit_scale_matrix.ps1（AutoCAD）と同じ観点。互換 CAD は「有効な印刷領域」を表示しないので、
#    窓と同じ矩形と対角線を描いて出力し、最終 SVG の内容が四辺まで届くことで、CADPeta が自分で計算した
#    フィット尺度のクロップと、CAD が実際に印字した範囲を照合する。尺度が食い違うと、矩形の辺が切れるか、
#    辺と縁の間に余白が残る。あわせて SVG の寸法が窓の縦横比のままであることを確かめる。
#
# CAD の起動・接続・後始末の作法は gradient_compat_e2e.ps1 と同じ（対話デスクトップ、
# Windows PowerShell 5.1 Desktop／STA、プロセス所有の照合、Close(false)→Quit、残留の報告）。
#
# 使い方（Windows PowerShell 5.1 で実行する）:
#   powershell -Sta -File peta_window_rotation_compat.ps1 -Product BricscadV26
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("Acad2026", "Acad2027", "Ares2020", "Ares2023", "Ares2024", "Ares2027", "Ijcad2026", "BricscadV26")]
  [string]$Product,
  [string]$LayoutName = "PETA_A3",
  [string]$LayoutMedia = "",           # 指定するとレイアウトの用紙を PDF デバイスのこの用紙（正式名）に替える
                                       # （PETA_A3 は余白の無い A3 なので、余白が非対称な用紙での回転は、これで確かめる）
  [int[]]$Rotations = @(0, 1, 2, 3),   # PlotRotation 列挙値（0=0度, 1=90度, 2=180度, 3=270度）
  [string]$Dwg,
  [string]$OutRoot = "$env:TEMP\CadPeta_peta_rotation_compat",
  [switch]$SkipModelFit,
  [string[]]$FitPapers = @("ISO_A4_(210.00_x_297.00_MM)", "ISO_full_bleed_A4_(210.00_x_297.00_MM)"),
  [switch]$LogMediaMargins,
  [switch]$AllowForceStop
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-CompatE2ePlan 'peta_window_rotation_compat.ps1' $Product $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)

$GuiStartupTimeoutSeconds = 120
# ARES 2027 は単独起動だとメインウィンドウ非表示のままスピンし ROT 登録されないことがある
# （2026-07-17 実測）。この秒数だけ待って ROT 未登録なら2つ目を起動して1つ目の起動を促す。
$SecondaryLaunchDelaySeconds = 30
$CommandCompletionTimeoutSeconds = 300
$ComRetryWindowMilliseconds = 60000
$ComRetryDelayMilliseconds = 100
$ProcessExitTimeoutSeconds = 20
$PollMilliseconds = 500
$StableFilePollCount = 3
$LoadQueueTimeoutSeconds = 90
$ExpectedWindowStation = "WinSta0"
$ExpectedDesktop = "Default"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path

# powershell -File 経由では配列引数がカンマ入り単一文字列で届き得るため、分割を受け付ける
$Rotations = @($Rotations | ForEach-Object { "$_" -split ',' } | ForEach-Object { [int]$_ })

. "$PSScriptRoot\..\common\compat_cad_com.ps1"
# 製品の固定の情報（実行ファイル、ProgID、DLL の場所など）は compat_cad_com.ps1 の $CompatProducts にあり、Get-CompatProduct で合わせる
$products = @{
  # AutoCAD も回転 90 度の1ケースしか実測が無かったため、同じ観点（依頼3）で測る。
  # GUI COM 駆動の作法は gradient_interactive_plot.ps1 で実測済みの形に合わせる
  Acad2026 = @{
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
  }
  Acad2027 = @{
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
  }
  Ares2020 = @{
    CommandStartKind = "AresUi"
    SetFileDia = $true
  }
  Ares2023 = @{
    CommandStartKind = "AresUi"
    SetFileDia = $true
  }
  Ares2024 = @{
    CommandStartKind = "AresUi"
    SetFileDia = $true
  }
  Ares2027 = @{
    CommandStartKind = "AresUi"
    SetFileDia = $true
  }
  Ijcad2026 = @{
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
  }
  BricscadV26 = @{
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
  }
}

$config = Get-CompatProduct $Product $products
$exe = [IO.Path]::GetFullPath($config.Exe)
$plugin = $config.Plugin
. "$PSScriptRoot\..\common\test_helpers.ps1"
# 配布物と同じビルド（tests/tools/build_e2e.ps1）の DLL であることを、CAD を起動する前に確かめる
Assert-E2ePluginBuild $plugin
if (-not $Dwg) { $Dwg = Join-Path $repo "tests\assets\testcharts\TESTCHART2_layout.dwg" }
$source = (Resolve-Path $Dwg).Path
$productRoot = Join-Path $OutRoot $Product
$evidenceLog = Join-Path $productRoot "peta_window_rotation_compat.log"
$windowP1 = "50,40"
$windowP2 = "150,110"
$expectedWidthMm = "100"
$expectedHeightMm = "70"
# モデル空間の窓のケース（窓の大きさは window_fit_scale_matrix.ps1 と同じ。横長の窓と縦長の窓、回転は 0・90・270 度）。
# 用紙は -FitPapers の正式名で指定する。既定は PDF デバイスの縦の A4 の、余白のある用紙と余白の無い用紙（フルブリード）
# （ARES 2027 の PDF デバイスで実在を確認。ほかに横向きと「拡張 A4」がある）。-LogMediaMargins で全部の用紙の余白を証跡へ書く
$FitPapers = @($FitPapers | ForEach-Object { "$_" -split ',' } | Where-Object { $_ })
$fitWindows = @(
  @{ id = "wide"; w = 92.028; h = 62.445 },
  @{ id = "tall"; w = 30.0; h = 100.0 })
$fitRotations = @(0, 1, 3)
# モデル空間の窓を描く位置（TESTCHART2 の図形から離した空き領域。ケースごとに X をずらす）
$FitOriginX = 100000.0
$FitOriginY = 100000.0
$FitCaseStepX = 500.0
# 寸法の許容: SVG の座標は小数 4 桁、内容は線幅（0.254mm）とクリップの丸めを含む（window_fit_scale_matrix.ps1 と同じ値）
$FitAspectTolMm = 0.02
$FitContentEdgeTolMm = 1.0
# AutoCAD の PDF は窓のクリップ（clip0）が印字した窓そのものなので、クロップの中心との差をこの値まで許す（mm）。
# PDF ドライバの 1pt の丸めを扱わなかったときは 0.07〜0.29mm ずれた（2026-10-30）。ARES のクリップは窓より約 0.4mm 広く、
# 中心も窓と一致しないので、差を記録するだけにする
$AcadWindowClipCenterTolMm = 0.05

foreach ($required in @($exe, $plugin, $source)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルが見つかりません: $required" }
}
New-Item -ItemType Directory -Force -Path $productRoot | Out-Null
if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
$dwgCopy = Join-Path $productRoot "target.dwg"
Copy-Item -LiteralPath $source -Destination $dwgCopy -Force
$dwgHash = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash

. "$PSScriptRoot\..\common\svg_compare.ps1"

if (-not ("CadPeta.CompatNativeWindow" -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace CadPeta {
  public static class CompatNativeWindow {
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", CharSet=CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    private static string ObjectName(IntPtr handle) {
      var value = new StringBuilder(256); int needed;
      return GetUserObjectInformation(handle, 2, value, value.Capacity, out needed) ? value.ToString() : "<unknown>";
    }
    public static string WindowStationName() { return ObjectName(GetProcessWindowStation()); }
    public static string DesktopName() { return ObjectName(GetThreadDesktop(GetCurrentThreadId())); }
  }
  [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ICompatOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
  }
  public sealed class CompatMessageFilter : ICompatOleMessageFilter {
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(ICompatOleMessageFilter newFilter, out ICompatOleMessageFilter oldFilter);
    private static ICompatOleMessageFilter oldFilter;
    public static int RetryWindowMilliseconds { get; set; }
    public static int RetryDelayMilliseconds { get; set; }
    int ICompatOleMessageFilter.HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    int ICompatOleMessageFilter.RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) {
      const int ServerCallRejected = 1;
      const int ServerCallRetryLater = 2;
      return (rejectType == ServerCallRejected || rejectType == ServerCallRetryLater)
        && tickCount < RetryWindowMilliseconds ? RetryDelayMilliseconds : -1;
    }
    int ICompatOleMessageFilter.MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
    public static void Register() {
      ICompatOleMessageFilter previous;
      int result = CoRegisterMessageFilter(new CompatMessageFilter(), out previous);
      if (result != 0) throw new InvalidOperationException("CoRegisterMessageFilter の登録に失敗: 0x" + result.ToString("X8"));
      oldFilter = previous;
    }
    public static void Revoke() { ICompatOleMessageFilter discarded; CoRegisterMessageFilter(oldFilter, out discarded); oldFilter = null; }
  }
}
'@
}






# PETADIAG の成果物（SVG＋EMF＋diag.json）が揃って安定するまで待つ（gradient_compat_e2e.ps1 と同じ理屈。
# ARES は .NET コマンド実行中に COM を拒否するため CMDNAMES を読まない）。
function Wait-PetaDiagArtifacts([string]$dir, $document, [datetime]$startedAt, [string]$commandStartKind) {
  $deadline = (Get-Date).AddSeconds($CommandCompletionTimeoutSeconds)
  $stable = 0
  $lastSignature = ""
  while ((Get-Date) -lt $deadline) {
    $commands = if ($commandStartKind -eq "AresUi") { "" } else { Read-VariableSafe $document "CMDNAMES" }
    if ($commands.StartsWith("<取得失敗")) { $commands = "busy" }
    $svg = @(Get-NewestSvgSince $dir $startedAt)
    if ($svg.Count -eq 1) {
      $emfPath = [IO.Path]::ChangeExtension($svg[0].FullName, ".emf")
      $diagPath = $svg[0].FullName + ".diag.json"
      if ((Test-Path -LiteralPath $emfPath) -and (Test-Path -LiteralPath $diagPath) -and [string]::IsNullOrWhiteSpace($commands)) {
        $signature = "{0}:{1}:{2}" -f $svg[0].Length, (Get-Item -LiteralPath $emfPath).Length, (Get-Item -LiteralPath $diagPath).Length
        if ($signature -eq $lastSignature) { $stable++ } else { $stable = 1; $lastSignature = $signature }
        if ($stable -ge $StableFilePollCount) {
          return @($svg[0].FullName, $emfPath, $diagPath)
        }
      }
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "PETADIAG 成果物が $CommandCompletionTimeoutSeconds 秒以内に完成しませんでした（$dir）"
}

# 成果物と補助ダンプをケースのフォルダへ移し、次のケースで取り違えないようにする。移した SVG と EMF のパスを返す
function Move-PetaDiagArtifacts([string]$dir, [datetime]$startedAt, [string]$caseDir, [string[]]$artifacts) {
  Get-ChildItem -LiteralPath $dir -File | Where-Object {
    $_.LastWriteTime -ge $startedAt -and $_.Extension -in @(".svg", ".emf", ".json", ".pdf", ".png")
  } | Move-Item -Destination $caseDir -Force
  return @((Join-Path $caseDir ([IO.Path]::GetFileName($artifacts[0]))), (Join-Path $caseDir ([IO.Path]::GetFileName($artifacts[1]))))
}

# CAD が PDF に書いた窓のクリップ（クロップ前のページの clip0）の中心と、CADPeta のクロップ（最終 SVG の viewBox）の中心の差を
# 証跡へ書き、AutoCAD では $AcadWindowClipCenterTolMm を超えたら判定の失敗にする。差（mm、X・Y）を返す（クリップが無ければ $null）
function Measure-WindowClipOffset([string]$caseDir, [string]$finalSvg, [string]$label) {
  $stage = Get-ChildItem -LiteralPath $caseDir -Filter '*.stage00.acquired.svg' | Select-Object -First 1
  $clip = if ($null -ne $stage) { [regex]::Match([IO.File]::ReadAllText($stage.FullName), '<clipPath id="clip0"><path d="([^"]+)"') }
  if ($null -eq $clip -or -not $clip.Success) {
    # ARES 2020 のレイアウトの PDF には窓のクリップが無い（2026-10-31）。AutoCAD は必ず書くので、無ければ判定の失敗にする
    Write-Evidence "WINDOW_CLIP ${label}: 窓のクリップ（クロップ前のページの clip0）が無い: $caseDir" | Out-Host
    if ($Product -like 'Acad*') { $script:verificationFailures += "${label}: AutoCAD の PDF に窓のクリップが無い（$caseDir）" }
    return $null
  }
  $clipBounds = Get-SvgPathBounds @($clip.Groups[1].Value)
  $vb = Get-SvgViewBox $finalSvg
  $dx = ($clipBounds.MinX + $clipBounds.MaxX) / 2 - ($vb.MinX + $vb.Width / 2)
  $dy = ($clipBounds.MinY + $clipBounds.MaxY) / 2 - ($vb.MinY + $vb.Height / 2)
  # Write-Evidence は出力にも書くので、戻り値（差）に混ぜないように画面へ流す
  Write-Evidence ("WINDOW_CLIP {0}: クリップの中心 − クロップの中心 = ({1:F3}, {2:F3})mm" -f $label, $dx, $dy) | Out-Host
  if ($Product -like 'Acad*' -and ([Math]::Abs($dx) -gt $AcadWindowClipCenterTolMm -or [Math]::Abs($dy) -gt $AcadWindowClipCenterTolMm)) {
    $script:verificationFailures += ("{0}: AutoCAD が印字した窓の中心とクロップの中心が ({1:F3}, {2:F3})mm ずれる（許容 {3}mm）" -f $label, $dx, $dy, $AcadWindowClipCenterTolMm)
  }
  return @($dx, $dy)
}

# EMF を GDI で固定寸法のビットマップへ描画する（画素比較の入力）。
function Render-EmfToPng([string]$emfPath, [string]$pngPath, [int]$width, [int]$height) {
  Add-Type -AssemblyName System.Drawing
  $mf = [System.Drawing.Imaging.Metafile]::new($emfPath)
  $bmp = New-Object System.Drawing.Bitmap $width, $height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::White)
  $g.DrawImage($mf, (New-Object System.Drawing.Rectangle 0, 0, $width, $height))
  $g.Dispose()
  $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $mf.Dispose()
}

# EMF 同士を描画して平行移動量と残差を測る。回転経由の PDF は描線座標の丸めが変わり
# EMF はバイト一致しないことがある（BricsCAD 実測: 90/270 度で全ストロークに 0.13mm 級の
# 縁取り差）。窓縁の既知精度 0.2mm（3px/15px毎mm）以内の移動に加え、前景を3px膨張しても
# 対応先がない画素が双方向0.01%以下なら同内容とみなす。生の差分率は線幅差を内容欠落と誤認する。
function Measure-EmfShift([string]$emfA, [string]$emfB, [string]$workPrefix) {
  $pngA = "$workPrefix.a.png"; $pngB = "$workPrefix.b.png"
  Render-EmfToPng $emfA $pngA 1500 1050
  Render-EmfToPng $emfB $pngB 1500 1050
  $tool = Join-Path $repo "tests\tools\regression\window_contract\measure_shift.py"
  $project = Join-Path $repo "tests\tools\regression\window_contract"
  # PS 5.1 は $ErrorActionPreference=Stop のもとで外部コマンドの stderr（警告含む）を
  # エラーレコードへ昇格させ得るため、cmd 経由で文字列として受ける
  $out = cmd /c "uv run --project `"$project`" python `"$tool`" `"$pngA`" `"$pngB`" 2>&1"
  $result = @{ Ok = $false; Dx = $null; Dy = $null; Percent = $null; UnmatchedA = $null; UnmatchedB = $null; Raw = ($out -join ' | ') }
  foreach ($line in $out) {
    if ($line -match 'BEST_DX=(-?\d+) BEST_DY=(-?\d+)') { $result.Dx = [int]$Matches[1]; $result.Dy = [int]$Matches[2] }
    if ($line -match '\(([\d.]+)%\)') { $result.Percent = [double]$Matches[1] }
    if ($line -match 'UNMATCHED_A_PERCENT=([\d.]+) UNMATCHED_B_PERCENT=([\d.]+)') {
      $result.UnmatchedA = [double]$Matches[1]; $result.UnmatchedB = [double]$Matches[2]
    }
  }
  if ($null -ne $result.Dx -and $null -ne $result.UnmatchedA -and $null -ne $result.UnmatchedB) {
    $result.Ok = ([Math]::Abs($result.Dx) -le 3) -and ([Math]::Abs($result.Dy) -le 3) -and
      ($result.UnmatchedA -le 0.01) -and ($result.UnmatchedB -le 0.01)
  }
  return $result
}

$application = $null
$candidateApplication = $null
$document = $null
$uiShell = $null
$startedProcess = $null
$secondaryProcess = $null
$pluginStage = $null
$autoloadState = $null
$connectedProgId = $null
$progIds = $null
$ownedPid = 0
$ownedProcess = $null
$messageFilterRegistered = $false
$oldFileDia = $null
$oldOsMode = $null
$completed = $false
$cleanupFailures = @()
$primaryFailure = $null
$verificationFailures = @()
$results = @()
$fitResults = @()
try {
  Write-Evidence "START Product=$Product Exe=$exe Plugin=$plugin Source=$source Layout=$LayoutName Rotations=$($Rotations -join ',')"
  $windowStation = [CadPeta.CompatNativeWindow]::WindowStationName()
  $desktop = [CadPeta.CompatNativeWindow]::DesktopName()
  Write-Evidence "DESKTOP WindowStation=$windowStation Desktop=$desktop"
  if ($windowStation -ne $ExpectedWindowStation -or $desktop -ne $ExpectedDesktop) {
    throw "対話デスクトップが必要です。期待=$ExpectedWindowStation\$ExpectedDesktop、実際=$windowStation\$desktop"
  }
  Assert-CompatLaunchReady $Product
  $uiShell = New-Object -ComObject WScript.Shell
  [CadPeta.CompatMessageFilter]::RetryWindowMilliseconds = $ComRetryWindowMilliseconds
  [CadPeta.CompatMessageFilter]::RetryDelayMilliseconds = $ComRetryDelayMilliseconds
  [CadPeta.CompatMessageFilter]::Register()
  $messageFilterRegistered = $true

  $existingProcesses = @(Get-Process -Name $config.ProcessName -ErrorAction SilentlyContinue)
  if ($existingProcesses.Count -gt 0) {
    throw "既存の $($config.ProcessName) プロセスがあり、起動プロセスを一意に所有できません: PID=$($existingProcesses.Id -join ',')"
  }
  # インストール済みの CADPeta が自動で読み込まれると、ビルドした DLL を試験できない。試験の間だけ登録を外す
  # （AutoCAD はプロファイルを記録して試験の後に戻す。Suspend-CompatProductAutoload）
  Suspend-CompatProductAutoload $Product ([ref]$autoloadState)

  # CADPeta の設定（今までの試験と同じく図面のフォルダに PDF・SVG・EMF）は、起動する CAD にだけ渡す。利用者の %APPDATA%\CADPeta を使わない
  $dataEnvironment = @{ CADPETA_DATA_DIR = (Write-E2eSettings (Join-Path $productRoot 'cadpeta-data')) }
  if ($config.DiscoverProgId) {
    Start-CadVisibleAndConnect $exe $SecondaryLaunchDelaySeconds $GuiStartupTimeoutSeconds ([ref]$startedProcess) ([ref]$candidateApplication) ([ref]$connectedProgId) ([ref]$secondaryProcess) ([ref]$progIds) $dataEnvironment
  } else {
    $startedProcess = Start-CadProcessHidden $exe $config.StartArguments $dataEnvironment
    Write-Evidence "PROCESS started PID=$($startedProcess.Id)"
    $candidateApplication = Wait-ActiveObject $config.ProgId $GuiStartupTimeoutSeconds
    if ($null -eq $candidateApplication) {
      throw "COM オブジェクト $($config.ProgId) が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした"
    }
  }
  $hwnd = Get-CadWindowHandle $candidateApplication $GuiStartupTimeoutSeconds $exe
  [uint32]$pidValue = 0
  [void][CadPeta.CompatNativeWindow]::GetWindowThreadProcessId([IntPtr]$hwnd, [ref]$pidValue)
  $candidatePid = [int]$pidValue
  $ownedStartIds = @($startedProcess.Id)
  if ($null -ne $secondaryProcess) { $ownedStartIds += $secondaryProcess.Id }
  if ($null -ne $startedProcess -and $candidatePid -notin $ownedStartIds) {
    throw "今回開始していないプロセスへ COM 接続しました。開始 PID=$($ownedStartIds -join ',')、接続 PID=$candidatePid"
  }
  $ownedProcess = Assert-ConnectedExecutable $candidatePid $exe
  $application = $candidateApplication
  $candidateApplication = $null
  $ownedPid = $candidatePid
  if (-not $connectedProgId) { $connectedProgId = $config.ProgId }
  Write-Evidence "COM attached ProgId=$connectedProgId HWND=$hwnd PID=$ownedPid Version=$($application.Version)"

  [void]$application.Documents.Open($dwgCopy)
  $document = Find-AndActivateDocument $application $dwgCopy $GuiStartupTimeoutSeconds
  Disable-CadFileDia $document $config.SetFileDia ([ref]$oldFileDia)
  Disable-CadOsMode $document ([ref]$oldOsMode) -Required

  # 図面を開いた直後は再描画等でコマンドを受け付けないことがある。待機状態を確認してから読み込む
  Wait-CommandNamesEmpty $document $GuiStartupTimeoutSeconds
  Write-Evidence "IDLE phase=before plugin load"

  # 信頼するフォルダへコピーした DLL を読み込み、確認の画面を出さずに無人で進める
  $pluginStage = New-CompatPluginStage $Product $plugin
  Assert-CadPluginFolderTrusted $document $pluginStage
  Write-Evidence "PLUGIN_STAGE plugin=$($pluginStage.Plugin)"
  $pluginForCommand = $pluginStage.Plugin -replace '\\', '/'
  # ARES の LOADDOTNETAPPLICATION は、ファイル名の後に明示の Enter が届かないと読み込まない
  # （(command "._LOADDOTNETAPPLICATION" "<path>") の引数渡しではファイル名プロンプトに
  # 表示されたまま入力待ちで止まる。2027 実測）。生テキストで CR を付けて送る
  $loadCommand = if ($config.LoadKind -eq "LoadDotNetApplication") {
    "._LOADDOTNETAPPLICATION`r$pluginForCommand"
  } else {
    "_.NETLOAD`r$pluginForCommand"
  }
  $loadMarker = Join-Path $productRoot "plugin_load_queue.marker"
  if (Test-Path -LiteralPath $loadMarker) { Remove-Item -LiteralPath $loadMarker -Force }
  $loadMarkerForLisp = $loadMarker -replace '\\', '/'
  $markerCommand = "(progn (setq cadpeta:marker (open `"$loadMarkerForLisp`" `"w`")) (write-line `"load-attempt-completed`" cadpeta:marker) (close cadpeta:marker))"
  Write-Evidence "LOAD begin kind=$($config.LoadKind)"
  $document.SendCommand($loadCommand + "`r")
  $document.SendCommand($markerCommand + "`r")
  Wait-FileExists $loadMarker "plugin load queue completion" $LoadQueueTimeoutSeconds
  Write-Evidence "LOAD queue completed"

  # 対象レイアウトをカレントにし、CVPORT の意味（ペーパー空間=1・ビューポート内≠1）を記録する
  $cvportFile = Join-Path $productRoot "cvport_probe.txt"
  $cvportForLisp = $cvportFile -replace '\\', '/'
  $document.SendCommand("(setvar `"CTAB`" `"$LayoutName`")`r")
  $document.SendCommand("(progn (setq cpcv (open `"$cvportForLisp`" `"w`")) (write-line (strcat `"paper CVPORT=`" (itoa (getvar `"CVPORT`")) `" TILEMODE=`" (itoa (getvar `"TILEMODE`"))) cpcv) (command `"_.MSPACE`") (write-line (strcat `"mspace CVPORT=`" (itoa (getvar `"CVPORT`"))) cpcv) (command `"_.PSPACE`") (write-line (strcat `"pspace CVPORT=`" (itoa (getvar `"CVPORT`"))) cpcv) (close cpcv))`r")
  Wait-FileExists $cvportFile "cvport probe" $LoadQueueTimeoutSeconds
  Get-Content -LiteralPath $cvportFile -Encoding Default | ForEach-Object { Write-Evidence "CVPORT_PROBE $_" }

  $layoutObject = $application.ActiveDocument.Layouts.Item($LayoutName)
  if ($LayoutMedia) {
    # 図面はコピーで、終了時に保存せずに閉じるので、用紙は戻さない
    $layoutObject.ConfigName = $config.PdfDevice
    $layoutObject.RefreshPlotDeviceInfo()
    $layoutObject.CanonicalMediaName = $LayoutMedia
    Write-Evidence "LAYOUT_MEDIA set=$LayoutMedia readback=$($layoutObject.CanonicalMediaName)"
  }
  $originalRotation = [int]$layoutObject.PlotRotation
  Write-Evidence "LAYOUT $LayoutName PlotRotation(original)=$originalRotation Device=$($layoutObject.ConfigName) Media=$($layoutObject.CanonicalMediaName)"

  foreach ($rot in $Rotations) {
    $rotDir = Join-Path $productRoot "rot$rot"
    if (Test-Path -LiteralPath $rotDir) { Remove-Item -LiteralPath $rotDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $rotDir | Out-Null

    $layoutObject.PlotRotation = $rot
    Write-Evidence "ROTATION set=$rot readback=$([int]$layoutObject.PlotRotation)"

    $artifactStart = Get-Date
    if ($config.CommandStartKind -eq "AresUi") {
      # ARES はキー入力の座標を GetCorner が基点相対と解釈する（窓契約計測で実測。基点 (50,40) に
      # "150,110" と打つと (200,150) になり窓が 150×110 に化ける）。(command) に点リストで渡すと
      # 絶対解釈になり、.NET コマンド実行中の COM 入力（ARES は拒否する）も要らない
      $lp1 = '(list {0} {1} 0.0)' -f ($windowP1 -split ',')[0], ($windowP1 -split ',')[1]
      $lp2 = '(list {0} {1} 0.0)' -f ($windowP2 -split ',')[0], ($windowP2 -split ',')[1]
      $document.SendCommand("(command ""_.PETADIAG"" $lp1 $lp2)`r")
    } else {
      $document.SendCommand("_.PETADIAG`r$windowP1`r$windowP2`r")
    }
    $artifacts = Wait-PetaDiagArtifacts $productRoot $document $artifactStart $config.CommandStartKind
    Write-Evidence "ARTIFACTS rot=$rot svg=$($artifacts[0]) emf=$($artifacts[1])"
    $svgMoved, $emfMoved = Move-PetaDiagArtifacts $productRoot $artifactStart $rotDir $artifacts
    $head = (Get-Content -LiteralPath $svgMoved -TotalCount 1 -Encoding UTF8)
    $entry = [ordered]@{ Rotation = $rot; Svg = $svgMoved; Emf = $emfMoved }
    if ($head -match 'width="([^"]+)mm" height="([^"]+)mm" viewBox="([^"]+)"') {
      $entry.WidthMm = $Matches[1]; $entry.HeightMm = $Matches[2]; $entry.ViewBox = $Matches[3]
    } else {
      $entry.WidthMm = "<読取不可>"; $entry.HeightMm = "<読取不可>"; $entry.ViewBox = "<読取不可>: $head"
    }
    $entry.EmfSha256 = (Get-FileHash -LiteralPath $emfMoved -Algorithm SHA256).Hash
    $entry.ClipOffset = Measure-WindowClipOffset $rotDir $svgMoved "rot$rot"
    $results += [pscustomobject]$entry
    Write-Evidence "RESULT rot=$rot width=$($entry.WidthMm) height=$($entry.HeightMm) viewBox=$($entry.ViewBox) emfSha=$($entry.EmfSha256.Substring(0,12))"
  }

  $layoutObject.PlotRotation = $originalRotation
  Write-Evidence "ROTATION restored=$originalRotation"

  if (-not $SkipModelFit) {
    # モデルタブのページ設定を PDF デバイスの A4 にする。図面はコピーで、終了時に保存せずに閉じるので、ページ設定は戻さない
    $document.SendCommand("(setvar `"CTAB`" `"Model`")`r")
    $modelLayout = $application.ActiveDocument.ModelSpace.Layout
    $modelLayout.ConfigName = $config.PdfDevice
    $modelLayout.RefreshPlotDeviceInfo()
    $mediaNames = @($modelLayout.GetCanonicalMediaNames())
    $a4Media = @($mediaNames | Where-Object { $_ -match 'A4' })
    Write-Evidence "FIT_MEDIA device=$($config.PdfDevice) A4=$(($a4Media | ForEach-Object { "$_ ($($modelLayout.GetLocaleMediaName($_)))" }) -join '; ')"
    if ($LogMediaMargins) {
      # 用紙ごとの余白（mm、縦向きの用紙座標で 左下=(左,下)・右上=(右,上)）。回転の向きは上下か左右の余白が非対称な用紙でだけ結果に出る
      foreach ($name in $mediaNames) {
        try {
          $modelLayout.CanonicalMediaName = $name
          $lowerLeft = $null; $upperRight = $null
          $modelLayout.GetPaperMargins([ref]$lowerLeft, [ref]$upperRight)
          Write-Evidence ("MEDIA_MARGIN {0} 左={1} 下={2} 右={3} 上={4}" -f $name, $lowerLeft[0], $lowerLeft[1], $upperRight[0], $upperRight[1])
        } catch {
          Write-Evidence "MEDIA_MARGIN $name 取得失敗: $($_.Exception.Message)"
        }
      }
    }
    $missingPapers = @($FitPapers | Where-Object { $_ -notin $mediaNames })
    if ($missingPapers.Count -gt 0) {
      throw "PDF デバイス $($config.PdfDevice) に用紙 $($missingPapers -join ', ') が無い（A4 の用紙: $($a4Media -join ', ')）。モデル空間のケースを省くときは -SkipModelFit"
    }

    $fitIndex = 0
    foreach ($paper in $FitPapers) {
      $modelLayout.CanonicalMediaName = $paper
      Write-Evidence "FIT_PAPER set=$paper readback=$($modelLayout.CanonicalMediaName)"
      foreach ($win in $fitWindows) {
        foreach ($rot in $fitRotations) {
          $fitId = "fit{0}_{1}_rot{2}" -f $fitIndex, $win.id, $rot
          $x1 = $FitOriginX + $fitIndex * $FitCaseStepX; $y1 = $FitOriginY
          $x2 = $x1 + $win.w; $y2 = $y1 + $win.h
          $fitIndex++
          $caseDir = Join-Path $productRoot $fitId
          if (Test-Path -LiteralPath $caseDir) { Remove-Item -LiteralPath $caseDir -Recurse -Force }
          New-Item -ItemType Directory -Force -Path $caseDir | Out-Null

          # 窓と同じ矩形と対角線を描き、描き終えたことをマーカーのファイルで確かめてから出力する
          $drawMarker = Join-Path $caseDir "draw.marker"
          $drawMarkerForLisp = $drawMarker -replace '\\', '/'
          $p = { param($x, $y) '(list {0} {1} 0.0)' -f $x, $y }
          $drawFormat = "(progn (command `"_.PLINE`" {0} {1} {2} {3} `"_C`") (command `"_.LINE`" {0} {2} `"`") (command `"_.LINE`" {3} {1} `"`") " +
            "(setq cpdm (open `"$drawMarkerForLisp`" `"w`")) (write-line `"drawn`" cpdm) (close cpdm))`r"
          $document.SendCommand(($drawFormat -f (& $p $x1 $y1), (& $p $x2 $y1), (& $p $x2 $y2), (& $p $x1 $y2)))
          Wait-FileExists $drawMarker "fit case drawing" $LoadQueueTimeoutSeconds

          $modelLayout.PlotRotation = $rot
          Write-Evidence "FIT_CASE $fitId window=($x1,$y1)-($x2,$y2) rotation set=$rot readback=$([int]$modelLayout.PlotRotation)"
          $artifactStart = Get-Date
          if ($config.CommandStartKind -eq "AresUi") {
            $document.SendCommand(("(command `"_.PETADIAG`" {0} {1})`r" -f (& $p $x1 $y1), (& $p $x2 $y2)))
          } else {
            $document.SendCommand("_.PETADIAG`r$x1,$y1`r$x2,$y2`r")
          }
          $artifacts = Wait-PetaDiagArtifacts $productRoot $document $artifactStart $config.CommandStartKind
          $svgMoved, $emfMoved = Move-PetaDiagArtifacts $productRoot $artifactStart $caseDir $artifacts

          # SVG の寸法が窓の縦横比のまま（尺度 = 幅 ÷ 窓の幅 で、高さも窓の高さ × 尺度）で、
          # 内容（窓の縁の矩形）が四辺まで届くこと
          $gaps = Get-SvgContentEdgeGaps $svgMoved
          $scale = $gaps.ViewBox.Width / $win.w
          $fitLine = "{0}: 用紙={1} 回転={2} 尺度={3:F6} viewBox={4}x{5} 縁との差 左={6:F3} 上={7:F3} 右={8:F3} 下={9:F3}" -f
            $fitId, $paper, ($rot * 90), $scale, $gaps.ViewBox.Width, $gaps.ViewBox.Height, $gaps.Left, $gaps.Top, $gaps.Right, $gaps.Bottom
          [void](Measure-WindowClipOffset $caseDir $svgMoved $fitId)
          Write-Evidence "FIT_RESULT $fitLine"
          $fitResults += $fitLine
          if ([Math]::Abs($gaps.ViewBox.Height - $win.h * $scale) -gt $FitAspectTolMm) {
            $verificationFailures += "${fitId}: viewBox の高さ $($gaps.ViewBox.Height) が 窓の高さ×尺度 $([Math]::Round($win.h * $scale, 3)) と不一致"
          }
          foreach ($edge in @(@{ n = "左"; d = $gaps.Left }, @{ n = "上"; d = $gaps.Top }, @{ n = "右"; d = $gaps.Right }, @{ n = "下"; d = $gaps.Bottom })) {
            if ([Math]::Abs($edge.d) -gt $FitContentEdgeTolMm) {
              $verificationFailures += "${fitId}: 窓の縁の矩形が SVG の$($edge.n)辺と $([Math]::Round($edge.d, 3))mm ずれる（CADPeta のフィット尺度と CAD の印字が食い違う）"
            }
          }
        }
      }
    }
  }
  $completed = $true
} catch {
  Write-Evidence "ERROR $($_.Exception.Message)"
  $primaryFailure = $_
} finally {
  if ($null -ne $document) {
    Restore-CadSystemVariables $document @(
      @{ Name = "FILEDIA"; Value = $oldFileDia },
      @{ Name = "OSMODE"; Value = $oldOsMode }) ([ref]$cleanupFailures)
  }
  if ($null -ne $application) {
    Close-CadApplication $application $ownedPid ([ref]$cleanupFailures) `
      -CloseFailurePrefix "Close(false) 失敗" -QuitFailurePrefix "Quit 失敗" -TolerateExitedProcess
  }
  Release-CadComResources $messageFilterRegistered @($uiShell, $document, $application, $candidateApplication)
  # 起動したが ROT 接続先にならなかったプロセス（1つ目・2つ目のうち捨てになった方）の後始末。
  # 自分が起動し exe で起動した所有プロセスのみが対象
  Stop-CadSessionProcesses $ownedProcess $ProcessExitTimeoutSeconds @($startedProcess, $secondaryProcess) $dwgCopy $dwgHash ([ref]$cleanupFailures) `
    -AllowForceStop:$AllowForceStop -VerifyForceStop -RecordDwgFailures -QuitExitCode $config.QuitExitCode
  Remove-CompatPluginStage $pluginStage ([ref]$cleanupFailures)
  Resume-CompatProductAutoload $autoloadState $exe ([ref]$cleanupFailures)
  Write-Evidence "END completed=$completed"
  if ($results.Count -gt 0) {
    Write-Output ""
    Write-Output "=== 判定（$Product / $LayoutName / 窓 $windowP1 - $windowP2） ==="
    $failures = @()
    $baseline = $results | Where-Object { $_.Rotation -eq 0 } | Select-Object -First 1
    foreach ($r in $results) {
      Write-Output ("回転 {0}: width={1}mm height={2}mm viewBox={3}" -f $r.Rotation, $r.WidthMm, $r.HeightMm, $r.ViewBox)
      if ($r.WidthMm -ne $expectedWidthMm -or $r.HeightMm -ne $expectedHeightMm) {
        $failures += "回転 $($r.Rotation): 寸法が ${expectedWidthMm}×${expectedHeightMm}mm でない（$($r.WidthMm)×$($r.HeightMm)）"
      }
      if ($null -ne $baseline -and $r.Rotation -ne 0 -and $r.EmfSha256 -ne $baseline.EmfSha256) {
        # バイト不一致は描画して判定する（座標の丸め差はバイトを変えるが見た目を変えない）
        try {
          $shift = Measure-EmfShift $baseline.Emf $r.Emf (Join-Path (Split-Path -Parent $r.Emf) "shift_vs_rot0")
          if ($shift.Ok) {
          Write-Output ("回転 {0}: EMF バイトは不一致だが描画は一致（dx={1}px dy={2}px 生差={3}% 未対応={4}%/{5}%）" -f $r.Rotation, $shift.Dx, $shift.Dy, $shift.Percent, $shift.UnmatchedA, $shift.UnmatchedB)
          } else {
          $failures += "回転 $($r.Rotation): 描画が回転0と一致しない（dx=$($shift.Dx) dy=$($shift.Dy) 未対応=$($shift.UnmatchedA)%/$($shift.UnmatchedB)% → クロップ矩形の回転対応を疑う）: $($shift.Raw)"
          }
        } catch {
          $failures += "回転 $($r.Rotation): EMF 描画比較に失敗: $($_.Exception.Message)"
        }
      }
    }
    if ($failures.Count -gt 0) {
      Write-Output ""
      Write-Output "=== 失敗 ==="
      $failures | ForEach-Object { Write-Output $_ }
      $verificationFailures += @($failures)
    } else {
      Write-Output "全回転で寸法 ${expectedWidthMm}×${expectedHeightMm}mm・EMF 一致（用紙対応付けと回転対応が成立）"
    }
  }
  if ($fitResults.Count -gt 0) {
    Write-Output ""
    Write-Output "=== モデル空間の窓 × 用紙合わせ（$Product） ==="
    $fitResults | ForEach-Object { Write-Output $_ }
  }
  $allFailures = @()
  if ($null -ne $primaryFailure) { $allFailures += "主処理: $($primaryFailure.Exception.Message)" }
  $allFailures += @($verificationFailures | ForEach-Object { "判定: $_" })
  $allFailures += @($cleanupFailures | ForEach-Object { "後始末: $_" })
  if ($allFailures.Count -gt 0) {
    throw "互換CAD回転回帰に失敗しました（$($allFailures.Count) 件）: $($allFailures -join '; ')"
  }
}
