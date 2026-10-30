[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
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
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-CompatE2ePlan 'gradient_compat_e2e.ps1' $Product $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)

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
    Plugin = "src\CadPeta.Plugin.Ares\bin\ares2020\Release\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    # ARES 2020 の PDF の印刷は、9 形状のグラデーションを 9 枚の画像で出す（2026-10-25 実測。画像 9 件・パス 1 件）。
    # 前の期待（パス 3000 件以上）は 2024 の実測を写したもので、2020 では確かめていなかった
    GradientRepresentation = "Images"
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
    Plugin = "src\CadPeta.Plugin.Ares\bin\ares2023\Release\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    # ARES 2023 の PDF の印刷は、9 形状のグラデーションを 9 枚の画像で出す（2026-10-25 実測。画像 9 件・パス 1 件）。
    # 前の期待（パス 3000 件以上）は 2024 の実測を写したもので、2023 では確かめていなかった
    GradientRepresentation = "Images"
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
    Plugin = "src\CadPeta.Plugin.Ares\bin\Release\net48\CadPeta.Plugin.Ares.dll"
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
    Plugin = "src\CadPeta.Plugin.Ares\bin\Release\net8.0-windows\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresUi"
    # ARES 2027（27.1.1.2150、ODA PDF Export 26.8）の PDF の印刷は、9 形状のグラデーションを 9 枚の画像で出す
    # （2026-10-25 実測。2026-07-15 には、作り直す前の ARES 2027 でパス 3050 件だった。2026-07-15 のコミット 3d894b7 の
    # プラグインでも今の ARES 2027 では画像になり、ARES の側の変化と確かめた）。PETA は印刷と同じ仕上がりなので、画像 9 件を期待する
    GradientRepresentation = "Images"
    SetFileDia = $false
    DiscoverProgId = $true
    ComActivate = $false
  }
  Ijcad2026 = @{
    Exe = "C:\Program Files\ITJP\IJCAD 2026 PRO\gcad.exe"
    StartArguments = @()
    ProgId = "Gcad.Application"
    ProcessName = "gcad"
    Plugin = "src\CadPeta.Plugin.Ijcad\bin\Release\net8.0-windows\CadPeta.Plugin.Ijcad.dll"
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
    Plugin = "src\CadPeta.Plugin.Bricscad\bin\Release\net8.0-windows\CadPeta.Plugin.Bricscad.dll"
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
. "$PSScriptRoot\..\common\test_helpers.ps1"
# 配布物と同じビルド（tests/tools/build_e2e.ps1）の DLL であることを、CAD を起動する前に確かめる
Assert-E2ePluginBuild $plugin
$source = (Resolve-Path $SourceDwg).Path
$evidenceLog = Join-Path $OutRoot "gradient_compat_e2e.log"

foreach ($required in @($exe, $plugin, $source)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルが見つかりません: $required" }
}
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
$dwg = Join-Path $OutRoot "gradient_e2e.dwg"
Copy-Item -LiteralPath $source -Destination $dwg -Force

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


function Wait-GradientArtifacts($document, [datetime]$startedAt, [string]$commandStartKind) {
  $deadline = (Get-Date).AddSeconds($CommandCompletionTimeoutSeconds)
  $unregisteredDeadline = $startedAt.AddSeconds($UnregisteredCommandTimeoutSeconds)
  $stable = 0
  $lastSignature = ""
  while ((Get-Date) -lt $deadline) {
    # ARES の .NET コマンド実行中は COM が拒否される。成果物の安定生成だけを待つ。
    $commands = if ($commandStartKind -eq "AresUi") { "" } else { [string]$document.GetVariable("CMDNAMES") }
    $svg = @(Get-NewestSvgSince $OutRoot $startedAt)
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
$startedProcess = $null
$connectedProgId = $null
$comActivatedPid = 0
$ownedPid = 0
$messageFilterRegistered = $false
$oldFileDia = $null
$completed = $false
$cleanupFailures = @()
$pluginStage = $null
$autoloadState = $null
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
    $startedProcess = Start-CadProcessHidden $exe $config.StartArguments
    Write-Evidence "PROCESS started PID=$($startedProcess.Id)"
    $candidateApplication = Wait-ActiveObject $config.ProgId $GuiStartupTimeoutSeconds
    if ($null -eq $candidateApplication) {
      $hint = if ($Product -like "Ares*") {
        " Verify that one floating license is free; a hidden read-only/license modal prevents ROT registration. Diagnose with visible startup."
      } else { "" }
      throw "COM オブジェクト $($config.ProgId) が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした。$hint"
    }
  }
  $hwnd = Get-CadWindowHandle $candidateApplication $GuiStartupTimeoutSeconds $exe
  [uint32]$pidValue = 0
  [void][CadPeta.CompatNativeWindow]::GetWindowThreadProcessId([IntPtr]$hwnd, [ref]$pidValue)
  $candidatePid = [int]$pidValue
  if ($null -ne $startedProcess -and $candidatePid -ne $startedProcess.Id) {
    throw "今回開始していないプロセスへ COM 接続しました。開始 PID=$($startedProcess.Id)、接続 PID=$candidatePid"
  }
  Assert-ConnectedExecutable $candidatePid $exe
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
  $document = Find-AndActivateDocument $application $dwg $GuiStartupTimeoutSeconds -IncludeDocumentList
  try { $acadState = $application.GetAcadState(); Write-Evidence "ACADSTATE available" }
  catch { Write-Evidence "ACADSTATE unavailable=$($_.Exception.Message); fallback=CMDNAMES" }
  Disable-CadFileDia $document $config.SetFileDia ([ref]$oldFileDia)
  Wait-CadIdle $acadState $document "before plugin load" $GuiStartupTimeoutSeconds

  # 信頼するフォルダへコピーした DLL を読み込み、確認の画面を出さずに無人で進める
  $pluginStage = New-CompatPluginStage $Product $plugin
  Assert-CadPluginFolderTrusted $document $pluginStage
  Write-Evidence "PLUGIN_STAGE plugin=$($pluginStage.Plugin)"
  $pluginForCommand = $pluginStage.Plugin -replace '\\','/'
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
    # ARES は .NET のコマンドの実行中に COM を拒否するので、応答を含めた LISP の (command ...) を 1 回で送る。
    # キー入力（SendKeys）は、前面のウィンドウと日本語入力・Shift の状態で別の文字として届き、
    # 「_ALL」が「\all」で届いて止まった（2026-10-25）。peta_option_smoke_compat.ps1 と同じ方式にする
    $lispArgs = ($promptInputs | ForEach-Object { "`"$_`"" }) -join " "
    $document.SendCommand("(command `"_.$commandName`" $lispArgs)`r")
    Write-Evidence "ARES_COMMAND_LISP sent name=$commandName args=$($promptInputs -join ',')"
  } elseif ($config.CommandStartKind -eq "SingleBatch") {
    $document.SendCommand("_.$commandName`r" + $promptBatch)
    Write-Evidence "COMMAND_BATCH_COMPLETED name=$commandName terminator=CR"
  } else {
    $document.SendCommand("_.$commandName`r")
    Wait-CommandStarted $document $commandName $config.CommandStartKind $CommandStartTimeoutSeconds
    $document.SendCommand($promptBatch)
  }
  $artifacts = Wait-GradientArtifacts $document $artifactStart $config.CommandStartKind
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
    Restore-CadSystemVariables $document @(
      @{ Name = "FILEDIA"; Value = $oldFileDia }) ([ref]$cleanupFailures)
  }
  if ($null -ne $application) { Close-CadApplication $application $ownedPid ([ref]$cleanupFailures) }
  Release-CadComResources $messageFilterRegistered @($acadState, $document, $application, $candidateApplication)
  Stop-OwnedCadProcess $ownedPid $ProcessExitTimeoutSeconds ([ref]$cleanupFailures) -AllowForceStop:$AllowForceStop
  if ($null -ne $startedProcess) {
    Stop-LeftoverCadProcess $startedProcess.Id "started" $ownedPid ([ref]$cleanupFailures) -AllowForceStop:$AllowForceStop
  }
  Stop-LeftoverCadProcess $comActivatedPid "COM-activated" $ownedPid ([ref]$cleanupFailures) -AllowForceStop:$AllowForceStop
  Remove-CompatPluginStage $pluginStage ([ref]$cleanupFailures)
  Resume-CompatProductAutoload $autoloadState $exe ([ref]$cleanupFailures)
  Write-Evidence "END completed=$completed"
  if ($completed -and $cleanupFailures.Count -gt 0) {
    throw "強制停止を使わない CAD 後始末に失敗しました: $($cleanupFailures -join '; ')"
  }
}
