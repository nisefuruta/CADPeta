# 複雑線種の記号展開チャート EMF の Office 分解 E2E（Phase 3 統合検証）。
# 既存の office_paste_regression.ps1（Phase 1、寸法・分解数・文字回収）と
# emf_ungroup_threshold/ungroup_case.ps1（Phase 3、msoGroup 判定で例外を無駄打ちしない
# 高速分解・実PID安全キル）の型を踏襲する。
# 目的: 記号（SHX シェイプ/文字）由来の path が「図形に変換」後も図形として存在することを
# 確認し、分解後図形数・貼付実寸を実数で報告する。leaf を msoFreeform かつ Nodes.Count が
# 多い（>=4）ものを「記号候補」として分類する（単純な2点線分・破線片との区別のため）。
param(
    [string]$Emf = "$env:TEMP\cadpeta_p3b_ltsym_matrix\2026\test_chart_Model.emf",
    [string]$OutJson = "$env:TEMP\cadpeta_p3b_ltsym_matrix\office_decompose.json",
    [switch]$Ppt
)
$ErrorActionPreference = "Continue"
function PtToMm($pt) { return [Math]::Round($pt * 25.4 / 72, 3) }

if (-not (Test-Path $Emf)) { throw "EMF がない: $Emf" }
$fileBytes = [IO.File]::ReadAllBytes($Emf)

Add-Type -Name Clip -Namespace CadPetaLtsymDecompose -MemberDefinition @'
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
$MSO_FREEFORM = 5

function Register-Emf {
    $hemf = [CadPetaLtsymDecompose.Clip]::GetEnhMetaFileW($Emf)
    if ($hemf -eq [IntPtr]::Zero) { throw "GetEnhMetaFile 失敗: $Emf" }
    $hCopy = [CadPetaLtsymDecompose.Clip]::CopyEnhMetaFileW($hemf, [NullString]::Value)
    $null = [CadPetaLtsymDecompose.Clip]::DeleteEnhMetaFile($hemf)
    if ($hCopy -eq [IntPtr]::Zero) { throw "CopyEnhMetaFile 失敗" }
    if (-not [CadPetaLtsymDecompose.Clip]::OpenClipboard([IntPtr]::Zero)) { throw "OpenClipboard 失敗" }
    try {
        $null = [CadPetaLtsymDecompose.Clip]::EmptyClipboard()
        if ([CadPetaLtsymDecompose.Clip]::SetClipboardData($CF_ENHMETAFILE, $hCopy) -eq [IntPtr]::Zero) { throw "SetClipboardData 失敗" }
    } finally { $null = [CadPetaLtsymDecompose.Clip]::CloseClipboard() }
    if (-not [CadPetaLtsymDecompose.Clip]::OpenClipboard([IntPtr]::Zero)) { throw "OpenClipboard(照合) 失敗" }
    try {
        $h = [CadPetaLtsymDecompose.Clip]::GetClipboardData($CF_ENHMETAFILE)
        $size = [CadPetaLtsymDecompose.Clip]::GetEnhMetaFileBits($h, 0, $null)
        $got = New-Object byte[] $size
        $null = [CadPetaLtsymDecompose.Clip]::GetEnhMetaFileBits($h, $size, $got)
        if (-not [Linq.Enumerable]::SequenceEqual($got, $fileBytes)) { throw "登録内容がファイルと不一致" }
    } finally { $null = [CadPetaLtsymDecompose.Clip]::CloseClipboard() }
}

# msoGroup 判定で Ungroup を呼ぶ（葉は例外を発生させない。ungroup_case.ps1 と同方式）
function Flatten-Shape($shape) {
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue(@{ s = $shape; isTop = $true })
    $leaves = New-Object System.Collections.Generic.List[object]
    $levels = 0
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $s = $item.s
        if ($item.isTop -or $s.Type -eq $MSO_GROUP) {
            $range = $s.Ungroup()
            $levels++
            for ($i = 1; $i -le $range.Count; $i++) { $queue.Enqueue(@{ s = $range.Item($i); isTop = $false }) }
        } else {
            $leaves.Add($s)
        }
    }
    return @{ leaves = $leaves; levels = $levels }
}

function Classify-Leaves($leaves) {
    $byType = @{}
    $complexFreeform = 0   # msoFreeform かつ Nodes.Count >= 4（記号候補）
    $simpleFreeform = 0
    $sampleComplex = @()
    foreach ($s in $leaves) {
        $t = "$($s.Type)"
        if (-not $byType.ContainsKey($t)) { $byType[$t] = 0 }
        $byType[$t]++
        if ($s.Type -eq $MSO_FREEFORM) {
            $nodeCount = 0
            try { $nodeCount = $s.Nodes.Count } catch { }
            if ($nodeCount -ge 4) {
                $complexFreeform++
                if ($sampleComplex.Count -lt 8) {
                    $sampleComplex += [ordered]@{
                        name = $s.Name; nodes = $nodeCount
                        widthMm = (PtToMm $s.Width); heightMm = (PtToMm $s.Height)
                    }
                }
            } else { $simpleFreeform++ }
        }
    }
    return [ordered]@{
        total = $leaves.Count
        byType = $byType
        complexFreeformCount = $complexFreeform
        simpleFreeformCount = $simpleFreeform
        sampleComplex = $sampleComplex
    }
}

$result = [ordered]@{ emf = $Emf; fileBytes = $fileBytes.Length; excel = $null; powerpoint = $null }

# ---------------- Excel ----------------
$excel = $null
$excelPid = 0
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    try { $null = [CadPetaLtsymDecompose.Clip]::GetWindowThreadProcessId([IntPtr]$excel.Hwnd, [ref]$excelPid) } catch { }
    $case = [ordered]@{ status = $null; widthMm = $null; heightMm = $null; leafShapeCount = $null; groupLevels = $null; classify = $null; error = $null; errorFull = $null }
    $wb = $null
    try {
        $wb = $excel.Workbooks.Add()
        $ws = $wb.Worksheets.Item(1)
        Register-Emf
        try { $ws.PasteSpecial("図 (拡張メタファイル)") } catch { $ws.PasteSpecial("Picture (Enhanced Metafile)") }
        $shape = $ws.Shapes.Item($ws.Shapes.Count)
        $case.widthMm = PtToMm $shape.Width
        $case.heightMm = PtToMm $shape.Height
        $flat = Flatten-Shape $shape
        $case.status = "success"
        $case.leafShapeCount = $flat.leaves.Count
        $case.groupLevels = $flat.levels
        $case.classify = Classify-Leaves $flat.leaves
    } catch {
        $case.status = "error"
        $case.error = $_.Exception.Message
        $case.errorFull = $_.Exception.ToString()
    } finally { if ($wb) { try { $wb.Close($false) } catch { } } }
    $result.excel = $case
} catch {
    $result.excel = [ordered]@{ status = "fatal"; error = $_.Exception.Message; errorFull = $_.Exception.ToString() }
} finally {
    if ($excel) { try { $excel.Quit() } catch { }; try { [Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null } catch { } }
    [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
    if ($excelPid -ne 0) {
        $deadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Process -Id $excelPid -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
        $p = Get-Process -Id $excelPid -ErrorAction SilentlyContinue
        if ($p) { try { Stop-Process -Id $excelPid -Force -ErrorAction Stop; $result.excelForceKilled = $true } catch { } }
    }
}
$excelRemain = Get-Process EXCEL -ErrorAction SilentlyContinue
$result.excelResidualAfter = if ($excelRemain) { @($excelRemain | ForEach-Object { $_.Id }) } else { @() }

# ---------------- PowerPoint（任意、貼付+変換1回のみ） ----------------
if ($Ppt) {
    $pptApp = $null
    $pptPid = 0
    $beforePids = @((Get-Process POWERPNT -ErrorAction SilentlyContinue) | ForEach-Object { $_.Id })
    try {
        # 変数名 $ppt は switch パラメータ $Ppt と大小無視で衝突する（PowerShell の変数名は
        # 大文字小文字を区別しない）ため、COM オブジェクトは $pptApp に束縛する
        $pptApp = New-Object -ComObject PowerPoint.Application
        # PowerPoint.Application.Hwnd はウィンドウ非表示時に null を返すことがあるため、
        # 起動前後の POWERPNT プロセス差分で実PIDを特定する（Excel の Hwnd 手法とは別方式）
        Start-Sleep -Milliseconds 300
        $afterPids = @((Get-Process POWERPNT -ErrorAction SilentlyContinue) | ForEach-Object { $_.Id })
        $newPids = @($afterPids | Where-Object { $beforePids -notcontains $_ })
        if ($newPids.Count -eq 1) { $pptPid = $newPids[0] }
        $pres = $pptApp.Presentations.Add(0)
        $case = [ordered]@{ status = $null; widthMm = $null; heightMm = $null; leafShapeCount = $null; error = $null; errorFull = $null }
        try {
            $slide = $pres.Slides.Add(1, 12)
            Register-Emf
            $shape = $slide.Shapes.PasteSpecial(2).Item(1)  # ppPasteEnhancedMetafile
            $case.widthMm = PtToMm $shape.Width
            $case.heightMm = PtToMm $shape.Height
            # 変換1回（Ungroup を1段だけ呼ぶ）
            $range = $shape.Ungroup()
            $case.status = "success"
            $case.leafShapeCount = $range.Count
            $byType = @{}
            for ($i = 1; $i -le $range.Count; $i++) {
                $t = "$($range.Item($i).Type)"
                if (-not $byType.ContainsKey($t)) { $byType[$t] = 0 }
                $byType[$t]++
            }
            $case.byType = $byType
        } catch {
            $case.status = "error"; $case.error = $_.Exception.Message; $case.errorFull = $_.Exception.ToString()
        }
        try { $pres.Close() } catch { }
        $result.powerpoint = $case
    } catch {
        $result.powerpoint = [ordered]@{ status = "fatal"; error = $_.Exception.Message; errorFull = $_.Exception.ToString() }
    } finally {
        if ($pptApp) { try { $pptApp.Quit() } catch { }; try { [Runtime.InteropServices.Marshal]::ReleaseComObject($pptApp) | Out-Null } catch { } }
        [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
        if ($pptPid -ne 0) {
            $deadline = (Get-Date).AddSeconds(6)
            while ((Get-Date) -lt $deadline) {
                if (-not (Get-Process -Id $pptPid -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 500
            }
            $p = Get-Process -Id $pptPid -ErrorAction SilentlyContinue
            if ($p) { try { Stop-Process -Id $pptPid -Force -ErrorAction Stop; $result.pptForceKilled = $true } catch { } }
        }
    }
    $pptRemain = Get-Process POWERPNT -ErrorAction SilentlyContinue
    $result.pptResidualAfter = if ($pptRemain) { @($pptRemain | ForEach-Object { $_.Id }) } else { @() }
}

($result | ConvertTo-Json -Depth 8) | Set-Content -Encoding UTF8 $OutJson
Get-Content $OutJson
