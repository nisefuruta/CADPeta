# probe63.ps1 -- 63(ACI) と 421(真色) の優先関係の決定実験（3枚のみ）
#  A: CYLINDER 黒→白、63省略      → 形状差が出るか＋63なしで entmake が通るか
#  B: LINEAR   63=1(赤)×421=緑    → どちらが描画に勝つか
#  C: SPHERICAL 黒→白、63省略     → 放射形状の確認
param(
  [string]$Accore = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe",
  [string]$Dwt = "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt",
  [int]$TimeoutSec = 120
)
$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot
$dirFs = $dir -replace '\\', '/'
$tmp = Join-Path $dir "tmp"
New-Item -ItemType Directory -Force $tmp | Out-Null

$charts = @(
  ,@("probe_CYLINDER_bw_no63",  "CYLINDER",  0.0, 0.0, 0,        16777215, -1, -1)
  ,@("probe_LINEAR_aci1_vs_green","LINEAR",  0.0, 0.0, 65280,    65280,     1,  1)
  ,@("probe_SPHERICAL_bw_no63", "SPHERICAL", 0.0, 0.0, 0,        16777215, -1, -1)
)
$inv = [Globalization.CultureInfo]::InvariantCulture
$enc932 = [Text.Encoding]::GetEncoding(932)
foreach ($c in $charts) {
  $name = $c[0]
  $pdf = Join-Path $tmp "$name.pdf"
  if (Test-Path $pdf) { Remove-Item $pdf -Force -Confirm:$false }
  $angS = [string]::Format($inv, "{0:0.0000000000}", $c[2])
  $shiftS = [string]::Format($inv, "{0:0.000}", $c[3])
  $lines = @(
    "(setq gh:dir ""$dirFs"")",
    "(load ""$dirFs/gh_charts.lsp"")",
    "(gh:prep ""$($c[1])"" $angS $shiftS $($c[4]) $($c[5]) $($c[6]) $($c[7]))",
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
      if (((Get-Date) - $pdfSeen).TotalSeconds -gt 8) { break }
    }
    Start-Sleep -Milliseconds 1500
  }
  if (-not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force -Confirm:$false } catch {} }
  if ((Test-Path $pdf) -and ((Get-Item $pdf).Length -gt 10000)) {
    "OK  {0}  {1:N0} bytes" -f $name, (Get-Item $pdf).Length
  } else { "FAIL $name" }
}
# FileDialog 復元
$keys = Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "General Configuration$" }
foreach ($k in $keys) {
  $v = Get-ItemProperty -Path $k.PSPath -Name FileDialog -ErrorAction SilentlyContinue
  if ($v -and $v.FileDialog -eq 0) { Set-ItemProperty -Path $k.PSPath -Name FileDialog -Value 1; "レジストリ復元: FileDialog 0→1" }
}
