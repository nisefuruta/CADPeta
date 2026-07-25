# 実プロセスが所有する非表示HWNDでクリップボードを開き、競合状態を決定的に作る。
param(
    [Parameter(Mandatory)][string]$ReadyPath,
    [Parameter(Mandatory)][string]$ReleasePath,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace CadPetaRegression -Name ClipboardNative -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError=true)]
public static extern bool OpenClipboard(System.IntPtr owner);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError=true)]
public static extern bool CloseClipboard();
'@

$window = [System.Windows.Forms.NativeWindow]::new()
$window.CreateHandle([System.Windows.Forms.CreateParams]::new())
try {
    if (-not [CadPetaRegression.ClipboardNative]::OpenClipboard($window.Handle)) {
        throw "OpenClipboardに失敗した: Win32=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    try {
        [pscustomobject]@{ pid = $PID; hwnd = $window.Handle.ToInt64() } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath $ReadyPath -Encoding UTF8
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (-not (Test-Path -LiteralPath $ReleasePath) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 50
        }
        if (-not (Test-Path -LiteralPath $ReleasePath)) {
            throw "解放指示が${TimeoutSeconds}秒以内に届かなかった"
        }
    }
    finally {
        if (-not [CadPetaRegression.ClipboardNative]::CloseClipboard()) {
            throw "CloseClipboardに失敗した: Win32=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
        }
    }
}
finally {
    $window.DestroyHandle()
}
