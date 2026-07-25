# EMF の変更前後差分が「EXTTEXTOUTW の文字列〜dx 間パディング」だけであることを検証する。
# 併せて新側のパディングが全て 0 であることも確認する。
param(
    [Parameter(Mandatory)][string]$BaseDir,
    [Parameter(Mandatory)][string]$NewDir
)
$ErrorActionPreference = "Stop"
$total = 0; $bad = 0
foreach ($nf in Get-ChildItem $NewDir -Filter *.emf) {
    $bf = Join-Path $BaseDir $nf.Name
    if (-not (Test-Path $bf)) { continue }
    $a = [IO.File]::ReadAllBytes($bf); $b = [IO.File]::ReadAllBytes($nf.FullName)
    if ($a.Length -ne $b.Length) { "$($nf.Name): 長さ不一致"; $bad++; continue }
    # パディング領域の一覧を作る
    $pads = New-Object System.Collections.Generic.List[object]
    $pos = 0
    while ($pos + 8 -le $b.Length) {
        $type = [BitConverter]::ToInt32($b, $pos); $size = [BitConverter]::ToInt32($b, $pos + 4)
        if ($size -le 0 -or $pos + $size -gt $b.Length) { break }
        if ($type -eq 84) {
            $nChars = [BitConverter]::ToInt32($b, $pos + 44)
            $offString = [BitConverter]::ToInt32($b, $pos + 48)
            $offDx = [BitConverter]::ToInt32($b, $pos + 72)
            $strEnd = $offString + 2 * $nChars
            if ($offDx -gt $strEnd) { $pads.Add(@(($pos + $strEnd), ($pos + $offDx))) }
        }
        $pos += $size
    }
    $outside = 0; $diffBytes = 0; $padNonZero = 0
    for ($i = 0; $i -lt $a.Length; $i++) {
        if ($a[$i] -ne $b[$i]) {
            $diffBytes++
            $inPad = $false
            foreach ($p in $pads) { if ($i -ge $p[0] -and $i -lt $p[1]) { $inPad = $true; break } }
            if (-not $inPad) { $outside++ }
        }
    }
    foreach ($p in $pads) { for ($i = $p[0]; $i -lt $p[1]; $i++) { if ($b[$i] -ne 0) { $padNonZero++ } } }
    $total++
    if ($outside -gt 0 -or $padNonZero -gt 0) {
        "$($nf.Name): NG（パディング外差分 $outside バイト / 新側パディング非0 $padNonZero）"; $bad++
    } else {
        "$($nf.Name): OK（差分 $diffBytes バイトすべてパディング内・新側は全て0）"
    }
}
"判定: $total ファイル中 NG $bad"
