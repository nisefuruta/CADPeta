# test_n.ps1: 合成コーパス生成→cadpeta.exe変換→Excel Ungroup を1本の N について通しで実行し、
# results.jsonl（1行1レコード）へ追記する。二分探索は本スクリプトを外側（対話セッション、
# または軽い駆動ループ）から N を変えて繰り返し呼ぶことで行う。
param(
    [Parameter(Mandatory=$true)][int]$N,
    [int]$Seg = 1,
    [double]$SegLen = 6.0,
    [double]$Cell = 0,               # 0=gen_corpus.py の自動算定に委ねる。seg大のとき明示指定してページ上限超過を回避する
    [string]$Label = $null,          # 結果へ添えるタグ（例: "size-axis" "record-axis" "anchor"）
    [string]$WorkDir = "$env:TEMP\cadpeta_p3b_threshold",
    [string]$Exe = "C:\Users\wk\repo\CADPeta\src\CadPeta.Cli\bin\Debug\net10.0-windows\cadpeta.exe",
    [string]$ResultsJsonl = "$env:TEMP\cadpeta_p3b_threshold\results.jsonl",
    [int]$TimeoutSec = 120,
    [switch]$ForceRegen               # 既存の pdf/emf を再利用せず作り直す
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $WorkDir)) { New-Item -ItemType Directory -Force $WorkDir | Out-Null }
if (-not (Test-Path $Exe)) { throw "cadpeta.exe が未ビルド: $Exe（再ビルドは方針で禁止。報告して停止すること）" }

$tag = if ($Label) { "$Label`_n$N`_seg$Seg`_len$SegLen" } else { "n$N`_seg$Seg`_len$SegLen" }
$pdf = Join-Path $WorkDir "$tag.pdf"
$svg = Join-Path $WorkDir "$tag.svg"
$emf = Join-Path $WorkDir "$tag.emf"
$outJson = Join-Path $WorkDir "$tag.ungroup.json"

if ($ForceRegen -or -not (Test-Path $pdf)) {
    $genScript = Join-Path $PSScriptRoot "gen_corpus.py"
    $genArgs = @("--n", $N, "--seg", $Seg, "--len", $SegLen, "--out", $pdf)
    if ($Cell -gt 0) { $genArgs += @("--cell", $Cell) }
    $genOut = & uv run --no-project python $genScript @genArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gen_corpus.py 失敗: $genOut" }
}

$cliOut = & $Exe $pdf $svg --emf $emf 2>&1
$cliExit = $LASTEXITCODE
$cliText = ($cliOut -join "`n")
if ($cliExit -ne 0) { throw "cadpeta.exe 失敗(exit=$cliExit): $cliText" }
if ($cliText -match "IR: paths=(\d+)") { $irPaths = [int]$Matches[1] } else { $irPaths = $null }
if ($cliText -match "EMF: paths=(\d+)") { $emfPaths = [int]$Matches[1] } else { $emfPaths = $null }
$emfBytes = (Get-Item $emf).Length

$run = Join-Path $PSScriptRoot "run_case.ps1"
$ug = & $run -Emf $emf -OutJson $outJson -Route clipboard -TimeoutSec $TimeoutSec

$record = [ordered]@{
    timestamp = (Get-Date).ToString("o")
    label = $Label
    n = $N
    seg = $Seg
    segLen = $SegLen
    irPaths = $irPaths
    emfPaths = $emfPaths
    emfBytes = $emfBytes
    ungroupStatus = $ug.status
    leafShapeCount = $ug.leafShapeCount
    expectedLeafShapeCount = if ($irPaths) { $irPaths + 1 } else { $null }  # +1=Excelの背景オートシェイプ枠（実測既知）
    leafCountMatches = if ($ug.leafShapeCount -and $irPaths) { $ug.leafShapeCount -eq ($irPaths + 1) } else { $null }
    hresult = $ug.hresult
    error = $ug.error
    errorFull = $ug.errorFull
    forceKilled = $ug.forceKilled
    pdf = $pdf; emf = $emf
}
($record | ConvertTo-Json -Depth 8 -Compress) | Add-Content -Encoding UTF8 $ResultsJsonl
$record | ConvertTo-Json -Depth 8
