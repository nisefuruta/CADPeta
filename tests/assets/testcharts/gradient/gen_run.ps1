# gen_run.ps1 -- グラデーション正解チャート一括生成ハーネス
# 全9形状 × {角度0/45°・Shift 0/0.5} の黒→白チャート＋色検証2枚を
# accoreconsole の -PLOT で PDF 化する（計画 §2.2 の正解レンダラ）。
#
# 設計上の注意（実測に基づく）:
# - accoreconsole はプロット後に稀に残留・ハングするため、1チャート=1プロセスで
#   分離実行し、PDF 完成を検知したらタイムアウトで kill する。
# - kill は FixedProfile\General Configuration\FileDialog=0 をレジストリへ永続化
#   するため、終了時に必ず点検・復元する。
# - .scr は CP932 で書く（用紙名が日本語のため）。
param(
  [string]$Accore = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe",
  [string]$Dwt = "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt",
  [int]$TimeoutSec = 120,
  [ValidateSet("Default", "ExtendedSemantics", "ShiftedAngles", "ShiftedRightAngles")]
  [string]$Suite = "Default",
  [string]$OutputDirectory = ""
)
$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot
$dirFs = $dir -replace '\\', '/'
$tmp = Join-Path $dir "tmp"
$chartsDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $dir "charts" }
$logPath = if ($Suite -eq "Default") { Join-Path $dir "charts.log" } else { Join-Path $chartsDir "charts.log" }
$logPathFs = $logPath -replace '\\', '/'
New-Item -ItemType Directory -Force $tmp | Out-Null
New-Item -ItemType Directory -Force $chartsDir | Out-Null
if (Test-Path $logPath) { Remove-Item $logPath -Force -Confirm:$false }

# GFNAME 公式9形状（LINEAR に INV は無い）
$shapes = "LINEAR","CYLINDER","INVCYLINDER","SPHERICAL","INVSPHERICAL",
          "HEMISPHERICAL","INVHEMISPHERICAL","CURVED","INVCURVED"
$BLACK = 0; $WHITE = 16777215; $RED = 16711680; $BLUE = 255
$A45 = 0.7853981634
$A90 = 1.5707963268
$A180 = 3.1415926536

$charts = @()
if ($Suite -eq "Default") {
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a000_s000", $s, 0.0, 0.0, $BLACK, $WHITE) }
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a045_s000", $s, $A45, 0.0, $BLACK, $WHITE) }
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a000_s050", $s, 0.0, 0.5, $BLACK, $WHITE) }
  $charts += ,@("grad_LINEAR_color_a000_s000", "LINEAR", 0.0, 0.0, $RED, $BLUE)
  $charts += ,@("grad_SPHERICAL_color_a000_s000", "SPHERICAL", 0.0, 0.0, $RED, $BLUE)
} elseif ($Suite -eq "ExtendedSemantics") {
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a090_s000", $s, $A90, 0.0, $BLACK, $WHITE) }
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a180_s000", $s, $A180, 0.0, $BLACK, $WHITE) }
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a000_s100", $s, 0.0, 1.0, $BLACK, $WHITE) }
} elseif ($Suite -eq "ShiftedAngles") {
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a045_s100", $s, $A45, 1.0, $BLACK, $WHITE) }
} else {
  foreach ($s in $shapes) { $charts += ,@("grad_${s}_bw_a090_s100", $s, $A90, 1.0, $BLACK, $WHITE) }
}

$inv = [Globalization.CultureInfo]::InvariantCulture
$enc932 = [Text.Encoding]::GetEncoding(932)
$done = 0; $failed = @()

foreach ($c in $charts) {
  $name = $c[0]; $shape = $c[1]; $ang = $c[2]; $shift = $c[3]; $c1 = $c[4]; $c2 = $c[5]
  $pdf = Join-Path $chartsDir "$name.pdf"
  if (Test-Path $pdf) { Remove-Item $pdf -Force -Confirm:$false }
  $angS = [string]::Format($inv, "{0:0.0000000000}", $ang)
  $shiftS = [string]::Format($inv, "{0:0.000}", $shift)
  # 63(ACI) は省略必須: ACI 7 が両ストップに付くと文脈色が真色421を上書きし
  # 全チャートが単色化する（2026-07-13 決定実験 probe63.ps1。通常ACIなら421が勝つ）
  $lines = @(
    "(setq gh:dir ""$dirFs"")",
    "(setq gh:log ""$logPathFs"")",
    "(load ""$dirFs/gh_charts.lsp"")",
    "(gh:prep ""$shape"" $angS $shiftS $c1 $c2 -1 -1)",
    "(setvar ""CMDECHO"" 1)",
    "-PLOT", "Y", "Model",
    "AutoCAD PDF (High Quality Print).pc3",
    "ISO フルブリード A3 (420.00 x 297.00 ミリ)",
    "M", "L", "N", "W", "0,0", "420,297", "1:1", "0.00,0.00",
    "Y", ".", "Y", "A",
    $pdf,
    "N", "Y",
    "_.QUIT _Y"
  )
  $scr = Join-Path $tmp "$name.scr"
  [IO.File]::WriteAllLines($scr, $lines, $enc932)

  $out = Join-Path $tmp "$name.out.txt"
  $proc = Start-Process -FilePath $Accore -ArgumentList "/i","`"$Dwt`"","/s","`"$scr`"","/l","ja-JP" `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput $out
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  $pdfSeen = $null
  while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
    if ((Test-Path $pdf) -and ((Get-Item $pdf).Length -gt 10000)) {
      if ($null -eq $pdfSeen) { $pdfSeen = Get-Date }
      # PDF 完成後 8 秒の猶予で自然終了を待ち、残留したら kill（既知の残留対策）
      if (((Get-Date) - $pdfSeen).TotalSeconds -gt 8) { break }
    }
    Start-Sleep -Milliseconds 1500
  }
  if (-not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force -Confirm:$false } catch {} }
  if ((Test-Path $pdf) -and ((Get-Item $pdf).Length -gt 10000)) {
    $done++
    "OK  {0}  {1:N0} bytes" -f $name, (Get-Item $pdf).Length
  } else {
    $failed += $name
    "FAIL $name"
  }
}

# kill による FileDialog=0 永続化の点検・復元（必須の後始末）
$keys = Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "General Configuration$" }
foreach ($k in $keys) {
  $v = Get-ItemProperty -Path $k.PSPath -Name FileDialog -ErrorAction SilentlyContinue
  if ($v -and $v.FileDialog -eq 0) {
    Set-ItemProperty -Path $k.PSPath -Name FileDialog -Value 1
    "レジストリ復元: $($k.Name) FileDialog 0→1"
  }
}

""
"生成: $done / $($charts.Count)"
if ($failed.Count -gt 0) { "失敗: $($failed -join ', ')"; exit 1 }
"charts.log に entget 証跡（450/453/460/461/470）を出力済み"
