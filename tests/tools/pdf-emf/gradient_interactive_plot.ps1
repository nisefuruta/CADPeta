param(
  [string]$ProgId = "AutoCAD.Application.25.1",
  [string]$Dwt = "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt",
  [string]$OutRoot = "",
  [string]$EvidenceLog = ""
)
$ErrorActionPreference = "Stop"

$PlotDeviceName = "AutoCAD PDF (High Quality Print).pc3"
$ExpectedLocaleMediaName = "ISO フルブリード A3 (420.00 x 297.00 ミリ)"
$PlotWidthMm = 420.0
$PlotHeightMm = 297.0
$MinimumPdfBytes = 10000
$GuiStartupTimeoutSeconds = 90
$CommandCompletionTimeoutSeconds = 30
$ComRetryWindowMilliseconds = 60000
$ComRetryDelayMilliseconds = 100
$ProcessExitTimeoutSeconds = 15
$PollMilliseconds = 500
# AutoCAD ActiveX enum values (verified from the installed 2026 Interop PIA).
$AcMillimeters = 1
$AcWindow = 4
$AcZeroDegrees = 0
$AcAllViewports = 1
$ExpectedWindowStation = "WinSta0"
$ExpectedDesktop = "Default"

if (-not $OutRoot) { $OutRoot = Join-Path $env:TEMP "CADPeta_gradient_interactive_plot" }
if (-not $EvidenceLog) { $EvidenceLog = Join-Path $OutRoot "interactive_plot_harness.log" }
New-Item -ItemType Directory -Force $OutRoot | Out-Null
if (Test-Path $EvidenceLog) { Remove-Item -LiteralPath $EvidenceLog -Force }

function Write-Evidence([string]$message) {
  $line = "{0:o} {1}" -f (Get-Date), $message
  [IO.File]::AppendAllText($EvidenceLog, $line + [Environment]::NewLine, [Text.Encoding]::UTF8)
  Write-Output $line
}

if (-not ("CadPeta.NativeWindow" -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace CadPeta {
  public static class NativeWindow {
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", CharSet=CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    private static string ObjectName(IntPtr handle) {
      var value = new StringBuilder(256);
      int needed;
      return GetUserObjectInformation(handle, 2, value, value.Capacity, out needed) ? value.ToString() : "<unknown>";
    }
    public static string WindowStationName() { return ObjectName(GetProcessWindowStation()); }
    public static string DesktopName() { return ObjectName(GetThreadDesktop(GetCurrentThreadId())); }
  }

  [ComImport, Guid("00000016-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
  }

  public sealed class MessageFilter : IOleMessageFilter {
    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);
    private static IOleMessageFilter oldFilter;
    public static int RetryWindowMilliseconds { get; set; }
    public static int RetryDelayMilliseconds { get; set; }

    int IOleMessageFilter.HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    int IOleMessageFilter.RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) {
      const int ServerCallRetryLater = 2;
      if (rejectType != ServerCallRetryLater || tickCount >= RetryWindowMilliseconds) { return -1; }
      return RetryDelayMilliseconds;
    }
    int IOleMessageFilter.MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }

    public static void Register() {
      IOleMessageFilter previous;
      int result = CoRegisterMessageFilter(new MessageFilter(), out previous);
      if (result != 0) { throw new InvalidOperationException("CoRegisterMessageFilter の登録に失敗: 0x" + result.ToString("X8")); }
      oldFilter = previous;
    }
    public static void Revoke() {
      IOleMessageFilter discarded;
      CoRegisterMessageFilter(oldFilter, out discarded);
      oldFilter = null;
    }
  }
}
'@
}

function Wait-AcadQuiescent($acadState, [string]$phase) {
  $deadline = (Get-Date).AddSeconds($CommandCompletionTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    if ([bool]$acadState.IsQuiescent) {
      Write-Evidence "QUIESCENT phase=$phase"
      return
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "AutoCAD は処理 '$phase' で $CommandCompletionTimeoutSeconds 秒以内に待機状態になりませんでした"
}

$generator = (Resolve-Path (Join-Path $PSScriptRoot "..\..\assets\testcharts\gradient\gh_charts.lsp")).Path -replace '\\','/'
$generatorDir = (Split-Path $generator) -replace '\\','/'
$pdf = Join-Path $OutRoot "grad_LINEAR_bw_a000_s000_interactive.pdf"
$entityLog = Join-Path $OutRoot "interactive_entity.log"
if (Test-Path $pdf) { Remove-Item -LiteralPath $pdf -Force }
if (Test-Path $entityLog) { Remove-Item -LiteralPath $entityLog -Force }

$application = $null
$document = $null
$layout = $null
$plot = $null
$acadState = $null
$ownedPid = 0
$completed = $false
$messageFilterRegistered = $false
# GUI AutoCAD の強制終了・タイムアウトでも永続 sysvar（SECURELOAD/FILEDIA 等）を利用者プロファイルへ残さない包括ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
$runError = $null
try {
  Write-Evidence "START ProgId=$ProgId Dwt=$Dwt"
  $windowStation = [CadPeta.NativeWindow]::WindowStationName()
  $desktop = [CadPeta.NativeWindow]::DesktopName()
  Write-Evidence "DESKTOP WindowStation=$windowStation Desktop=$desktop"
  if ($windowStation -ne $ExpectedWindowStation -or $desktop -ne $ExpectedDesktop) {
    throw "対話デスクトップが必要です。期待=$ExpectedWindowStation\$ExpectedDesktop、実際=$windowStation\$desktop"
  }
  $apartmentState = [Threading.Thread]::CurrentThread.GetApartmentState()
  Write-Evidence "COM ApartmentState=$apartmentState"
  if ($apartmentState -ne [Threading.ApartmentState]::STA) { throw "AutoCAD COM メッセージフィルターには STA スレッドが必要です" }
  [CadPeta.MessageFilter]::RetryWindowMilliseconds = $ComRetryWindowMilliseconds
  [CadPeta.MessageFilter]::RetryDelayMilliseconds = $ComRetryDelayMilliseconds
  [CadPeta.MessageFilter]::Register()
  $messageFilterRegistered = $true
  Write-Evidence "MSGFILTER registered retry=${ComRetryDelayMilliseconds}ms window=${ComRetryWindowMilliseconds}ms"
  $application = New-Object -ComObject $ProgId
  $application.Visible = $true
  $deadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
  $hwnd = 0
  while ((Get-Date) -lt $deadline) {
    try { $hwnd = [int64]$application.HWND } catch { $hwnd = 0 }
    if ($hwnd -ne 0) { break }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  if ($hwnd -eq 0) { throw "AutoCAD GUI は $GuiStartupTimeoutSeconds 秒以内に HWND を作成しませんでした" }
  [uint32]$pidValue = 0
  [void][CadPeta.NativeWindow]::GetWindowThreadProcessId([IntPtr]$hwnd, [ref]$pidValue)
  $ownedPid = [int]$pidValue
  Write-Evidence "GUI HWND=$hwnd PID=$ownedPid"

  $document = $application.Documents.Add($Dwt)
  # ActiveX SetVariable requires the native Int16 type for these integer vars.
  $document.SetVariable("TILEMODE", [int16]1)
  $document.SetVariable("SECURELOAD", [int16]0)
  $document.SetVariable("BACKGROUNDPLOT", [int16]0)
  Write-Evidence "SYSVARS TILEMODE=1 SECURELOAD=0 BACKGROUNDPLOT=0"
  $acadState = $application.GetAcadState()
  Wait-AcadQuiescent $acadState "before SendCommand"
  $setupCommands = @(
    "(setq gh:dir `"$generatorDir`")",
    "(setq gh:log `"$($entityLog -replace '\\','/')`")",
    "(load `"$generator`")",
    "(gh:prep `"LINEAR`" 0.0 0.0 0 16777215 -1 -1)"
  )
  Write-Evidence "SENDCOMMAND begin"
  $document.SendCommand(($setupCommands -join "`r`n") + "`r`n")
  Write-Evidence "SENDCOMMAND returned"
  Wait-AcadQuiescent $acadState "after SendCommand"
  Write-Evidence "ENTITY_SETUP completed"

  $layout = $document.ActiveLayout
  if ([string]$layout.Name -ne "Model") { throw "アクティブなレイアウトが Model ではありません: $($layout.Name)" }
  Write-Evidence "CFG ConfigName begin=$PlotDeviceName"
  $layout.ConfigName = $PlotDeviceName
  Write-Evidence "CFG ConfigName ok"
  Write-Evidence "CFG RefreshPlotDeviceInfo begin"
  $layout.RefreshPlotDeviceInfo()
  Write-Evidence "CFG RefreshPlotDeviceInfo ok"
  $canonicalMediaName = $null
  foreach ($candidate in @($layout.GetCanonicalMediaNames())) {
    $localeName = [string]$layout.GetLocaleMediaName([string]$candidate)
    if ($localeName -eq $ExpectedLocaleMediaName) { $canonicalMediaName = [string]$candidate; break }
  }
  if (-not $canonicalMediaName) { throw "プロット用紙が見つかりません: $ExpectedLocaleMediaName" }
  Write-Evidence "CFG CanonicalMediaName begin=$canonicalMediaName"
  $layout.CanonicalMediaName = $canonicalMediaName
  Write-Evidence "CFG CanonicalMediaName ok"
  Write-Evidence "CFG PaperUnits begin"
  $layout.PaperUnits = $AcMillimeters
  Write-Evidence "CFG PaperUnits ok"
  Write-Evidence "CFG PlotRotation begin"
  $layout.PlotRotation = $AcZeroDegrees
  Write-Evidence "CFG PlotRotation ok"
  Write-Evidence "CFG CenterPlot begin"
  $layout.CenterPlot = $false
  Write-Evidence "CFG CenterPlot ok"
  Write-Evidence "CFG PlotOrigin begin"
  $layout.PlotOrigin = [double[]]@(0.0, 0.0)
  Write-Evidence "CFG PlotOrigin ok"
  Write-Evidence "CFG UseStandardScale begin"
  $layout.UseStandardScale = $false
  Write-Evidence "CFG UseStandardScale ok"
  Write-Evidence "CFG SetCustomScale begin"
  $layout.SetCustomScale(1.0, 1.0)
  Write-Evidence "CFG SetCustomScale ok"
  Write-Evidence "CFG SetWindowToPlot begin"
  $layout.SetWindowToPlot([double[]]@(0.0, 0.0), [double[]]@($PlotWidthMm, $PlotHeightMm))
  Write-Evidence "CFG SetWindowToPlot ok"
  Write-Evidence "CFG PlotType begin=$AcWindow"
  $layout.PlotType = $AcWindow
  Write-Evidence "CFG PlotType ok"
  Write-Evidence "CFG PlotWithPlotStyles begin"
  $layout.PlotWithPlotStyles = $true
  Write-Evidence "CFG PlotWithPlotStyles ok"
  Write-Evidence "CFG PlotWithLineweights begin"
  $layout.PlotWithLineweights = $true
  Write-Evidence "CFG PlotWithLineweights ok"
  Write-Evidence "CFG Regen begin"
  $document.Regen($AcAllViewports)
  Write-Evidence "CFG Regen ok"
  Write-Evidence "PLOT_CONFIG device=$PlotDeviceName media=$canonicalMediaName window=${PlotWidthMm}x${PlotHeightMm}mm scale=1:1"

  $plot = $document.Plot
  $plot.QuietErrorMode = $true
  $plot.NumberOfCopies = 1
  $plotResult = [bool]$plot.PlotToFile($pdf, $PlotDeviceName)
  if (-not $plotResult) { throw "PlotToFile が false を返しました" }
  if (-not (Test-Path $pdf) -or (Get-Item $pdf).Length -le $MinimumPdfBytes) {
    throw "対話プロットが生成されていないか、ファイルが小さすぎます: $pdf"
  }
  Write-Evidence "PDF=$pdf BYTES=$((Get-Item $pdf).Length)"
  $completed = $true
} catch {
  Write-Evidence "ERROR $($_.Exception.Message)"
  # 例外は退避し、finally のレジストリ復元検証と合流させてから末尾で投げ直す
  $runError = $_
} finally {
  if ($null -ne $document) { try { Write-Evidence "CLOSE begin"; $document.Close($false); Write-Evidence "CLOSE ok" } catch { Write-Evidence "CLOSE error $($_.Exception.Message)" } }
  if ($null -ne $application) { try { Write-Evidence "QUIT begin"; $application.Quit(); Write-Evidence "QUIT ok" } catch { Write-Evidence "QUIT error $($_.Exception.Message)" } }
  if ($messageFilterRegistered) { try { [CadPeta.MessageFilter]::Revoke(); Write-Evidence "MSGFILTER revoked" } catch { Write-Evidence "MSGFILTER revoke error $($_.Exception.Message)" } }
  foreach ($comObject in @($plot, $layout, $acadState, $document, $application)) {
    if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
      try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) } catch {}
    }
  }
  [GC]::Collect()
  [GC]::WaitForPendingFinalizers()
  if ($ownedPid -gt 0) {
    $deadline = (Get-Date).AddSeconds($ProcessExitTimeoutSeconds)
    while ((Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
      Start-Sleep -Milliseconds $PollMilliseconds
    }
    if (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) {
      Write-Evidence "FORCE_STOP owned PID=$ownedPid"
      Stop-Process -Id $ownedPid -Force
    }
  }
  # 旧来の手書き FileDialog=0→1 復元は CadRegGuard の包括復元（開始値復元＋FileDialog は安全値1へ矯正）に
  # 完全包含されるため撤去し、復元処理を一本化して二重復元を排除する（CAD 終了処理の後に1回だけ実施）
  $regReport = Restore-CadRegState $regGuard
  if ($regReport.actions.Count -gt 0) { Write-Evidence ("レジストリ復元: " + ($regReport.actions -join '; ')) }
  Write-Evidence "END completed=$completed"
}
if ($runError -and -not $regReport.ok) {
  throw [AggregateException]::new(
    'CAD実行失敗に加えてレジストリ復元にも失敗した',
    [Exception[]]@($runError.Exception, [InvalidOperationException]::new($regReport.mismatch -join '; ')))
}
if ($runError) { throw $runError }
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
