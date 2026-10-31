# 画面のある CAD のリボンとダイアログを UI オートメーションで操作する関数。AutoCAD の画面の確認（regression\petaopt_gui_check.ps1）が読み込む。
# ARES の画面（Qt）は UI オートメーションにリボンを出さないので、ARES には使えない。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
. "$PSScriptRoot\screen_capture.ps1"

if (-not ('CadPeta.Gui.Win' -as [type])) {
    Add-Type -Namespace CadPeta.Gui -Name Win -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct CopyData { public System.IntPtr dwData; public int cbData; public System.IntPtr lpData; }
[System.Runtime.InteropServices.DllImport("user32.dll")] static extern System.IntPtr SendMessageTimeout(System.IntPtr hwnd, uint msg, System.IntPtr wParam, ref CopyData lParam, uint flags, uint timeout, out System.IntPtr result);
// AutoCAD のメイン ウィンドウへ、コマンド ラインに入力する文字列を送る（WM_COPYDATA、dwData = 1、UTF-16 の文字列）。送れたら true
public static bool SendCommandLine(System.IntPtr hwnd, string text) {
    var data = new CopyData { dwData = new System.IntPtr(1), cbData = (text.Length + 1) * 2, lpData = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(text) };
    try { System.IntPtr r; return SendMessageTimeout(hwnd, 0x004A, System.IntPtr.Zero, ref data, 0x0002, 10000, out r) != System.IntPtr.Zero; }
    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(data.lpData); }
}
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(System.IntPtr hwnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
// ウィンドウ hwnd へ、仮想キー vk を押して離したこと（WM_KEYDOWN と WM_KEYUP）を送る。ほかのアプリのキーボード入力を使わない。送れたら true
public static bool PostKey(System.IntPtr hwnd, int vk) {
    return PostMessage(hwnd, 0x0100, new System.IntPtr(vk), new System.IntPtr(1))
        && PostMessage(hwnd, 0x0101, new System.IntPtr(vk), new System.IntPtr(unchecked((int)0xC0000001)));
}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct ScreenPoint { public int X; public int Y; }
[System.Runtime.InteropServices.DllImport("user32.dll")] static extern System.IntPtr WindowFromPoint(ScreenPoint p);
// 画面の点 (x, y) に表示されているウィンドウのプロセスの ID
public static uint ProcessAt(int x, int y) { uint p; GetWindowThreadProcessId(WindowFromPoint(new ScreenPoint { X = x, Y = y }), out p); return p; }
// プロセス pid の表示されている最上位のウィンドウ
public static System.Collections.Generic.List<System.IntPtr> TopWindows(uint pid) {
    var list = new System.Collections.Generic.List<System.IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, System.IntPtr.Zero);
    return list;
}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hwnd, out Rect rect);
'@
}

function Find-ById($root, [string]$id) {
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}

# 名前が name で、押せる（Invoke か SelectionItem の）要素を探す
function Find-Pressable($root, [string]$name) {
    $byName = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty, $name)
    foreach ($e in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $byName)) {
        if ($e.Current.IsEnabled -and ($e.GetSupportedPatterns() | Where-Object { $_.ProgrammaticName -match 'InvokePattern|SelectionItemPattern' })) { return $e }
    }
    $null
}

function Invoke-Element($e) {
    $pattern = $null
    if ($e.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

# 要素の範囲を画面から取り込んで PNG に保存する
function Save-ElementImage($element, [string]$path) {
    $rect = $element.Current.BoundingRectangle
    Save-ScreenImage ([int]$rect.X) ([int]$rect.Y) ([int]$rect.Width) ([int]$rect.Height) $path
}

# メイン ウィンドウ mainHandle のリボンで「アドイン」タブを開き、表示名が name のボタンが画面に出るまで待つ（どちらも 10 秒まで）。
# 見つかればその要素、無ければ null で理由（タブが無ければ見えたタブの名前）を $reason に入れる
function Find-RibbonButton([IntPtr]$mainHandle, [string]$name, [ref]$reason) {
    $main = [Windows.Automation.AutomationElement]::FromHandle($mainHandle)
    $limit = (Get-Date).AddSeconds(10)
    while (-not ($tab = Find-Pressable $main 'アドイン') -and (Get-Date) -lt $limit) { Start-Sleep -Milliseconds 300 }
    if (-not $tab) {
        $byType = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem)
        $tabs = $main.FindAll([Windows.Automation.TreeScope]::Descendants, $byType) | ForEach-Object { $_.Current.Name }
        $reason.Value = "「アドイン」タブが見つからない（見えたタブ: $($tabs -join ', ')）"
        return $null
    }
    Invoke-Element $tab
    $limit = (Get-Date).AddSeconds(10)
    do {
        $button = Find-Pressable $main $name
        if ($button -and -not $button.Current.IsOffscreen) { return $button }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $limit)
    $reason.Value = "「$name」のボタンが見つからない"
    $null
}
# 要素 target にマウスを重ね、出たツールチップ（プロセス processId の、window と exclude 以外のウィンドウ）に、expected の文字列が
# すべて部品の名前として書かれているかを確かめる。ツールチップの画像を image に残す（出なければ window の画像を missing- を付けた名前で残す）。
# マウスは確かめる間だけ動かし、元の位置へ戻す。書かれていれば空、無ければ理由
function Test-HoverTooltip([int]$processId, $window, $target, [string[]]$expected, [string]$image, [datetime]$deadline, [IntPtr[]]$exclude = @()) {
    $windowHandle = [IntPtr]$window.Current.NativeWindowHandle
    # ツールチップは、マウスがその要素の上にあるときだけ出る。ウィンドウを前面にし、要素の上にほかのウィンドウが無いことを確かめる
    # （利用者がほかのアプリを使っていると前面にできないことがある）
    [void][CadPeta.Gui.Win]::SetForegroundWindow($windowHandle)
    Start-Sleep -Milliseconds 500
    $rect = $target.Current.BoundingRectangle
    $x = [int]($rect.X + [Math]::Min($rect.Width / 2, 20))
    $y = [int]($rect.Y + $rect.Height / 2)
    $owner = [CadPeta.Gui.Win]::ProcessAt($x, $y)
    if ($owner -ne $processId) { return "要素の上にほかのプロセス（PID $owner）のウィンドウがある" }
    $saved = [Windows.Forms.Cursor]::Position
    try {
        [Windows.Forms.Cursor]::Position = New-Object Drawing.Point $x, $y
        $limit = [datetime]::Now.AddSeconds(15)
        if ($limit -gt $deadline) { $limit = $deadline }
        $seen = @()
        $userMoves = 0
        while ((Get-Date) -lt $limit) {
            # 待つ間に利用者がマウスを動かしたら、要素の上へ戻し、回数を理由に書く
            $now = [Windows.Forms.Cursor]::Position
            if ($now.X -ne $x -or $now.Y -ne $y) {
                $userMoves++
                [Windows.Forms.Cursor]::Position = New-Object Drawing.Point $x, $y
            }
            foreach ($h in [CadPeta.Gui.Win]::TopWindows([uint32]$processId)) {
                if ($h -eq $windowHandle -or $exclude -contains $h) { continue }
                $tip = [Windows.Automation.AutomationElement]::FromHandle($h)
                $texts = @($tip.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
                    ForEach-Object { $_.Current.Name } | Where-Object { $_ })
                if ($texts.Count) { $seen = $texts }
                if (-not @($expected | Where-Object { $texts -notcontains $_ }).Count) {
                    Save-ElementImage $tip $image
                    return ''
                }
            }
            Start-Sleep -Milliseconds 300
        }
        $missing = Join-Path (Split-Path -Parent $image) ('missing-' + (Split-Path -Leaf $image))
        Save-ElementImage $window $missing
        "ツールチップに $($expected -join '・') が無い（見えた文字列: $($seen -join ' / ')。待つ間にマウスが動かされた回数: $userMoves。画面: $missing）"
    }
    finally { [Windows.Forms.Cursor]::Position = $saved }
}

# リボンの表示名が name のボタンのツールチップに、題名の name とコマンド名の command が出るかを確かめる。出れば空、出なければ理由
function Test-RibbonTooltip([int]$processId, [IntPtr]$mainHandle, [string]$name, [string]$command, [string]$image, [datetime]$deadline) {
    $reason = ''
    $button = Find-RibbonButton $mainHandle $name ([ref]$reason)
    if (-not $button) { return $reason }
    Test-HoverTooltip $processId ([Windows.Automation.AutomationElement]::FromHandle($mainHandle)) $button @($name, $command) $image $deadline
}
