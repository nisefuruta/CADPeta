# test_helpers.ps1 の Get-JsonLines の約束を、一時フォルダのファイルで確かめる。CAD は使わない。
# 試験の記録（prompt-trace.jsonl）は、CAD の中のプラグインが File.AppendAllText で追記している間にも試験のスクリプトが読む。
# 互換 CAD の試験は Windows PowerShell 5.1 で読むので、pwsh と 5.1 の両方で実行する（単体試験の ScriptSelfTests）。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\test_helpers.ps1"
Repair-WindowsPowerShellModulePath

$work = Join-Path ([IO.Path]::GetTempPath()) ("JsonLines_" + [guid]::NewGuid().ToString('N'))
$checked = New-Object System.Collections.Generic.List[string]
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $path = Join-Path $work 'prompt-trace.jsonl'
    [IO.File]::WriteAllText($path, "{`"operation`":`"Plugin.Load`",`"count`":null}`r`n{`"operation`":`"日本語`",`"count`":2}`r`n", (New-Object Text.UTF8Encoding $false))

    # 追記中のファイルを読む: File.AppendAllText と同じく、書き込みで開き、ほかのプロセスには読み取りだけを許す
    $writer = New-Object IO.FileStream($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $items = @(Get-JsonLines $path) }
    finally { $writer.Dispose() }
    if ($items.Count -ne 2 -or $items[1].operation -ne '日本語' -or $items[1].count -ne 2) { throw "追記中のファイルを読めなかった: $($items | ConvertTo-Json -Compress)" }
    $checked.Add('プラグインが追記している間も、記録を読める')

    # 追記の途中を読んだとき: プラグインは 1 行を改行まで 1 回で追記するので、改行で終わっていない最後の行は書き込みの途中。
    # その行は読まずに、書き終えた行だけを返す（次に読むときには書き終えている）
    $partial = Join-Path $work 'partial.jsonl'
    [IO.File]::WriteAllText($partial, "{`"operation`":`"A`"}`r`n{`"operation`":`"B`"}`r`n{`"runId`":`"x`",`"ca", (New-Object Text.UTF8Encoding $false))
    $read = @(Get-JsonLines $partial)
    if ($read.Count -ne 2 -or $read[1].operation -ne 'B') { throw "書き込みの途中の最後の行を読んだ: $($read.Count) 件" }
    $checked.Add('改行で終わっていない最後の行(書き込みの途中)は読まず、書き終えた行だけを返す')

    # 改行で終わった行が壊れていれば、書き込みの途中ではないので、これまでどおり失敗にする
    $broken = Join-Path $work 'broken.jsonl'
    [IO.File]::WriteAllText($broken, "{`"operation`":`"A`"}`r`n{`"runId`":`"x`",`"ca`r`n", (New-Object Text.UTF8Encoding $false))
    $threw = $false
    try { [void](Get-JsonLines $broken) } catch { $threw = $_.Exception.Message -like '*trace JSONL が不正*' }
    if (-not $threw) { throw '改行で終わった壊れた行を失敗にしなかった' }
    $checked.Add('改行で終わった壊れた行は失敗にする')

    if (@(Get-JsonLines (Join-Path $work 'none.jsonl')).Count -ne 0) { throw '無いファイルで空の配列を返さなかった' }
    $checked.Add('ファイルが無ければ空の配列')

    "JsonLines自己試験 OK（確かめた約束 $($checked.Count) 件、PowerShell $($PSVersionTable.PSEdition)）"
    $checked | ForEach-Object { "  - $_" }
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
