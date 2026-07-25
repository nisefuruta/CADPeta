# 窓プロットの座標契約を測る計測ケースの定義（唯一の情報源）。
# 計測ハーネス（window_contract_matrix.ps1）とオラクル（window_contract_oracle.ps1）から
# ドットソースで読む。ケース定義を両方へ複製しない。
#
# 1変数ずつ切り分ける設計:
#   A 対照 / B TARGET単独 / C UCS原点単独 / D 原点+TARGET
#   E UCS回転(+平面視のねじれ) / F E+TARGET / G 原点+回転+TARGET（実図面相当）
#   H ねじれ単独 / I 回転とねじれが同角 / J 回転とねじれが異角
#   K 傾斜UCS+その面への平面視 / L 3Dビュー / M 傾斜UCS+3Dビュー
#   N 視線がUCS平面と平行（明示中止が期待）
#   T0〜T360 ViewTwist の象限境界 / U0〜U360 UCS 回転の象限境界
#
# viewDir は WCS ベクトル、twist は WCS 基準のねじれ角（度）、center は DCS 座標（Autodesk 公式仕様）。
# 回転角は 3-4-5 の角（cos=0.8 / sin=0.6）を使う。期待値を手計算で厳密に出せるため、
# 近似三角関数値を期待値算出の誤差要因として持ち込まない。

$script:CpCos = 0.8
$script:CpSin = 0.6
$script:CpRotDeg = [Math]::Atan2($script:CpSin, $script:CpCos) * 180.0 / [Math]::PI   # 36.869897645844021

$CadPetaWindowCases = @{
    'A' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                     Target='0,0,0';    Dir='0,0,1';       Twist=0;               Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'B' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                     Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'C' = @{ Ucs=@('123,-45,0','1,0,0','0,1,0');                 Target='0,0,0';    Dir='0,0,1';       Twist=0;               Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'D' = @{ Ucs=@('123,-45,0','1,0,0','0,1,0');                 Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'E' = @{ Ucs=@('0,0,0','0.8,0.6,0','-0.6,0.8,0');            Target='0,0,0';    Dir='0,0,1';       Twist=$script:CpRotDeg; Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'F' = @{ Ucs=@('0,0,0','0.8,0.6,0','-0.6,0.8,0');            Target='60,50,0';  Dir='0,0,1';       Twist=$script:CpRotDeg; Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'G' = @{ Ucs=@('123,-45,0','0.8,0.6,0','-0.6,0.8,0');        Target='60,50,0';  Dir='0,0,1';       Twist=$script:CpRotDeg; Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'H' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                     Target='0,0,0';    Dir='0,0,1';       Twist=15;              Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'I' = @{ Ucs=@('0,0,0','0.8,0.6,0','-0.6,0.8,0');            Target='0,0,0';    Dir='0,0,1';       Twist=$script:CpRotDeg; Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'J' = @{ Ucs=@('0,0,0','0.8,0.6,0','-0.6,0.8,0');            Target='0,0,0';    Dir='0,0,1';       Twist=15;              Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'K' = @{ Ucs=@('0,0,0','1,0,0','0,0.8,0.6');                 Target='0,0,0';    Dir='0,-0.6,0.8';  Twist=0;               Center='100,75,0'; H=200; W=280; P1='50,40'; P2='150,110' }
    'L' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                     Target='0,0,0';    Dir='1,1,1';       Twist=0;               Center='100,75,0'; H=250; W=350; P1='50,40'; P2='150,110' }
    'M' = @{ Ucs=@('0,0,0','1,0,0','0,0.8,0.6');                 Target='0,0,0';    Dir='1,1,1';       Twist=0;               Center='100,75,0'; H=250; W=350; P1='50,40'; P2='150,110' }
    'N' = @{ Ucs=@('0,0,0','1,0,0','0,0.8,0.6');                 Target='0,0,0';    Dir='0,0.8,0.6';   Twist=0;               Center='100,75,0'; H=250; W=350; P1='50,40'; P2='150,110' }
    'T0'   = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'T90'  = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=90;              Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'T180' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=180;             Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'T270' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=270;             Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'T360' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=360;             Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'U0'   = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'U90'  = @{ Ucs=@('0,0,0','0,1,0','-1,0,0');                 Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'U180' = @{ Ucs=@('0,0,0','-1,0,0','0,-1,0');                Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'U270' = @{ Ucs=@('0,0,0','0,-1,0','1,0,0');                 Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
    'U360' = @{ Ucs=@('0,0,0','1,0,0','0,1,0');                  Target='60,50,0';  Dir='0,0,1';       Twist=0;               Center='40,25,0';  H=200; W=280; P1='50,40'; P2='150,110' }
}

<#
.SYNOPSIS
"x,y[,z]" を LISP の点リスト式へ直す。
.DESCRIPTION
カンマは LISP の区切りではないため、"50,40" をそのまま埋め込むと読み取りに失敗し、
エラーが表に出ないまま何も出力されない（実測で踏んだ）。空白区切りの実数へ明示変換する。
#>
function ConvertTo-LispPoint([string]$s) {
    $parts = $s -split ',' | ForEach-Object { $_.Trim() }
    if ($parts.Count -lt 2) { throw "点として読めない: `"$s`"" }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $x = [double]::Parse($parts[0], $inv)
    $y = [double]::Parse($parts[1], $inv)
    $z = if ($parts.Count -ge 3) { [double]::Parse($parts[2], $inv) } else { 0.0 }
    '(list {0} {1} {2})' -f $x.ToString('R', $inv), $y.ToString('R', $inv), $z.ToString('R', $inv)
}

<#
.SYNOPSIS
計測条件（UCS・ビュー）を環境変数へ設定し、計測器へ渡す .scr の設定行を返す。
#>
function Set-CadPetaWindowCaseEnv($cfg) {
    $env:CPWIN_UCS_ORIGIN = $cfg.Ucs[0]; $env:CPWIN_UCS_XDIR = $cfg.Ucs[1]; $env:CPWIN_UCS_YDIR = $cfg.Ucs[2]
    $env:CPWIN_VIEW_TARGET = $cfg.Target; $env:CPWIN_VIEW_DIR = $cfg.Dir
    $env:CPWIN_VIEW_TWIST_DEG = ([string]$cfg.Twist)
    $env:CPWIN_VIEW_CENTER = $cfg.Center
    $env:CPWIN_VIEW_HEIGHT = ([string]$cfg.H); $env:CPWIN_VIEW_WIDTH = ([string]$cfg.W)
    return "CPWINSETUCS`r`nCPWINSETVIEW"
}
