# Office 貼り付け回帰（Phase 1 クリップボード、2026-07-07 承認の検証方式(c)）。
# 検証の分担:
#   - PETA 自身のクリップボード登録の正しさ（バイト一致）は clip_verify.ps1 が担う（E2E）
#   - 本スクリプトは「その登録状態を Office が受け付けるか」を担う。Office の貼り付け・
#     図形変換・ブック終了はクリップボードを消去し得るため（2026-07-07 実測: Excel の
#     file 経路実行後に CF_ENHMETAFILE が消えた）、クリップボード経路の各ケース直前に
#     本番と同一の API 列（GetEnhMetaFile→CopyEnhMetaFile→OpenClipboard(NULL)→Empty→
#     SetClipboardData→読み戻し照合）で登録し直す。phase0 ハーネス（excel_matrix.ps1）も
#     同じ理由で貼り付け直前に毎回登録していた
# 計測: Excel / PowerPoint は file / clipboard 両経路で寸法・「図形に変換」分解数・
#   文字回収数・書換一致。Word は寸法のみ（分解編集は非サポート方針、設計書§6.1）。
# phase0 突き合わせ値（TESTCHART2 系、検証記録_EMF接続.md §4・§6、_EMF最小生成.md §4.2）:
#   Excel 分解 3,065 図形・文字 75 個全数回収・書換一致・寸法誤差は率 0.24% 以下・
#   クリップボード/ファイル二経路で結果不変
param(
    [string]$Emf = "$env:TEMP\CadPeta_clip_verify\TESTCHART2_layout_PETA_A3.emf",
    [string]$OutDir = "$env:TEMP\CadPeta_clip_verify",
    [switch]$AllowExistingOffice
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$jsonPath = Join-Path $OutDir "office_paste_regression.json"
$logPath = Join-Path $OutDir "office_paste_regression.log"
"" | Set-Content $logPath

. "$PSScriptRoot\..\common\test_helpers.ps1"
. "$PSScriptRoot\..\common\office_com.ps1"

if (-not (Test-Path $Emf)) { throw "EMF がない: $Emf（clip_verify.ps1 を先に実行すること）" }
$fileBytes = [IO.File]::ReadAllBytes($Emf)
if ($fileBytes.Length -lt 88 -or [BitConverter]::ToUInt32($fileBytes, 0) -ne 1 -or
    [BitConverter]::ToUInt32($fileBytes, 40) -ne 0x464D4520) {
    throw "入力EMFのヘッダが不正: $Emf"
}
$emfWidthMm = ([BitConverter]::ToInt32($fileBytes, 32) - [BitConverter]::ToInt32($fileBytes, 24)) / 100.0
$emfHeightMm = ([BitConverter]::ToInt32($fileBytes, 36) - [BitConverter]::ToInt32($fileBytes, 28)) / 100.0
if ($emfWidthMm -le 0 -or $emfHeightMm -le 0) { throw "入力EMFの物理枠が不正: ${emfWidthMm}×${emfHeightMm}mm" }

Add-Type -Name Clip -Namespace CadPetaPaste -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
[DllImport("user32.dll")] public static extern bool EmptyClipboard();
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetClipboardData(uint f, IntPtr h);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
[DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint f);
[DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetEnhMetaFileW(string f);
[DllImport("gdi32.dll")] public static extern IntPtr CopyEnhMetaFileW(IntPtr h, string f);
[DllImport("gdi32.dll")] public static extern bool DeleteEnhMetaFile(IntPtr h);
[DllImport("gdi32.dll")] public static extern uint GetEnhMetaFileBits(IntPtr h, uint c, byte[] b);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
'@
$CF_ENHMETAFILE = 14

$officeProcessNames = @{ Excel = 'EXCEL'; PowerPoint = 'POWERPNT'; Word = 'WINWORD' }
$officeBefore = @{}
foreach ($entry in $officeProcessNames.GetEnumerator()) {
    $officeBefore[$entry.Key] = @(Get-Process -Name $entry.Value -ErrorAction SilentlyContinue | ForEach-Object Id)
}
$ownedOfficePids = New-Object System.Collections.Generic.List[int]
function Register-OwnedOfficeApplication([string]$name, $application) {
    [uint32]$processId = 0
    $hwnd = [IntPtr][int64]$application.Hwnd
    if ($hwnd -eq [IntPtr]::Zero) {
        try { $hwnd = [IntPtr][int64]$application.ActiveWindow.Hwnd } catch {}
    }
    [void][CadPetaPaste.Clip]::GetWindowThreadProcessId($hwnd, [ref]$processId)
    if ($processId -eq 0) {
        $currentIds = @(Get-Process -Name $officeProcessNames[$name] -ErrorAction SilentlyContinue | ForEach-Object Id)
        $newIds = @($currentIds | Where-Object { $_ -notin $officeBefore[$name] })
        if ($newIds.Count -eq 1) {
            $processId = [uint32]$newIds[0]
            Log "  HWND未生成のため開始前後のPID差で所有照合: $name PID=$processId"
        } elseif ($AllowExistingOffice -and $newIds.Count -eq 0 -and $officeBefore[$name].Count -eq 1) {
            $processId = [uint32]$officeBefore[$name][0]
            Log "  HWND未生成の既存プロセスを許可: $name PID=$processId"
        } else {
            throw "$name の所有PIDを一意に決定できない: HWND=$hwnd new=[$($newIds -join ',')] existing=[$($officeBefore[$name] -join ',')]"
        }
    }
    if ([int]$processId -in $officeBefore[$name] -and -not $AllowExistingOffice) {
        throw "$name の既存プロセスへ接続したため安全に試験できない: PID=$processId"
    }
    if ([int]$processId -in $officeBefore[$name]) {
        Log "  既存プロセスの使用と終了を許可: $name PID=$processId"
    }
    $ownedOfficePids.Add([int]$processId)
    Log "  所有プロセス: $name PID=$processId HWND=$hwnd"
    return [int]$processId
}



function Set-ShapeText($s, $app, $text) {
    if ($app -eq "Excel") { $s.TextFrame.Characters().Text = $text }
    else { $s.TextFrame.TextRange.Text = $text }
}

function Measure-Case($case, $shape, $doUngroup, $app) {
    $case.widthMm = PtToMm $shape.Width -Digits 2
    $case.heightMm = PtToMm $shape.Height -Digits 2
    Log "  size: $($case.widthMm) x $($case.heightMm) mm"
    if ($doUngroup) {
        $ug = Recurse-Ungroup $shape
        $case.ungroupSuccess = $ug.success
        $case.ungroupError = $ug.error
        $case.leafShapeCount = $ug.leafCount
        if ($ug.success) {
            $texts = 0
            $edit = [ordered]@{ attempted = $false }
            foreach ($s in $ug.leaves) {
                $t = Get-ShapeText $s $app
                if ($t -and $t.Length -gt 0) {
                    $texts++
                    if (-not $edit.attempted) {
                        try {
                            Set-ShapeText $s $app ($t + "_EDIT")
                            $edit = [ordered]@{ attempted = $true; matched = ((Get-ShapeText $s $app) -eq ($t + "_EDIT")) }
                        } catch { $edit = [ordered]@{ attempted = $true; matched = $false; error = $_.Exception.Message } }
                    }
                }
            }
            $case.textShapeCount = $texts
            $case.editTest = $edit
        }
        Log "  ungroup=$($ug.success) leaves=$($ug.leafCount) texts=$($case.textShapeCount) editMatched=$($case.editTest.matched) error=$($ug.error)"
    }
    return $case
}

$results = New-Object System.Collections.ArrayList
$failures = New-Object System.Collections.Generic.List[string]

# ---------------- Excel ----------------
$excel = $null
$excelPid = 0
try {
    $excel = New-Object -ComObject Excel.Application
    $excelPid = Register-OwnedOfficeApplication 'Excel' $excel
    $excel.Visible = $false; $excel.DisplayAlerts = $false
    Log "=== Excel $($excel.Version) ==="
    foreach ($route in @("file", "clipboard")) {
        $case = [ordered]@{ app = "Excel"; route = $route }
        $wb = $null
        try {
            Log "--- Excel [$route] ---"
            $wb = $excel.Workbooks.Add()
            $ws = $wb.Worksheets.Item(1)
            if ($route -eq "file") {
                $shape = $ws.Shapes.AddPicture($Emf, 0, -1, 10, 10, -1, -1)
            } else {
                Register-Emf ([CadPetaPaste.Clip]) $Emf $fileBytes $CF_ENHMETAFILE
                Log "  再登録OK: CF_ENHMETAFILE $($fileBytes.Length) bytes（読み戻し一致）"
                try { $ws.PasteSpecial("図 (拡張メタファイル)"); $case.pasteMethod = "PasteSpecial(JP)" }
                catch { $ws.PasteSpecial("Picture (Enhanced Metafile)"); $case.pasteMethod = "PasteSpecial(EN)" }
                $shape = $ws.Shapes.Item($ws.Shapes.Count)
            }
            $case = Measure-Case $case $shape $true "Excel"
        } catch { $case.caseError = $_.Exception.Message; Log "  CASE ERROR: $($_.Exception.Message)" }
        finally {
            if ($wb) {
                try { $wb.Close($false) }
                catch { $failures.Add("Excel [$route] のブック終了失敗: $($_.Exception.Message)") }
            }
        }
        $results.Add($case) | Out-Null
    }
} catch {
    $failures.Add("Excel FATAL: $($_.Exception.Message)")
    Log $failures[$failures.Count - 1]
}
finally {
    if ($excel) {
        if ($excelPid -gt 0) {
            try { $excel.Quit() }
            catch { $failures.Add("Excel 終了失敗: $($_.Exception.Message)") }
        }
        [Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
    }
}

# ---------------- PowerPoint ----------------
$ppt = $null
$pptPid = 0
try {
    $ppt = New-Object -ComObject PowerPoint.Application
    Log "=== PowerPoint $($ppt.Version) ==="
    $pres = $ppt.Presentations.Add(0)
    $pptPid = Register-OwnedOfficeApplication 'PowerPoint' $ppt
    $slideIdx = 1
    foreach ($route in @("file", "clipboard")) {
        $case = [ordered]@{ app = "PowerPoint"; route = $route }
        $slide = $null
        $shape = $null
        try {
            Log "--- PowerPoint [$route] ---"
            $slide = $pres.Slides.Add($slideIdx, 12); $slideIdx++
            if ($route -eq "file") {
                $shape = $slide.Shapes.AddPicture($Emf, 0, -1, 10, 10)
            } else {
                Register-Emf ([CadPetaPaste.Clip]) $Emf $fileBytes $CF_ENHMETAFILE
                Log "  再登録OK: CF_ENHMETAFILE $($fileBytes.Length) bytes（読み戻し一致）"
                $shape = $slide.Shapes.PasteSpecial(2).Item(1)  # ppPasteEnhancedMetafile
            }
            $case = Measure-Case $case $shape $true "PowerPoint"
        } catch { $case.caseError = $_.Exception.Message; Log "  CASE ERROR: $($_.Exception.Message)" }
        finally {
            foreach ($comObject in @($shape, $slide)) {
                if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                    try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) } catch {}
                }
            }
        }
        $results.Add($case) | Out-Null
    }
    try { $pres.Close() }
    catch { $failures.Add("PowerPoint のプレゼンテーション終了失敗: $($_.Exception.Message)") }
    if ($pres -and [Runtime.InteropServices.Marshal]::IsComObject($pres)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($pres)
        $pres = $null
    }
} catch {
    $failures.Add("PowerPoint FATAL: $($_.Exception.Message)")
    Log $failures[$failures.Count - 1]
}
finally {
    if ($ppt) {
        if ($pptPid -gt 0) {
            try { $ppt.Quit() }
            catch { $failures.Add("PowerPoint 終了失敗: $($_.Exception.Message)") }
        }
        [Runtime.InteropServices.Marshal]::ReleaseComObject($ppt) | Out-Null
    }
}

# ---------------- Word（寸法のみ。分解編集は非サポート方針=設計書§6.1） ----------------
$word = $null
$wordPid = 0
try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false; $word.DisplayAlerts = 0
    Log "=== Word $($word.Version) ==="
    foreach ($route in @("file", "clipboard")) {
        $case = [ordered]@{ app = "Word"; route = $route }
        $doc = $null
        try {
            Log "--- Word [$route] ---"
            $doc = $word.Documents.Add()
            if ($wordPid -eq 0) { $wordPid = Register-OwnedOfficeApplication 'Word' $word }
            if ($route -eq "file") {
                $shape = $doc.Shapes.AddPicture($Emf, $false, $true, 10, 10)
            } else {
                Register-Emf ([CadPetaPaste.Clip]) $Emf $fileBytes $CF_ENHMETAFILE
                Log "  再登録OK: CF_ENHMETAFILE $($fileBytes.Length) bytes（読み戻し一致）"
                $word.Selection.PasteSpecial([Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 9)
                if ($doc.Shapes.Count -gt 0) { $shape = $doc.Shapes.Item($doc.Shapes.Count) }
                else { $shape = $doc.InlineShapes.Item($doc.InlineShapes.Count) }
            }
            $case = Measure-Case $case $shape $false "Word"
        } catch { $case.caseError = $_.Exception.Message; Log "  CASE ERROR: $($_.Exception.Message)" }
        finally {
            if ($doc) {
                try { $doc.Close(0) }
                catch { $failures.Add("Word [$route] の文書終了失敗: $($_.Exception.Message)") }
            }
        }
        $results.Add($case) | Out-Null
    }
} catch {
    $failures.Add("Word FATAL: $($_.Exception.Message)")
    Log $failures[$failures.Count - 1]
}
finally {
    if ($word) {
        if ($wordPid -gt 0) {
            try { $word.Quit() }
            catch { $failures.Add("Word 終了失敗: $($_.Exception.Message)") }
        }
        [Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
    }
}

[GC]::Collect()
[GC]::WaitForPendingFinalizers()
foreach ($ownedOfficePid in $ownedOfficePids) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Id $ownedOfficePid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (Get-Process -Id $ownedOfficePid -ErrorAction SilentlyContinue) {
        $failures.Add("所有Officeプロセスが終了後も残留した: PID=$ownedOfficePid")
    }
}

foreach ($case in $results) {
    $label = "$($case.app) [$($case.route)]"
    if ($case.caseError) { $failures.Add("$label CASE ERROR: $($case.caseError)") }
    if (-not $case.caseError -and ($case.widthMm -le 0 -or $case.heightMm -le 0)) {
        $failures.Add("$label の貼付寸法が不正: $($case.widthMm)×$($case.heightMm)mm")
    }
    if (-not $case.caseError) {
        if ($case.app -in @('PowerPoint', 'Word')) {
            $emfAspect = $emfWidthMm / $emfHeightMm
            $officeAspect = $case.widthMm / $case.heightMm
            if ([Math]::Abs($officeAspect / $emfAspect - 1) -gt 0.0024) {
                $failures.Add("$label の縦横比がEMF物理枠と0.24%以内で一致しない")
            }
        } else {
            if ([Math]::Abs($case.widthMm / $emfWidthMm - 1) -gt 0.0024 -or
                [Math]::Abs($case.heightMm / $emfHeightMm - 1) -gt 0.0024) {
                $failures.Add("$label の貼付寸法がEMF物理枠${emfWidthMm}×${emfHeightMm}mmと0.24%以内で一致しない")
            }
        }
    }
    if ($case.app -ne "Word" -and $case.ungroupSuccess -ne $true) {
        $failures.Add("$label の図形変換に失敗: $($case.ungroupError)")
    }
    if ($case.app -ne "Word" -and
        ($case.editTest.attempted -ne $true -or $case.editTest.matched -ne $true)) {
        $failures.Add("$label の文字書換照合に失敗")
    }
}

# 同じEMFのfile／clipboard経路は、Officeへ渡す方法だけが違う。寸法、分解後の葉数、文字数が
# 変わればクリップボード形式または貼付経路の回帰であり、どちらか一方が成功しても合格にしない。
foreach ($appName in @('Excel', 'PowerPoint', 'Word')) {
    $fileCase = @($results | Where-Object { $_.app -eq $appName -and $_.route -eq 'file' })
    $clipCase = @($results | Where-Object { $_.app -eq $appName -and $_.route -eq 'clipboard' })
    if ($fileCase.Count -ne 1 -or $clipCase.Count -ne 1) {
        $failures.Add("$appName のfile／clipboard結果が各1件でない")
        continue
    }
    if (-not $fileCase[0].caseError -and -not $clipCase[0].caseError) {
        if ($appName -ne 'Word' -and
            ($fileCase[0].leafShapeCount -ne $clipCase[0].leafShapeCount -or
             $fileCase[0].textShapeCount -ne $clipCase[0].textShapeCount)) {
            $failures.Add("$appName のfile／clipboard分解結果が一致しない")
        }
    }
}

# 承認済みTESTCHART2のExcelオラクル。単なるUngroup成功だけでは、図形や文字の大半が欠けても
# PASSになるため、phase0実測（葉3065、文字75）から下限を固定する。
$excelCases = @($results | Where-Object { $_.app -eq 'Excel' -and -not $_.caseError })
foreach ($case in $excelCases) {
    if ($case.leafShapeCount -lt 3000) {
        $failures.Add("Excel [$($case.route)] の分解図形が回帰床3000未満: $($case.leafShapeCount)")
    }
    if ($case.textShapeCount -lt 75) {
        $failures.Add("Excel [$($case.route)] の文字図形が回帰床75未満: $($case.textShapeCount)")
    }
}

($results | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $jsonPath
Log "JSON: $jsonPath"
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Log "FAIL: $failure" }
    throw "Office貼付回帰が失敗した（$($failures.Count)件）。詳細: $logPath"
}
Log "PASS: 全$($results.Count)ケース（Excel/PowerPoint/Word × file/clipboard）が独立寸法・分解一致・所有プロセス終了を満たした"
