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
  [int[]]$Rotations = @(0, 1, 2, 3),   # PlotRotation 列挙値（0=0度, 1=90度, 2=180度, 3=270度）
  [string]$Dwg,
  [string]$OutRoot = "$env:TEMP\CadPeta_peta_rotation_compat",
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

$products = @{
  # AutoCAD も回転 90 度の1ケースしか実測が無かったため、同じ観点（依頼3）で測る。
  # GUI COM 駆動の作法は gradient_interactive_plot.ps1 で実測済みの形に合わせる
  Acad2026 = @{
    Exe = "C:\Program Files\Autodesk\AutoCAD 2026\acad.exe"
    StartArguments = @("/nologo")
    ProgId = "AutoCAD.Application.25.1"
    ProcessName = "acad"
    Plugin = "src\CadPeta.Plugin.Acad\bin\Release\net8.0-windows\CadPeta.Plugin.Acad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
  }
  Acad2027 = @{
    Exe = "C:\Program Files\Autodesk\AutoCAD 2027\acad.exe"
    StartArguments = @("/nologo")
    ProgId = "AutoCAD.Application.26"
    ProcessName = "acad"
    Plugin = "src\CadPeta.Plugin.Acad\bin\Release\net10.0-windows\CadPeta.Plugin.Acad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
  }
  # ARES の net48 世代（2020/2023/2024）は SDK が年式別のため、プラグインも年式別の
  # 出力（既定=bin、既定以外=bin\ares<年>）を読み込む。ProgID の版番号は製品表示年ではなく
  # COM API 版（2020=4.4／2023=4.6／2024=23.1／2027=26.8。本機 HKCR 実測）
  Ares2020 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2020\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.4.4"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\ares2020\Release\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ares2023 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2023\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.4.6"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\ares2023\Release\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ares2024 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2024\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.23.1"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\Release\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ares2027 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2027\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.26.8"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\Release\net8.0-windows\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ijcad2026 = @{
    Exe = "C:\Program Files\ITJP\IJCAD 2026 PRO\gcad.exe"
    StartArguments = @()
    ProgId = "Gcad.Application"
    ProcessName = "gcad"
    Plugin = "src\CadPeta.Plugin.Ijcad\bin\Release\net8.0-windows\CadPeta.Plugin.Ijcad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
  }
  BricscadV26 = @{
    Exe = "C:\Program Files\Bricsys\BricsCAD V26 ja_JP\bricscad.exe"
    StartArguments = @("/Automation")
    ProgId = "BricscadApp.AcadApplication.26.0"
    ProcessName = "bricscad"
    Plugin = "src\CadPeta.Plugin.Bricscad\bin\Release\net8.0-windows\CadPeta.Plugin.Bricscad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
  }
}

$config = $products[$Product]
$exe = [IO.Path]::GetFullPath($config.Exe)
$plugin = [IO.Path]::GetFullPath((Join-Path $repo $config.Plugin))
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

foreach ($required in @($exe, $plugin, $source)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルが見つかりません: $required" }
}
New-Item -ItemType Directory -Force -Path $productRoot | Out-Null
if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
$dwgCopy = Join-Path $productRoot "target.dwg"
Copy-Item -LiteralPath $source -Destination $dwgCopy -Force
$dwgHash = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash

. "$PSScriptRoot\..\common\compat_cad_com.ps1"

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
$messageFilterRegistered = $false
$oldFileDia = $null
$oldOsMode = $null
$completed = $false
$cleanupFailures = @()
$primaryFailure = $null
$verificationFailures = @()
$results = @()
try {
  Write-Evidence "START Product=$Product Exe=$exe Plugin=$plugin Source=$source Layout=$LayoutName Rotations=$($Rotations -join ',')"
  $windowStation = [CadPeta.CompatNativeWindow]::WindowStationName()
  $desktop = [CadPeta.CompatNativeWindow]::DesktopName()
  Write-Evidence "DESKTOP WindowStation=$windowStation Desktop=$desktop"
  if ($windowStation -ne $ExpectedWindowStation -or $desktop -ne $ExpectedDesktop) {
    throw "対話デスクトップが必要です。期待=$ExpectedWindowStation\$ExpectedDesktop、実際=$windowStation\$desktop"
  }
  Assert-WindowsPowerShellSta
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
  Suspend-CompatProductAutoload $Product ([ref]$autoloadState)

  if ($config.DiscoverProgId) {
    Start-CadVisibleAndConnect $exe $SecondaryLaunchDelaySeconds $GuiStartupTimeoutSeconds ([ref]$startedProcess) ([ref]$candidateApplication) ([ref]$connectedProgId) ([ref]$secondaryProcess) ([ref]$progIds)
  } else {
    $startedProcess = Start-CadProcessHidden $exe $config.StartArguments
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
  Assert-ConnectedExecutable $candidatePid $exe
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
  $originalRotation = [int]$layoutObject.PlotRotation
  Write-Evidence "LAYOUT $LayoutName PlotRotation(original)=$originalRotation"

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

    # 成果物と補助ダンプを回転別フォルダへ退避し、次の回で取り違えないようにする
    Get-ChildItem -LiteralPath $productRoot -File | Where-Object {
      $_.LastWriteTime -ge $artifactStart -and $_.Extension -in @(".svg", ".emf", ".json", ".pdf", ".png")
    } | Move-Item -Destination $rotDir -Force

    $svgMoved = Join-Path $rotDir ([IO.Path]::GetFileName($artifacts[0]))
    $emfMoved = Join-Path $rotDir ([IO.Path]::GetFileName($artifacts[1]))
    $head = (Get-Content -LiteralPath $svgMoved -TotalCount 1 -Encoding UTF8)
    $entry = [ordered]@{ Rotation = $rot; Svg = $svgMoved; Emf = $emfMoved }
    if ($head -match 'width="([^"]+)mm" height="([^"]+)mm" viewBox="([^"]+)"') {
      $entry.WidthMm = $Matches[1]; $entry.HeightMm = $Matches[2]; $entry.ViewBox = $Matches[3]
    } else {
      $entry.WidthMm = "<読取不可>"; $entry.HeightMm = "<読取不可>"; $entry.ViewBox = "<読取不可>: $head"
    }
    $entry.EmfSha256 = (Get-FileHash -LiteralPath $emfMoved -Algorithm SHA256).Hash
    $results += [pscustomobject]$entry
    Write-Evidence "RESULT rot=$rot width=$($entry.WidthMm) height=$($entry.HeightMm) viewBox=$($entry.ViewBox) emfSha=$($entry.EmfSha256.Substring(0,12))"
  }

  $layoutObject.PlotRotation = $originalRotation
  Write-Evidence "ROTATION restored=$originalRotation"
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
  Stop-CadSessionProcesses $ownedPid $ProcessExitTimeoutSeconds @($startedProcess, $secondaryProcess) $dwgCopy $dwgHash ([ref]$cleanupFailures) `
    -AllowForceStop:$AllowForceStop -VerifyForceStop -RecordDwgFailures
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
  $allFailures = @()
  if ($null -ne $primaryFailure) { $allFailures += "主処理: $($primaryFailure.Exception.Message)" }
  $allFailures += @($verificationFailures | ForEach-Object { "判定: $_" })
  $allFailures += @($cleanupFailures | ForEach-Object { "後始末: $_" })
  if ($allFailures.Count -gt 0) {
    throw "互換CAD回転回帰に失敗しました（$($allFailures.Count) 件）: $($allFailures -join '; ')"
  }
}
