# CADPeta のダイアログ（PETAOPT の設定のダイアログと、PETA のコメントのダイアログ）を UI オートメーションで確かめる関数。
# AutoCAD の画面の確認（regression\petaopt_gui_check.ps1）と、互換 CAD のスモーク（regression\peta_option_smoke_compat.ps1）が読み込み、
# 4 製品で同じ手順と同じ判定を使う。互換 CAD のスモークは Windows PowerShell 5.1 で動くので、5.1 の構文で書く。
# 判定は @{ Name; Ok; Detail } の配列で返し、呼び出し側が自分の判定の一覧に加える。
. "$PSScriptRoot\ribbon_ui.ps1"

# PETAOPT のダイアログで設定するファイル名の形（続く PETA がコメントのダイアログを開く）と、コメントのダイアログで入力する文字列
$CadPetaCommentFileName = '{drawing}_{tab}_{datetime}_{comment}'
$CadPetaComment = '立面'
$CadPetaInvalidComment = '立面/1'
$CadPetaCommentIds = @('CadPeta.Comment.Input', 'CadPeta.Comment.Status', 'CadPeta.Comment.Ok', 'CadPeta.Comment.Cancel')
$CadPetaSettingsIds = @('CadPeta.Settings.Folder', 'CadPeta.Settings.FileName', 'CadPeta.Settings.Format.Png', 'CadPeta.Settings.PngDpi', 'CadPeta.Settings.Message', 'CadPeta.Settings.Ok', 'CadPeta.Settings.Cancel')
# ダイアログの中心と CAD のメイン ウィンドウの中心の差として許す画素数（WPF が位置を整数の画素に丸める分）
$CenterTolerancePx = 2
$VkReturn = 0x0D
$VkEscape = 0x1B

# プロセス processId のメイン ウィンドウ（起動の途中で無ければ 0）
function Get-CadMainWindow([int]$processId) {
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($proc) { $proc.MainWindowHandle } else { [IntPtr]::Zero }
}

# プロセス processId のウィンドウから、ID が id の部品を持つダイアログを探す（設定のダイアログは PNG の解像度の入力欄、コメントのダイアログは
# 入力欄）。見つからなければ null。CAD のメイン ウィンドウは除く（UI オートメーションのツリーでは、CAD が所有するダイアログの部品が
# メイン ウィンドウの下にも出るので、メイン ウィンドウをダイアログと取り違える）。メイン ウィンドウは起動の途中で現れるので、探すたびに読む
function Find-CadDialog([int]$processId, [string]$id, [datetime]$deadline) {
    while ((Get-Date) -lt $deadline -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        $main = Get-CadMainWindow $processId
        foreach ($h in [CadPeta.Gui.Win]::TopWindows([uint32]$processId) | Where-Object { $_ -ne $main }) {
            $element = [Windows.Automation.AutomationElement]::FromHandle($h)
            if (Find-ById $element $id) { return $element }
        }
        Start-Sleep -Milliseconds 500
    }
    $null
}

# ダイアログの部品を、操作する前にすべて探す（最初の部品が見えた時点では、ほかの部品がまだ UI オートメーションのツリーに無いことがある）。
# 戻り値: ID から要素への表。見つからない部品があれば、その ID の一覧を $missing に入れる
function Get-DialogParts($dialog, [string[]]$ids, [datetime]$deadline, [ref]$missing) {
    $parts = @{}
    do {
        foreach ($id in $ids) { if (-not $parts[$id]) { $parts[$id] = Find-ById $dialog $id } }
        $missing.Value = @($ids | Where-Object { -not $parts[$_] })
        if ($missing.Value.Count) { Start-Sleep -Milliseconds 300 }
    } while ($missing.Value.Count -and (Get-Date) -lt $deadline)
    $parts
}

# ウィンドウ hwnd の画面の枠（GetWindowRect）。読めなければ例外
function Get-WindowRect([IntPtr]$hwnd) {
    $rect = New-Object CadPeta.Gui.Win+Rect
    if (-not [CadPeta.Gui.Win]::GetWindowRect($hwnd, [ref]$rect)) { throw "ウィンドウ $hwnd の枠を読めない（GetWindowRect が失敗した）" }
    $rect
}

function Format-WindowRect($rect) { "$($rect.Left),$($rect.Top)-$($rect.Right),$($rect.Bottom)" }

function New-DialogCheck([string]$name, [bool]$ok, [string]$detail = '') { @{ Name = $name; Ok = $ok; Detail = $detail } }

# PETAOPT のダイアログを確かめて設定する: 題名、入力欄のツールチップ、設定のファイルの場所のツールチップ（マウスを重ねる）、
# PNG をオン・PNG の解像度を 150・ファイル名を CadPetaCommentFileName にして OK を押す。
# imageDir にダイアログとツールチップの画像を残す
function Invoke-SettingsDialogChecks([int]$processId, [string]$settingsPath, [string]$imageDir, [datetime]$deadline) {
    $checks = New-Object System.Collections.Generic.List[object]
    $dialog = Find-CadDialog $processId 'CadPeta.Settings.PngDpi' $deadline
    $checks.Add((New-DialogCheck 'dialogOpened' ([bool]$dialog)))
    if (-not $dialog) { return $checks.ToArray() }
    $missing = @()
    $parts = Get-DialogParts $dialog $CadPetaSettingsIds ([datetime]::Now.AddSeconds(30)) ([ref]$missing)
    $checks.Add((New-DialogCheck 'dialogParts' ($missing.Count -eq 0) ($missing -join ', ')))
    if ($missing.Count) { return $checks.ToArray() }
    Save-ElementImage $dialog (Join-Path $imageDir 'dialog.png')
    $checks.Add((New-DialogCheck 'dialogTitle' ($dialog.Current.Name -eq 'CADPeta の設定') $dialog.Current.Name))
    # 出力先とファイル名の入力欄に、説明のツールチップがある（WPF は ToolTip を UI オートメーションの HelpText にする）
    foreach ($id in 'CadPeta.Settings.Folder', 'CadPeta.Settings.FileName') {
        $help = $parts[$id].Current.HelpText
        $checks.Add((New-DialogCheck "tooltip:$id" ($help.Length -gt 0) ($help -replace '\r?\n', ' / ')))
    }
    # 左下の設定のファイルの場所は途中で省略されるので、マウスを重ねるとツールチップに全体が出る
    $file = Find-ById $dialog 'CadPeta.Settings.File'
    $reason = if ($file) { Test-HoverTooltip $processId $dialog $file @($settingsPath) (Join-Path $imageDir 'tooltip-settings-file.png') $deadline @(Get-CadMainWindow $processId) } else { '設定のファイルの場所の表示が見つからない' }
    $checks.Add((New-DialogCheck 'settingsFileTooltip' ($reason -eq '') $reason))
    $parts['CadPeta.Settings.Format.Png'].GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    $dpi = $parts['CadPeta.Settings.PngDpi'].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    # ファイル名の下の 1 行に、使える値ならファイル名のプレビューを、使えない値があればその理由を出す（コメントのダイアログと同じ。
    # メッセージだけの行を別に取らない）。使えない値では OK を押せない
    $dpi.SetValue('1')
    Start-Sleep -Milliseconds 300
    $status = $parts['CadPeta.Settings.Message'].Current.Name
    $checks.Add((New-DialogCheck 'settingsInvalidBlocksOk' (-not $parts['CadPeta.Settings.Ok'].Current.IsEnabled -and $status.Length -gt 0 -and -not $status.StartsWith('プレビュー: ')) $status))
    $dpi.SetValue('150')
    Start-Sleep -Milliseconds 300
    $status = $parts['CadPeta.Settings.Message'].Current.Name
    $checks.Add((New-DialogCheck 'settingsPreview' ($status.StartsWith('プレビュー: ')) $status))
    $checks.Add((New-DialogCheck 'dpiEntered' ($dpi.Current.Value -eq '150') $dpi.Current.Value))
    $parts['CadPeta.Settings.FileName'].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($CadPetaCommentFileName)
    $ok = $parts['CadPeta.Settings.Ok']
    $checks.Add((New-DialogCheck 'okEnabled' $ok.Current.IsEnabled))
    Invoke-Element $ok
    $checks.ToArray()
}

# PETA のコメントのダイアログを確かめて、コメントを入力し Enter で確定する: 題名、開いた位置（CAD のメイン ウィンドウの中央）、
# 入力欄のフォーカス、使えない文字で OK を押せず理由を出すこと、使えるコメントでファイル名（図面名 stem で始まる）を表示すること。
# Enter が確定したかは、呼び出し側が試験の記録（ShowCommentDialog）で確かめる。image にダイアログの画像を残す
function Invoke-CommentDialogChecks([int]$processId, [string]$stem, [string]$image, [datetime]$deadline) {
    $checks = New-Object System.Collections.Generic.List[object]
    $dialog = Find-CadDialog $processId 'CadPeta.Comment.Input' $deadline
    $checks.Add((New-DialogCheck 'commentDialogOpened' ([bool]$dialog)))
    if (-not $dialog) { return $checks.ToArray() }
    $missing = @()
    $parts = Get-DialogParts $dialog $CadPetaCommentIds ([datetime]::Now.AddSeconds(30)) ([ref]$missing)
    $checks.Add((New-DialogCheck 'commentDialogParts' ($missing.Count -eq 0) ($missing -join ', ')))
    if ($missing.Count) { return $checks.ToArray() }
    $checks.Add((New-DialogCheck 'commentDialogTitle' ($dialog.Current.Name -eq 'ファイル名に入れるコメント') $dialog.Current.Name))
    # CAD のメイン ウィンドウの中央に開く（カーソルの位置に開くと、画面の端でボタンがカーソルの下に来る、CAD と別のモニターに出る）。
    # 2 つの枠は同じ GetWindowRect で測る。UI オートメーションの BoundingRectangle は、ARES 2023 の Qt のメイン ウィンドウでは
    # GetWindowRect（WPF の CenterOwner が中央を求める枠）より上で 9 画素、左右と下で 3 画素小さく、中央に開いても 3 画素ずれて見えた
    $rect = Get-WindowRect ([IntPtr]$dialog.Current.NativeWindowHandle)
    $main = Get-WindowRect (Get-CadMainWindow $processId)
    $offsetX = ($rect.Left + $rect.Right) / 2 - ($main.Left + $main.Right) / 2
    $offsetY = ($rect.Top + $rect.Bottom) / 2 - ($main.Top + $main.Bottom) / 2
    $checks.Add((New-DialogCheck 'commentDialogCentered' ([Math]::Abs($offsetX) -le $CenterTolerancePx -and [Math]::Abs($offsetY) -le $CenterTolerancePx) "ダイアログ $(Format-WindowRect $rect)、メイン ウィンドウ $(Format-WindowRect $main)"))
    $input = $parts['CadPeta.Comment.Input']
    $checks.Add((New-DialogCheck 'commentInputFocused' $input.Current.HasKeyboardFocus))
    $value = $input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue($CadPetaInvalidComment)
    Start-Sleep -Milliseconds 300
    $status = $parts['CadPeta.Comment.Status'].Current.Name
    $checks.Add((New-DialogCheck 'commentInvalidBlocksOk' (-not $parts['CadPeta.Comment.Ok'].Current.IsEnabled -and $status.Contains($CadPetaInvalidComment)) $status))
    $value.SetValue($CadPetaComment)
    Start-Sleep -Milliseconds 300
    $status = $parts['CadPeta.Comment.Status'].Current.Name
    $expected = '^プレビュー: ' + [regex]::Escape($stem) + '_.*_' + [regex]::Escape($CadPetaComment) + '$'
    $checks.Add((New-DialogCheck 'commentPreview' ($parts['CadPeta.Comment.Ok'].Current.IsEnabled -and $status -match $expected) $status))
    Save-ElementImage $dialog $image
    $sent = [CadPeta.Gui.Win]::PostKey([IntPtr]$dialog.Current.NativeWindowHandle, $VkReturn)
    $checks.Add((New-DialogCheck 'commentEnterSent' $sent))
    $checks.ToArray()
}

# PETA のコメントのダイアログを、how（Cancel は [キャンセル] のボタン、Escape は Esc のキー）で閉じる。閉じる操作を送れたら空、送れなければ理由
function Close-CommentDialog([int]$processId, [ValidateSet('Cancel', 'Escape')][string]$how, [datetime]$deadline) {
    $dialog = Find-CadDialog $processId 'CadPeta.Comment.Input' $deadline
    if (-not $dialog) { return 'コメントのダイアログが開かなかった' }
    $missing = @()
    $parts = Get-DialogParts $dialog $CadPetaCommentIds $deadline ([ref]$missing)
    if ($missing.Count) { return "部品が無い: $($missing -join ', ')" }
    if ($how -eq 'Cancel') { Invoke-Element $parts['CadPeta.Comment.Cancel']; return '' }
    if ([CadPeta.Gui.Win]::PostKey([IntPtr]$dialog.Current.NativeWindowHandle, $VkEscape)) { '' } else { 'Esc を送れなかった' }
}
