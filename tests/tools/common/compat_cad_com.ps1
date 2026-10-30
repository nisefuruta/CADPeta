# 互換 CAD（ARES／IJCAD／BricsCAD）の COM E2E ハーネスが共有する関数。
# 呼び出し側スクリプトがドットソースで読み込む。関数は呼び出し側のスクリプト変数を参照する。
#   $evidenceLog     Write-Evidence の追記先ファイル
#   $PollMilliseconds  待機ループの間隔（ミリ秒）
. "$PSScriptRoot\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）
# 互換 CAD の試験は Windows PowerShell 5.1 で動く。Get-FileHash などを使う前に PSModulePath を直す
Repair-WindowsPowerShellModulePath

# 証跡ログ（$evidenceLog）へ時刻付きで追記し、同じ行を出力する。
function Write-Evidence([string]$message) {
  $line = "{0:o} {1}" -f (Get-Date), $message
  [IO.File]::AppendAllText($evidenceLog, $line + [Environment]::NewLine, [Text.Encoding]::UTF8)
  Write-Output $line
}

# 指定した実行ファイルを LocalServer32 に持つ PCAD_AC_X.AcadApplication.* の ProgID を返す。
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

# 開いている図面から指定パスのものを探して Activate する。見つからなければ例外を投げる。
# -IncludeDocumentList を付けると、例外の文言へ開いている図面の一覧を加える。
function Find-AndActivateDocument($application, [string]$expectedPath, [int]$timeoutSeconds,
                                  [switch]$IncludeDocumentList) {
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
  if ($IncludeDocumentList) {
    $visibleDocuments = @()
    $documents = $application.Documents
    for ($index = 0; $index -lt $documents.Count; $index++) {
      $candidate = $documents.Item($index)
      $visibleDocuments += "Name=$($candidate.Name),FullName=$($candidate.FullName)"
    }
    throw "開いた図面 '$expectedFullPath' が $timeoutSeconds 秒以内に見つかりませんでした。図面一覧=$($visibleDocuments -join '; ')"
  }
  throw "開いた図面 '$expectedFullPath' が $timeoutSeconds 秒以内に見つかりませんでした"
}

function Read-VariableSafe($document, [string]$name) {
  try { return [string]$document.GetVariable($name) } catch { return "<取得失敗: $($_.Exception.Message)>" }
}

# CMDNAMES が空になる（コマンドが終わる）まで待つ。待ちきれなければ $false を返す。
# -IncludeLastPrompt を付けると、タイムアウト時の証跡へ LASTPROMPT も記録する。
function Wait-CommandIdle($document, [string]$phase, [int]$timeoutSeconds, [switch]$IncludeLastPrompt) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    $commands = Read-VariableSafe $document "CMDNAMES"
    if (-not $commands.StartsWith("<取得失敗") -and [string]::IsNullOrWhiteSpace($commands)) {
      Write-Evidence "IDLE phase=$phase"
      return $true
    }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
  if ($IncludeLastPrompt) {
    Write-Evidence "IDLE_TIMEOUT phase=$phase CMDNAMES=$(Read-VariableSafe $document 'CMDNAMES') LASTPROMPT=$(Read-VariableSafe $document 'LASTPROMPT')"
  } else {
    Write-Evidence "IDLE_TIMEOUT phase=$phase CMDNAMES=$(Read-VariableSafe $document 'CMDNAMES')"
  }
  return $false
}

# 実行環境の前提を確かめる。Marshal.GetActiveObject を使うため、Windows PowerShell 5.1 の STA スレッドが必要。
function Assert-WindowsPowerShellSta {
  if ($PSVersionTable.PSEdition -ne "Desktop") {
    throw "Marshal.GetActiveObject のため Windows PowerShell 5.1 が必要です。実際の PSEdition=$($PSVersionTable.PSEdition)"
  }
  if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) { throw "STA スレッドが必要です" }
}

# CAD を非表示で起動して Process を返す。$startArguments が空なら引数なしで起動する。
function Start-CadProcessHidden([string]$exe, $startArguments) {
  if ($startArguments.Count -gt 0) {
    Start-Process -FilePath $exe -ArgumentList (ConvertTo-NativeArgString @($startArguments)) -PassThru -WindowStyle Hidden
  } else {
    Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
  }
}

# 指定の ProgID の COM オブジェクトが ROT に登録されるまで待つ。登録されなければ $null を返す。
function Wait-ActiveObject([string]$progId, [int]$timeoutSeconds) {
  $candidate = $null
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline -and $null -eq $candidate) {
    try { $candidate = [Runtime.InteropServices.Marshal]::GetActiveObject($progId) } catch { Start-Sleep -Milliseconds $PollMilliseconds }
  }
  return ,$candidate
}

# exe を指す登録済みの ProgID を調べ、CAD を可視で起動して、いずれかの ProgID が ROT に登録されるまで待つ。
# 登録済みの ProgID が無い場合と、時間内に ROT へ登録されない場合は例外を投げる（後者の文言の末尾へ $failureHint を付ける）。
# 起動した Process・見つかった COM オブジェクトと ProgID・調べた候補は参照で返す。
# 単独起動で ROT 未登録が続くとき、$secondaryLaunchDelaySeconds 秒後に 2 つ目を起動して 1 つ目の起動を促す
# （ARES 2027 対策）。2 つ目も所有プロセスとして終了時に停止対象へ含める（自分が起動し exe を照合したもの）。
function Start-CadVisibleAndConnect([string]$exe, [int]$secondaryLaunchDelaySeconds, [int]$timeoutSeconds,
                                    [ref]$startedProcess, [ref]$candidateApplication, [ref]$connectedProgId,
                                    [ref]$secondaryProcess, [ref]$progIds, [string]$failureHint = "") {
  $progIds.Value = @(Get-RegisteredProgIdsForExe $exe)
  if ($progIds.Value.Count -eq 0) { throw "'$exe' を指す登録済み ProgID がありません" }
  Write-Evidence "PROGID candidates=$($progIds.Value -join ',') source=HKLM"
  $startedProcess.Value = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -PassThru
  Write-Evidence "PROCESS visible-start PID=$($startedProcess.Value.Id)"
  $secondaryLaunchAt = (Get-Date).AddSeconds($secondaryLaunchDelaySeconds)
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline -and $null -eq $candidateApplication.Value) {
    foreach ($progId in $progIds.Value) {
      try {
        $candidateApplication.Value = [Runtime.InteropServices.Marshal]::GetActiveObject($progId)
        $connectedProgId.Value = $progId
        break
      } catch {}
    }
    if ($null -eq $candidateApplication.Value) {
      if ($null -eq $secondaryProcess.Value -and (Get-Date) -gt $secondaryLaunchAt) {
        $secondaryProcess.Value = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -PassThru
        Write-Evidence "PROCESS secondary-start PID=$($secondaryProcess.Value.Id)（単独起動不全の回避。1つ目の ROT 登録を促す）"
      }
      Start-Sleep -Milliseconds $PollMilliseconds
    }
  }
  if ($null -eq $candidateApplication.Value) {
    throw "CAD が $timeoutSeconds 秒以内に ROT 登録されませんでした。候補=$($progIds.Value -join ',')$failureHint"
  }
}

# $exe のプロセスが起動の途中で出す、ビデオ カード ドライバの確認（題名「ドライバが対応していません」、ボタンは [中止] と [無視]）に
# [無視] を押す。画面が Standard VGA のドライバで動いているとき（リモート デスクトップの切断の後など）、ARES 2023 は起動のたびにこれを出し、
# 閉じるまで COM の呼び出しに応答しない（2026-10-25 実測）。押したら本文を証跡に書く。押した内容が profile.xml に残っても、
# 試験の後で profile.xml を元のバイト列へ戻す（Resume-CompatProductAutoload）。押したら $true
function Close-CadDriverWarning([string]$exe) {
  if (-not $exe) { return $false }
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
  $pressed = $false
  $processes = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue | Where-Object {
      try { [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
  foreach ($process in $processes) {
    $byProcess = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    foreach ($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byProcess)) {
      try { $pressed = (Close-CadDriverWarningWindow $window $process.Id) -or $pressed }
      catch [Windows.Automation.ElementNotAvailableException] {
        # 調べている間にウィンドウが閉じた（起動の途中の一時的なウィンドウ）。押すものが無いので、次のウィンドウへ進む。
        # 確認のダイアログだったなら、次の待機の回にもう一度見つかる
        Write-Evidence "DIALOG window-closed PID=$($process.Id)（調べている間に閉じた）"
      }
    }
  }
  return $pressed
}

# Close-CadDriverWarning の 1 つのウィンドウの処理。ドライバの確認なら [無視] を押して $true を返す
function Close-CadDriverWarningWindow($window, [int]$processId) {
  if ($window.Current.Name -ne 'ドライバが対応していません') { return $false }
  $texts = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
  $ignore = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, '無視')))
  if ($null -ne $ignore) {
    ([Windows.Automation.InvokePattern]$ignore.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Evidence "DIALOG driver-warning PID=$processId pressed=無視 text=$(($texts -join ' ') -replace '\s+', ' ')"
  }
  else {
    # ARES 2027（Qt 6.8）のダイアログは、UI オートメーションに本文とボタンを見せない。既定のボタンは [無視] なので
    # （2026-10-25 に画面の画像と、Enter で閉じて ARES が動き続けることで確かめた）、ダイアログのウィンドウへ Enter を送る。
    # 前面のウィンドウは変えない。閉じなければ、次の待機の回でもう一度送る
    if (-not ('CadPetaDialogKeys' -as [type])) {
      Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class CadPetaDialogKeys { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l); }'
    }
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    [void][CadPetaDialogKeys]::PostMessage($hwnd, 0x0100, [IntPtr]0x0D, [IntPtr]0x001C0001)  # WM_KEYDOWN（Enter）
    [void][CadPetaDialogKeys]::PostMessage($hwnd, 0x0101, [IntPtr]0x0D, [IntPtr]0xC01C0001)  # WM_KEYUP（Enter）
    Write-Evidence "DIALOG driver-warning PID=$processId pressed=Enter（既定のボタン [無視]。ボタンが UI オートメーションに見えない）"
  }
  return $true
}

# CAD を可視にして HWND が取れるまで待つ。取れなければ例外を投げる。
# $exe を渡すと、待つ間に起動の途中のドライバの確認を閉じる（Close-CadDriverWarning）。
function Get-CadWindowHandle($application, [int]$timeoutSeconds, [string]$exe = '') {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  $hwnd = 0
  while ((Get-Date) -lt $deadline -and $hwnd -eq 0) {
    try { $application.Visible = $true; $hwnd = [int64]$application.HWND }
    catch {
      [void](Close-CadDriverWarning $exe)
      # 製品のプロセスが無くなったら待たずに止める（リモート デスクトップの切断中の ARES 2027 は、起動の途中で
      # KERNELBASE.dll のアクセス違反で終了した。2026-10-25 実測）
      if ($exe -and -not @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue).Count) {
        throw "$exe のプロセスが起動の途中で終了した。Windows のイベント ログ（Application の Application Error）と、画面の接続の状態を確認すること"
      }
      Start-Sleep -Milliseconds $PollMilliseconds
    }
  }
  if ($hwnd -eq 0) { throw "COM アプリケーションが $timeoutSeconds 秒以内に自動操作可能になりませんでした" }
  return $hwnd
}

# COM 接続先のプロセスの実行ファイルが、起動した exe と一致することを確かめる。
function Assert-ConnectedExecutable([int]$candidatePid, [string]$exe) {
  $actualExe = (Get-Process -Id $candidatePid).Path
  if (-not [string]::Equals([IO.Path]::GetFullPath($actualExe), $exe, [StringComparison]::OrdinalIgnoreCase)) {
    throw "COM が想定外の実行ファイルへ接続しました。期待='$exe'、実際='$actualExe'"
  }
}

# $setFileDia が真なら FILEDIA を 0 にする（ファイル名のプロンプトをコマンド行に出す）。変更前の値は参照で返す（復元用）。
# SECURELOAD は変えない（利用者の CAD の安全の設定のため）。DLL は信頼する場所から読み込む（Assert-CadPluginFolderTrusted。TRUSTEDPATHS も変えない）。
function Disable-CadFileDia($document, [bool]$setFileDia, [ref]$oldFileDia) {
  if ($setFileDia) {
    $oldFileDia.Value = $document.GetVariable("FILEDIA")
    $document.SetVariable("FILEDIA", [int16]0)
    Write-Evidence "SYSVAR FILEDIA=$($oldFileDia.Value)->0"
  } else {
    Write-Evidence "SYSVAR FILEDIA=unchanged"
  }
}

# 定常オブジェクト スナップ（OSMODE）を 0 にする。スクリプトと (command) から入力した座標も定常スナップに吸着する
# （BricsCAD 実測: -PLOT の窓コーナーが近傍の図形の頂点へ吸着し、格納値が入力とずれた）。変更前の値は参照で返す（復元用）。
# 値を読めない・変えられない製品では、-Required なら止め、そうでなければ記録して進む（$oldOsMode は $null のままで、戻さない）。
function Disable-CadOsMode($document, [ref]$oldOsMode, [switch]$Required) {
  try {
    $old = $document.GetVariable("OSMODE")
    $document.SetVariable("OSMODE", [int16]0)
    $oldOsMode.Value = $old
    Write-Evidence "SYSVAR OSMODE=$old->0"
  } catch {
    if ($Required) { throw "OSMODE の保存または一時変更に失敗しました: $($_.Exception.Message)" }
    Write-Evidence "SYSVAR OSMODE unavailable=$($_.Exception.Message)"
  }
}

# New-CompatPluginStage の DLL を、確認の画面を出さずに読み込めることを確かめる。TRUSTEDPATHS と SECURELOAD は変えない。
# 信頼するフォルダへコピーした製品（ARES）はそのまま読み込める。それ以外の製品は、DLL のフォルダが TRUSTEDPATHS にあることを
# 読むだけで確かめ、無ければ（読めなければ）何も変えずに例外で止める（利用者が信頼済みにしたフォルダに DLL を置いて試験する）。
function Assert-CadPluginFolderTrusted($document, $stage) {
  if ($null -ne $stage.Dir) {
    Write-Evidence "SYSVAR TRUSTEDPATHS=unchanged（信頼するフォルダ $($stage.Dir) から読み込む）"
    return
  }
  $folder = [IO.Path]::GetDirectoryName($stage.Plugin)
  try {
    $trusted = [string]$document.GetVariable("TRUSTEDPATHS")
  } catch {
    throw "TRUSTEDPATHS を読めないため、DLL の確認の画面を出さずに読み込めるかを確かめられません: $($_.Exception.Message)"
  }
  if (-not (Test-TrustedPathsContain $trusted $folder)) {
    throw "DLL のフォルダが TRUSTEDPATHS に無いので、確認の画面を出さずに読み込めません（TRUSTEDPATHS は変えない）: $folder（TRUSTEDPATHS: $trusted）"
  }
  Write-Evidence "SYSVAR TRUSTEDPATHS=unchanged（$folder は信頼済み）"
}

# 図面を開いた直後は再描画等でコマンドを受け付けないことがある。CMDNAMES が空になるまで待つ（時間切れでも例外にしない）。
function Wait-CommandNamesEmpty($document, [int]$timeoutSeconds) {
  $idleDeadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $idleDeadline) {
    $commands = Read-VariableSafe $document "CMDNAMES"
    if (-not $commands.StartsWith("<取得失敗") -and [string]::IsNullOrWhiteSpace($commands)) { break }
    Start-Sleep -Milliseconds $PollMilliseconds
  }
}

# 変更したシステム変数を元の値へ戻し、読み戻して確かめる。$restores は @{ Name; Value } の配列で、
# Value が $null のものは変更していないので戻さない。失敗は $cleanupFailures（参照）へ追加する。
function Restore-CadSystemVariables($document, [object[]]$restores, [ref]$cleanupFailures) {
  foreach ($restore in $restores) {
    if ($null -eq $restore.Value) { continue }
    try {
      $document.SetVariable($restore.Name, $restore.Value)
      $readBack = $document.GetVariable($restore.Name)
      if ("$readBack" -ne "$($restore.Value)") { throw "再読値=$readBack" }
      Write-Evidence "RESTORE $($restore.Name)=[$($restore.Value)] verified"
    } catch {
      $cleanupFailures.Value += "$($restore.Name) 復元失敗: $($_.Exception.Message)"
      Write-Evidence "RESTORE $($restore.Name) error=$($_.Exception.Message)"
    }
  }
}

# 開いている図面を保存せず閉じ、CAD を終了する。
# -CloseFailurePrefix／-QuitFailurePrefix を指定すると、その失敗を「<接頭辞>: <例外文>」として $cleanupFailures（参照）へ追加する。
# -TolerateExitedProcess を付けると、Quit が失敗しても所有 PID が既に消えていれば失敗にしない
# （BricsCAD は最後の図面を Close(false) した時点で終了し、直後の Quit だけが RPC_S_SERVER_UNAVAILABLE になる）。
function Close-CadApplication($application, [int]$ownedPid, [ref]$cleanupFailures,
                              [string]$CloseFailurePrefix = '', [string]$QuitFailurePrefix = '', [switch]$TolerateExitedProcess) {
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
  } catch {
    if ($CloseFailurePrefix) { $cleanupFailures.Value += "${CloseFailurePrefix}: $($_.Exception.Message)" }
    Write-Evidence "CLOSE error=$($_.Exception.Message)"
  }
  try { $application.Quit(); Write-Evidence "QUIT ok" }
  catch {
    if ($TolerateExitedProcess -and $ownedPid -gt 0 -and -not (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue)) {
      Write-Evidence "QUIT skipped process-already-exited error=$($_.Exception.Message)"
    } else {
      if ($QuitFailurePrefix) { $cleanupFailures.Value += "${QuitFailurePrefix}: $($_.Exception.Message)" }
      Write-Evidence "QUIT error=$($_.Exception.Message)"
    }
  }
}

# メッセージフィルタの解除、COM オブジェクトの解放、GC を行う。
function Release-CadComResources([bool]$messageFilterRegistered, [object[]]$comObjects) {
  if ($messageFilterRegistered) { try { [CadPeta.CompatMessageFilter]::Revoke(); Write-Evidence "MSGFILTER revoked" } catch { Write-Evidence "MSGFILTER revoke error=$($_.Exception.Message)" } }
  foreach ($comObject in $comObjects) {
    if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
      try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) } catch {}
    }
  }
  [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

# プロセスが終了するまで待つ（時間切れでも例外にしない）。
function Wait-ProcessExit([int]$processId, [int]$timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Process -Id $processId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds $PollMilliseconds }
}

# 強制停止の後、プロセスが消えるまで 5 秒まで待ち、停止の失敗と残留を $cleanupFailures（参照）へ追加する。
function Stop-ProcessVerified([int]$processId, [string]$label, [ref]$cleanupFailures) {
  try { Stop-Process -Id $processId -Force -ErrorAction Stop }
  catch { $cleanupFailures.Value += "$label PID=$processId の強制停止失敗: $($_.Exception.Message)" }
  $forceDeadline = (Get-Date).AddSeconds(5)
  while ((Get-Process -Id $processId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $forceDeadline) { Start-Sleep -Milliseconds 100 }
  if (Get-Process -Id $processId -ErrorAction SilentlyContinue) { $cleanupFailures.Value += "$label PID=$processId が強制停止後も残留" }
}

# 自分が起動した CAD の所有 PID が終了するまで待ち、残っていれば後始末する。
# -AllowForceStop のときだけ強制停止する（-VerifyForceStop なら停止後の残留まで確かめる）。許可がなければ残留を失敗にする。
function Stop-OwnedCadProcess([int]$ownedPid, [int]$exitTimeoutSeconds, [ref]$cleanupFailures,
                              [switch]$AllowForceStop, [switch]$VerifyForceStop) {
  if ($ownedPid -le 0) { return }
  Wait-ProcessExit $ownedPid $exitTimeoutSeconds
  if (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) {
    if ($AllowForceStop) {
      Write-Evidence "FORCE_STOP explicitly-allowed owned PID=$ownedPid"
      if ($VerifyForceStop) { Stop-ProcessVerified $ownedPid "owned" $cleanupFailures }
      else { Stop-Process -Id $ownedPid -Force }
    } else {
      $cleanupFailures.Value += "owned PID=$ownedPid remained after Quit"
      Write-Evidence "RESIDUAL owned PID=$ownedPid force-stop=disabled"
    }
  }
}

# 起動したが ROT の接続先にならなかったプロセス（$label は extra／started など）の後始末。
# 自分が起動し exe を照合した所有プロセスだけが対象で、接続先の所有 PID は除く。
function Stop-LeftoverCadProcess([int]$processId, [string]$label, [int]$ownedPid, [ref]$cleanupFailures,
                                 [switch]$AllowForceStop, [switch]$VerifyForceStop) {
  if ($processId -gt 0 -and $processId -ne $ownedPid -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
    if ($AllowForceStop) {
      Write-Evidence "FORCE_STOP explicitly-allowed $label PID=$processId"
      if ($VerifyForceStop) { Stop-ProcessVerified $processId $label $cleanupFailures }
      else { Stop-Process -Id $processId -Force }
    } else {
      $cleanupFailures.Value += "$label PID=$processId remained"
      Write-Evidence "RESIDUAL $label PID=$processId force-stop=disabled"
    }
  }
}

# 入力 DWG のコピーが試験の前後で変わっていないことを証跡に残す。
# -RecordFailures を付けると、変更・欠落・ハッシュ計算の失敗を $cleanupFailures（参照）へ追加する。付けなければ、コピーがあるときだけ証跡を書く。
function Confirm-DwgUnchanged([string]$dwgCopy, [string]$dwgHash, [ref]$cleanupFailures, [switch]$RecordFailures) {
  if (Test-Path -LiteralPath $dwgCopy) {
    if ($RecordFailures) {
      try {
        $hashAfter = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash
        $unchanged = $hashAfter -eq $dwgHash
        Write-Evidence "DWG_HASH before=$dwgHash after=$hashAfter unchanged=$unchanged"
        if (-not $unchanged) { $cleanupFailures.Value += "入力DWGが変更された: before=$dwgHash after=$hashAfter" }
      } catch {
        $cleanupFailures.Value += "入力DWGの終了時ハッシュ検査失敗: $($_.Exception.Message)"
        Write-Evidence "DWG_HASH error=$($_.Exception.Message)"
      }
    } else {
      $hashAfter = (Get-FileHash -LiteralPath $dwgCopy -Algorithm SHA256).Hash
      Write-Evidence "DWG_HASH before=$dwgHash after=$hashAfter unchanged=$($hashAfter -eq $dwgHash)"
    }
  } elseif ($RecordFailures) {
    $cleanupFailures.Value += "入力DWGコピーが終了時に存在しない: $dwgCopy"
    Write-Evidence "DWG_HASH missing=$dwgCopy"
  }
}

# 開始時刻以降に作られた最新の SVG（ステージ中間の *.stage*.svg を除く）を、0 件か 1 件の配列で返す。
function Get-NewestSvgSince([string]$dir, [datetime]$startedAt) {
  @(Get-ChildItem -LiteralPath $dir -Filter "*.svg" | Where-Object {
    $_.Name -notlike "*.stage*.svg" -and $_.LastWriteTime -ge $startedAt
  } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

# CAD を終了させたあとのプロセスの後始末をまとめて行う。所有 PID の終了待ち（Stop-OwnedCadProcess）、
# 接続先にならなかった起動プロセス（$leftoverProcesses。$null は読み飛ばす）の後始末（Stop-LeftoverCadProcess）、
# 入力 DWG のコピーが変わっていないことの記録（Confirm-DwgUnchanged）を、この順に行う。
# -VerifyForceStop は強制停止の残留まで確かめ、-RecordDwgFailures は DWG の変更・欠落を失敗に加える。
function Stop-CadSessionProcesses([int]$ownedPid, [int]$exitTimeoutSeconds, [object[]]$leftoverProcesses,
                                  [string]$dwgCopy, [string]$dwgHash, [ref]$cleanupFailures,
                                  [switch]$AllowForceStop, [switch]$VerifyForceStop, [switch]$RecordDwgFailures) {
  Stop-OwnedCadProcess $ownedPid $exitTimeoutSeconds $cleanupFailures -AllowForceStop:$AllowForceStop -VerifyForceStop:$VerifyForceStop
  foreach ($extra in $leftoverProcesses) {
    if ($null -ne $extra) {
      Stop-LeftoverCadProcess $extra.Id "extra" $ownedPid $cleanupFailures -AllowForceStop:$AllowForceStop -VerifyForceStop:$VerifyForceStop
    }
  }
  Confirm-DwgUnchanged $dwgCopy $dwgHash $cleanupFailures -RecordFailures:$RecordDwgFailures
}

# 互換 CAD が暗黙に信頼するフォルダ。ARES は「使っているソフトの %AppData%\<ソフト名>」を信頼し、
# ここに置いた DLL では「信頼できるフォルダにありません」の確認の画面を出さない
# （Graebert のヘルプ「Controlling Safety Options Against Malicious Files」、配下のフォルダは 2020・2024・2027 で実測）。
# 信頼するフォルダが分からない製品は $null を返し、呼び出し側は DLL をそのまま読み込む。
function Get-CompatTrustedPluginRoot([string]$product) {
  if ($product -like "Ares*") { return Join-Path $env:APPDATA "ARES Standard Edition x64" }
  return $null
}

# プラグインのフォルダ（依存 DLL と lang を含む）を、信頼するフォルダの下の実行ごとに固有のフォルダへコピーし、
# コピーした DLL のパスとフォルダを返す。信頼するフォルダが無い製品は、元の DLL をそのまま返す（Dir は $null）。
# 固有の名前のフォルダが既にあれば中止する（後始末で削除するのが自分のコピーだけになるようにする）。
# AutoCAD（peta_window_rotation_compat.ps1 の Acad2026・Acad2027）は、TRUSTEDPATHS の項目の末尾が \... でなければ下のフォルダを信頼しないので、
# 信頼済みのフォルダ（$CadPetaTrustedDotnetFolder）の直下へコピーする。同じ名前のファイルがあれば何もコピーせずに止め、
# コピーしたファイルとフォルダを返す（Remove-CompatPluginStage がそれだけを削除する）。
function New-CompatPluginStage([string]$product, [string]$pluginPath) {
  if ($product -like 'Acad*') {
    $source = Split-Path -Parent ([IO.Path]::GetFullPath($pluginPath))
    $copies = @(Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {
        [pscustomobject]@{ Source = $_.FullName; Target = Join-Path $CadPetaTrustedDotnetFolder $_.FullName.Substring($source.Length).TrimStart('\'); Root = $CadPetaTrustedDotnetFolder }
      })
    # 同じ名前のファイルの確認、作る中間のフォルダ、途中で失敗したときの後始末は CadRegGuard と共通（test_helpers.ps1）
    $plan = Get-TrustedFolderCopyPlan $copies
    Invoke-TrustedFolderCopy $plan
    return @{ Plugin = (Join-Path $CadPetaTrustedDotnetFolder (Split-Path -Leaf $pluginPath)); Dir = $null
      Copies = @($plan.Copies | ForEach-Object { $_.Target }); CreatedDirs = @($plan.Dirs) }
  }
  $root = Get-CompatTrustedPluginRoot $product
  if ($null -eq $root) { return @{ Plugin = $pluginPath; Dir = $null } }
  if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "信頼するフォルダがありません: $root" }
  $dir = Join-Path $root ("CADPeta-e2e-" + [guid]::NewGuid().ToString("N"))
  if (Test-Path -LiteralPath $dir) { throw "コピー先が既にあります: $dir" }
  New-Item -ItemType Directory -Path $dir | Out-Null
  Copy-Item -Path (Join-Path (Split-Path -Parent $pluginPath) "*") -Destination $dir -Recurse
  $staged = Join-Path $dir (Split-Path -Leaf $pluginPath)
  if (-not (Test-Path -LiteralPath $staged -PathType Leaf)) { throw "プラグインをコピーできませんでした: $staged" }
  return @{ Plugin = $staged; Dir = $dir }
}

# New-CompatPluginStage のフォルダを削除する。CAD の終了直後は DLL が開かれていることがあるので、
# 0.5 秒の間隔で 20 回まで繰り返す。削除できなければ $cleanupFailures（参照）へ追加する。
function Remove-CompatPluginStage($stage, [ref]$cleanupFailures) {
  if ($null -ne $stage -and $stage.ContainsKey('Copies')) {
    # 信頼済みのフォルダの直下へコピーしたもの（AutoCAD）だけを削除する
    foreach ($failure in (Remove-TrustedFolderCopies @($stage.Copies) @($stage.CreatedDirs))) { $cleanupFailures.Value += $failure }
    Write-Evidence "PLUGIN_STAGE removed=$($stage.Copies.Count) files in $CadPetaTrustedDotnetFolder"
    return
  }
  if ($null -eq $stage -or $null -eq $stage.Dir) { return }
  $lastError = $null
  for ($i = 0; $i -lt 20; $i++) {
    try {
      if (Test-Path -LiteralPath $stage.Dir) { Remove-Item -LiteralPath $stage.Dir -Recurse -Force -ErrorAction Stop }
      Write-Evidence "PLUGIN_STAGE removed=$($stage.Dir)"
      return
    } catch {
      $lastError = $_.Exception.Message
      Start-Sleep -Milliseconds 500
    }
  }
  $cleanupFailures.Value += "信頼するフォルダへコピーしたプラグインを削除できませんでした: $($stage.Dir): $lastError"
}

# 互換 CAD がインストール済みの CADPeta を起動時に自動で読み込む登録のキー。登録があると、同じ名前の
# アセンブリを後から読み込めず、試験はビルドした DLL ではなくインストール済みの DLL を試験してしまう。
# ARES 2024 以降は HKCU の Applications に登録する（2023 以前は profile.xml のスタートアップで、Suspend-CompatProfileAutoload が扱う）。
function Get-CompatAutoloadKey([string]$product) {
  if ($product -match '^Ares(\d{4})$' -and [int]$Matches[1] -ge 2024) {
    return "HKCU:\Software\Graebert GmbH\ARES Standard $($Matches[1])\Applications\CADPeta"
  }
  return $null
}

# 外した登録のバックアップの置き場所。試験の作業フォルダとは別の決まった場所に置き、
# 戻せなかったときに compat_autoload_restore.ps1 で戻せるようにする。
function Get-CompatAutoloadBackupFile([string]$product) {
  Join-Path $env:LOCALAPPDATA "CadPeta-e2e\autoload-backup\$product.reg"
}

# キーと配下の全キーの値を「相対パス|名前|型|値」の並びで返す（戻した結果の照合用）。
function Get-CompatRegistryTreeSnapshot([string]$key) {
  $rootName = (Get-Item -LiteralPath $key).Name
  $rows = New-Object System.Collections.Generic.List[string]
  foreach ($item in @(Get-Item -LiteralPath $key) + @(Get-ChildItem -LiteralPath $key -Recurse)) {
    $relative = $item.Name.Substring($rootName.Length)
    foreach ($name in $item.GetValueNames()) {
      $value = $item.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
      if ($value -is [array]) { $value = ($value | ForEach-Object { "$_" }) -join "`n" }
      $rows.Add("$relative|$name|$($item.GetValueKind($name))|$value")
    }
    if ($item.GetValueNames().Count -eq 0) { $rows.Add("$relative|||") }
  }
  return @($rows | Sort-Object)
}

# reg.exe を実行し、終了コードを返す（Windows PowerShell 5.1 は標準エラーの出力を例外にするため cmd で受ける）。
function Invoke-CompatRegExe([string]$arguments) {
  cmd /c "reg.exe $arguments >nul 2>&1"
  return $LASTEXITCODE
}

# バックアップを作る途中で失敗したときに、一時の名前の作りかけのバックアップ（ファイルかフォルダ）を削除して、$message の例外を投げる。
# 作りかけのバックアップは元の設定を変える前に作るものなので、削除してよい。削除できなければ、その理由も例外の文に入れる
function Remove-CompatPartialBackup([string]$partialPath, [string]$message) {
  try { if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Recurse -Force } }
  catch { $message += "（作りかけのバックアップも削除できませんでした: ${partialPath}: $($_.Exception.Message)）" }
  throw $message
}

# 自動読み込みの登録を試験の間だけ外す。外す前に .reg へ書き出し、戻すための状態を $stateRef へ入れてから
# キーを削除する（削除の途中で失敗しても、呼び出し側の finally の Resume-CompatAutoload で戻せる）。
# 前回のバックアップが残っていれば、元の値を持つのはそのファイルだけなので、上書きせずに中止する。
function Suspend-CompatAutoload([string]$key, [string]$backupFile, [ref]$stateRef) {
  if (Test-Path -LiteralPath $backupFile) {
    throw "前回の実行で戻していない自動読み込みの登録のバックアップがあります。tests\tools\common\compat_autoload_restore.ps1 で戻してから実行してください: $backupFile"
  }
  if ([string]::IsNullOrEmpty($key) -or -not (Test-Path -LiteralPath $key)) { return }
  $snapshot = Get-CompatRegistryTreeSnapshot $key
  $nativeKey = (Get-Item -LiteralPath $key).Name
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupFile) | Out-Null
  # 書き出しが途中で失敗しても、書きかけのファイルが $backupFile として残らないように、一時の名前で書いてから名前を変える
  $partialFile = "$backupFile.partial"
  $exitCode = Invoke-CompatRegExe "export `"$nativeKey`" `"$partialFile`" /y"
  if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $partialFile)) {
    Remove-CompatPartialBackup $partialFile "自動読み込みの登録を書き出せませんでした（reg export の終了コード $exitCode）: $nativeKey"
  }
  Move-Item -LiteralPath $partialFile -Destination $backupFile
  $stateRef.Value = @{ Key = $key; BackupFile = $backupFile; Snapshot = $snapshot }
  Remove-Item -LiteralPath $key -Recurse -Force
  if (Test-Path -LiteralPath $key) { throw "自動読み込みの登録を外せませんでした: $nativeKey" }
  Write-Evidence "AUTOLOAD suspended key=$nativeKey backup=$backupFile"
}

# バックアップの .reg を読み込み、照合が一致したらバックアップを消す。失敗の文を配列で返す（一致すれば空）。
# $snapshot が $null のとき（戻すだけの実行）は、キーができたことだけを確かめる。
function Restore-CompatAutoloadCore([string]$key, [string]$backupFile, $snapshot) {
  $exitCode = Invoke-CompatRegExe "import `"$backupFile`""
  if ($exitCode -ne 0) { return @("自動読み込みの登録を読み込めませんでした（reg import の終了コード $exitCode）: $backupFile") }
  if (-not (Test-Path -LiteralPath $key)) { return @("読み込んだ後も自動読み込みの登録のキーがありません: $key") }
  if ($null -ne $snapshot) {
    $after = Get-CompatRegistryTreeSnapshot $key
    $diff = @(Compare-Object -ReferenceObject @($snapshot) -DifferenceObject @($after) -CaseSensitive)
    if ($diff.Count -gt 0) {
      return @("戻した自動読み込みの登録が外す前と一致しません: " + (($diff | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join '; '))
    }
  }
  Remove-Item -LiteralPath $backupFile -Force
  return @()
}

# Suspend-CompatAutoload で外した登録を戻す。CAD の終了後に呼ぶ。試験の間に CAD が同じキーを作っていれば、
# 消してから戻す（外す前と同じ値にする）。失敗は $cleanupFailures（参照）へ追加し、バックアップを残す。
function Resume-CompatAutoload($state, [ref]$cleanupFailures) {
  if ($null -eq $state) { return }
  try {
    if (Test-Path -LiteralPath $state.Key) { Remove-Item -LiteralPath $state.Key -Recurse -Force }
    $errors = @(Restore-CompatAutoloadCore $state.Key $state.BackupFile $state.Snapshot)
  } catch {
    $errors = @("自動読み込みの登録を戻せませんでした: $($_.Exception.Message)")
  }
  if ($errors.Count -eq 0) { Write-Evidence "AUTOLOAD restored key=$($state.Key)"; return }
  foreach ($e in $errors) { $cleanupFailures.Value += "$e（バックアップ: $($state.BackupFile)）" }
}

# 戻すだけの実行（compat_autoload_restore.ps1）。キーが既にあれば、利用者が登録し直した可能性があるので上書きしない。
function Restore-CompatAutoloadBackup([string]$key, [string]$backupFile) {
  if (-not (Test-Path -LiteralPath $backupFile)) { return @() }
  if (Test-Path -LiteralPath $key) { return @("登録のキーが既にあるため、バックアップから戻しませんでした: $key（バックアップ: $backupFile）") }
  return @(Restore-CompatAutoloadCore $key $backupFile $null)
}

# ARES 2023 以前は、インストール済みの CADPeta を profile.xml のスタートアップ（applications の load_cadpeta.lsp）で読み込む。
# 登録を外す処理（Remove-CadPetaFromProfile）と profile.xml の探し方（Get-AresProfileXmls）は、配布物の register.ps1・unregister.ps1 と共有する
. (Join-Path $PSScriptRoot "..\..\..\dist\compat\compat_common.ps1")

# profile.xml を扱う ARES の年。ARES 以外は $null
function Get-CompatProfileAutoloadYear([string]$product) {
  if ($product -match '^Ares(\d{4})$') { return $Matches[1] }
  return $null
}

# profile.xml のバックアップのフォルダ。戻せなかったときに compat_autoload_restore.ps1 で戻せるように、決まった場所に置く
function Get-CompatProfileBackupDir([string]$product) {
  Join-Path $env:LOCALAPPDATA "CadPeta-e2e\autoload-backup\$product.profile"
}

# ARES の profile.xml を試験の前の内容へ戻せるようにし、スタートアップの load_cadpeta.lsp（2023 以前の登録）を試験の間だけ外す。
# ARES は終了時に profile.xml を書き直し、最後に読み込んだ DLL のフォルダ（lastapploadfolder）などを残すので、登録の有無に
# かかわらず全体を $backupDir へコピーし、元の場所と登録の有無を paths.txt に書いてから、戻すための状態を $stateRef へ入れて
# 登録を外す（外す途中で失敗しても、呼び出し側の finally の Resume-CompatProfileAutoload で戻せる）。前回のバックアップが
# 残っていれば、元の内容を持つのはそのフォルダだけなので、変えずに中止する。
function Suspend-CompatProfileAutoload([string[]]$profileXmls, [string]$backupDir, [ref]$stateRef) {
  if (Test-Path -LiteralPath $backupDir) {
    throw "前回の実行で戻していない profile.xml のバックアップがあります。tests\tools\common\compat_autoload_restore.ps1 で戻してから実行してください: $backupDir"
  }
  # 空の一覧や $null は、[string[]] では空の文字列 1 つになるので除く
  $targets = @($profileXmls | Where-Object { $_ })
  if ($targets.Count -eq 0) { return }
  # 途中で失敗しても、作りかけのフォルダ（paths.txt の無いもの）が $backupDir として残らないように、一時の名前のフォルダで
  # 作り終えてから名前を変える。前の実行の一時のフォルダが残っていれば、元の profile.xml を変える前のものなので削除する
  $partialDir = "$backupDir.partial"
  if (Test-Path -LiteralPath $partialDir) { Remove-Item -LiteralPath $partialDir -Recurse -Force }
  $entries = New-Object System.Collections.Generic.List[object]
  try {
    New-Item -ItemType Directory -Path $partialDir | Out-Null
    for ($i = 0; $i -lt $targets.Count; $i++) {
      $partial = Join-Path $partialDir "$i.xml"
      Copy-Item -LiteralPath $targets[$i] -Destination $partial
      $entries.Add(@{ Path = $targets[$i]; Backup = (Join-Path $backupDir "$i.xml"); HadStartup = ([IO.File]::ReadAllText($partial) -match 'load_cadpeta\.lsp') })
    }
    [IO.File]::WriteAllLines((Join-Path $partialDir 'paths.txt'), [string[]]@($entries | ForEach-Object { "$([IO.Path]::GetFileName($_.Backup))|$([int]$_.HadStartup)|$($_.Path)" }), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::Move($partialDir, $backupDir)
  }
  catch {
    Remove-CompatPartialBackup $partialDir "profile.xml のバックアップを作れませんでした（profile.xml は変えていません）: $($_.Exception.Message)"
  }
  $stateRef.Value = @{ BackupDir = $backupDir; Entries = $entries.ToArray() }
  foreach ($entry in $entries) {
    Write-Evidence "PROFILE saved=$($entry.Path) backup=$($entry.Backup)"
    if (-not $entry.HadStartup) { continue }
    [void](Remove-CadPetaFromProfile $entry.Path)
    if ([IO.File]::ReadAllText($entry.Path) -match 'load_cadpeta\.lsp') { throw "スタートアップの登録を外せませんでした: $($entry.Path)" }
    Write-Evidence "AUTOLOAD suspended profile=$($entry.Path)"
  }
}

# バックアップの profile.xml を元の場所へコピーし、バイト列が一致したらバックアップのフォルダを消す。失敗の文を配列で返す
function Restore-CompatProfileCore($entries, [string]$backupDir) {
  $errors = New-Object System.Collections.Generic.List[string]
  foreach ($entry in $entries) {
    try {
      Copy-Item -LiteralPath $entry.Backup -Destination $entry.Path -Force
      if ((Get-FileSha256Shared $entry.Path) -ne (Get-FileSha256Shared $entry.Backup)) { $errors.Add("戻した profile.xml がバックアップと一致しません: $($entry.Path)") }
      # 外した登録が戻ったことも確かめる（バックアップが壊れていれば、一致しても登録が戻らない）
      elseif ($entry.HadStartup -and [IO.File]::ReadAllText($entry.Path) -notmatch 'load_cadpeta\.lsp') { $errors.Add("戻した profile.xml にスタートアップの登録がありません: $($entry.Path)") }
    } catch {
      $errors.Add("profile.xml を戻せませんでした: $($entry.Path): $($_.Exception.Message)")
    }
  }
  if ($errors.Count -eq 0) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
  return @($errors)
}

# Suspend-CompatProfileAutoload で保存した profile.xml を戻す。CAD の終了後に呼ぶ。失敗は $cleanupFailures（参照）へ追加し、バックアップを残す。
function Resume-CompatProfileAutoload($state, [ref]$cleanupFailures) {
  if ($null -eq $state) { return }
  $errors = @(Restore-CompatProfileCore @($state.Entries) $state.BackupDir)
  if ($errors.Count -eq 0) { Write-Evidence "PROFILE restored count=$(@($state.Entries).Count)"; return }
  foreach ($e in $errors) { $cleanupFailures.Value += "$e（バックアップ: $($state.BackupDir)）" }
}

# 戻すだけの実行（compat_autoload_restore.ps1）。paths.txt に書いた元の場所へ戻す。
function Restore-CompatProfileBackup([string]$backupDir) {
  if (-not (Test-Path -LiteralPath $backupDir)) { return @() }
  $entries = @([IO.File]::ReadAllLines((Join-Path $backupDir 'paths.txt'), [Text.Encoding]::UTF8) | Where-Object { $_ } | ForEach-Object {
    $name, $hadStartup, $path = $_.Split('|', 3)
    @{ Path = $path; Backup = (Join-Path $backupDir $name); HadStartup = ($hadStartup -eq '1') }
  })
  return @(Restore-CompatProfileCore $entries $backupDir)
}

# 製品の自動読み込みの登録を外す（試験のスクリプトの入口）。ARES 2024 以降はレジストリの登録を外す。ARES は全部の版で profile.xml を保存し、2023 以前はそのスタートアップの登録を外す。
# 登録が無い製品でも、前回のバックアップの検査は行う。状態は途中で失敗しても $stateRef に残し、Resume-CompatProductAutoload で戻す。
# 最初にインストール先（$InstallRoots。既定は Get-CadPetaInstallRoots）のファイルの名前と内容を記録し、Resume-CompatProductAutoload で比べる。
# 読めないファイルがあれば、何も変えずに例外で止まる。-KeepAutoload は、インストール済みの CADPeta を試験する実行のためのもので、
# インストール先の記録だけを行い、登録と profile.xml には触れない。
function Suspend-CompatProductAutoload([string]$product, [ref]$stateRef, [string[]]$InstallRoots = $null, [switch]$KeepAutoload) {
  if ($null -eq $InstallRoots) { $InstallRoots = Get-CadPetaInstallRoots }
  $state = @{ Registry = $null; Profile = $null; InstallTree = @(Save-RecordBaseline @($InstallRoots | ForEach-Object { New-RecordRow 'インストール先' $_ 'Verify' })) }
  $stateRef.Value = $state
  # ここから先は環境を変える（登録を外し、CAD を起動する）。戻し終えるまで、実行前の記録は取らせない
  Set-RecordEnvironmentChanged "Suspend-CompatProductAutoload（$product）"
  if ($KeepAutoload) { return }
  $registry = $null
  try { Suspend-CompatAutoload (Get-CompatAutoloadKey $product) (Get-CompatAutoloadBackupFile $product) ([ref]$registry) }
  finally { $state.Registry = $registry }
  $year = Get-CompatProfileAutoloadYear $product
  $profile = $null
  try {
    # if の式は空の配列を $null にするので、全体を @() で包む
    $xmls = @(if ($year) { Get-AresProfileXmls $year })
    Suspend-CompatProfileAutoload $xmls (Get-CompatProfileBackupDir $product) ([ref]$profile)
  }
  finally { $state.Profile = $profile }
}

# 自動読み込みの登録と profile.xml を戻してよいか。$exe のプロセスがまだ動いていれば、戻さずにバックアップを残す理由の文を返す（動いていなければ $null）。
# 動いている CAD は終了時に、メモリ上の設定で profile.xml とレジストリを書き直すので、先に戻すと外した登録と元の設定を失う。
# 試験の後（Resume-CompatProductAutoload）と戻すだけの実行（Restore-CompatProductBackups）の両方が、この関数で決める。
function Get-CompatCadRunningFailure([string]$exe) {
  if ([string]::IsNullOrEmpty($exe)) { return "CAD の実行ファイルが分からないため、自動読み込みの登録と profile.xml を戻さずにバックアップを残した" }
  $name = [IO.Path]::GetFileNameWithoutExtension($exe)
  $running = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {
      try { [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) } catch { $true }  # 場所を読めないプロセスは、同じ製品として扱う
    })
  if ($running.Count -eq 0) { return $null }
  return "$exe（PID: $(($running | ForEach-Object Id) -join ',')）が終了していないため、自動読み込みの登録と profile.xml を戻さずにバックアップを残した。" +
    "CAD を終了してから tests\tools\common\compat_autoload_restore.ps1 で戻すこと"
}

# 戻すだけの実行（compat_autoload_restore.ps1）。$exe の CAD が終了していることを確かめてから、レジストリの登録（$key が空なら扱わない）と
# profile.xml をバックアップから戻す。失敗の文を配列で返す（CAD が動いていれば、何も戻さずにその理由だけを返す）
function Restore-CompatProductBackups([string]$key, [string]$backupFile, [string]$profileBackupDir, [string]$exe) {
  $running = Get-CompatCadRunningFailure $exe
  if ($running) { return @($running) }
  $errors = @()
  if (-not [string]::IsNullOrEmpty($key)) { $errors += @(Restore-CompatAutoloadBackup $key $backupFile) }
  $errors += @(Restore-CompatProfileBackup $profileBackupDir)
  return $errors
}

# Suspend-CompatProductAutoload で外した登録を、CAD の終了後に戻す。失敗は $cleanupFailures（参照）へ追加する。
# $exe のプロセスがまだ動いていれば（Quit に失敗し、強制終了も許されていないとき）、戻さずにバックアップを残す（Get-CompatCadRunningFailure）。
function Resume-CompatProductAutoload($state, [string]$exe, [ref]$cleanupFailures) {
  if ($null -eq $state) { return }
  $running = Get-CompatCadRunningFailure $exe
  if ($running) { $cleanupFailures.Value += $running; return }
  $failuresBefore = @($cleanupFailures.Value).Count
  Resume-CompatAutoload $state.Registry $cleanupFailures
  Resume-CompatProfileAutoload $state.Profile $cleanupFailures
  # 登録と profile.xml を戻せたら、実行前の記録を取れるようにする（戻せなかったときはバックアップが残り、印も残す）
  if (@($cleanupFailures.Value).Count -eq $failuresBefore) { Clear-RecordEnvironmentChanged }
  # インストール先は戻さない。試験の前と違えば失敗の一覧に入れる
  if ($null -ne $state.InstallTree) {
    foreach ($d in (Compare-RecordBaseline $state.InstallTree @('Verify'))) { $cleanupFailures.Value += $d }
  }
}
