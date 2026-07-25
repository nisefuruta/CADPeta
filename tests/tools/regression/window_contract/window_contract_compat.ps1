# 窓プロットの座標契約を互換CAD（ARES／IJCAD／BricsCAD）で実測する駆動ハーネス。
#
# accoreconsole を持たない互換CADは COM で GUI を駆動する。CAD の起動・接続・後始末の作法は
# tests/tools/pdf-emf/gradient_compat_e2e.ps1 で実測済みの形をそのまま使う（対話デスクトップ、
# Windows PowerShell 5.1 Desktop／STA、プロセス所有の照合、Close(false)→Quit、残留の報告）。
# 計測器は各製品 SDK へ移植した WindowContractProbe.<製品>（製品コード非参照）。
# ケース定義は window_contract_cases.ps1（唯一の情報源）をドットソースで読む。
#
# 1ケースの流れ:
#   1. 計測条件（UCS・ビュー）をパラメータファイル経由で計測器へ渡し CPWINSETUCS／CPWINSETVIEW で設定
#      （GUI は1プロセスで複数ケースを流すため、起動時に固定されるプロセス環境変数では渡せない）
#   2. CAD 自身の trans に UCS→WCS・UCS→DCS を答えさせて記録する（変換の向きを推測しない一次情報）
#   3. CPWINPROBE が pick 2点から3候補（生値／UCS→WCS／WCS→DCS）の窓プロット PDF を発行し JSON を記録
#   4. オラクル: CAD 自身の -PLOT に同じ2点を与え、ページ設定へ格納された窓（DXF 48/49/140/141）を読む
#
# 使い方（Windows PowerShell 5.1 で実行する）:
#   powershell -Sta -File window_contract_compat.ps1 -Product BricscadV26 -ProbeOnly
#   powershell -Sta -File window_contract_compat.ps1 -Product BricscadV26 -Cases G,J,M
#
# 規律（破ると環境が壊れる。tests/tools/README.md を基準とする）:
# - SECURELOAD／FILEDIA は変更したら必ず復元する。
# - 起動時の未保存図面を含め、全図面を Close(false) してから Quit する。保存確認を残さない。
# - 自分が起動し実行ファイルまで照合した PID だけを終了対象にする。既定では強制終了しない。
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("Ares2024", "Ares2027", "Ijcad2026", "BricscadV26")]
  [string]$Product,
  [string[]]$Cases = @('A', 'G', 'J', 'M', 'T90', 'T180', 'T270', 'T360', 'U90', 'U180', 'U270', 'U360'),
  [string]$Dwg,
  [string]$OutRoot = "$env:TEMP\CadPeta_window_contract_compat",
  [switch]$ProbeOnly,
  [switch]$SkipOracle,
  [switch]$SkipProbe,
  [switch]$AllowForceStop
)
$ErrorActionPreference = "Stop"

$GuiStartupTimeoutSeconds = 120
# ARES 2027 は単独起動だとメインウィンドウ非表示のままスピンし ROT 登録されないことがある
# （2026-07-17 実測。終了直後の再起動で再現）。この秒数だけ待って ROT 未登録なら2つ目を起動して
# 1つ目の表示・ROT 登録を促す（利用者の経験則を2起動診断で実測確認）。
$SecondaryLaunchDelaySeconds = 30
$ProbeJsonTimeoutSeconds = 180
$OracleIdleTimeoutSeconds = 120
$ComRetryWindowMilliseconds = 60000
$ComRetryDelayMilliseconds = 100
$ProcessExitTimeoutSeconds = 20
$PollMilliseconds = 500
$LoadQueueTimeoutSeconds = 90
$ExpectedWindowStation = "WinSta0"
$ExpectedDesktop = "Default"
$WindowMatchToleranceDrawingUnits = 0.001

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
. "$PSScriptRoot\window_contract_cases.ps1"

# powershell -File 経由では配列引数が "A,G,J,M" の1要素で届くため、カンマ区切りも受け付ける
$Cases = @($Cases | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# オラクル（-PLOT）の応答列。トークンの意味: {DEVICE}=出力デバイス名、{P1}/{P2}=窓の2点
# （LISP の点リストとして渡す）、{PDF}=出力ファイル、'' は Enter（既定値）。
# AutoCAD 2026 ja-JP で実機確定した並び（window_contract_oracle.ps1）を初期値とし、
# 製品でプロンプト列が違う場合はこの表だけを直す。キーワードは _ 付き英語で言語非依存にする。
$oracleResponsesAutocadStyle = @(
  '_Yes',      # 詳細印刷設定を行いますか
  '',          # レイアウト名（既定=現在）
  '{DEVICE}',  # 出力デバイス名
  '',          # 用紙サイズ（Enter=既定）。プロンプトは表示名を要求し、正式名との対応は製品ごとに
               # 違って当てられない（IJCAD 実測: "A4"・"User66011" とも「見つかりません」で応答列が
               # 崩れた）。ここでは名指しせず、オラクルが実際に使った用紙の正式名（DXF 4）を後で
               # 読み戻して計測器側へ CPWIN_MEDIA で渡し、両者の用紙を揃える
  '',          # 印刷尺度などの既定継続
  '',
  '',
  '_Window',   # 印刷領域
  '{P1}',      # 最初のコーナー
  '{P2}',      # もう一方のコーナー
  '_Fit',      # 尺度
  '_Center',   # 印刷オフセット
  '',          # 印刷スタイルを使用して印刷しますか（既定）
  '',          # 印刷スタイル テーブル名（既定）
  '',          # 線の太さを使用して印刷しますか（既定）
  '',          # シェーディング印刷の設定（既定）
  '{PDF}',     # 出力ファイル名
  '_Yes',      # 変更をページ設定に保存しますか
  '_Yes'       # 印刷を続行しますか
)

# ARES の -PLOT は -PRINT の別名で、プロンプト列が AutoCAD 型と違う（ares.chm の -Print と
# 2024 実機で確認）。範囲キーワードは Window でなく Specify。プリンタは既定値が DWG 保存値
# （AutoCAD の .pc3 名）のままだと「見つかりません」を繰り返すため必ず名指しする。
$oracleResponsesAresStyle = @(
  '_Yes',      # 印刷オプションの詳細設定?
  '',          # シート名（既定=Model）
  '{DEVICE}',  # プリンタ名（ARES 内蔵の PDF）
  '',          # 用紙サイズ（Enter=既定。正式名は読み戻して計測器側へ CPWIN_MEDIA で渡す）
  '',          # 単位（既定=ミリメートル）
  '',          # 方向（既定=横）
  '',          # 上下を逆にして印刷しますか（既定=いいえ）
  '_Specify',  # 印刷範囲（指定=2点）
  '{P1}',      # 始点コーナー
  '{P2}',      # 反対側のコーナー
  '_Fit',      # 印刷尺度
  '_Center',   # オフセット
  '_No',       # 印刷スタイル テーブルを使用しますか（名前プロンプトへの分岐を避け明示で No）
  '',          # 線の太さを使用しますか（既定）
  '',          # シェード モード（既定=表示どおり）
  '{PDF}',     # 出力ファイル名（内蔵 PDF プリンタは印刷ファイルの確認なしで直接聞かれる）
  '_Yes',      # 印刷設定をシートに適用しますか（=ページ設定へ保存。DXF 48/49 が書かれる）
  '_Yes'       # 今すぐ印刷しますか
)

$products = @{
  Ares2024 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2024\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.23.1"
    ProcessName = "ARESS"
    Probe = "tests\tools\regression\window_contract\WindowContractProbe.Ares\bin\Release\net48\WindowContractProbe.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    ProbeInputKind = "AresUi"
    # ARES は FILEDIA=1 のままだと LOADDOTNETAPPLICATION のファイル名プロンプトが
    # (command) の引数を受け取らず入力待ちのまま止まる（2027 実測）。COM で保存→0→復元する
    SetFileDia = $true
    DiscoverProgId = $true
    PdfDevice = "PDF"
    OracleCommand = "-PLOT"
    OracleResponses = $oracleResponsesAresStyle
  }
  Ares2027 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2027\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.26.8"
    ProcessName = "ARESS"
    Probe = "tests\tools\regression\window_contract\WindowContractProbe.Ares\bin\Release\net8.0-windows\WindowContractProbe.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    ProbeInputKind = "AresUi"
    # ARES は FILEDIA=1 のままだと LOADDOTNETAPPLICATION のファイル名プロンプトが
    # (command) の引数を受け取らず入力待ちのまま止まる（2027 実測）。COM で保存→0→復元する
    SetFileDia = $true
    DiscoverProgId = $true
    PdfDevice = "PDF"
    OracleCommand = "-PLOT"
    OracleResponses = $oracleResponsesAresStyle
  }
  Ijcad2026 = @{
    Exe = "C:\Program Files\ITJP\IJCAD 2026 PRO\gcad.exe"
    StartArguments = @()
    ProgId = "Gcad.Application"
    ProcessName = "gcad"
    Probe = "tests\tools\regression\window_contract\WindowContractProbe.Ijcad\bin\Release\net8.0-windows\WindowContractProbe.Ijcad.dll"
    LoadKind = "DirectNetload"
    ProbeInputKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
    PdfDevice = "DWG To PDF.pc3"
    OracleCommand = "-PLOT"
    OracleResponses = $oracleResponsesAutocadStyle
  }
  BricscadV26 = @{
    Exe = "C:\Program Files\Bricsys\BricsCAD V26 ja_JP\bricscad.exe"
    StartArguments = @("/Automation")
    ProgId = "BricscadApp.AcadApplication.26.0"
    ProcessName = "bricscad"
    Probe = "tests\tools\regression\window_contract\WindowContractProbe.Bricscad\bin\Release\net8.0-windows\WindowContractProbe.Bricscad.dll"
    LoadKind = "DirectNetload"
    ProbeInputKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
    PdfDevice = "Print As PDF.pc3"
    OracleCommand = "-PLOT"
    OracleResponses = $oracleResponsesAutocadStyle
  }
}

$config = $products[$Product]
$exe = [IO.Path]::GetFullPath($config.Exe)
$probe = [IO.Path]::GetFullPath((Join-Path $repo $config.Probe))
if (-not $Dwg) { $Dwg = Join-Path $repo "tests\assets\testcharts\TESTCHART2.dwg" }
$source = (Resolve-Path $Dwg).Path
$productRoot = Join-Path $OutRoot $Product
$evidenceLog = Join-Path $productRoot "window_contract_compat.log"

foreach ($required in @($exe, $source)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルが見つかりません: $required" }
}
if (-not $ProbeOnly -and -not (Test-Path -LiteralPath $probe -PathType Leaf)) {
  throw "計測器が未ビルドです: $probe"
}
foreach ($c in $Cases) {
  if (-not $CadPetaWindowCases.ContainsKey($c)) { throw "未知のケース: $c（共通ケース定義を参照）" }
}
New-Item -ItemType Directory -Force -Path $productRoot | Out-Null
if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
$dwgCopy = Join-Path $productRoot "target.dwg"
Copy-Item -LiteralPath $source -Destination $dwgCopy -Force
$dwgHash = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash
$paramsFile = Join-Path $productRoot "cpwin_params.txt"

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

function Assert-CompletePdf([string]$path, [string]$label) {
  [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
  if ($bytes.Length -lt 64) { throw "$label PDFが短すぎる: $($bytes.Length) bytes" }
  $headLength = [Math]::Min(8, $bytes.Length)
  $head = [Text.Encoding]::ASCII.GetString($bytes, 0, $headLength)
  if (-not $head.StartsWith('%PDF-')) { throw "$label PDFヘッダがない: $path" }
  $tailStart = [Math]::Max(0, $bytes.Length - 2048)
  $tail = [Text.Encoding]::ASCII.GetString($bytes, $tailStart, $bytes.Length - $tailStart)
  if ($tail -notmatch 'startxref\s+(\d+)\s+%%EOF\s*$') { throw "$label PDF終端が完結していない: $path" }
  $xrefOffset = [int64]$Matches[1]
  if ($xrefOffset -lt 0 -or $xrefOffset -ge $bytes.Length) {
    throw "$label PDFのstartxrefが範囲外: offset=$xrefOffset length=$($bytes.Length)"
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
  throw "開いた図面 '$expectedFullPath' が $timeoutSeconds 秒以内に見つかりませんでした"
}

# ARES は .NET コマンドのプロンプト待ち中に COM を拒否するため、pick 点は UI キー入力で与える
# （gradient_compat_e2e.ps1 で実測済みの作法）。
function Send-UiPromptInput($shell, [int]$processId, [string[]]$inputs) {
  $deadline = (Get-Date).AddSeconds(10)
  while ((Get-Date) -lt $deadline -and -not $shell.AppActivate($processId)) {
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  if (-not $shell.AppActivate($processId)) { throw "CAD プロセスをアクティブ化できませんでした: PID=$processId" }
  Start-Sleep -Milliseconds 750
  foreach ($input in $inputs) {
    if ($input.Length -gt 0) { $shell.SendKeys($input) }
    $shell.SendKeys("{ENTER}")
    Start-Sleep -Milliseconds 500
  }
  $described = ($inputs | ForEach-Object { if ($_.Length -gt 0) { $_ } else { "ENTER" } }) -join ","
  Write-Evidence "UI_INPUT sent PID=$processId sequence=$described"
}

function Read-VariableSafe($document, [string]$name) {
  try { return [string]$document.GetVariable($name) } catch { return "<取得失敗: $($_.Exception.Message)>" }
}

function Wait-CommandIdle($document, [string]$phase, [int]$timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    $commands = Read-VariableSafe $document "CMDNAMES"
    if (-not $commands.StartsWith("<取得失敗") -and [string]::IsNullOrWhiteSpace($commands)) {
      Write-Evidence "IDLE phase=$phase"
      return $true
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  Write-Evidence "IDLE_TIMEOUT phase=$phase CMDNAMES=$(Read-VariableSafe $document 'CMDNAMES') LASTPROMPT=$(Read-VariableSafe $document 'LASTPROMPT')"
  return $false
}

# パラメータファイル（KEY=VALUE）: 計測器がコマンド実行のたびに読み直す。
# プロセス環境変数は CAD 起動時に固定されるため、ケースごとの条件はここで渡す。
# $mediaCanonical はオラクルが実際に使った用紙の正式名（読み戻し後の2回目の書き込みで与える）。
function Write-CaseParams([string]$caseName, $cfg, [string]$caseWork, [string]$mediaCanonical) {
  $lines = @(
    "CPWIN_OUT=$caseWork",
    "CPWIN_CASE=$caseName",
    "CPWIN_UCS_ORIGIN=$($cfg.Ucs[0])",
    "CPWIN_UCS_XDIR=$($cfg.Ucs[1])",
    "CPWIN_UCS_YDIR=$($cfg.Ucs[2])",
    "CPWIN_VIEW_TARGET=$($cfg.Target)",
    "CPWIN_VIEW_DIR=$($cfg.Dir)",
    "CPWIN_VIEW_TWIST_DEG=$($cfg.Twist)",
    "CPWIN_VIEW_CENTER=$($cfg.Center)",
    "CPWIN_VIEW_HEIGHT=$($cfg.H)",
    "CPWIN_VIEW_WIDTH=$($cfg.W)",
    "CPWIN_IN1=$($cfg.P1)",
    "CPWIN_IN2=$($cfg.P2)"
  )
  if ($mediaCanonical) { $lines += "CPWIN_MEDIA=$mediaCanonical" }
  [IO.File]::WriteAllLines($paramsFile, $lines, (New-Object Text.UTF8Encoding($false)))
}

# オラクル応答列を LISP の (command ...) へ組み立てる。点はカンマ文字列でなく点リストで渡す
# （カンマ入り文字列は座標として解釈されない実測がある）。
# $mediaName を与えると用紙サイズの既定（Enter）を実名指定へ置き換える。計測器が使った用紙と
# オラクルの用紙を揃えないと、Fit 尺度が変わり画素比較が領域の一致を判定できない（BricsCAD 実測）。
function Build-OracleLisp([string]$caseWork, $cfg, [string]$mediaName) {
  $pdfPath = (Join-Path $caseWork "oracle.pdf") -replace '\\', '/'
  $markerPath = (Join-Path $caseWork "oracle_command_done.txt") -replace '\\', '/'
  $args = @()
  foreach ($token in $config.OracleResponses) {
    switch ($token) {
      '{DEVICE}' { $args += '"{0}"' -f $config.PdfDevice }
      '{MEDIA}' {
        # 用紙プロンプトは表示名を受け取るため、正式名のアンダースコアを空白へ直して渡す。
        # 計測器の用紙が取れていないときは既定（Enter）
        if ($mediaName) { $args += '"{0}"' -f ($mediaName -replace '_', ' ') } else { $args += '""' }
      }
      '{P1}' { $args += (ConvertTo-LispPoint $cfg.P1) }
      '{P2}' { $args += (ConvertTo-LispPoint $cfg.P2) }
      '{PDF}' { $args += '"{0}"' -f $pdfPath }
      default { $args += '"{0}"' -f $token }
    }
  }
  # "_."+コマンド名（例 _.-PLOT）: _ は言語非依存の正式名、. は再定義を無視する組み込み呼び出し
  $command = '(command "_.{0}" {1})' -f $config.OracleCommand, ($args -join ' ')
  # コマンドの成否に依らず完了マーカーを書く（(command) の失敗は残りの評価を巻き込むため、
  # マーカーは別式で書き、失敗時も後段の読み戻しへ進めるようにする）
  return @(
    $command,
    "(progn (setq cpom (open ""$markerPath"" ""w"")) (write-line ""oracle-command-returned"" cpom) (close cpom))"
  )
}

# ページ設定へ格納された窓（DXF 48/49/140/141）・印刷種別（74）・用紙の正式名（4）を
# 全レイアウト分読み戻す。用紙名は空白を含み得るため行末に置く。
# 本体ファイルの存在は完了の証拠にならない（open 直後から存在し、close 前に読むと途中までしか
# 無い。ARES 2024 実測で media が空に化けた）。close 後に別の完了マーカーを書き、それを待つ。
function Build-StoredReadbackLisp([string]$caseWork) {
  $storedPath = (Join-Path $caseWork "oracle_stored.txt") -replace '\\', '/'
  $donePath = (Join-Path $caseWork "oracle_stored_done.txt") -replace '\\', '/'
  return @(
    '(defun cplastg (e k / v) (setq v nil) (foreach p e (if (= (car p) k) (setq v (cdr p)))) v)',
    '(defun cpnum (e k) (if (assoc k e) (rtos (cdr (assoc k e)) 2 6) "<なし>"))',
    "(setq cprf (open ""$storedPath"" ""w""))",
    '(setq cpld (cdr (assoc -1 (dictsearch (namedobjdict) "ACAD_LAYOUT"))))',
    '(setq cpe (dictnext cpld T))',
    '(while cpe (write-line (strcat "LAYOUT=" (cplastg cpe 1) " 74=" (cpnum cpe 74) " 48=" (cpnum cpe 48) " 49=" (cpnum cpe 49) " 140=" (cpnum cpe 140) " 141=" (cpnum cpe 141) " MEDIA=" (if (cplastg cpe 4) (cplastg cpe 4) "<なし>")) cprf) (setq cpe (dictnext cpld)))',
    '(close cprf)',
    "(progn (setq cprd (open ""$donePath"" ""w"")) (write-line ""stored-done"" cprd) (close cprd))"
  )
}

# CAD 自身の trans に UCS→WCS・UCS→DCS を答えさせる（一次情報。向き・符号を自分で導出しない）。
# 完了は本体でなく close 後の別マーカーで判定する（Build-StoredReadbackLisp と同じ理由）。
function Build-TransLisp([string]$caseWork, $cfg) {
  $transPath = (Join-Path $caseWork "trans.txt") -replace '\\', '/'
  $donePath = (Join-Path $caseWork "trans_done.txt") -replace '\\', '/'
  $lispP1 = ConvertTo-LispPoint $cfg.P1
  $lispP2 = ConvertTo-LispPoint $cfg.P2
  return @(
    '(defun cpfmt (p) (strcat (rtos (car p) 2 6) "," (rtos (cadr p) 2 6) "," (rtos (caddr p) 2 6)))',
    "(setq cptf (open ""$transPath"" ""w""))",
    "(foreach cpp (list $lispP1 $lispP2) (write-line (strcat ""UCS="" (cpfmt cpp) "" WCS="" (cpfmt (trans cpp 1 0)) "" DCS="" (cpfmt (trans cpp 1 2))) cptf))",
    '(write-line (strcat "VIEWTWIST=" (rtos (getvar "VIEWTWIST") 2 9)) cptf)',
    '(write-line (strcat "TARGET=" (cpfmt (getvar "TARGET"))) cptf)',
    '(write-line (strcat "TARGETWCS=" (cpfmt (trans (getvar "TARGET") 1 0))) cptf)',
    '(write-line (strcat "VIEWDIR=" (cpfmt (getvar "VIEWDIR"))) cptf)',
    '(write-line (strcat "VIEWDIRWCS=" (cpfmt (trans (getvar "VIEWDIR") 1 0 T))) cptf)',
    '(close cptf)',
    "(progn (setq cptd (open ""$donePath"" ""w"")) (write-line ""trans-done"" cptd) (close cptd))"
  )
}

function Send-LispLines($document, [string[]]$lines, [string]$phase) {
  foreach ($line in $lines) {
    $document.SendCommand($line + "`r")
  }
  Write-Evidence "LISP_SENT phase=$phase lines=$($lines.Count)"
}

function Parse-TransFile([string]$path) {
  $points = @()
  foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
    if ($line -match '^UCS=([^ ]+) WCS=([^ ]+) DCS=([^ ]+)$') {
      $points += @{ Ucs = $Matches[1]; Wcs = $Matches[2]; Dcs = $Matches[3] }
    }
  }
  return $points
}

function Read-TransMetadata([string]$path) {
  $metadata = @{}
  foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
    if ($line -match '^(VIEWTWIST|TARGET|TARGETWCS|VIEWDIR|VIEWDIRWCS)=(.+)$') {
      $metadata[$Matches[1]] = $Matches[2]
    }
  }
  return $metadata
}

function Test-CsvPointMatch([string]$actual, [string]$expected) {
  $a = $actual -split ','
  $e = $expected -split ','
  if ($a.Count -ne 3 -or $e.Count -ne 3) { return $false }
  for ($i = 0; $i -lt 3; $i++) {
    $av = [double]::Parse($a[$i], [Globalization.CultureInfo]::InvariantCulture)
    $ev = [double]::Parse($e[$i], [Globalization.CultureInfo]::InvariantCulture)
    if ([Math]::Abs($av - $ev) -gt $WindowMatchToleranceDrawingUnits) { return $false }
  }
  return $true
}

function Test-AngleMatch([string]$actualRadians, [double]$expectedDegrees) {
  $actual = [double]::Parse($actualRadians, [Globalization.CultureInfo]::InvariantCulture)
  $expected = $expectedDegrees * [Math]::PI / 180.0
  $period = 2.0 * [Math]::PI
  $delta = [Math]::Abs((($actual - $expected + [Math]::PI) % $period + $period) % $period - [Math]::PI)
  return $delta -le 1.0e-8
}

function ConvertTo-XyPair([string]$csv) {
  $parts = $csv -split ','
  return @([double]::Parse($parts[0], [Globalization.CultureInfo]::InvariantCulture),
           [double]::Parse($parts[1], [Globalization.CultureInfo]::InvariantCulture))
}

# 2点（"x,y,z" 文字列）から min/max の窓 4値 [minX minY maxX maxY] を作る
function ConvertTo-Window($pointA, $pointB) {
  $a = ConvertTo-XyPair $pointA
  $b = ConvertTo-XyPair $pointB
  return @([Math]::Min($a[0], $b[0]), [Math]::Min($a[1], $b[1]),
           [Math]::Max($a[0], $b[0]), [Math]::Max($a[1], $b[1]))
}

function Test-WindowMatch($w1, $w2) {
  if ($null -eq $w1 -or $null -eq $w2) { return $false }
  for ($i = 0; $i -lt 4; $i++) {
    if ([Math]::Abs([double]$w1[$i] - [double]$w2[$i]) -gt $WindowMatchToleranceDrawingUnits) { return $false }
  }
  return $true
}

$application = $null
$candidateApplication = $null
$document = $null
$uiShell = $null
$startedProcess = $null
$secondaryProcess = $null
$connectedProgId = $null
$ownedPid = 0
$messageFilterRegistered = $false
$oldSecureLoad = $null
$oldFileDia = $null
$oldOsMode = $null
$oldTrustedPaths = $null
$savedParamsEnv = $env:CPWIN_PARAMS
$completed = $false
$cleanupFailures = @()
$caseSummaries = @()
try {
  Write-Evidence "START Product=$Product Exe=$exe Probe=$probe Source=$source Cases=$($Cases -join ',')"
  $windowStation = [CadPeta.CompatNativeWindow]::WindowStationName()
  $desktop = [CadPeta.CompatNativeWindow]::DesktopName()
  Write-Evidence "DESKTOP WindowStation=$windowStation Desktop=$desktop"
  if ($windowStation -ne $ExpectedWindowStation -or $desktop -ne $ExpectedDesktop) {
    throw "対話デスクトップが必要です。期待=$ExpectedWindowStation\$ExpectedDesktop、実際=$windowStation\$desktop"
  }
  if ($PSVersionTable.PSEdition -ne "Desktop") {
    throw "Marshal.GetActiveObject のため Windows PowerShell 5.1 が必要です。実際の PSEdition=$($PSVersionTable.PSEdition)"
  }
  if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) { throw "STA スレッドが必要です" }
  # pick 点の UI 入力と、詰まったプロンプトの ESC 解除に使う。CAD 起動前に生成する
  $uiShell = New-Object -ComObject WScript.Shell
  [CadPeta.CompatMessageFilter]::RetryWindowMilliseconds = $ComRetryWindowMilliseconds
  [CadPeta.CompatMessageFilter]::RetryDelayMilliseconds = $ComRetryDelayMilliseconds
  [CadPeta.CompatMessageFilter]::Register()
  $messageFilterRegistered = $true

  $existingProcesses = @(Get-Process -Name $config.ProcessName -ErrorAction SilentlyContinue)
  if ($existingProcesses.Count -gt 0) {
    throw "既存の $($config.ProcessName) プロセスがあり、起動プロセスを一意に所有できません: PID=$($existingProcesses.Id -join ',')"
  }

  # 計測器はパラメータファイルをコマンド実行のたびに読み直す。パスは起動前に環境変数で固定する
  $env:CPWIN_PARAMS = $paramsFile

  if ($config.DiscoverProgId) {
    $progIds = @(Get-RegisteredProgIdsForExe $exe)
    if ($progIds.Count -eq 0) { throw "'$exe' を指す登録済み ProgID がありません" }
    Write-Evidence "PROGID candidates=$($progIds -join ',') source=HKLM"
    $startedProcess = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -PassThru
    Write-Evidence "PROCESS visible-start PID=$($startedProcess.Id)"
    # 単独起動で ROT 未登録が続くとき2つ目を起動して1つ目の起動を促す（ARES 2027 対策）。
    # 2つ目も所有プロセスとして終了時に停止対象へ含める（自分が起動し exe を照合したもの）
    $secondaryLaunchAt = (Get-Date).AddSeconds($SecondaryLaunchDelaySeconds)
    $deadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and $null -eq $candidateApplication) {
      foreach ($progId in $progIds) {
        try {
          $candidateApplication = [Runtime.InteropServices.Marshal]::GetActiveObject($progId)
          $connectedProgId = $progId
          break
        } catch {}
      }
      if ($null -eq $candidateApplication) {
        if ($null -eq $secondaryProcess -and (Get-Date) -gt $secondaryLaunchAt) {
          $secondaryProcess = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -PassThru
          Write-Evidence "PROCESS secondary-start PID=$($secondaryProcess.Id)（単独起動不全の回避。1つ目の ROT 登録を促す）"
        }
        Start-Sleep -Milliseconds $PollMilliseconds
      }
    }
    if ($null -eq $candidateApplication) {
      throw "CAD が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした。候補=$($progIds -join ',')。表示中のライセンス／版ダイアログを確認してください"
    }
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
      throw "COM オブジェクト $($config.ProgId) が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした"
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
  $ownedStartIds = @($startedProcess.Id)
  if ($null -ne $secondaryProcess) { $ownedStartIds += $secondaryProcess.Id }
  if ($null -ne $startedProcess -and $candidatePid -notin $ownedStartIds) {
    throw "今回開始していないプロセスへ COM 接続しました。開始 PID=$($ownedStartIds -join ',')、接続 PID=$candidatePid"
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

  [void]$application.Documents.Open($dwgCopy)
  $document = Find-AndActivateDocument $application $dwgCopy $GuiStartupTimeoutSeconds
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
  # 定常オブジェクトスナップを外す。スクリプト・(command) 経由の座標入力は定常スナップに
  # 吸着し得る（BricsCAD 実測: -PLOT の窓コーナーが近傍の図形頂点へ吸着し、格納値が入力と
  # ずれた）。計測は入力座標そのものを評価するため、実行中だけ 0 にして復元する
  try {
    $oldOsMode = $document.GetVariable("OSMODE")
    $document.SetVariable("OSMODE", [int16]0)
    Write-Evidence "SYSVAR OSMODE=$oldOsMode->0"
  } catch {
    $oldOsMode = $null
    Write-Evidence "SYSVAR OSMODE unavailable=$($_.Exception.Message)"
  }
  # 計測器のフォルダを信頼できる場所へ一時追加する（未署名 DLL の信頼確認ダイアログが
  # 読み込みを黙って阻む。ARES 2024 実機で利用者が確認）。必ず復元する
  try {
    $oldTrustedPaths = $document.GetVariable("TRUSTEDPATHS")
    $probeDir = [IO.Path]::GetDirectoryName($probe)
    $newTrustedPaths = if ([string]::IsNullOrEmpty($oldTrustedPaths)) { $probeDir } else { "$oldTrustedPaths;$probeDir" }
    $document.SetVariable("TRUSTEDPATHS", $newTrustedPaths)
    Write-Evidence "SYSVAR TRUSTEDPATHS=[$oldTrustedPaths]->[$newTrustedPaths]"
  } catch {
    $oldTrustedPaths = $null
    Write-Evidence "SYSVAR TRUSTEDPATHS unavailable=$($_.Exception.Message)"
  }

  # 図面を開いた直後は再描画等でコマンドを受け付けないことがある。待機状態を確認してから読み込む
  # （実測済みの作法は gradient_compat_e2e.ps1 と同じ。ARES は GetAcadState を持たない版があるため
  # CMDNAMES の空を待機の証拠にする）
  $idleDeadline = (Get-Date).AddSeconds($GuiStartupTimeoutSeconds)
  while ((Get-Date) -lt $idleDeadline) {
    $commands = Read-VariableSafe $document "CMDNAMES"
    if (-not $commands.StartsWith("<取得失敗") -and [string]::IsNullOrWhiteSpace($commands)) { break }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  Write-Evidence "IDLE phase=before probe load CMDNAMES=$(Read-VariableSafe $document 'CMDNAMES')"

  # 計測器の読み込み。完了はマーカーファイルで判定する（読込コマンド自体は結果を返さない）
  $probeForCommand = $probe -replace '\\', '/'
  # ARES の LOADDOTNETAPPLICATION は、ファイル名の後に明示の Enter が届かないと読み込まない
  # （(command "._LOADDOTNETAPPLICATION" "<path>") の引数渡しではファイル名プロンプトに
  # 表示されたまま入力待ちで止まる。2027 実測）。生テキストで CR を付けて送る
  $loadCommand = if ($config.LoadKind -eq "LoadDotNetApplication") {
    "._LOADDOTNETAPPLICATION`r$probeForCommand"
  } else {
    "_.NETLOAD`r$probeForCommand"
  }
  $loadMarker = Join-Path $productRoot "probe_load_queue.marker"
  if (Test-Path -LiteralPath $loadMarker) { Remove-Item -LiteralPath $loadMarker -Force }
  $loadMarkerForLisp = $loadMarker -replace '\\', '/'
  $markerCommand = "(progn (setq cadpeta:marker (open `"$loadMarkerForLisp`" `"w`")) (write-line `"load-attempt-completed`" cadpeta:marker) (close cadpeta:marker))"
  Write-Evidence "LOAD begin kind=$($config.LoadKind)"
  $document.SendCommand($loadCommand + "`r")
  $document.SendCommand($markerCommand + "`r")
  Wait-FileExists $loadMarker "probe load queue completion" $LoadQueueTimeoutSeconds
  Write-Evidence "LOAD queue completed"

  foreach ($caseName in $Cases) {
    $cfg = $CadPetaWindowCases[$caseName]
    $caseWork = Join-Path $productRoot $caseName
    if (Test-Path -LiteralPath $caseWork) { Remove-Item -LiteralPath $caseWork -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $caseWork | Out-Null
    Write-CaseParams $caseName $cfg $caseWork
    Write-Evidence "CASE begin name=$caseName params=$paramsFile"

    # 条件設定（モデル空間・UCS・ビュー）。完了はマーカーで待つ
    $setupMarker = (Join-Path $caseWork "setup_done.txt") -replace '\\', '/'
    $document.SendCommand("(setvar `"TILEMODE`" 1)`r")
    $document.SendCommand("CPWINSETUCS`r")
    $document.SendCommand("CPWINSETVIEW`r")
    $document.SendCommand("(progn (setq cpsm (open `"$setupMarker`" `"w`")) (write-line `"setup-done`" cpsm) (close cpsm))`r")
    Wait-FileExists ($setupMarker -replace '/', '\') "case $caseName setup" $LoadQueueTimeoutSeconds

    # trans の一次情報
    Send-LispLines $document (Build-TransLisp $caseWork $cfg) "case $caseName trans"
    Wait-FileExists (Join-Path $caseWork "trans_done.txt") "case $caseName trans" $LoadQueueTimeoutSeconds

    # setup マーカーはハーネス側 LISP が書くため、計測器コマンドが未実行でも完了し得る。
    # 設定後のシステム変数を期待値と照合し、UCS は UCS→WCS の実変換で確認する。
    # ARES 2024 の trans は VIEWTWIST が直角のとき UCS→DCS を恒等変換として返すため、
    # raw/wcs/dcs の相違だけを設定成功の根拠にはしない。
    $transPath = Join-Path $caseWork "trans.txt"
    $transCheck = Parse-TransFile $transPath
    if ($transCheck.Count -lt 2) { throw "ケース $caseName の trans が2点読めない" }
    $metadata = Read-TransMetadata $transPath
    if (-not $metadata.ContainsKey('VIEWTWIST') -or -not (Test-AngleMatch $metadata.VIEWTWIST ([double]$cfg.Twist)) -or
        -not $metadata.ContainsKey('TARGETWCS') -or -not (Test-CsvPointMatch $metadata.TARGETWCS $cfg.Target) -or
        -not $metadata.ContainsKey('VIEWDIRWCS') -or -not (Test-CsvPointMatch $metadata.VIEWDIRWCS $cfg.Dir)) {
      Write-Evidence "SETUP_INEFFECTIVE case=$caseName view-metadata mismatch"
      throw "ケース $caseName のビュー設定が期待値と一致しない。計測器 DLL の読込・実行結果を確認すること"
    }
    $expectsNonIdentityUcs = -not (
      $cfg.Ucs[0] -eq '0,0,0' -and $cfg.Ucs[1] -eq '1,0,0' -and $cfg.Ucs[2] -eq '0,1,0')
    if ($expectsNonIdentityUcs) {
      $rawCheck = ConvertTo-Window $transCheck[0].Ucs $transCheck[1].Ucs
      $wcsCheck = ConvertTo-Window $transCheck[0].Wcs $transCheck[1].Wcs
      if (Test-WindowMatch $rawCheck $wcsCheck) {
        Write-Evidence "SETUP_INEFFECTIVE case=$caseName raw=wcs（UCSが変わっていない）"
        throw "ケース $caseName の UCS 設定が期待値と一致しない。計測器 DLL の読込・実行結果を確認すること"
      }
    }

    # オラクルを先に流す（全応答が既定＝製品差の影響が最小の形）。完走したらページ設定へ
    # 格納された用紙の正式名を読み戻し、計測器へ CPWIN_MEDIA で渡して同じ用紙で発行させる。
    # 失敗（oracle.pdf 不在）ならこのケースの後続を止める（用紙を揃えられず比較が成立しない）
    $probeJson = Join-Path $caseWork "$caseName.probe.json"
    $oraclePdf = Join-Path $caseWork "oracle.pdf"
    $oracleOk = [bool]$SkipOracle
    $oracleMedia = $null
    if (-not $SkipOracle) {
      $oracleMarker = Join-Path $caseWork "oracle_command_done.txt"
      Send-LispLines $document (Build-OracleLisp $caseWork $cfg $null) "case $caseName oracle"
      Wait-FileExists $oracleMarker "case $caseName oracle command" $OracleIdleTimeoutSeconds
      if (-not (Wait-CommandIdle $document "case $caseName after oracle" 30)) {
        # 応答列がずれてプロンプトに残っている。ESC で解除して証跡を残す（成果物は不採用）
        if ($uiShell.AppActivate($ownedPid)) {
          Start-Sleep -Milliseconds 500
          $uiShell.SendKeys("{ESC}{ESC}")
          Start-Sleep -Milliseconds 500
        }
        Write-Evidence "ORACLE_PROMPT_STUCK case=$caseName（応答列を製品に合わせて直すこと）"
      }
      if (Test-Path -LiteralPath $oraclePdf) {
        $oracleOk = $true
        Send-LispLines $document (Build-StoredReadbackLisp $caseWork) "case $caseName stored readback"
        Wait-FileExists (Join-Path $caseWork "oracle_stored_done.txt") "case $caseName stored readback" $LoadQueueTimeoutSeconds
        # LISP の open/write-line はシステム ANSI（CP932）で書くため Default で読む
        foreach ($line in Get-Content -LiteralPath (Join-Path $caseWork "oracle_stored.txt") -Encoding Default) {
          if ($line -match '74=4(\.0+)? ' -and $line -match 'MEDIA=(.+)$' -and $Matches[1] -ne '<なし>') {
            $oracleMedia = $Matches[1].Trim()
          }
        }
        Write-Evidence "ORACLE_OK case=$caseName media=$oracleMedia"
      } else {
        Write-Evidence "ORACLE_FAILED case=$caseName oracle.pdf が生成されていない（-PLOT の応答列を製品に合わせて直すこと）"
      }
    }

    if (-not $SkipProbe) {
      if ($oracleOk -or $SkipOracle) {
        # オラクルが使った用紙の正式名を渡して計測器を同じ用紙で発行させる
        Write-CaseParams $caseName $cfg $caseWork $oracleMedia
        if ($config.ProbeInputKind -eq "AresUi") {
          # 座標はキー入力でなく LISP の点リストで1行にして与える。キー入力はフォーカスと
          # 動的入力の解釈に依存し、GetCorner が "150,110" を基点相対 (200,150) と解釈したり、
          # 入力が届かず GetPoint のまま止まる取りこぼしを実測した（ARES 2024）。
          # (command) の点データは解釈が一意で、コマンド開始前に全入力が確定する
          $document.SendCommand("(command ""_CPWINPROBE"" $(ConvertTo-LispPoint $cfg.P1) $(ConvertTo-LispPoint $cfg.P2))`r")
        } else {
          $document.SendCommand("CPWINPROBE`r$($cfg.P1)`r$($cfg.P2)`r")
        }
        Wait-FileExists $probeJson "case $caseName probe json" $ProbeJsonTimeoutSeconds
        # JSON は計測器の finally で最後に書かれるため、存在すれば PDF 発行も終わっている
        [void](Wait-CommandIdle $document "case $caseName after probe" $OracleIdleTimeoutSeconds)
        # pick 点が意図した絶対座標で API から返ったことの検証（入力の解釈は製品で違い得る）
        try {
          $probeData = Get-Content -LiteralPath $probeJson -Raw | ConvertFrom-Json
          $expected1 = ConvertTo-XyPair $cfg.P1
          $expected2 = ConvertTo-XyPair $cfg.P2
          $api1 = $probeData.apiPoint1
          $api2 = $probeData.apiPoint2
          if ($probeData.fatal) { throw "計測器が fatal を報告した: $($probeData.fatal)" }
          if ($null -eq $api1 -or $null -eq $api2) {
            throw "API 戻り点が欠落した"
          } elseif ([Math]::Abs($api1[0] - $expected1[0]) -gt 0.001 -or [Math]::Abs($api1[1] - $expected1[1]) -gt 0.001 -or
                    [Math]::Abs($api2[0] - $expected2[0]) -gt 0.001 -or [Math]::Abs($api2[1] - $expected2[1]) -gt 0.001) {
            throw "API 戻り点が不一致: api1=$($api1 -join ',') api2=$($api2 -join ',') 期待=$($cfg.P1) / $($cfg.P2)"
          } else {
            Write-Evidence "PROBE_POINTS_OK case=$caseName"
          }
        } catch { throw "ケース $caseName の probe JSON／入力点検査に失敗: $($_.Exception.Message)" }
      } else {
        Write-Evidence "PROBE_SKIPPED case=$caseName オラクル不成立のため計測器の発行を行わない"
      }
    }

    # 判定材料の要約（判定は窓4値の数値一致。生PDFの画素比較は compare_pages.py で別途行う）
    $summary = [ordered]@{ Case = $caseName }
    $transFile = Join-Path $caseWork "trans.txt"
    $trans = if (Test-Path -LiteralPath $transFile) { Parse-TransFile $transFile } else { @() }
    if ($trans.Count -ge 2) {
      $summary.RawWindow = ConvertTo-Window $trans[0].Ucs $trans[1].Ucs
      $summary.WcsWindow = ConvertTo-Window $trans[0].Wcs $trans[1].Wcs
      $summary.DcsWindow = ConvertTo-Window $trans[0].Dcs $trans[1].Dcs
    }
    $storedFile = Join-Path $caseWork "oracle_stored.txt"
    $oraclePdf = Join-Path $caseWork "oracle.pdf"
    if (-not $SkipOracle -and -not (Test-Path -LiteralPath $oraclePdf)) {
      # -PLOT が完走していない（応答列のずれ等）。格納値は前ケースの残骸であり得るため判定しない
      $summary.OracleMatches = '<オラクル不成立: oracle.pdf が生成されていない>'
      Write-Evidence "ORACLE_PDF_MISSING case=$caseName"
    } elseif (Test-Path -LiteralPath $storedFile) {
      foreach ($line in Get-Content -LiteralPath $storedFile -Encoding Default) {
        if ($line -match '74=4(\.0+)? ' -and $line -match '48=([-0-9.]+) 49=([-0-9.]+) 140=([-0-9.]+) 141=([-0-9.]+)') {
          $summary.StoredWindow = @([double]$Matches[1], [double]$Matches[2], [double]$Matches[3], [double]$Matches[4])
        }
      }
    }
    if ($summary.Contains('StoredWindow')) {
      $verdicts = @()
      if (Test-WindowMatch $summary.StoredWindow $summary.RawWindow) { $verdicts += 'raw' }
      if (Test-WindowMatch $summary.StoredWindow $summary.WcsWindow) { $verdicts += 'ucs2wcs' }
      if (Test-WindowMatch $summary.StoredWindow $summary.DcsWindow) { $verdicts += 'wcs2dcs' }
      $summary.OracleMatches = if ($verdicts.Count -gt 0) { $verdicts -join ',' } else { '<どの候補とも不一致>' }
      # trans と独立な第二判定: 計測器（SDK の view 読み＋行列計算）の候補窓との照合。
      # ARES 2024 の LISP trans 1→2 はねじれ付きビューで DCS でなく WCS を返す（実測）ため、
      # trans 由来の判定だけでは偽の不一致になる。probe 側の判定を併記して突き合わせる
      if (Test-Path -LiteralPath $probeJson) {
        try {
          $probeData = Get-Content -LiteralPath $probeJson -Raw | ConvertFrom-Json
          if ($probeData.fatal) { throw "計測器 fatal: $($probeData.fatal)" }
          $probeVerdicts = @()
          foreach ($kind in 'raw', 'ucs2wcs', 'wcs2dcs') {
            $candidateWindow = $probeData.candidates.$kind.window
            if ($null -ne $candidateWindow -and (Test-WindowMatch $summary.StoredWindow $candidateWindow)) { $probeVerdicts += $kind }
          }
          $summary.OracleMatchesProbe = if ($probeVerdicts.Count -gt 0) { $probeVerdicts -join ',' } else { '<どの候補とも不一致>' }
          if ('wcs2dcs' -notin $probeVerdicts) {
            throw "-PLOT格納窓が計測器のDCS候補と一致しない: verdict=$($summary.OracleMatchesProbe)"
          }
        } catch { throw "ケース $caseName のオラクル／probe照合に失敗: $($_.Exception.Message)" }
      }
    }
    if (-not $SkipOracle -and -not $summary.Contains('StoredWindow')) {
      throw "ケース $caseName の -PLOT 格納窓を読み取れない"
    }
    if (-not $SkipOracle) { Assert-CompletePdf $oraclePdf "ケース $caseName のオラクル" }
    $caseSummaries += [pscustomobject]$summary
    Write-Evidence "CASE end name=$caseName"
  }
  $completed = $true
} catch {
  Write-Evidence "ERROR $($_.Exception.Message)"
  throw
} finally {
  if ($null -ne $document) {
    foreach ($restore in @(
      @{ Name='SECURELOAD'; Value=$oldSecureLoad },
      @{ Name='FILEDIA'; Value=$(if ($null -eq $oldFileDia) { $null } else { [int16]1 }) },
      @{ Name='OSMODE'; Value=$oldOsMode }, @{ Name='TRUSTEDPATHS'; Value=$oldTrustedPaths })) {
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
  $env:CPWIN_PARAMS = $savedParamsEnv
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
    } catch { $cleanupFailures += "CLOSE失敗: $($_.Exception.Message)"; Write-Evidence "CLOSE error=$($_.Exception.Message)" }
  }
  if ($null -ne $application) { try { $application.Quit(); Write-Evidence "QUIT ok" } catch { $cleanupFailures += "QUIT失敗: $($_.Exception.Message)"; Write-Evidence "QUIT error=$($_.Exception.Message)" } }
  if ($messageFilterRegistered) { try { [CadPeta.CompatMessageFilter]::Revoke(); Write-Evidence "MSGFILTER revoked" } catch { Write-Evidence "MSGFILTER revoke error=$($_.Exception.Message)" } }
  foreach ($comObject in @($uiShell, $document, $application, $candidateApplication)) {
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
  # 起動したが ROT 接続先にならなかったプロセス（1つ目・2つ目のうち捨てになった方）の後始末。
  # 自分が起動し exe で起動した所有プロセスのみが対象
  foreach ($extra in @($startedProcess, $secondaryProcess)) {
    if ($null -ne $extra -and $extra.Id -ne $ownedPid -and
        (Get-Process -Id $extra.Id -ErrorAction SilentlyContinue)) {
      if ($AllowForceStop) { Write-Evidence "FORCE_STOP explicitly-allowed extra PID=$($extra.Id)"; Stop-Process -Id $extra.Id -Force }
      else {
        $cleanupFailures += "extra PID=$($extra.Id) remained"
        Write-Evidence "RESIDUAL extra PID=$($extra.Id) force-stop=disabled"
      }
    }
  }
  if (Test-Path -LiteralPath $dwgCopy) {
    $hashAfter = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash
    Write-Evidence "DWG_HASH before=$dwgHash after=$hashAfter unchanged=$($hashAfter -eq $dwgHash)"
  }
  Write-Evidence "END completed=$completed"
  if ($caseSummaries.Count -gt 0) {
    Write-Output ""
    Write-Output "=== ケース別の判定材料（$Product） ==="
    foreach ($s in $caseSummaries) {
      Write-Output ("ケース {0}:" -f $s.Case)
      if ($s.PSObject.Properties['RawWindow']) { Write-Output ("  生値の窓    : {0}" -f (($s.RawWindow | ForEach-Object { $_.ToString('F6', [Globalization.CultureInfo]::InvariantCulture) }) -join ', ')) }
      if ($s.PSObject.Properties['WcsWindow']) { Write-Output ("  UCS→WCSの窓 : {0}" -f (($s.WcsWindow | ForEach-Object { $_.ToString('F6', [Globalization.CultureInfo]::InvariantCulture) }) -join ', ')) }
      if ($s.PSObject.Properties['DcsWindow']) { Write-Output ("  UCS→DCSの窓 : {0}" -f (($s.DcsWindow | ForEach-Object { $_.ToString('F6', [Globalization.CultureInfo]::InvariantCulture) }) -join ', ')) }
      if ($s.PSObject.Properties['StoredWindow']) { Write-Output ("  -PLOT格納値 : {0}" -f (($s.StoredWindow | ForEach-Object { $_.ToString('F6', [Globalization.CultureInfo]::InvariantCulture) }) -join ', ')) }
      if ($s.PSObject.Properties['OracleMatches']) { Write-Output ("  格納値と一致(trans基準)  : {0}" -f $s.OracleMatches) }
      if ($s.PSObject.Properties['OracleMatchesProbe']) { Write-Output ("  格納値と一致(計測器基準): {0}" -f $s.OracleMatchesProbe) }
    }
  }
  if ($completed -and $cleanupFailures.Count -gt 0) {
    throw "強制停止を使わない CAD 後始末に失敗しました: $($cleanupFailures -join '; ')"
  }
}
