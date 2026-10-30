# PowerPoint COM 単一ケース実行スクリプト（Phase 4 検証: 数万要素級 EMF の「図形に変換」閾値特定）。
# tests/tools/cad-typography/emf_ungroup_threshold/ungroup_case.ps1（Excel 版）の PowerPoint 移植。
# クリップボード貼付（CF_ENHMETAFILE）→「図形に変換」（Ungroup）を1ケース分実行し JSON へ書く。
#
# Excel 版との差:
#   - COM は PowerPoint.Application。PowerPoint は不可視モードでの貼付が不安定なため Visible=msoTrue で走る
#     （閾値は図形数だけに依存する内部制約であり、可視・不可視で結果は変わらない）
#   - 貼付は Slide.Shapes.PasteSpecial(ppPasteEnhancedMetafile=2)。返り値の ShapeRange から
#     貼付シェイプを取る
#   - 図形の数え上げ・ネスト群の展開（msoGroup=6 のときだけ Ungroup を呼ぶ）は Excel 版と同一方針
param(
    [Parameter(Mandatory=$true)][string]$Emf,
    [Parameter(Mandatory=$true)][string]$OutJson,
    [string]$Route = "clipboard",  # clipboard | file
    [string]$PidFile = $null
)
$ErrorActionPreference = "Stop"
if (-not $PidFile) { $PidFile = "$OutJson.pid" }
Remove-Item $PidFile -ErrorAction SilentlyContinue

. "$PSScriptRoot\..\..\common\office_com.ps1"
. "$PSScriptRoot\..\..\common\threshold_case.ps1"

$result = [ordered]@{
    emf = $Emf
    route = $Route
    app = "powerpoint"
    fileBytes = 0
    status = $null          # success | com_error | case_error
    widthMm = $null
    heightMm = $null
    topShapeType = $null
    leafShapeCount = $null
    groupLevelsFlattened = 0
    error = $null
    errorFull = $null
    hresult = $null
    shapesRemainingAfterFailure = $null
    pptPid = $null
    forceKilled = $false
}

$fileBytes = Read-CaseEmf $Emf $result $OutJson
if ($null -eq $fileBytes) { exit 1 }
$result.fileBytes = $fileBytes.Length

Add-Type -Name Clip -Namespace CadPetaPptThreshold -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
[DllImport("user32.dll")] public static extern bool EmptyClipboard();
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetClipboardData(uint f, IntPtr h);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint f);
[DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetEnhMetaFileW(string f);
[DllImport("gdi32.dll")] public static extern IntPtr CopyEnhMetaFileW(IntPtr h, string f);
[DllImport("gdi32.dll")] public static extern bool DeleteEnhMetaFile(IntPtr h);
[DllImport("gdi32.dll")] public static extern uint GetEnhMetaFileBits(IntPtr h, uint c, byte[] b);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
'@
$CF_ENHMETAFILE = 14


$MSO_GROUP = 6


$ppt = $null
[uint32]$pptPid = 0
try {
    $ppt = New-Object -ComObject PowerPoint.Application
    # PowerPoint は不可視での貼付が不安定なため可視で走る（DisplayAlerts はオフ）
    $ppt.Visible = 1
    try { $ppt.DisplayAlerts = 1 } catch { }  # ppAlertsNone=1

    try {
        $null = [CadPetaPptThreshold.Clip]::GetWindowThreadProcessId([IntPtr]$ppt.HWND, [ref]$pptPid)
        if ($pptPid -ne 0) {
            "$pptPid" | Set-Content -Encoding ASCII $PidFile
            $result.pptPid = $pptPid
        }
    } catch { }

    $pres = $ppt.Presentations.Add(1)          # WithWindow=msoTrue
    $slide = $pres.Slides.Add(1, 12)           # 12 = ppLayoutBlank
    $shape = $null
    if ($Route -eq "file") {
        # msoFalse=0（リンクしない・埋め込み）、-1=既定サイズ
        $shape = $slide.Shapes.AddPicture($Emf, 0, -1, 10, 10, -1, -1)
    } else {
        Register-Emf ([CadPetaPptThreshold.Clip]) $Emf $fileBytes $CF_ENHMETAFILE
        $before = $slide.Shapes.Count
        $range = $slide.Shapes.PasteSpecial(2)  # 2 = ppPasteEnhancedMetafile
        $shape = if ($range.Count -ge 1) { $range.Item(1) } else { $slide.Shapes.Item($slide.Shapes.Count) }
    }
    $result.widthMm = PtToMm $shape.Width
    $result.heightMm = PtToMm $shape.Height
    $result.topShapeType = $shape.Type

    try {
        $flat = Flatten-Shape $shape
        $result.status = "success"
        $result.leafShapeCount = $flat.leafCount
        $result.groupLevelsFlattened = $flat.levels
    } catch {
        $result.status = "com_error"
        $result.error = $_.Exception.Message
        $result.errorFull = $_.Exception.ToString()
        try { $result.hresult = "0x{0:X8}" -f $_.Exception.HResult } catch { }
        try { $result.shapesRemainingAfterFailure = $slide.Shapes.Count } catch { }
    }
    try { $pres.Close() } catch { }
} catch {
    $result.status = "case_error"
    $result.error = $_.Exception.Message
    $result.errorFull = $_.Exception.ToString()
    try { $result.hresult = "0x{0:X8}" -f $_.Exception.HResult } catch { }
} finally {
    if ($ppt) {
        try { $ppt.Quit() } catch { }
        try { [Runtime.InteropServices.Marshal]::ReleaseComObject($ppt) | Out-Null } catch { }
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    if ($pptPid -ne 0) {
        $deadline = (Get-Date).AddSeconds(5)
        $alive = $true
        while ((Get-Date) -lt $deadline) {
            $p = Get-Process -Id $pptPid -ErrorAction SilentlyContinue
            if (-not $p) { $alive = $false; break }
            Start-Sleep -Milliseconds 500
        }
        if ($alive) {
            try { Stop-Process -Id $pptPid -Force -ErrorAction Stop; $result.forceKilled = $true } catch { }
        }
    }
}

($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
Remove-Item $PidFile -ErrorAction SilentlyContinue
if ($result.status -eq "success") { exit 0 } else { exit 1 }
