# Excel COM 単一ケース実行スクリプト（Phase 3 検証: 数万要素級 EMF の「図形に変換」閾値特定）。
# 別プロセスとして起動される前提（親側 run_case.ps1 がタイムアウト監視・強制終了を担う）。
# クリップボード貼付（CF_ENHMETAFILE）→「図形に変換」（Ungroup）を1ケース分実行し、
# 結果を JSON へ書く。Excel の PID は Ungroup 実行前に PidFile へ書き出す
# （親側が Ungroup 呼び出し中にハングしたと判断した場合、この PID を名指しで強制終了できる
# ようにするため。ユーザーが別途開いている Excel を誤って巻き込まないよう、
# GetWindowThreadProcessId で「自分が作った COM インスタンスの実PID」だけを特定する）。
#
# 図形数の数え上げ方針（既存ハーネス office_paste_regression.ps1 からの変更点）:
#   既存の Recurse-Ungroup は「葉かどうか」を Ungroup() を呼んで例外で判定していた
#   （3千図形規模では妥当）。だが本検証は最大数万〜十万規模のため、全葉に対して
#   COM例外を1個ずつ発生させると例外オーバーヘッドだけで実用外の時間がかかる
#   （例外は正常系より桁違いに遅い）。本スクリプトは Shape.Type で群（msoGroup=6）か
#   どうかを判定し、群のときだけ Ungroup() を呼ぶ。葉は例外を発生させずに数え上げる。
#   意味的には既存版と同じ（ネスト群があっても正しく展開する）が、例外を無駄打ちしない。
param(
    [Parameter(Mandatory=$true)][string]$Emf,
    [Parameter(Mandatory=$true)][string]$OutJson,
    [string]$Route = "clipboard",  # clipboard | file
    [string]$PidFile = $null,
    [switch]$Visible
)
$ErrorActionPreference = "Stop"
if (-not $PidFile) { $PidFile = "$OutJson.pid" }
Remove-Item $PidFile -ErrorAction SilentlyContinue

. "$PSScriptRoot\..\..\common\office_com.ps1"
. "$PSScriptRoot\..\..\common\threshold_case.ps1"

$result = [ordered]@{
    emf = $Emf
    route = $Route
    fileBytes = 0
    status = $null          # success | com_error | case_error
    widthMm = $null
    heightMm = $null
    topShapeType = $null
    leafShapeCount = $null
    groupLevelsFlattened = 0
    error = $null           # ex.Message
    errorFull = $null       # ex.ToString()（連鎖含む全文）
    hresult = $null
    shapesRemainingAfterFailure = $null
    excelPid = $null
    forceKilled = $false
}

$fileBytes = Read-CaseEmf $Emf $result $OutJson
if ($null -eq $fileBytes) { exit 1 }
$result.fileBytes = $fileBytes.Length

Add-Type -Name Clip -Namespace CadPetaThreshold -MemberDefinition @'
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


# msoGroup 定数（MsoShapeType 列挙）。「図形に変換」の起点シェイプ（貼付直後の
# msoPicture=13）は種別によらず必ず Ungroup() を呼ぶ（これが変換操作そのもの）。
# その結果得られた各シェイプは、種別が msoGroup のときだけ再度 Ungroup() を呼ぶ
# （ネスト群の展開）。葉（msoFreeform 等）は例外を発生させずにそのまま数える
$MSO_GROUP = 6


$excel = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = [bool]$Visible
    $excel.DisplayAlerts = $false
    # 自分が作った COM インスタンスの実PIDを特定する（$pid は自動変数 $PID と同一名で
    # 衝突するため使わない）。親プロセスのハング強制終了、および本スクリプト自身の
    # finally での「Quit() が効かなかった場合の強制終了」の両方で使う
    [uint32]$excelPid = 0
    try {
        $null = [CadPetaThreshold.Clip]::GetWindowThreadProcessId([IntPtr]$excel.Hwnd, [ref]$excelPid)
        if ($excelPid -ne 0) {
            "$excelPid" | Set-Content -Encoding ASCII $PidFile
            $result.excelPid = $excelPid
        }
    } catch { }

    $wb = $excel.Workbooks.Add()
    $ws = $wb.Worksheets.Item(1)
    $shape = $null
    if ($Route -eq "file") {
        $shape = $ws.Shapes.AddPicture($Emf, 0, -1, 10, 10, -1, -1)
    } else {
        Register-Emf ([CadPetaThreshold.Clip]) $Emf $fileBytes $CF_ENHMETAFILE
        $shape = Add-EmfShapeByPasteSpecial $ws
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
        try { $result.shapesRemainingAfterFailure = $ws.Shapes.Count } catch { }
    }
    try { $wb.Close($false) } catch { }
} catch {
    $result.status = "case_error"
    $result.error = $_.Exception.Message
    $result.errorFull = $_.Exception.ToString()
    try { $result.hresult = "0x{0:X8}" -f $_.Exception.HResult } catch { }
} finally {
    if ($excel) {
        try { $excel.Quit() } catch { }
        try { [Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null } catch { }
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    # Quit() は「閉じる要求」であり実プロセス終了を保証しない（Ungroup の COM エラーで
    # Excel が不安定化した後は特に、実測でプロセスが残ることを確認済み）。実PIDが分かって
    # いれば猶予を置いて実際の終了を確認し、終了しなければ名指しで強制終了する
    # （ユーザーが別途開いている他の Excel を巻き込まないよう、PID 一致でのみ操作する）
    if ($excelPid -ne 0) {
        # 実測（2026-07-10）: この環境では自然終了が10秒枠内でも観測されず、常に強制終了に
        # 落ちる（N=10 の軽量ケースでも同様のため、Excel COM の実装特性であり本ハーネスの
        # 不具合ではない）。結果は Quit() 呼び出し前に result へ確定済みのため、猶予を
        # 短縮しても正しさに影響しない。スイープの総所要時間を優先し5秒とする
        $deadline = (Get-Date).AddSeconds(5)
        $alive = $true
        while ((Get-Date) -lt $deadline) {
            $p = Get-Process -Id $excelPid -ErrorAction SilentlyContinue
            if (-not $p) { $alive = $false; break }
            Start-Sleep -Milliseconds 500
        }
        if ($alive) {
            try {
                Stop-Process -Id $excelPid -Force -ErrorAction Stop
                $result.forceKilled = $true
            } catch { }
        }
    }
}

($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $OutJson
Remove-Item $PidFile -ErrorAction SilentlyContinue
if ($result.status -eq "success") { exit 0 } else { exit 1 }
