# PETA オプション処理の互換CADスモークを ARES／IJCAD／BricsCAD で実測する。
#
# PETA/PETADIAG のオプション処理は4製品共通の単一実装（src\CadPeta.Plugin.Shared\RangePrompt.cs・
# PetaRunner.cs）で、accoreconsole を持つ AutoCAD 側は全因子マトリクス
# （regression\peta_option_matrix.ps1）で検証済み。互換CADは accoreconsole 相当を持たず COM で
# GUI を駆動するしかないため、出力オプション2ケースに加えて、オブジェクト絞り込みの入力経路を
# 製品ごとの生プロンプト値で確認するスモークに留める。
#
# ケースの入力経路・製品表・COM 駆動と終了処理は次の実証済みハーネスから流用する:
# - 製品表（ProgID・exe パス照合・2起動回避・FILEDIA 保存復元（TRUSTEDPATHS は変えない）・Close(false)→Quit・
#   所有PID管理）: peta_window_rotation_compat.ps1（本ファイルと同じフォルダ）
# - ARES の癖（LOADDOTNETAPPLICATION のファイル名Enter要求・-PLOTのプロンプト列・GetCornerの
#   基点相対解釈・.NETコマンド実行中のCOM拒否・成果物はcloseマーカーでなくファイル安定待ち）:
#   window_contract\window_contract_compat.ps1、window_contract\window_contract_cases.ps1
#   （ConvertTo-LispPoint をドットソースで借用する）
#
# 入力経路の選択（実機実測で確定した手順を適用する）:
# - BricsCAD／IJCAD: 1回の SendCommand へ「コマンド名+キーワード+値」を `r 区切りで積んだバッチ
#   文字列を送る（peta_window_rotation_compat.ps1 の CommandStartKind=SingleBatch、118行目
#   `_.PETADIAG`r$windowP1`r$windowP2`r` と同型）。IJCAD は入力待ち中 SendCommand が戻らないため
#   バッチを分けない。末尾に不要な空Enterを足すと直前コマンドの再実行を招く
#   ため、最後の `r は範囲確定（Enter=全体、または第2コーナー）
#   だけに使い、その後ろへ追加しない。
# - ARES: .NET コマンド（PETA）実行中は COM 入力を拒否する（実測）ため、
#   全入力を (command "_.PETA" ...) の1バッチへ LISP 式として積む。キーボード座標は GetCorner が
#   基点相対と解釈し得るため、窓の2点は "50,40" のような文字列でなく
#   (list 50.0 40.0 0.0) の点リストで渡す（window_contract_compat.ps1 508〜512行と同じ理由）。
#   キーワード確定・空欄Enterは "" を渡す（AutoLISP (command) の標準作法）。
#
# 成果物の完了判定はファイルの存在でなく、SVG＋EMF＋PDF 3点がそろい、かつ（ARES 以外は）
# CMDNAMES が空になった状態でサイズが複数回安定することを見る
# （peta_window_rotation_compat.ps1 の Wait-PetaDiagArtifacts と同型）。
# ARES は .NET コマンド実行中 CMDNAMES を読めないため、ファイル安定のみで判定する。
#
# 使い方（Windows PowerShell 5.1 で実行する）:
#   powershell -Sta -File peta_option_smoke_compat.ps1 -Product BricscadV26
#   powershell -Sta -File peta_option_smoke_compat.ps1 -Product Ares2027 -AllowForceStop
#   powershell -Sta -File peta_option_smoke_compat.ps1 -Product Ijcad2026 -ProbeOnly
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("Ares2020", "Ares2023", "Ares2024", "Ares2027", "Ijcad2026", "BricscadV26")]
  [string]$Product,
  [string]$OutRoot = "$env:TEMP\CadPeta_option_smoke_compat",
  [string]$Config = "Release",   # 配布物と同じビルド（tests/tools/build_e2e.ps1）の構成
  [ValidateSet("a", "b", "o", "object-global", "pickfirst", "postselect", "empty-enter", "current-layer")]
  [string]$CaseName,
  [switch]$ObjectCasesOnly,
  [switch]$ProbeOnly,
  [switch]$SkipPluginLoad,
  [switch]$AllowForceStop
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-CompatE2ePlan 'peta_option_smoke_compat.ps1' $Product $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)

$GuiStartupTimeoutSeconds = 120
# ARES 2027 は単独起動だとメインウィンドウ非表示のままスピンし ROT 登録されないことがある
# （peta_window_rotation_compat.ps1 2026-07-17 実測）。同じ回避を踏襲する。
$SecondaryLaunchDelaySeconds = 30
$CommandCompletionTimeoutSeconds = 300
$ComRetryWindowMilliseconds = 60000
$ComRetryDelayMilliseconds = 100
$ProcessExitTimeoutSeconds = 20
$PollMilliseconds = 500
$StableFilePollCount = 3
$LoadQueueTimeoutSeconds = 90
$ArtifactAfterCommandEndTimeoutSeconds = 15
$ExpectedWindowStation = "WinSta0"
$ExpectedDesktop = "Default"

$LayoutName = "PETA_A3"
# crop_regression.ps1／peta_option_matrix.ps1 の $PaperWin・$ExpectPaperWinM5 と同じ窓・同じ図面。
# PETA のオプション処理は製品非依存の共通実装のため、期待値も AutoCAD 実測とそろえられる。
$WindowP1 = "50,40"
$WindowP2 = "150,110"
$MarginMm = "5"
$ExpectedWindowSvgHeader = 'width="110mm" height="80mm" viewBox="155 108.5 110 80"'

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
# ConvertTo-LispPoint（"x,y"→LISP点リスト文字列）を借用する。ARES の窓2点は文字列座標でなく
# 点リストで渡す必要がある（GetCorner の基点相対解釈を避ける。window_contract_compat.ps1 と同じ理由）
. (Join-Path $repo "tests\tools\regression\window_contract\window_contract_cases.ps1")

$products = @{
  # ARES の net48 世代（2020/2023/2024）は SDK が年式別のため、プラグインも年式別の出力
  # （既定=bin、既定以外=bin\ares<年>）を読み込む。ProgID の版番号は COM API 版
  # （2020=4.4／2023=4.6／2024=23.1／2027=26.8。本機 HKCR 実測）
  Ares2020 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2020\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.4.4"
    ProcessName = "ARESS"
    PluginRelative = "src\CadPeta.Plugin.Ares\bin\ares2020\{0}\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresCommand"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ares2023 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2023\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.4.6"
    ProcessName = "ARESS"
    PluginRelative = "src\CadPeta.Plugin.Ares\bin\ares2023\{0}\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresCommand"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ares2024 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2024\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.23.1"
    ProcessName = "ARESS"
    PluginRelative = "src\CadPeta.Plugin.Ares\bin\{0}\net48\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresCommand"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ares2027 = @{
    Exe = "C:\Program Files\Graebert GmbH\ARES Standard 2027\BIN\ARESS.exe"
    StartArguments = @()
    ProgId = "PCAD_AC_X.AcadApplication.26.8"
    ProcessName = "ARESS"
    PluginRelative = "src\CadPeta.Plugin.Ares\bin\{0}\net8.0-windows\CadPeta.Plugin.Ares.dll"
    LoadKind = "LoadDotNetApplication"
    CommandStartKind = "AresCommand"
    SetFileDia = $true
    DiscoverProgId = $true
  }
  Ijcad2026 = @{
    Exe = "C:\Program Files\ITJP\IJCAD 2026 PRO\gcad.exe"
    StartArguments = @()
    ProgId = "Gcad.Application"
    ProcessName = "gcad"
    PluginRelative = "src\CadPeta.Plugin.Ijcad\bin\{0}\net8.0-windows\CadPeta.Plugin.Ijcad.dll"
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
    PluginRelative = "src\CadPeta.Plugin.Bricscad\bin\{0}\net8.0-windows\CadPeta.Plugin.Bricscad.dll"
    LoadKind = "DirectNetload"
    CommandStartKind = "SingleBatch"
    SetFileDia = $true
    DiscoverProgId = $false
  }
}

# 局所変数は $config でなく $productConfig と名付ける。PowerShell の変数は大文字小文字を
# 区別せず、パラメータ [string]$Config の型制約が変数に残るため、$config へハッシュ表を
# 代入すると文字列 "System.Collections.Hashtable" へ強制変換される（実測でExeが空になった）
$productConfig = $products[$Product]
if ($ObjectCasesOnly -and -not [string]::IsNullOrWhiteSpace($CaseName)) {
  throw "-ObjectCasesOnly と -CaseName は同時に指定できません"
}
$exe = [IO.Path]::GetFullPath($productConfig.Exe)
$plugin = [IO.Path]::GetFullPath((Join-Path $repo ($productConfig.PluginRelative -f $Config)))
$modelCaseNames = @("o", "object-global", "pickfirst", "postselect", "empty-enter", "current-layer")
$modelOnlyRun = $ObjectCasesOnly -or
  (-not [string]::IsNullOrWhiteSpace($CaseName) -and $CaseName -in $modelCaseNames)
$sourceName = if ($modelOnlyRun) { "TESTCHART2.dwg" } else { "TESTCHART2_layout.dwg" }
$source = (Resolve-Path (Join-Path $repo "tests\assets\testcharts\$sourceName")).Path
$productRoot = Join-Path $OutRoot $Product
$evidenceLog = Join-Path $productRoot "peta_option_smoke_compat.log"
$resultsJson = Join-Path $productRoot "results.json"
$promptTrace = Join-Path $productRoot "prompt-trace.jsonl"
$promptTraceRunId = "compat-$Product-$([guid]::NewGuid().ToString('N'))"

foreach ($required in @($exe, $source)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルが見つかりません: $required" }
}
if (-not $ProbeOnly) {
  # 配布物と同じビルド（tests/tools/build_e2e.ps1）の DLL であることを、CAD を起動する前に確かめる
  . "$PSScriptRoot\..\common\test_helpers.ps1"
  Assert-E2ePluginBuild $plugin
}
New-Item -ItemType Directory -Force -Path $productRoot | Out-Null
if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
if (Test-Path -LiteralPath $promptTrace) { Remove-Item -LiteralPath $promptTrace -Force }
$dwgCopy = Join-Path $productRoot "target.dwg"
Copy-Item -LiteralPath $source -Destination $dwgCopy -Force
$dwgHash = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash

. "$PSScriptRoot\..\common\compat_cad_com.ps1"
. "$PSScriptRoot\..\common\test_helpers.ps1"
# Get-JsonLines が不正行で投げる例外の文言の先頭（共通既定の「trace JSONL が不正」とは語尾が異なる）
$JsonLinesInvalidLabel = "trace JSONL が不正です"


function Test-PromptTrace($case, [object[]]$events, [int]$expectedPid) {
  $failures = New-Object System.Collections.ArrayList
  if ($events.Count -eq 0) {
    [void]$failures.Add("生プロンプト trace がない")
    return @($failures)
  }
  if (-not ($events | Where-Object { $_.operation -eq "Command.End" })) {
    [void]$failures.Add("Command.End trace がない")
  }
  # PETA は必ずコマンドラインへ結果か理由を出す。その行が trace に無ければ、失敗の理由を確かめられない
  if (-not ($events | Where-Object { $_.operation -eq "Message" })) {
    [void]$failures.Add("コマンドラインの行（Message）の trace がない")
  }
  foreach ($event in $events) {
    if ([string]$event.runId -cne $promptTraceRunId) {
      [void]$failures.Add("trace run ID が不一致です: $($event.runId)")
    }
    if ([string]$event.caseId -cne $Product) {
      [void]$failures.Add("trace case ID が不一致です: $($event.caseId)")
    }
    if ([int]$event.pid -ne $expectedPid) {
      [void]$failures.Add("trace PID が所有 PID と不一致です: trace=$($event.pid) owned=$expectedPid")
    }
  }

  $windowEvents = @($events | Where-Object { $_.operation -eq "PromptWindow.GetPoint" })
  $selectionEvents = @($events | Where-Object { $_.operation -eq "PromptSelection.GetSelection" })
  if ($case.ExpectSelectionCount -and -not ($selectionEvents | Where-Object { [int]$_.count -eq $case.ExpectSelectionCount })) {
    [void]$failures.Add("選択件数が $($case.ExpectSelectionCount) 件ではありません: $(@($selectionEvents | ForEach-Object { $_.count }) -join ',')")
  }
  $pickfirstEvents = @($events | Where-Object { $_.operation -eq "GetPickfirstSelection.SelectImplied" })
  if ($case.ExpectKeyword) {
    $keywordEvents = @($windowEvents | Where-Object { [string]$_.status -match "(?i)^Keyword$" })
    if ($keywordEvents.Count -eq 0) {
      [void]$failures.Add("'$($case.Token)' が生 Status=Keyword にならない")
    }
    elseif (-not ($keywordEvents | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.stringResult) })) {
      [void]$failures.Add("'$($case.Token)' の生 StringResult が空です")
    }
    elseif (-not ($keywordEvents | Where-Object {
      [string]$_.stringResult -in @("O", "Object", "_Object", "オブジェクト(O)", "Object(O)")
    })) {
      [void]$failures.Add("'$($case.Token)' の生 StringResult が登録済みの O/Object 表記ではありません")
    }
  }
  switch ([string]$case.ExpectSelection) {
    "pickfirst" {
      if ($pickfirstEvents.Count -eq 0) {
        [void]$failures.Add("SelectImplied trace がない")
      }
      elseif (-not ($pickfirstEvents | Where-Object { [int]$_.count -gt 0 })) {
        [void]$failures.Add("先選択件数が 0 です")
      }
      elseif (-not ($pickfirstEvents | Where-Object { [string]$_.status -match "(?i)^OK$" })) {
        [void]$failures.Add("先選択の生 Status が OK ではありません")
      }
    }
    "postselect" {
      if ($selectionEvents.Count -eq 0) {
        [void]$failures.Add("GetSelection trace がない")
      }
      elseif (-not ($selectionEvents | Where-Object { [int]$_.count -gt 0 })) {
        [void]$failures.Add("後選択件数が 0 です")
      }
      elseif (-not ($selectionEvents | Where-Object { [string]$_.status -match "(?i)^OK$" })) {
        [void]$failures.Add("後選択の生 Status が OK ではありません")
      }
    }
    "empty" {
      if ($selectionEvents.Count -eq 0) {
        [void]$failures.Add("空 Enter の GetSelection trace がない")
      }
      elseif ($selectionEvents | Where-Object { [int]$_.count -gt 0 }) {
        [void]$failures.Add("空 Enter なのに選択件数が 0 ではありません")
      }
      elseif (-not ($selectionEvents | Where-Object { [string]$_.status -match "(?i)^(Error|None)$" })) {
        [void]$failures.Add("空 Enter の生 Status が Error/None ではありません")
      }
    }
  }
  return @($failures)
}

function Wait-PromptTraceEnd([int]$startIndex, [string]$phase, [int]$timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    $events = @(Get-JsonLines $promptTrace -InvalidLabel $JsonLinesInvalidLabel)
    if ($events.Count -gt $startIndex) {
      $newEvents = @($events[$startIndex..($events.Count - 1)])
      if ($newEvents | Where-Object { $_.operation -eq "Command.End" }) {
        [void](Write-Evidence "TRACE_READY phase=$phase events=$($newEvents.Count)")
        return @($newEvents)
      }
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "処理 '$phase' の Command.End trace が $timeoutSeconds 秒以内に記録されませんでした"
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






# PETA の成果物（SVG＋EMF＋PDF）が3点そろって安定するまで待つ。PETA は診断オフのため
# diag.json は書かない（peta_window_rotation_compat.ps1 の Wait-PetaDiagArtifacts は PETADIAG 用に
# diag.json も待つが、本ハーネスは PETA を叩くため対象外にする）。
# ARES は .NET コマンド実行中 COM（CMDNAMES 読み取り）を拒否するため commands チェックを飛ばす
# （window_contract_compat.ps1 と同じ理由。CommandStartKind=AresCommand で判定する）。
function Wait-PetaArtifacts([string]$dir, $document, [datetime]$startedAt, [string]$commandStartKind,
                            [string]$tracePath, [int]$traceStartCount) {
  $deadline = (Get-Date).AddSeconds($CommandCompletionTimeoutSeconds)
  $stable = 0
  $lastSignature = ""
  $commandEndedAt = $null
  while ((Get-Date) -lt $deadline) {
    $commands = if ($commandStartKind -eq "AresCommand") { "" } else { Read-VariableSafe $document "CMDNAMES" }
    if ($commands.StartsWith("<取得失敗")) { $commands = "busy" }
    $svg = @(Get-NewestSvgSince $dir $startedAt)
    if ($svg.Count -eq 1) {
      $emfPath = [IO.Path]::ChangeExtension($svg[0].FullName, ".emf")
      $pdfPath = [IO.Path]::ChangeExtension($svg[0].FullName, ".pdf")
      if ((Test-Path -LiteralPath $emfPath) -and (Test-Path -LiteralPath $pdfPath) -and [string]::IsNullOrWhiteSpace($commands)) {
        $signature = "{0}:{1}:{2}" -f $svg[0].Length, (Get-Item -LiteralPath $emfPath).Length, (Get-Item -LiteralPath $pdfPath).Length
        if ($signature -eq $lastSignature) { $stable++ } else { $stable = 1; $lastSignature = $signature }
        if ($stable -ge $StableFilePollCount) {
          return @{ Svg = $svg[0].FullName; Emf = $emfPath; Pdf = $pdfPath }
        }
      }
    }
    if ($commandStartKind -eq "AresCommand") {
      $trace = @(Get-JsonLines $tracePath -InvalidLabel $JsonLinesInvalidLabel)
      if ($null -eq $commandEndedAt -and $trace.Count -gt $traceStartCount -and
          @($trace[$traceStartCount..($trace.Count - 1)] | Where-Object { $_.operation -eq "Command.End" }).Count -gt 0) {
        $commandEndedAt = Get-Date
      }
      if ($null -ne $commandEndedAt -and
          (Get-Date) -gt $commandEndedAt.AddSeconds($ArtifactAfterCommandEndTimeoutSeconds)) {
        throw "PETA は終了しましたが、$ArtifactAfterCommandEndTimeoutSeconds 秒以内に SVG+EMF+PDF が生成されませんでした（$dir）"
      }
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  throw "PETA 成果物（SVG+EMF+PDF）が $CommandCompletionTimeoutSeconds 秒以内に完成しませんでした（$dir）"
}

# ケース直下の SVG（ステージ中間を除く）・EMF・PDF が各 1 件であることを確かめ、外れた分を $failures へ追加する。
function Add-ArtifactCountFailures($failures, [string]$caseDir) {
  $svgCount = @(Get-ChildItem -LiteralPath $caseDir -Filter "*.svg" | Where-Object { $_.Name -notlike "*.stage*.svg" }).Count
  $emfCount = @(Get-ChildItem -LiteralPath $caseDir -Filter "*.emf").Count
  $pdfCount = @(Get-ChildItem -LiteralPath $caseDir -Filter "*.pdf").Count
  if ($svgCount -ne 1) { [void]$failures.Add("SVG が1件でない（$svgCount 件）") }
  if ($emfCount -ne 1) { [void]$failures.Add("EMF が1件でない（$emfCount 件）") }
  if ($pdfCount -ne 1) { [void]$failures.Add("PDF が1件でない（$pdfCount 件）") }
}

# ケースa（L+T 全体）: 判定は SVG/EMF/PDF が各1件・SVGにレイヤグループあり・<text>なし。
function Test-CaseA([hashtable]$artifacts, [string]$caseDir) {
  $failures = New-Object System.Collections.ArrayList
  Add-ArtifactCountFailures $failures $caseDir
  $svgContent = Get-Content -LiteralPath $artifacts.Svg -Raw -Encoding UTF8
  # レイヤ情報は発行 PDF のレイヤ（OCG）から取る仕様で、ドライバが OCG を埋め込まない製品では
  # 「警告して縮退（グループ化なしで出力続行）」が文書化済みの正規挙動（docs/06）。
  # 判定は PDF の OCG 実在で分岐する（実測: IJCAD 2026 は OCG なし＝縮退が正、BricsCAD V26 はあり）
  $pdfText = [IO.File]::ReadAllText($artifacts.Pdf, [Text.Encoding]::GetEncoding('latin1'))
  $pdfHasOcg = $pdfText.Contains('/OCProperties')
  $svgHasLayers = $svgContent -match [regex]::Escape('inkscape:groupmode="layer"')
  if ($pdfHasOcg -and -not $svgHasLayers) {
    [void]$failures.Add('PDF に OCG があるのに SVG にレイヤグループ（inkscape:groupmode="layer"）がない')
  }
  elseif (-not $pdfHasOcg) {
    if ($svgHasLayers) { [void]$failures.Add('PDF に OCG がないのに SVG にレイヤグループがある（取得元が仕様と不整合）') }
    # Write-Evidence の出力が関数の戻り値（失敗配列）へ混入しないよう void 化する
    else { [void](Write-Evidence "CASE_A layer=OCGなしの警告付き縮退（仕様どおり。グループ化なしで出力）") }
  }
  if ($svgContent -match '<text') {
    [void]$failures.Add('SVG に <text> が残っている（文字アウトライン化(_Textoutline)が効いていない）')
  }
  return @($failures)
}

# ケースb（M5 窓）: 判定は SVG ヘッダが余白5mmを四辺へ適用した寸法・viewBoxと一致すること。
function Test-CaseB([hashtable]$artifacts, [string]$caseDir) {
  $failures = New-Object System.Collections.ArrayList
  Add-ArtifactCountFailures $failures $caseDir
  $head = Get-Content -LiteralPath $artifacts.Svg -TotalCount 1 -Encoding UTF8
  if ($head -notmatch [regex]::Escape($ExpectedWindowSvgHeader)) {
    [void]$failures.Add("SVG ヘッダが期待値と不一致（期待: $ExpectedWindowSvgHeader / 実際: $head）")
  }
  return @($failures)
}

function Test-ObjectCase([hashtable]$artifacts, [string]$caseDir) {
  $failures = New-Object System.Collections.ArrayList
  foreach ($extension in @(".svg", ".emf", ".pdf")) {
    $count = @(Get-ChildItem -LiteralPath $caseDir -File | Where-Object { $_.Extension -eq $extension }).Count
    if ($count -ne 1) { [void]$failures.Add("$extension 成果物が1件でない（$count 件）") }
  }
  return @($failures)
}

$application = $null
$candidateApplication = $null
$document = $null
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
$caseResults = @()
$traceEnvironment = @{
  CADPETA_PROMPT_TRACE = $promptTrace
  CADPETA_PROMPT_TRACE_RUN_ID = $promptTraceRunId
  CADPETA_PROMPT_TRACE_CASE_ID = $Product
}
$savedTraceEnvironment = @{}
$traceEnvironmentActive = $false
# 保存しておいたプロセス環境変数の値（Exists／Value）へ戻し、読み戻して一致すれば $true を返す。
function Restore-ProcessEnvironmentVariable([string]$name, $saved) {
  if ($saved.Exists) {
    [Environment]::SetEnvironmentVariable($name, [string]$saved.Value, "Process")
  } else {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
  }
  $actualExists = Test-Path -LiteralPath "Env:$name"
  $actualValue = [Environment]::GetEnvironmentVariable($name, "Process")
  return -not (($saved.Exists -and (-not $actualExists -or $actualValue -cne $saved.Value)) -or
               (-not $saved.Exists -and $actualExists))
}
foreach ($name in $traceEnvironment.Keys) {
  $savedTraceEnvironment[$name] = @{
    Exists = Test-Path -LiteralPath "Env:$name"
    Value = [Environment]::GetEnvironmentVariable($name, "Process")
  }
  [Environment]::SetEnvironmentVariable($name, [string]$traceEnvironment[$name], "Process")
}
$traceEnvironmentActive = $true
try {
  Write-Evidence "START Product=$Product Exe=$exe Plugin=$plugin Source=$source Layout=$LayoutName"
  $windowStation = [CadPeta.CompatNativeWindow]::WindowStationName()
  $desktop = [CadPeta.CompatNativeWindow]::DesktopName()
  Write-Evidence "DESKTOP WindowStation=$windowStation Desktop=$desktop"
  if ($windowStation -ne $ExpectedWindowStation -or $desktop -ne $ExpectedDesktop) {
    throw "対話デスクトップが必要です。期待=$ExpectedWindowStation\$ExpectedDesktop、実際=$windowStation\$desktop"
  }
  Assert-WindowsPowerShellSta
  [CadPeta.CompatMessageFilter]::RetryWindowMilliseconds = $ComRetryWindowMilliseconds
  [CadPeta.CompatMessageFilter]::RetryDelayMilliseconds = $ComRetryDelayMilliseconds
  [CadPeta.CompatMessageFilter]::Register()
  $messageFilterRegistered = $true

  $existingProcesses = @(Get-Process -Name $productConfig.ProcessName -ErrorAction SilentlyContinue)
  if ($existingProcesses.Count -gt 0) {
    throw "既存の $($productConfig.ProcessName) プロセスがあり、起動プロセスを一意に所有できません: PID=$($existingProcesses.Id -join ',')"
  }
  # インストール済みの CADPeta が自動で読み込まれると、ビルドした DLL を試験できない。試験の間だけ登録を外す
  # 起動時の自動読み込みそのものを試験するときは、登録を外さない
  # -SkipPluginLoad はインストール済みの CADPeta を試験するので、自動読み込みの登録は外さず、インストール先の記録だけを行う
  Suspend-CompatProductAutoload $Product ([ref]$autoloadState) -KeepAutoload:$SkipPluginLoad

  if ($productConfig.DiscoverProgId) {
    Start-CadVisibleAndConnect $exe $SecondaryLaunchDelaySeconds $GuiStartupTimeoutSeconds ([ref]$startedProcess) ([ref]$candidateApplication) ([ref]$connectedProgId) ([ref]$secondaryProcess) ([ref]$progIds)
  } else {
    $startedProcess = Start-CadProcessHidden $exe $productConfig.StartArguments
    Write-Evidence "PROCESS started PID=$($startedProcess.Id)"
    $candidateApplication = Wait-ActiveObject $productConfig.ProgId $GuiStartupTimeoutSeconds
    if ($null -eq $candidateApplication) {
      throw "COM オブジェクト $($productConfig.ProgId) が $GuiStartupTimeoutSeconds 秒以内に ROT 登録されませんでした"
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
  if (-not $connectedProgId) { $connectedProgId = $productConfig.ProgId }
  Write-Evidence "COM attached ProgId=$connectedProgId HWND=$hwnd PID=$ownedPid Version=$($application.Version)"

  # 起動済み CAD は環境変数を継承済みなので、親 PowerShell の環境を直ちに元へ戻す。
  foreach ($name in $traceEnvironment.Keys) {
    if (-not (Restore-ProcessEnvironmentVariable $name $savedTraceEnvironment[$name])) {
      throw "親 PowerShell の環境変数を復元できませんでした: $name"
    }
  }
  $traceEnvironmentActive = $false

  if ($ProbeOnly) {
    Write-Evidence "PROBE_ONLY completed Product=$Product"
    $completed = $true
  } else {

  [void]$application.Documents.Open($dwgCopy)
  $document = Find-AndActivateDocument $application $dwgCopy $GuiStartupTimeoutSeconds
  Disable-CadFileDia $document $productConfig.SetFileDia ([ref]$oldFileDia)
  # 定常オブジェクトスナップを外す（実行中だけ 0 にして復元する）
  Disable-CadOsMode $document ([ref]$oldOsMode)

  # 図面を開いた直後は再描画等でコマンドを受け付けないことがある。待機状態を確認してから読み込む
  Wait-CommandNamesEmpty $document $GuiStartupTimeoutSeconds
  Write-Evidence "IDLE phase=before plugin load"

  if ($SkipPluginLoad) {
    Write-Evidence "LOAD skipped: startup registration is under test"
  }
  else {
    # 信頼するフォルダへコピーした DLL を読み込み、確認の画面を出さずに無人で進める
    $pluginStage = New-CompatPluginStage $Product $plugin
    Assert-CadPluginFolderTrusted $document $pluginStage
    Write-Evidence "PLUGIN_STAGE plugin=$($pluginStage.Plugin)"
    $pluginForCommand = $pluginStage.Plugin -replace '\\', '/'
    # ARES の LOADDOTNETAPPLICATION は、ファイル名の後に明示の Enter が届かないと読み込まない
    # （peta_window_rotation_compat.ps1 と同じ実測）。生テキストで CR を付けて送る
    $loadCommand = if ($productConfig.LoadKind -eq "LoadDotNetApplication") {
      "._LOADDOTNETAPPLICATION`r$pluginForCommand"
    } else {
      "_.NETLOAD`r$pluginForCommand"
    }
    $loadMarker = Join-Path $productRoot "plugin_load_queue.marker"
    if (Test-Path -LiteralPath $loadMarker) { Remove-Item -LiteralPath $loadMarker -Force }
    $loadMarkerForLisp = $loadMarker -replace '\\', '/'
    $markerCommand = "(progn (setq cadpeta:marker (open `"$loadMarkerForLisp`" `"w`")) (write-line `"load-attempt-completed`" cadpeta:marker) (close cadpeta:marker))"
    Write-Evidence "LOAD begin kind=$($productConfig.LoadKind)"
    $document.SendCommand($loadCommand + "`r")
    $document.SendCommand($markerCommand + "`r")
    Wait-FileExists $loadMarker "plugin load queue completion" $LoadQueueTimeoutSeconds
    Write-Evidence "LOAD queue completed"
    # 読み込まれたのがコピーした DLL だけであることを確かめる。別の CADPeta が先に読み込まれていると、
    # 同じ名前のアセンブリは後から読み込めず、記録が無いか別の場所の記録になる
    $loadEvents = @()
    if (Test-Path -LiteralPath $promptTrace) {
      $loadEvents = @(Get-JsonLines $promptTrace -InvalidLabel $JsonLinesInvalidLabel | Where-Object { $_.operation -eq "Plugin.Load" })
    }
    $loadedPaths = @($loadEvents | ForEach-Object { [string]$_.stringResult })
    Write-Evidence "LOAD loaded=$($loadedPaths -join ';')"
    # ARES 2020 は読み込んだ場所を / 区切りで返す（2026-10-25 実測）ので、完全なパスにそろえて比べる
    if ($loadedPaths.Count -ne 1 -or -not [string]::Equals([IO.Path]::GetFullPath($loadedPaths[0]), [IO.Path]::GetFullPath($pluginStage.Plugin), [StringComparison]::OrdinalIgnoreCase)) {
      throw "読み込まれたプラグインがコピーした DLL ではありません: 読み込まれた=$($loadedPaths -join ';') 期待=$($pluginStage.Plugin)"
    }
  }

  if ($modelOnlyRun) {
    if ([int]$document.GetVariable("TILEMODE") -ne 1) {
      throw "モデル空間保存済みの入力図面が TILEMODE=1 で開かれませんでした: $source"
    }
    Write-Evidence "LAYOUT set=Model source-saved"
  }
  else {
    # ペーパー空間ケースだけ、レイアウト付き入力を対象レイアウトへ切り替える。
    $document.SendCommand("(setvar `"TILEMODE`" 0)`r")
    $document.SendCommand("(setvar `"CTAB`" `"$LayoutName`")`r")
    $document.SendCommand("_.PSPACE`r")
    [void](Wait-CommandIdle $document "after layout switch" $LoadQueueTimeoutSeconds)
    Write-Evidence "LAYOUT set=$LayoutName"
  }

  $lp1 = ConvertTo-LispPoint $WindowP1
  $lp2 = ConvertTo-LispPoint $WindowP2

  # 現在レイアウトの全要素から1件だけ除き、空でも全選択でもない選択集合を作る。
  # O の成否を全体出力との見かけの一致で誤判定しないため、選択件数は trace でも確認する。
  # オブジェクト絞り込みはモデル空間で実行する。ペーパー空間 PETA_A3 は印刷不能な Viewport だけが
  # 選択集合になり得るため、モデル空間の全図形から1件だけ除いた真部分集合を固定する。
  $prepareSubset = '(progn (sssetfirst nil nil) (setq cadpeta:all (ssget "_X" (list (cons 410 (getvar "CTAB"))))) (setq cadpeta:subset (ssadd)) (if (and cadpeta:all (> (sslength cadpeta:all) 1)) (progn (setq cadpeta:i 0) (repeat (1- (sslength cadpeta:all)) (ssadd (ssname cadpeta:all cadpeta:i) cadpeta:subset) (setq cadpeta:i (1+ cadpeta:i))))) (princ))'

  $cases = @(
    @{ Name = "a"; Description = "L+T 全体"
       SingleBatchCmd = "_.PETA`r_Layer`r_Textoutline`r`r"
       AresLisp = '(command "_.PETA" "_Layer" "_Textoutline" "")'
       Judge = ${function:Test-CaseA} }
    @{ Name = "b"; Description = "M5 窓"
       SingleBatchCmd = "_.PETA`r_Margin`r$MarginMm`r$WindowP1`r$WindowP2`r"
       AresLisp = "(command ""_.PETA"" ""_Margin"" ""$MarginMm"" $lp1 $lp2)"
       Judge = ${function:Test-CaseB} }
    @{ Name = "o"; Description = "裸 O と後選択"; Space = "Model"; Prepare = $prepareSubset
       SingleBatchCmd = "_.PETA`rO`r!cadpeta:subset`r`r`r"
       AresLisp = '(command "_.PETA" "O" cadpeta:subset "" "")'
       ExpectKeyword = $true; Token = "O"; ExpectSelection = "postselect"; Judge = ${function:Test-ObjectCase} }
    @{ Name = "object-global"; Description = "グローバル _Object と後選択"; Space = "Model"; Prepare = $prepareSubset
       SingleBatchCmd = "_.PETA`r_Object`r!cadpeta:subset`r`r`r"
       AresLisp = '(command "_.PETA" "_Object" cadpeta:subset "" "")'
       ExpectKeyword = $true; Token = "_Object"; ExpectSelection = "postselect"; Judge = ${function:Test-ObjectCase} }
    @{ Name = "pickfirst"; Description = "先選択"; Space = "Model"; Prepare = $prepareSubset
       BeforeCommand = '(sssetfirst nil cadpeta:subset)'
       SingleBatchCmd = "_.PETA`r`r"
       AresLisp = '(command "_.PETA" "")'
       ExpectSelection = "pickfirst"; Judge = ${function:Test-ObjectCase} }
    @{ Name = "postselect"; Description = "後選択"; Space = "Model"; Prepare = $prepareSubset
       SingleBatchCmd = "_.PETA`rO`r!cadpeta:subset`r`r`r"
       AresLisp = '(command "_.PETA" "O" cadpeta:subset "" "")'
       ExpectKeyword = $true; Token = "O"; ExpectSelection = "postselect"; Judge = ${function:Test-ObjectCase} }
    @{ Name = "empty-enter"; Description = "O の選択で空 Enter"; Space = "Model"; Prepare = '(progn (sssetfirst nil nil) (princ))'
       SingleBatchCmd = "_.PETA`rO`r`r`r"
       AresLisp = '(command "_.PETA" "O" "" "")'
       ExpectKeyword = $true; Token = "O"; ExpectSelection = "empty"; Judge = ${function:Test-ObjectCase} }
    # 画層 0 と、元図面の現在の画層にした CADPETA_CUR の矩形だけを選ぶ（側DB は元図面の現在の画層を引き継ぐ）。
    # 準備で図形を足すので、ほかのケースの選択に入らないよう最後に置く
    @{ Name = "current-layer"; Description = "画層 0 と現在の画層の図形を後選択"; Space = "Model"
       Prepare = '(progn (entmake (list ''(0 . "LAYER") ''(100 . "AcDbSymbolTableRecord") ''(100 . "AcDbLayerTableRecord") ''(2 . "CADPETA_CUR") ''(70 . 0) ''(62 . 1) ''(6 . "Continuous"))) (setvar "CLAYER" "CADPETA_CUR") (sssetfirst nil nil) (setq cadpeta:cur (ssadd)) (ssadd (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") (cons 8 "0") ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 0.0 0.0) ''(10 100.0 0.0) ''(10 100.0 50.0) ''(10 0.0 50.0))) cadpeta:cur) (ssadd (entmakex (list ''(0 . "LWPOLYLINE") ''(100 . "AcDbEntity") (cons 8 "CADPETA_CUR") ''(100 . "AcDbPolyline") ''(90 . 4) ''(70 . 1) ''(10 120.0 0.0) ''(10 220.0 0.0) ''(10 220.0 50.0) ''(10 120.0 50.0))) cadpeta:cur) (princ))'
       SingleBatchCmd = "_.PETA`rO`r!cadpeta:cur`r`r`r"
       AresLisp = '(command "_.PETA" "O" cadpeta:cur "" "")'
       ExpectKeyword = $true; Token = "O"; ExpectSelection = "postselect"; ExpectSelectionCount = 2; ExpectClayer = "CADPETA_CUR"
       Judge = ${function:Test-ObjectCase} }
  )
  if (-not [string]::IsNullOrWhiteSpace($CaseName)) {
    $cases = @($cases | Where-Object { $_.Name -eq $CaseName })
  }
  elseif ($ObjectCasesOnly) {
    $cases = @($cases | Where-Object { $_.Name -in $modelCaseNames })
  }

  foreach ($case in $cases) {
    $caseDir = Join-Path $productRoot $case.Name
    if (Test-Path -LiteralPath $caseDir) { Remove-Item -LiteralPath $caseDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null

    $caseStart = Get-Date
    Write-Evidence "CASE begin name=$($case.Name) desc=$($case.Description)"
    $caseFailures = New-Object System.Collections.ArrayList
    $rawPrompt = @()
    $traceBefore = @(Get-JsonLines $promptTrace -InvalidLabel $JsonLinesInvalidLabel).Count
    $abortRemainingCases = $false
    try {
      if ($case.Space -eq "Model" -and [int]$document.GetVariable("TILEMODE") -ne 1) {
        $document.SetVariable("TILEMODE", [int16]1)
        [void](Wait-CommandIdle $document "case $($case.Name) model-space switch" $LoadQueueTimeoutSeconds)
        Write-Evidence "SPACE_READY case=$($case.Name) TILEMODE=1"
      }
      if ($productConfig.CommandStartKind -eq "AresCommand") {
        # 準備式の停止を PETA の成果物待ちと誤認しないよう、完了を確認してから本コマンドを送る。
        if ($case.Prepare -or $case.BeforeCommand) {
          $prepareMarker = Join-Path $caseDir "prepare.marker"
          $prepareMarkerForLisp = $prepareMarker -replace '\\', '/'
          $prepareParts = @()
          if ($case.Prepare) { $prepareParts += $case.Prepare }
          if ($case.BeforeCommand) { $prepareParts += $case.BeforeCommand }
          $prepareParts += "(progn (setq cadpeta:prepare-marker (open `"$prepareMarkerForLisp`" `"w`")) (write-line `"ready`" cadpeta:prepare-marker) (close cadpeta:prepare-marker))"
          $document.SendCommand(("(progn {0})" -f ($prepareParts -join " ")) + "`r")
          Wait-FileExists $prepareMarker "case $($case.Name) preparation" $LoadQueueTimeoutSeconds
          Write-Evidence "PREPARE_READY case=$($case.Name)"
        }
        $document.SendCommand($case.AresLisp + "`r")
      } else {
        $singleBatchParts = @()
        if ($case.Prepare) { $singleBatchParts += $case.Prepare + "`r" }
        if ($case.BeforeCommand) { $singleBatchParts += $case.BeforeCommand + "`r" }
        $singleBatchParts += $case.SingleBatchCmd
        $document.SendCommand(($singleBatchParts -join ""))
      }
      $artifacts = Wait-PetaArtifacts $productRoot $document $caseStart $productConfig.CommandStartKind `
        $promptTrace $traceBefore
      $caseTrace = @(Wait-PromptTraceEnd $traceBefore $case.Name $LoadQueueTimeoutSeconds)
      Write-Evidence "ARTIFACTS case=$($case.Name) svg=$($artifacts.Svg) emf=$($artifacts.Emf) pdf=$($artifacts.Pdf)"

      # 成果物をケース別フォルダへ退避し、次のケースと取り違えないようにする
      Get-ChildItem -LiteralPath $productRoot -File | Where-Object {
        $_.LastWriteTime -ge $caseStart -and $_.Extension -in @(".svg", ".emf", ".pdf", ".json")
      } | Move-Item -Destination $caseDir -Force
      $artifacts.Svg = Join-Path $caseDir ([IO.Path]::GetFileName($artifacts.Svg))
      $artifacts.Emf = Join-Path $caseDir ([IO.Path]::GetFileName($artifacts.Emf))
      $artifacts.Pdf = Join-Path $caseDir ([IO.Path]::GetFileName($artifacts.Pdf))

      $judgeFailures = @(& $case.Judge $artifacts $caseDir)
      # 試験の前提（準備で現在の画層を切り替えた）が成り立っていたことを確かめる
      if ($case.ExpectClayer) {
        $clayer = [string](Read-VariableSafe $document "CLAYER")
        Write-Evidence "CLAYER case=$($case.Name) value=$clayer"
        if ($clayer -cne $case.ExpectClayer) { $judgeFailures += "現在の画層が $($case.ExpectClayer) ではありません: $clayer" }
      }
      foreach ($f in $judgeFailures) { [void]$caseFailures.Add($f) }

      $traceFailures = @(Test-PromptTrace $case $caseTrace $ownedPid)
      foreach ($f in $traceFailures) { [void]$caseFailures.Add($f) }
      $rawPrompt = @($caseTrace | ForEach-Object {
        [pscustomobject]@{
          Operation = $_.operation; Status = $_.status; StringResult = $_.stringResult; Count = $_.count
        }
      })
    } catch {
      $abortRemainingCases = $true
      [void]$caseFailures.Add("実行時エラー: $($_.Exception.Message)")
      Write-Evidence "CASE_ERROR case=$($case.Name) error=$($_.Exception.Message)"
      try {
        $allTrace = @(Get-JsonLines $promptTrace -InvalidLabel $JsonLinesInvalidLabel)
        if ($allTrace.Count -gt $traceBefore) {
          # プラグインがコマンドラインに出した中止の理由を残す（ARES は COM でコマンドの履歴を読めない）
          foreach ($message in @($allTrace[$traceBefore..($allTrace.Count - 1)] | Where-Object { $_.operation -eq "Message" })) {
            Write-Evidence "CASE_ERROR_MESSAGE case=$($case.Name) line=$($message.stringResult)"
          }
          $rawPrompt = @($allTrace[$traceBefore..($allTrace.Count - 1)] | ForEach-Object {
            [pscustomobject]@{
              Operation = $_.operation; Status = $_.status; StringResult = $_.stringResult; Count = $_.count
            }
          })
        }
      } catch {
        [void]$caseFailures.Add("失敗時 trace の読取エラー: $($_.Exception.Message)")
      }
    }

    $pass = $caseFailures.Count -eq 0
    $caseResults += [pscustomobject]@{
      Name = $case.Name; Description = $case.Description; Pass = $pass; Failures = @($caseFailures)
      RawPrompt = @($rawPrompt)
    }
    Write-Evidence "CASE end name=$($case.Name) pass=$pass failures=$($caseFailures.Count)"
    if ($abortRemainingCases) {
      Write-Evidence "CASES_ABORTED after=$($case.Name) reason=infrastructure-error"
      break
    }
  }

  $objectResults = @($caseResults | Where-Object { $_.Name -in $modelCaseNames })
  $hasCompletePassingObjectSet = $objectResults.Count -eq $modelCaseNames.Count -and
    @($modelCaseNames | Where-Object { $_ -notin $objectResults.Name }).Count -eq 0 -and
    @($objectResults | Where-Object { -not $_.Pass }).Count -eq 0
  if ($hasCompletePassingObjectSet) {
    $fullDir = Join-Path $productRoot "empty-enter"
    $fullSvg = @(Get-ChildItem -LiteralPath $fullDir -Filter "*.svg" |
      Where-Object { $_.Name -notlike "*.stage*.svg" })
    $fullEmf = @(Get-ChildItem -LiteralPath $fullDir -Filter "*.emf")
    foreach ($selectedName in @("o", "object-global", "pickfirst", "postselect")) {
      $selectedResult = $caseResults | Where-Object { $_.Name -eq $selectedName }
      $selectedDir = Join-Path $productRoot $selectedName
      $selectedSvg = @(Get-ChildItem -LiteralPath $selectedDir -Filter "*.svg" |
        Where-Object { $_.Name -notlike "*.stage*.svg" })
      $selectedEmf = @(Get-ChildItem -LiteralPath $selectedDir -Filter "*.emf")
      $crossFailures = @()
      if ($fullSvg.Count -ne 1 -or $selectedSvg.Count -ne 1 -or
          (Get-FileHash -LiteralPath $fullSvg[0].FullName).Hash -eq
          (Get-FileHash -LiteralPath $selectedSvg[0].FullName).Hash) {
        $crossFailures += "選択 SVG が空 Enter の全体 SVG と異ならない"
      }
      if ($fullEmf.Count -ne 1 -or $selectedEmf.Count -ne 1 -or
          (Get-FileHash -LiteralPath $fullEmf[0].FullName).Hash -eq
          (Get-FileHash -LiteralPath $selectedEmf[0].FullName).Hash) {
        $crossFailures += "選択 EMF が空 Enter の全体 EMF と異ならない"
      }
      if ($crossFailures.Count -gt 0) {
        $selectedResult.Pass = $false
        $selectedResult.Failures = @($selectedResult.Failures) + $crossFailures
      }
      Write-Evidence "CROSS selected-vs-full case=$selectedName pass=$($crossFailures.Count -eq 0)"
    }
  }

  $completed = $true
  }
} catch {
  Write-Evidence "ERROR $($_.Exception.Message)"
  throw
} finally {
  if ($traceEnvironmentActive) {
    foreach ($name in $traceEnvironment.Keys) {
      if (-not (Restore-ProcessEnvironmentVariable $name $savedTraceEnvironment[$name])) {
        $cleanupFailures += "親 PowerShell の環境変数を復元できませんでした: $name"
      }
    }
    $traceEnvironmentActive = $false
  }
  if ($null -ne $document) {
    # sysvar 復元は読み戻しで確認し、失敗は $cleanupFailures 経由で最終 throw へ集約する
    Restore-CadSystemVariables $document @(
      @{ Name = "FILEDIA"; Value = $oldFileDia },
      @{ Name = "OSMODE"; Value = $oldOsMode }) ([ref]$cleanupFailures)
  }
  if ($null -ne $application) { Close-CadApplication $application $ownedPid ([ref]$cleanupFailures) }
  Release-CadComResources $messageFilterRegistered @($document, $application, $candidateApplication)
  # 起動したが ROT 接続先にならなかったプロセス（1つ目・2つ目のうち捨てになった方）の後始末。
  # 自分が起動し exe で起動した所有プロセスのみが対象
  Stop-CadSessionProcesses $ownedPid $ProcessExitTimeoutSeconds @($startedProcess, $secondaryProcess) $dwgCopy $dwgHash ([ref]$cleanupFailures) -AllowForceStop:$AllowForceStop
  Remove-CompatPluginStage $pluginStage ([ref]$cleanupFailures)
  Resume-CompatProductAutoload $autoloadState $exe ([ref]$cleanupFailures)
  Write-Evidence "END completed=$completed"

  if ($caseResults.Count -gt 0) {
    $caseResults | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $resultsJson -Encoding utf8
    Write-Evidence "RESULTS written=$resultsJson"
    Write-Output ""
    Write-Output "=== 判定（$Product） ==="
    $overallFailures = @()
    foreach ($r in $caseResults) {
      $status = if ($r.Pass) { "PASS" } else { "FAIL" }
      Write-Output ("ケース {0}（{1}）: {2}" -f $r.Name, $r.Description, $status)
      foreach ($f in $r.Failures) {
        Write-Output ("  - $f")
        $overallFailures += "ケース $($r.Name): $f"
      }
    }
    if ($overallFailures.Count -gt 0 -and $completed) {
      Write-Output ""
      Write-Output "=== 失敗 ==="
      $overallFailures | ForEach-Object { Write-Output $_ }
      throw "判定失敗（$($overallFailures.Count) 件）"
    }
  }
  if ($completed -and $cleanupFailures.Count -gt 0) {
    throw "強制停止を使わない CAD 後始末に失敗しました: $($cleanupFailures -join '; ')"
  }
}
