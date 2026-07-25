param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("Ares2020", "Ares2023", "Ares2024", "Ares2027", "Ijcad2026", "BricscadV26")]
  [string]$Product,
  # Print は PETADIAG（PDF印刷経路）、DataExport は PETADATADIAG（構造SVG書き出し・DB直接取得）。
  # DataExport は EMF/PDF を生成せず、グラデーションを ExpectedGradientImageCount 枚の画像として検査する。
  [ValidateSet("Print", "DataExport")]
  [string]$Mode = "Print",
  [Parameter(Mandatory = $true)]
  [string]$SourceDwg,
  [Parameter(Mandatory = $true)]
  [string]$OutRoot,
  [ValidateRange(0, 1000)]
  [int]$ExpectedGradientImageCount = 9,
  [switch]$ProbeOnly,
  [switch]$AllowForceStop
)
$ErrorActionPreference = "Stop"

$GuiStartupTimeoutSeconds = 120
$CommandCompletionTimeoutSeconds = 300
$ComRetryWindowMilliseconds = 60000
$ComRetryDelayMilliseconds = 100
$ProcessExitTimeoutSeconds = 20
$PollMilliseconds = 500
$StableFilePollCount = 3
$UnregisteredCommandTimeoutSeconds = 20
$CommandStartTimeoutSeconds = 10
$LoadQueueTimeoutSeconds = 30
$RangePromptKeywordOrderPattern = "(?s)<u>A</u>.*<u>O</u>.*<u>M</u>.*<u>F</u>.*<u>B</u>"
$ExpectedWindowStation = "WinSta0"
$ExpectedDesktop = "Default"

$products = @{
  # ARES の net48 世代（2020/2023/2024）は SDK が年式別のため、プラグインも年式別の出力
  # （既定=bin、既定以外=bin\ares<年>）を読み込む。ProgID の版番号は COM API 版
  # （2020=4.4／2023=4.6／2024=23.1／2027=26.8。本機 HKCR 実測）
  Ares2020 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2020\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.4.4"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\ares2020\Debug\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    GradientRepresentation = "VectorPaths"
    MinGradientPathCount = 3000
    # 2020 は (command) 引数渡しの読込が入力待ちで止まる（2026-07-21 実測）。生テキスト＋CR で
    # 送り、そのためファイル名プロンプトをコマンド行へ出す FILEDIA=0（保存→0→復元）にする
    RawTextLoad = $true
    SetFileDia = $true
    DiscoverProgId = $true
    ComActivate = $false
  }
  Ares2023 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2023\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.4.6"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\ares2023\Debug\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    GradientRepresentation = "VectorPaths"
    MinGradientPathCount = 3000
    # 2020 と同じく (command) 引数渡しが止まるため生テキスト＋CR＋FILEDIA=0 で読み込む
    RawTextLoad = $true
    SetFileDia = $true
    DiscoverProgId = $true
    ComActivate = $false
  }
  Ares2024 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2024\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.23.1"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\Debug\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    GradientRepresentation = "VectorPaths"
    MinGradientPathCount = 3000
    SetFileDia = $false
    DiscoverProgId = $true
    ComActivate = $false
  }
  Ares2027 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2027\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.26.8"
    ProcessName = "ARESS"
    Plugin = "src\CadPeta.Plugin.Ares\bin\Debug\net8.0-windows\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    GradientRepresentation = "VectorPaths"
    MinGradientPathCount = 3000
    SetFileDia = $false
    DiscoverProgId = $true
    ComActivate = $false
  }
  Ijcad2026 = @{
    Exe = "C:\Program Files\ITJP\IJCAD 2026 PRO\gcad.exe"
    StartArguments = @()
    ProgId = "Gcad.Application"
    ProcessName = "gcad"
    Plugin = "src\CadPeta.Plugin.Ijcad\bin\Debug\net8.0-windows\CadPeta.Plugin.Ijcad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    GradientRepresentation = "VectorPaths"
    MinGradientPathCount = 200
    SetFileDia = $true
    ComActivate = $false
  }
  BricscadV26 = @{
    Exe = "C:\Program Files\Bricsys\BricsCAD V26 ja_JP\bricscad.exe"
    StartArguments = @("/Automation")
    ProgId = "BricscadApp.AcadApplication.26.0"
    ProcessName = "bricscad"
    Plugin = "src\CadPeta.Plugin.Bricscad\bin\Debug\net8.0-windows\CadPeta.Plugin.Bricscad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    GradientRepresentation = "VectorPaths"
    MinGradientPathCount = 200
    SetFileDia = $true
    ComActivate = $false
  }
}

$config = $products[$Product]
# DataExport（PETADATADIAG）は範囲プロンプトを持たず選択から始まり、成果物は SVG＋diag.json のみ。
$commandName = if ($Mode -eq "DataExport") { "PETADATADIAG" } else { "PETADIAG" }
$promptInputs = if ($Mode -eq "DataExport") { @("_ALL", "", "") } else { @("O", "_ALL", "", "") }
$requiresEmf = ($Mode -ne "DataExport")
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$exe = [IO.Path]::GetFullPath($config.Exe)
$plugin = [IO.Path]::GetFullPath((Join-Path $repo $config.Plugin))
$source = (Resolve-Path $SourceDwg).Path
$evidenceLog = Join-Path $OutRoot "gradient_compat_e2e.log"

foreach ($required in @($exe, $plugin, $source)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルが見つかりません: $required" }
}
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
$dwg = Join-Path $OutRoot "gradient_e2e.dwg"
Copy-Item -LiteralPath $source -Destination $dwg -Force

function Write-Evidence([string]$message) {
  $line = "{0:o} {1}" -f (Get-Date), $message
  [IO.File]::AppendAllText($evidenceLog, $line + [Environment]::NewLine, [Text.Encoding]::UTF8)
  Write-Output $line
}

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

function Test-CadIdle($acadState, $document) {
  if ($null -ne $acadState) { return [bool]$acadState.IsQuiescent }
  return [string]::IsNullOrWhiteSpace([string]$document.GetVariable("CMDNAMES"))
}

function Wait-CadIdle($acadState, $document, [string]$phase, [int]$timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    if (Test-CadIdle $acadState $document) {
      Write-Evidence "IDLE phase=$phase"
      return
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "CAD コマンドが処理 '$phase' で $timeoutSeconds 秒以内に待機状態になりませんでした"
}

function Wait-CommandStarted($document, [string]$commandName, [string]$evidenceKind, [int]$timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    $commands = [string]$document.GetVariable("CMDNAMES")
    $lastPrompt = [string]$document.GetVariable("LASTPROMPT")
    $started = if ($evidenceKind -eq "RangePrompt") {
      $lastPrompt -match $RangePromptKeywordOrderPattern
    } else {
      $commands -match [regex]::Escape($commandName)
    }
    if ($started) {
      Write-Evidence "COMMAND_STARTED name=$commandName evidence=$evidenceKind CMDNAMES=$commands LASTPROMPT=$lastPrompt"
      return
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  Write-CommandStateEvidence $document "command start timeout: $commandName"
  throw "CAD コマンド '$commandName' は $timeoutSeconds 秒以内に開始証拠 '$evidenceKind' を示しませんでした"
}

function Wait-FileExists([string]$path, [string]$phase, [int]$timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
      Write-Evidence "FILE_READY phase=$phase path=$path"
      return
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "処理 '$phase' の期待ファイルが $timeoutSeconds 秒以内に作成されませんでした: $path"
}

function Get-RegisteredProgIdsForExe([string]$expectedExe) {
  $result = @()
  foreach ($key in Get-ChildItem "HKLM:\SOFTWARE\Classes" | Where-Object {
    $_.PSChildName -like "PCAD_AC_X.AcadApplication.*"
  }) {
    $clsid = (Get-ItemProperty -LiteralPath (Join-Path $key.PSPath "CLSID") -ErrorAction SilentlyContinue).'(default)'
    if (-not $clsid) { continue }
    $server = (Get-ItemProperty -LiteralPath "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$clsid\LocalServer32" -ErrorAction SilentlyContinue).'(default)'
    if (-not $server) { continue }
    $serverText = $server.Trim()
    $serverExe = if ($serverText -match '^"([^"]+\.exe)"') {
      $Matches[1]
    } elseif ($serverText -match '^([^\s]+\.exe)') {
      $Matches[1]
    } else {
      continue
    }
    if ([string]::Equals([IO.Path]::GetFullPath($serverExe), $expectedExe, [StringComparison]::OrdinalIgnoreCase)) {
      $result += $key.PSChildName
    }
  }
  return @($result | Sort-Object -Unique)
}

function Send-AresPromptInput($shell, [int]$processId, [string[]]$inputs) {
  $deadline = (Get-Date).AddSeconds($CommandStartTimeoutSeconds)
  while ((Get-Date) -lt $deadline -and -not $shell.AppActivate($processId)) {
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  if (-not $shell.AppActivate($processId)) { throw "ARES プロセスをアクティブ化できませんでした: PID=$processId" }
  Start-Sleep -Milliseconds 750
  foreach ($input in $inputs) {
    if ($input.Length -gt 0) { $shell.SendKeys($input) }
    $shell.SendKeys("{ENTER}")
    Start-Sleep -Milliseconds 500
  }
  $described = ($inputs | ForEach-Object { if ($_.Length -gt 0) { $_ } else { "ENTER" } }) -join ","
  Write-Evidence "ARES_UI_INPUT sent PID=$processId sequence=$described"
}

function Write-CommandStateEvidence($document, [string]$phase) {
  foreach ($variableName in @("CMDNAMES", "LASTPROMPT", "ERRNO")) {
    try {
      $value = [string]$document.GetVariable($variableName)
      Write-Evidence "COMMAND_STATE phase=$phase $variableName=$value"
    } catch {
      Write-Evidence "COMMAND_STATE phase=$phase $variableName=unavailable error=$($_.Exception.Message)"
    }
  }
}

function Find-AndActivateDocument($application, [string]$expectedPath, [int]$timeoutSeconds) {
  $expectedFullPath = [IO.Path]::GetFullPath($expectedPath)
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    $documents = $application.Documents
    for ($index = 0; $index -lt $documents.Count; $index++) {
      $candidate = $documents.Item($index)
      $candidatePath = [string]$candidate.FullName
      if ($candidatePath -and [string]::Equals([IO.Path]::GetFullPath($candidatePath), $expectedFullPath,
        [StringComparison]::OrdinalIgnoreCase)) {
        $candidate.Activate()
        Write-Evidence "DOCUMENT activated Name=$($candidate.Name) FullName=$candidatePath Count=$($documents.Count)" | Out-Null
        return $candidate
      }
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  $visibleDocuments = @()
  $documents = $application.Documents
  for ($index = 0; $index -lt $documents.Count; $index++) {
    $candidate = $documents.Item($index)
    $visibleDocuments += "Name=$($candidate.Name),FullName=$($candidate.FullName)"
  }
  throw "開いた図面 '$expectedFullPath' が $timeoutSeconds 秒以内に見つかりませんでした。図面一覧=$($visibleDocuments -join '; ')"
}

function Wait-Artifacts($document, [datetime]$startedAt, [string]$commandStartKind) {
  $deadline = (Get-Date).AddSeconds($CommandCompletionTimeoutSeconds)
  $unregisteredDeadline = $startedAt.AddSeconds($UnregisteredCommandTimeoutSeconds)
  $stable = 0
  $lastSignature = ""
  while ((Get-Date) -lt $deadline) {
    # ARES の .NET コマンド実行中は COM が拒否される。成果物の安定生成だけを待つ。
    $commands = if ($commandStartKind -eq "AresUi") { "" } else { [string]$document.GetVariable("CMDNAMES") }
    $svg = @(Get-ChildItem -LiteralPath $OutRoot -Filter "*.svg" | Where-Object {
      $_.Name -notlike "*.stage*.svg" -and $_.LastWriteTime -ge $startedAt
    } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    if ($svg.Count -eq 1) {
      $emfPath = [IO.Path]::ChangeExtension($svg[0].FullName, ".emf")
      $diagPath = $svg[0].FullName + ".diag.json"
      $emfReady = (-not $requiresEmf) -or (Test-Path -LiteralPath $emfPath)
      if ($emfReady -and (Test-Path -LiteralPath $diagPath) -and [string]::IsNullOrWhiteSpace($commands)) {
        $emfLength = if ($requiresEmf) { (Get-Item -LiteralPath $emfPath).Length } else { 0 }
        $signature = "{0}:{1}:{2}" -f $svg[0].Length, $emfLength, (Get-Item -LiteralPath $diagPath).Length
        if ($signature -eq $lastSignature) { $stable++ } else { $stable = 1; $lastSignature = $signature }
        if ($stable -ge $StableFilePollCount) {
          return @($svg[0].FullName, $(if ($requiresEmf) { $emfPath } else { $null }), $diagPath)
        }
      }
    }
    if ($commandStartKind -eq "CmdNames" -and (Get-Date) -ge $unregisteredDeadline -and
        [string]::IsNullOrWhiteSpace($commands)) {
      throw "$commandName が成果物なしで $UnregisteredCommandTimeoutSeconds 秒以内にコマンド待機へ戻りました。プラグイン読込またはコマンド登録に失敗しています"
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "$commandName 成果物が $CommandCompletionTimeoutSeconds 秒以内に完成しませんでした"
}

$application = $null
$candidateApplication = $null
$document = $null
$acadState = $null
$uiShell = $null
$startedProcess = $null
$connectedProgId = $null
$comActivatedPid = 0
$ownedPid = 0
$messageFilterRegistered = $false
$oldSecureLoad = $null
$oldFileDia = $null
$completed = $false
$cleanupFailures = @()
try {
  Write-Evidence "START Product=$Product Exe=$exe Plugin=$plugin Source=$source"
  $windowStation = [CadPeta.CompatNativeWindow]::WindowStationName()
  $desktop = [CadPeta.CompatNativeWindow]::DesktopName()
  Write-Evidence "DESKTOP WindowStation=$windowStation Desktop=$desktop"
  if ($windowStation -ne $ExpectedWindowStation -or $desktop -ne $ExpectedDesktop) {
    throw "対話デスクトップが必要です。期待=$ExpectedWindowStation\$ExpectedDesktop、実際=$windowStation\$desktop"
  }
  if ($PSVersionTable.PSEdition -ne "Desktop") {
    throw "Marshal.GetActiveObject のため Windows PowerShell 5.1 が必要です。実際の PSEdition=$($PSVersionTable.PSEdition)、Version=$($PSVersionTable.PSVersion)"
  }
  if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) { throw "STA スレッドが必要です" }
  if ($config.CommandStartKind -eq "AresUi") {
    # .NETコマンド開始後はARESがCOM呼出しを拒否するため、UI入力用COMは先に生成する。
    $uiShell = New-Object -ComObject WScript.Shell
    if ($null -eq $uiShell) { throw "ARES 起動前に WScript.Shell を作成できませんでした" }
  }
  [CadPeta.CompatMessageFilter]::RetryWindowMilliseconds = $ComRetryWindowMilliseconds
  [CadPeta.CompatMessageFilter]::RetryDelayMilliseconds = $ComRetryDelayMilliseconds
  [CadPeta.CompatMessageFilter]::Register()
  $messageFilterRegistered = $true

  $existingProcesses = @(Get-Process -Name $config.ProcessName -ErrorAction SilentlyContinue)
  if ($existingProcesses.Count -gt 0) {
    throw "既存の $($config.ProcessName) プロセスがあり、起動プロセスを一意に所有できません: PID=$($existingProcesses.Id -join ',')"
  }
  if ($config.DiscoverProgId) {
    $progIds = @(Get-RegisteredProgIdsForExe $exe)
    if ($progIds.Count -eq 0) { throw "'$exe' を指す登録済み ARES ProgID がありません" }
    Write-Evidence "PROGID candidates=$($progIds -join ',') source=HKLM"
    $startedProcess = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -PassThru
    Write-Evidence "PROCESS visible-start PID=$($startedProcess.Id)"
    $deadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and $null -eq $candidateApplication) {
      foreach ($progId in $progIds) {
        try {
          $candidateApplication = [Runtime.InteropServices.Marshal]::GetActiveObject($progId)
          $connectedProgId = $progId
          break
        } catch {}
      }
      if ($null -eq $candidateApplication) { Start-Sleep -Milliseconds $PollMilliseconds }
    }
    if ($null -eq $candidateApplication) {
      throw "ARES が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした。候補=$($progIds -join ',')。表示中のライセンスまたはバージョンダイアログを確認してください"
    }
  } elseif ($config.ComActivate) {
    # ARES は冷起動した exe を ROT へ登録しない場合がある。LocalServer32 の正規
    # ProgID を COM activation し、起動直後の RPC_E_CALL_REJECTED は下の再試行で待つ。
    $candidateApplication = New-Object -ComObject $config.ProgId
    Write-Evidence "COM activation requested ProgId=$($config.ProgId)"
    $pidDeadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
    while ((Get-Date) -lt $pidDeadline -and $comActivatedPid -eq 0) {
      $activated = @(Get-Process -Name $config.ProcessName -ErrorAction SilentlyContinue)
      if ($activated.Count -eq 1) { $comActivatedPid = $activated[0].Id }
      elseif ($activated.Count -gt 1) { throw "COM 起動で複数の $($config.ProcessName) プロセスが開始されました: PID=$($activated.Id -join ',')" }
      else { Start-Sleep -Milliseconds $PollMilliseconds }
    }
    if ($comActivatedPid -eq 0) { throw "COM 起動で $GuiStartupTimeoutSeconds 秒以内に $($config.ProcessName) が開始されませんでした" }
    Write-Evidence "COM activation process PID=$comActivatedPid"
  } else {
    $startedProcess = if ($config.StartArguments.Count -gt 0) {
      Start-Process -FilePath $exe -ArgumentList $config.StartArguments -PassThru -WindowStyle Hidden
    } else {
      Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
    }
    Write-Evidence "PROCESS started PID=$($startedProcess.Id)"
    $deadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and $null -eq $candidateApplication) {
      try { $candidateApplication = [Runtime.InteropServices.Marshal]::GetActiveObject($config.ProgId) } catch { Start-Sleep -Milliseconds $PollMilliseconds }
    }
    if ($null -eq $candidateApplication) {
      $hint = if ($Product -like "Ares*") {
        " Verify that one floating license is free; a hidden read-only/license modal prevents ROT registration. Diagnose with visible startup."
      } else { "" }
      throw "COM オブジェクト $($config.ProgId) が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした。$hint"
    }
  }
  $deadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
  $hwnd = 0
  while ((Get-Date) -lt $deadline -and $hwnd -eq 0) {
    try { $candidateApplication.Visible = $true; $hwnd = [int64]$candidateApplication.HWND }
    catch { Start-Sleep -Milliseconds $PollMilliseconds }
  }
  if ($hwnd -eq 0) { throw "COM アプリケーションが $GuiStartupTimeoutSeconds 秒以内に自動操作可能になりませんでした" }
  [uint32]$pidValue = 0
  [void][CadPeta.CompatNativeWindow]::GetWindowThreadProcessId([IntPtr]$hwnd, [ref]$pidValue)
  $candidatePid = [int]$pidValue
  if ($null -ne $startedProcess -and $candidatePid -ne $startedProcess.Id) {
    throw "今回開始していないプロセスへ COM 接続しました。開始 PID=$($startedProcess.Id)、接続 PID=$candidatePid"
  }
  $actualExe = (Get-Process -Id $candidatePid).Path
  if (-not [string]::Equals([IO.Path]::GetFullPath($actualExe), $exe, [StringComparison]::OrdinalIgnoreCase)) {
    throw "COM が想定外の実行ファイルへ接続しました。期待='$exe'、実際='$actualExe'"
  }
  $application = $candidateApplication
  $candidateApplication = $null
  $ownedPid = $candidatePid
  if (-not $connectedProgId) { $connectedProgId = $config.ProgId }
  Write-Evidence "COM attached ProgId=$connectedProgId HWND=$hwnd PID=$ownedPid Version=$($application.Version)"

  if ($ProbeOnly) {
    Write-Evidence "ROT_PROBE completed Product=$Product"
    $completed = $true
    return
  }

  [void]$application.Documents.Open($dwg)
  $document = Find-AndActivateDocument $application $dwg $GuiStartupTimeoutSeconds
  try { $acadState = $application.GetAcadState(); Write-Evidence "ACADSTATE available" }
  catch { Write-Evidence "ACADSTATE unavailable=$($_.Exception.Message); fallback=CMDNAMES" }
  $secureLoadEvidence = "unavailable"
  try {
    $oldSecureLoad = $document.GetVariable("SECURELOAD")
    $document.SetVariable("SECURELOAD", [int16]0)
    $secureLoadEvidence = "$oldSecureLoad->0"
  } catch {
    $oldSecureLoad = $null
    Write-Evidence "SYSVAR SECURELOAD unavailable=$($_.Exception.Message)"
  }
  if ($config.SetFileDia) {
    $oldFileDia = $document.GetVariable("FILEDIA")
    $document.SetVariable("FILEDIA", [int16]0)
    Write-Evidence "SYSVARS SECURELOAD=$secureLoadEvidence FILEDIA=$oldFileDia->0"
  } else {
    Write-Evidence "SYSVARS SECURELOAD=$secureLoadEvidence FILEDIA=unchanged"
  }
  Wait-CadIdle $acadState $document "before plugin load" $GuiStartupTimeoutSeconds

  $pluginForCommand = $plugin -replace '\\','/'
  # 既定は (command "._LOADDOTNETAPPLICATION" "<path>") の引数渡し（2024/2027 で実績のある形。
  # 挙動を変えない）。ただし ARES 2020/2023 はこの引数渡しではファイル名プロンプトが引数を
  # 受け取らず入力待ちのまま止まる（2026-07-21 2020 実測）。該当版だけ RawTextLoad で
  # 生テキスト＋CR の形へ切り替える（この形はファイル名プロンプトがコマンド行に出る
  # FILEDIA=0 が前提のため、該当版は SetFileDia=$true にしてある）
  $loadCommand = if ($config.LoadKind -eq "LoadDotNetApplication") {
    if ($config.RawTextLoad) { "._LOADDOTNETAPPLICATION`r$pluginForCommand" }
    else { "(command `"._LOADDOTNETAPPLICATION`" `"$pluginForCommand`")" }
  } elseif ($config.LoadKind -eq "DirectNetload") {
    "_.NETLOAD`r$pluginForCommand"
  } else {
    "(command `"_.NETLOAD`" `"$pluginForCommand`")"
  }
  $loadMarker = Join-Path $OutRoot "plugin_load_queue.marker"
  if (Test-Path -LiteralPath $loadMarker) { Remove-Item -LiteralPath $loadMarker -Force }
  $loadMarkerForLisp = $loadMarker -replace '\\','/'
  $markerCommand = "(progn (setq cadpeta:marker (open `"$loadMarkerForLisp`" `"w`")) (write-line `"load-attempt-completed`" cadpeta:marker) (close cadpeta:marker))"
  Write-Evidence "LOAD begin kind=$($config.LoadKind)"
  $document.SendCommand($loadCommand + "`r")
  $document.SendCommand($markerCommand + "`r")
  Wait-FileExists $loadMarker "plugin load queue completion" $LoadQueueTimeoutSeconds
  Write-Evidence "LOAD queue completed"
  Write-CommandStateEvidence $document "after plugin load queue"

  $artifactStart = Get-Date
  $promptBatch = ($promptInputs -join "`r") + "`r"
  Write-Evidence "$commandName begin selection=ALL scale=default mode=$Mode"
  if ($config.CommandStartKind -eq "AresUi") {
    $document.SendCommand("_.$commandName`r")
    Send-AresPromptInput $uiShell $ownedPid $promptInputs
  } elseif ($config.CommandStartKind -eq "SingleBatch") {
    $document.SendCommand("_.$commandName`r" + $promptBatch)
    Write-Evidence "COMMAND_BATCH_COMPLETED name=$commandName terminator=CR"
  } else {
    $document.SendCommand("_.$commandName`r")
    Wait-CommandStarted $document $commandName $config.CommandStartKind $CommandStartTimeoutSeconds
    $document.SendCommand($promptBatch)
  }
  $artifacts = Wait-Artifacts $document $artifactStart $config.CommandStartKind
  if ($config.CommandStartKind -eq "AresUi") {
    Write-Evidence "IDLE phase=after $commandName evidence=stable-artifacts"
  } else {
    Wait-CadIdle $acadState $document "after $commandName" $CommandCompletionTimeoutSeconds
  }

  # Windows PowerShell 5.1 の既定 ANSI 読み込みでは BOM なし UTF-8 の日本語が
  # 誤復号され、偶然生成された '<' により正常な SVG を不正 XML と判定する。
  $svgText = Get-Content -LiteralPath $artifacts[0] -Raw -Encoding UTF8
  $imageCount = ([regex]::Matches($svgText, "<image\b")).Count
  $pngCount = ([regex]::Matches($svgText, "data:image/png")).Count
  $pathCount = ([regex]::Matches($svgText, "<path\b")).Count
  if ($Mode -eq "DataExport") {
    # 構造SVG書き出しは CADPeta 自身がグラデーションを PNG 画像へラスタ化する。
    if ($imageCount -ne $ExpectedGradientImageCount -or $pngCount -ne $ExpectedGradientImageCount) {
      throw "構造SVGのグラデーション画像数が想定外です。期待=$ExpectedGradientImageCount、実際=画像${imageCount}件・PNG${pngCount}件"
    }
  } elseif ($config.GradientRepresentation -eq "VectorPaths") {
    if ($imageCount -ne 0 -or $pathCount -lt $config.MinGradientPathCount) {
      throw "PDF のグラデーション表現が想定外です。期待=画像0件かつパス$($config.MinGradientPathCount)件以上、実際=画像${imageCount}件・パス${pathCount}件"
    }
  } elseif ($imageCount -ne $ExpectedGradientImageCount) {
    throw "SVG 画像数が想定外です。期待=$ExpectedGradientImageCount、実際=$imageCount"
  }
  $diag = Get-Content -LiteralPath $artifacts[2] -Raw -Encoding UTF8 | ConvertFrom-Json
  $fallbackDiagnostics = @($diag | Where-Object {
    ([string]$_.type -match "縮退|fallback") -or ([string]$_.targets -match "縮退|fallback")
  })
  if ($fallbackDiagnostics.Count -gt 0) { throw "診断にグラデーション縮退記録が含まれています: $($fallbackDiagnostics.Count)件" }
  Write-Evidence "ARTIFACT SVG=$($artifacts[0]) BYTES=$((Get-Item $artifacts[0]).Length) IMAGES=$imageCount PNG=$pngCount PATHS=$pathCount MODE=$Mode"
  if ($requiresEmf) {
    Write-Evidence "ARTIFACT EMF=$($artifacts[1]) BYTES=$((Get-Item $artifacts[1]).Length)"
  }
  Write-Evidence "ARTIFACT DIAG=$($artifacts[2]) BYTES=$((Get-Item $artifacts[2]).Length) ENTRIES=$(@($diag).Count)"
  $completed = $true
} catch {
  Write-Evidence "ERROR $($_.Exception.Message)"
  throw
} finally {
  if ($null -ne $document) {
    # sysvar 復元は読み戻しで確認し、失敗は $cleanupFailures 経由で最終 throw へ集約する
    foreach ($restore in @(
      @{ Name = "SECURELOAD"; Value = $oldSecureLoad },
      @{ Name = "FILEDIA"; Value = $oldFileDia })) {
      if ($null -eq $restore.Value) { continue }
      try {
        $document.SetVariable($restore.Name, $restore.Value)
        $readBack = $document.GetVariable($restore.Name)
        if ("$readBack" -ne "$($restore.Value)") { throw "再読値=$readBack" }
        Write-Evidence "RESTORE $($restore.Name)=[$($restore.Value)] verified"
      } catch {
        $cleanupFailures += "$($restore.Name) 復元失敗: $($_.Exception.Message)"
        Write-Evidence "RESTORE $($restore.Name) error=$($_.Exception.Message)"
      }
    }
  }
  if ($null -ne $application) {
    try {
      while ($application.Documents.Count -gt 0) {
        $closingDocument = $application.Documents.Item($application.Documents.Count - 1)
        $closingName = [string]$closingDocument.Name
        $closingDocument.Close($false)
        Write-Evidence "CLOSE ok Name=$closingName SaveChanges=false"
        if ([Runtime.InteropServices.Marshal]::IsComObject($closingDocument)) {
          [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($closingDocument)
        }
      }
    } catch { Write-Evidence "CLOSE error=$($_.Exception.Message)" }
  }
  if ($null -ne $application) { try { $application.Quit(); Write-Evidence "QUIT ok" } catch { Write-Evidence "QUIT error=$($_.Exception.Message)" } }
  if ($messageFilterRegistered) { try { [CadPeta.CompatMessageFilter]::Revoke(); Write-Evidence "MSGFILTER revoked" } catch { Write-Evidence "MSGFILTER revoke error=$($_.Exception.Message)" } }
  foreach ($comObject in @($uiShell, $acadState, $document, $application, $candidateApplication)) {
    if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
      try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) } catch {}
    }
  }
  [GC]::Collect(); [GC]::WaitForPendingFinalizers()
  if ($ownedPid -gt 0) {
    $deadline = (Get-Date).AddSeconds($ProcessExitTimeoutSeconds)
    while ((Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds $PollMilliseconds }
    if (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) {
      if ($AllowForceStop) { Write-Evidence "FORCE_STOP explicitly-allowed owned PID=$ownedPid"; Stop-Process -Id $ownedPid -Force }
      else {
        $cleanupFailures += "owned PID=$ownedPid remained after Quit"
        Write-Evidence "RESIDUAL owned PID=$ownedPid force-stop=disabled"
      }
    }
  }
  if ($null -ne $startedProcess -and $startedProcess.Id -ne $ownedPid -and
      (Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue)) {
    if ($AllowForceStop) { Write-Evidence "FORCE_STOP explicitly-allowed started PID=$($startedProcess.Id)"; Stop-Process -Id $startedProcess.Id -Force }
    else {
      $cleanupFailures += "started PID=$($startedProcess.Id) remained"
      Write-Evidence "RESIDUAL started PID=$($startedProcess.Id) force-stop=disabled"
    }
  }
  if ($comActivatedPid -gt 0 -and $comActivatedPid -ne $ownedPid -and
      (Get-Process -Id $comActivatedPid -ErrorAction SilentlyContinue)) {
    if ($AllowForceStop) { Write-Evidence "FORCE_STOP explicitly-allowed COM-activated PID=$comActivatedPid"; Stop-Process -Id $comActivatedPid -Force }
    else {
      $cleanupFailures += "COM-activated PID=$comActivatedPid remained"
      Write-Evidence "RESIDUAL COM-activated PID=$comActivatedPid force-stop=disabled"
    }
  }
  Write-Evidence "END completed=$completed"
  if ($completed -and $cleanupFailures.Count -gt 0) {
    throw "強制停止を使わない CAD 後始末に失敗しました: $($cleanupFailures -join '; ')"
  }
}
