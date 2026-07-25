# 窓の座標契約の恒久回帰: UCS・ビューの向きが変わっても窓が正しく切り出されることを固定する。
#
# 判定の考え方: 窓は「利用者が画面でドラッグした矩形」であり、契約上は pick 2点を
# ビューの DCS へ写した値の min/max になる（AutoCAD 実機で確定。
# docs/validation/architecture-decisions.md）。したがって不変条件はこうなる。
#
#   **同じモデル上の2点を、同じ画面の向きで pick したら、UCS の原点・回転や
#     ビューの目標点がどう置かれていても、出力は同一でなければならない**
#
# UCS の置き方と目標点は「同じ点をどの数値で呼ぶか」を変えるだけで、画面に見える矩形は
# 変わらない。座標系を取り違えた実装（例: UCS→WCS）はここで必ず破れる。
#
# ビューのねじれは別扱いにする。ねじれは画面の向きそのものを変えるため、同じ2点でも
# 覆う領域が変わる（窓は画面に沿った矩形であり、世界座標に沿った矩形ではない）。
# ねじれ有りの群は群内で比較する。
#
# 注意: 「pick の DCS が同じなら同じ出力」は成り立たない。プロットは DCS を発行時のビューで
# 世界座標へ戻すため、目標点が違えば同じ DCS でも別の領域になる（設計時にこれを取り違えた）。
#
# なぜ必要か: 既存のテストチャートは世界UCS・目標点0・ねじれ0で、UCS・WCS・DCS がすべて一致する
# 条件だった。そのため座標系を二度取り違えても（DCS→WCS→UCS）テストは緑のままで、UCS が回転した
# 実物図面で初めて露見した。
#
# 使い方: pwsh -File tests/tools/regression/window_ucs_regression.ps1 -Ver 2026
#
# 条件は DVIEW と UCS コマンドで実行時に作る（実測: accoreconsole の LISP から
# `_.DVIEW "" _PO <目標点> <視点> ""` で目標点、`_.DVIEW "" _TW <角度> ""` でねじれを設定できる）。
# 新しい図面資産を足さず、TESTCHART2 のモデル空間だけで完結させる。
param(
    [Parameter(Mandatory)][string]$Ver,     # 2024 / 2026 / 2027
    [string]$OutRoot = "$env:TEMP\CadPeta_window_ucs_regression",
    [string]$Config = "Debug",
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$tfm = switch ($Ver) {
    "2024" { "net48" }
    "2026" { "net8.0-windows" }
    "2027" { "net10.0-windows" }
    default { throw "未知の版: $Ver" }
}
$acc = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\$Config\$tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $acc)) { throw "accoreconsole がない: $acc" }
if (-not (Test-Path $plugin)) { throw "プラグイン未ビルド: $plugin" }
$dwgSrc = "$repo\tests\assets\testcharts\TESTCHART2.dwg"
if (-not (Test-Path $dwgSrc)) { throw "入力図面がない: $dwgSrc" }

# 3-4-5 の角（cos=0.8 / sin=0.6）と、その2倍角（cos=0.28 / sin=0.96）。この角だけを使うと
# 期待する pick 点がすべて有限小数になり、丸め誤差が成果物の一致判定に混ざらない
# （無理数の pick を桁で打ち切ると DCS が僅かにずれ、SVG のバイト一致が壊れる）。
$rotDeg = "36.869897645844"        # atan2(0.6, 0.8)
$rot2Deg = "73.739795291688"       # その2倍角

# 全ケースとも、pick はモデル上の同じ2点（世界座標の (50,40) と (150,110)）を指す。
# その点を現在の UCS でどう呼ぶかだけが違う。Group が同じケースは同一の成果物になるはず。
$cases = @(
    # --- Group "plan": ねじれなし。UCS の置き方と目標点だけを振る ---
    @{ Group = "plan"; Key = "base";      Setup = @();
       P1 = "50,40";     P2 = "150,110" }

    # 目標点だけを動かす。過去の直接原因（DCS は目標点を引く）を単独で切り分ける。
    # 同じモデル点を指すので pick は base と同じ
    @{ Group = "plan"; Key = "target";    Setup = @('(command "_.DVIEW" "" "_PO" "60,50,0" "60,50,1" "")');
       P1 = "50,40";     P2 = "150,110" }

    # UCS 原点だけを動かす。同じモデル点の UCS 表現へ pick を置き換える
    @{ Group = "plan"; Key = "ucsorigin"; Setup = @('(command "_.UCS" "_O" "123,-45,0")');
       P1 = "-73,85";    P2 = "27,155" }

    # UCS を Z 軸まわりに回す。旧実装（UCS→WCS）はここで必ず破れる
    @{ Group = "plan"; Key = "ucsrot";    Setup = @("(command `"_.UCS`" `"_Z`" `"$rotDeg`")");
       P1 = "64,2";      P2 = "186,-2" }

    # UCS 原点移動＋回転＋目標点。実物図面（UCS 回転・目標点非ゼロ）に相当する
    @{ Group = "plan"; Key = "combined";  Setup = @('(command "_.UCS" "_O" "123,-45,0")', "(command `"_.UCS`" `"_Z`" `"$rotDeg`")",
                                                    '(command "_.DVIEW" "" "_PO" "60,50,0" "60,50,1" "")');
       P1 = "-7.4,111.8"; P2 = "114.6,107.8" }

    # --- Group "twisted": 画面がねじれた向き。窓は画面に沿うため plan とは別の領域になる ---
    @{ Group = "twisted"; Key = "twist_world"; Setup = @("(command `"_.DVIEW`" `"`" `"_TW`" `"$rotDeg`" `"`")");
       P1 = "50,40";     P2 = "150,110" }

    # ねじれた画面で、さらに UCS も回す。同じモデル点を指すので twist_world と一致するはず
    @{ Group = "twisted"; Key = "twist_ucsrot"; Setup = @("(command `"_.UCS`" `"_Z`" `"$rotDeg`")",
                                                          "(command `"_.DVIEW`" `"`" `"_TW`" `"$rotDeg`" `"`")");
       P1 = "64,2";      P2 = "186,-2" }
)

function Invoke-Case($c) {
    $work = Join-Path $OutRoot "$($Ver)_$($c.Key)"
    if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
    $dwgCopy = Join-Path $work "target.dwg"
    Copy-Item $dwgSrc $dwgCopy
    $srcHash = (Get-FileHash $dwgCopy -Algorithm SHA256).Hash

    # SECURELOAD は既定のまま、プラグインのフォルダだけを TRUSTEDPATHS へ一時追加して復元する
    $pd = ([IO.Path]::GetDirectoryName($plugin)) -replace '\\','/'
    $pl = $plugin -replace '\\','/'
    $scr = Join-Path $work "run.scr"
    # 定常オブジェクトスナップは実行中だけ OSMODE=0 にする（スクリプト座標入力の2点目が
    # 近傍ジオメトリへ吸着し pick が入力値からずれる。AutoCAD 2026 実測・BricsCAD と同種）。
    # OSMODE はプロファイル保存のため QUIT 前に必ず元値へ戻す
    $body = @(
        '(setvar "TILEMODE" 1)'
        '(setq cpOsmOld (getvar "OSMODE"))'
        '(setvar "OSMODE" 0)'
        '(setq cpOld (getvar "TRUSTEDPATHS"))'
        "(setvar `"TRUSTEDPATHS`" (if (= cpOld `"`") `"$pd`" (strcat cpOld `";`" `"$pd`")))"
        "(command `"_.NETLOAD`" `"$pl`")"
        '(setvar "TRUSTEDPATHS" cpOld)'
    ) + $c.Setup + @("PETA", $c.P1, $c.P2, '(setvar "OSMODE" cpOsmOld)', "_.QUIT", "_Y")
    ($body -join "`r`n") + "`r`n" | Out-File $scr -Encoding ascii -NoNewline

    $stdout = Join-Path $work "accore_stdout.log"
    $p = Start-Process -FilePath $acc -ArgumentList @('/i', $dwgCopy, '/s', $scr, '/l', 'ja-JP') `
          -RedirectStandardOutput $stdout -RedirectStandardError "$work\stderr.log" -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force; $p.WaitForExit()
        throw "[$($c.Key)] accoreconsole が ${TimeoutSeconds}秒で完了しない"
    }
    if ($p.ExitCode -ne 0) { throw "[$($c.Key)] accoreconsole が異常終了した: exit=$($p.ExitCode)" }
    if ((Get-FileHash $dwgCopy -Algorithm SHA256).Hash -ne $srcHash) { throw "[$($c.Key)] 入力 DWG が変更された" }

    $svg = Get-ChildItem $work -Filter *.svg | Where-Object { $_.Name -notlike "*stage*" } | Select-Object -First 1
    if (-not $svg) { throw "[$($c.Key)] SVG が生成されていない（$stdout）" }
    $head = Get-Content $svg.FullName -TotalCount 1
    if ($head -notmatch 'width="([^"]+)mm" height="([^"]+)mm" viewBox="([^"]+)"') {
        throw "[$($c.Key)] SVG の寸法・viewBox を読み取れない: $head"
    }
    return @{
        Group = $c.Group; Key = $c.Key
        Width = $Matches[1]; Height = $Matches[2]; ViewBox = $Matches[3]
        # 成果物の同一性はハッシュで見る。モデル空間は用紙合わせが強制され viewBox が
        # 用紙全面になるため、viewBox では領域の違いを判別できない（実測）
        Sha = (Get-FileHash $svg.FullName -Algorithm SHA256).Hash
    }
}

# FILEDIA/OSMODE 等のレジストリ保存値は開始時に記録し、エラー終了を含む finally で必ず復元する
. "$PSScriptRoot\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
$failures = New-Object System.Collections.ArrayList
try {
    $results = @($cases | ForEach-Object { Invoke-Case $_ })
}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
    if (-not $regReport.ok) { [void]$failures.Add("レジストリ保全の復元検証に失敗: " + ($regReport.mismatch -join '; ')) }
}

"=== 版 $Ver / 同じモデル点・同じ画面の向きなら同一の成果物になるはず ==="
foreach ($g in ($results | Group-Object Group)) {
    $ref = $g.Group[0]
    foreach ($r in $g.Group) {
        "[{0,-8}/{1,-12}] viewBox={2} sha={3}" -f $r.Group, $r.Key, $r.ViewBox, $r.Sha.Substring(0, 12)
        if ($r.Key -eq $ref.Key) { continue }
        if ($r.Sha -ne $ref.Sha) {
            [void]$failures.Add("[$($r.Key)] 成果物が $($ref.Key) と一致しない（SVG のハッシュ相違）")
        }
    }
}
# 群が違えば領域も違う。ここが一致するとねじれが無視されていることになる
$planSha = ($results | Where-Object { $_.Group -eq "plan" })[0].Sha
$twistSha = ($results | Where-Object { $_.Group -eq "twisted" })[0].Sha
if ($planSha -eq $twistSha) {
    [void]$failures.Add("ねじれ有無で成果物が同一。窓が画面の向きに追従していない")
}

if ($failures.Count -gt 0) {
    ""
    "=== 失敗 ==="
    $failures | ForEach-Object { $_ }
    throw "版 $Ver の窓座標回帰が失敗した（$($failures.Count) 件）"
}
$residual = (Get-Process AcCoreConsole -ErrorAction SilentlyContinue | Measure-Object).Count
if ($residual -gt 0) { Write-Warning "accoreconsole が $residual 件残留している（失敗証跡）" }
"版 $Ver の窓座標回帰 $($cases.Count) ケース合格（UCS・ビューの向きに依らず同一の切り出し）"
