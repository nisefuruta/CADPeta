# test_n_ppt.ps1: 合成コーパス生成→cadpeta.exe変換→PowerPoint Ungroup を1本の N について
# 通しで実行し results_ppt.jsonl へ追記する。二分探索は外側から N を変えて繰り返し呼ぶ。
# コーパス生成器は phase3 の gen_corpus.py を共用する（PDF 合成は Excel 版と同一のため）。
param(
    [Parameter(Mandatory=$true)][int]$N,
    [int]$Seg = 1,
    [double]$SegLen = 6.0,
    [double]$Cell = 0,
    [string]$Label = $null,
    [string]$WorkDir = "$env:TEMP\cadpeta_p4_ppt_threshold",
    [string]$Exe = "C:\Users\wk\repo\CADPeta\src\CadPeta.Cli\bin\Debug\net10.0-windows\cadpeta.exe",
    [string]$GenScript = "C:\Users\wk\repo\CADPeta\tests\tools\cad-typography\emf_ungroup_threshold\gen_corpus.py",
    [string]$ResultsJsonl = "$env:TEMP\cadpeta_p4_ppt_threshold\results_ppt.jsonl",
    [int]$TimeoutSec = 120,
    [switch]$ForceRegen
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $WorkDir)) { New-Item -ItemType Directory -Force $WorkDir | Out-Null }
if (-not (Test-Path $Exe)) { throw "cadpeta.exe が未ビルド: $Exe" }
if (-not (Test-Path $GenScript)) { throw "gen_corpus.py が無い: $GenScript" }

$tag = if ($Label) { "$Label`_n$N`_seg$Seg`_len$SegLen" } else { "n$N`_seg$Seg`_len$SegLen" }
$pdf = Join-Path $WorkDir "$tag.pdf"
$svg = Join-Path $WorkDir "$tag.svg"
$emf = Join-Path $WorkDir "$tag.emf"
$outJson = Join-Path $WorkDir "$tag.ungroup.json"

if ($ForceRegen -or -not (Test-Path $pdf)) {
    $genArgs = @("--n", $N, "--seg", $Seg, "--len", $SegLen, "--out", $pdf)
    if ($Cell -gt 0) { $genArgs += @("--cell", $Cell) }
    $genOut = & uv run --no-project python $GenScript @genArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gen_corpus.py 失敗: $genOut" }
}

$cliOut = & $Exe $pdf $svg --emf $emf 2>&1
$cliExit = $LASTEXITCODE
$cliText = ($cliOut -join "`n")
if ($cliExit -ne 0) { throw "cadpeta.exe 失敗(exit=$cliExit): $cliText" }
if ($cliText -match "IR: paths=(\d+)") { $irPaths = [int]$Matches[1] } else { $irPaths = $null }
if ($cliText -match "EMF: paths=(\d+)") { $emfPaths = [int]$Matches[1] } else { $emfPaths = $null }
$emfBytes = (Get-Item $emf).Length

$run = Join-Path $PSScriptRoot "run_case_ppt.ps1"
$ug = & $run -Emf $emf -OutJson $outJson -Route clipboard -TimeoutSec $TimeoutSec

$record = [ordered]@{
    timestamp = (Get-Date).ToString("o")
    app = "powerpoint"
    label = $Label
    n = $N
    seg = $Seg
    segLen = $SegLen
    irPaths = $irPaths
    emfPaths = $emfPaths
    emfBytes = $emfBytes
    ungroupStatus = $ug.status
    leafShapeCount = $ug.leafShapeCount
    hresult = $ug.hresult
    error = $ug.error
    errorFull = $ug.errorFull
    forceKilled = $ug.forceKilled
    pdf = $pdf; emf = $emf
}
($record | ConvertTo-Json -Depth 8 -Compress) | Add-Content -Encoding UTF8 $ResultsJsonl
$record | ConvertTo-Json -Depth 8
