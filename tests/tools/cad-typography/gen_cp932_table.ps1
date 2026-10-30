# CP932（Shift_JIS）エンコード表の生成。
# SHX ビッグフォントのグリフ索引は CP932 のバイト値で引くため、Unicode → CP932 の
# 変換表を製品コード（CadPeta.Glyphs.Shx）へ埋め込みリソースとして持たせる。
# 実行系の Encoding.GetEncoding(932) を使わない理由: .NET Framework は OS の NLS、
# .NET 8/10 は CodePages パッケージの独自データを使い、ベストフィット等の縁で写像が
# 食い違い得る。埋め込み表なら 3系 TFM・ホスト環境に依らずバイト決定的になる。
#
# 生成は Windows PowerShell 5.1（.NET Framework = OS の CP932 表）で行う:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File gen_cp932_table.ps1
#
# 出力形式（リトルエンディアン、Unicode 昇順）:
#   エントリ = uint16 unicode, uint16 cp932code の 4 バイト
#   - U+0000..U+007F は恒等写像のため表へ含めない（参照側がコードで処理する）
#   - 1バイト符号（半角カナ 0xA1-0xDF）は code = バイト値
#   - 2バイト符号は code = (先行バイト<<8) | 後続バイト
#   - ベストフィット変換は除外する（EncoderExceptionFallback。'?' 置換や近似字形を
#     グリフ索引へ流さないため）
param(
    [string]$OutPath = "$PSScriptRoot\..\..\..\src\CadPeta.Glyphs.Shx\Cp932Encode.bin"
)
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") {
    throw "Windows PowerShell 5.1（.NET Framework）で実行すること。現在: $($PSVersionTable.PSEdition)"
}
# Get-FileHash を使う前に PSModulePath を直す（pwsh から cmd などを経由して起動したとき）
. "$PSScriptRoot\..\common\test_helpers.ps1"
Repair-WindowsPowerShellModulePath

$enc = [System.Text.Encoding]::GetEncoding(
    932,
    (New-Object System.Text.EncoderExceptionFallback),
    (New-Object System.Text.DecoderExceptionFallback))

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$count = 0
for ($u = 0x0080; $u -le 0xFFFF; $u++) {
    if ($u -ge 0xD800 -and $u -le 0xDFFF) { continue }  # サロゲート符号単位は文字でない
    $s = [string][char]$u
    try { $bytes = $enc.GetBytes($s) } catch [System.Text.EncoderFallbackException] { continue }
    if ($bytes.Length -eq 1) {
        $code = [int]$bytes[0]
    } elseif ($bytes.Length -eq 2) {
        $code = ([int]$bytes[0] -shl 8) -bor [int]$bytes[1]
    } else {
        continue
    }
    $writer.Write([uint16]$u)
    $writer.Write([uint16]$code)
    $count++
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $stream.ToArray())
$hash = (Get-FileHash $OutPath -Algorithm SHA256).Hash
"エントリ数: $count / 出力: $OutPath ($($stream.Length) バイト)"
"SHA256: $hash"
