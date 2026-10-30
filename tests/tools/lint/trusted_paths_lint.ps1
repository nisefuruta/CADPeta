# tests/tools の .ps1 と .scr に、TRUSTEDPATHS と SECURELOAD を書き換える行が無いことを確かめる。
# 試験は、読み込む DLL と LISP を、すでに信頼済みのフォルダ(C:\Tools\ACAD\dotnet、C:\Tools\ACAD\lisp)へコピーして読み込み、
# この 2 つの値は変えない(一時的に変えて戻す形も使わない)。読む行(getvar、GetVariable、GetValue)は違反にしない。
# 対象: LISP の setvar、COM の SetVariable、レジストリへの書き込み(Set-ItemProperty、New-ItemProperty、SetValue)、
#       .scr でコマンドとして値を入力する行(SECURELOAD や TRUSTEDPATHS だけの行)。
# 使い方: pwsh -File tests/tools/lint/trusted_paths_lint.ps1 [-Root <tests/tools のフォルダ>]
param([string]$Root = "")
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Join-Path $PSScriptRoot '..' }
$Root = (Resolve-Path $Root).Path

$names = 'TRUSTEDPATHS|SECURELOAD'
$patterns = @(
    "\(\s*setvar\s+[`"']($names)[`"']",
    "SetVariable\s*\(\s*[`"']($names)[`"']",
    "\b(Set|New)-ItemProperty\b[^\r\n]*\b($names)\b",
    "\.SetValue\s*\(\s*[`"']($names)[`"']",
    "^\s*[_.]*($names)\s*$"
)
# 書き換える行があっても違反にしないファイル(理由つき)。lint の自身のフォルダ(lint/)は、違反の例を文字列で持つので除く
$allowed = @{
    'regression/cad_reg_guard_selftest.ps1' = 'AutoCAD のプロファイルではない一時的な HKCU のキーに値を作り、記録と復元を確かめる'
}

$violations = New-Object System.Collections.Generic.List[string]
foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Include '*.ps1', '*.scr')) {
    $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
    if ($relative -like 'lint/*' -or $allowed.ContainsKey($relative)) { continue }
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $lineNo++
        if ($line.TrimStart().StartsWith('#') -or $line.TrimStart().StartsWith(';')) { continue }
        foreach ($pattern in $patterns) {
            if ([regex]::IsMatch($line, $pattern, 'IgnoreCase')) { $violations.Add("${relative}:${lineNo}: $($line.Trim())"); break }
        }
    }
}
# .scr が読み込む DLL と LISP は、信頼済みのフォルダ(C:\Tools\ACAD\dotnet、C:\Tools\ACAD\lisp)のものにする
# (TRUSTEDPATHS を変えないので、ほかの場所のファイルは確認の画面で止まるか、読み込まれない)。
# 読み込み先を確かめられるのは、(command "_.NETLOAD" "<パス>") と (load "<パス>") の、パスを文字どおりに書いた形だけなので、
# 読み込みの語(NETLOAD、APPLOAD、ARX、load、読み込むファイルの拡張子)を含む行のうち、その形を除いても語が残る行は違反にする
# (コマンドの次の行にパスを書く形や、strcat で組み立てるパスを見逃さないように)
$loadPattern = '\(command\s+"_\.NETLOAD"\s+"([^"]+)"\s*\)|\(load\s+"([^"]+)"\s*\)'
# 語として現れるときだけ読み込みとみなす（parx のような名前の一部では違反にしない）
$loadWord = '\b(NETLOAD|APPLOAD|ARX|ARXLOAD)\b|\(\s*load\b|\.(dll|lsp|arx|fas|vlx)\b'
foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.scr')) {
    $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName, [Text.Encoding]::Latin1)) {
        $lineNo++
        if ($line.TrimStart().StartsWith(';')) { continue }
        foreach ($match in [regex]::Matches($line, $loadPattern, 'IgnoreCase')) {
            $path = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            $target = $path.Replace('\\', '/').Replace('\', '/')
            if ($target -notmatch '^C:/Tools/ACAD/(dotnet|lisp)/[^/]+$') { $violations.Add("${relative}:${lineNo}: 信頼済みのフォルダの外を読み込む: $target") }
        }
        if ([regex]::IsMatch([regex]::Replace($line, $loadPattern, '', 'IgnoreCase'), $loadWord, 'IgnoreCase')) {
            $violations.Add("${relative}:${lineNo}: 読み込み先を確かめられない形の読み込み((command `"_.NETLOAD`" `"<パス>`") か (load `"<パス>`") で書く): $($line.Trim())")
        }
    }
}
foreach ($file in $allowed.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $file) -PathType Leaf)) { $violations.Add("除外のファイルが無い: $file") }
}
if ($violations.Count -gt 0) { throw ("TRUSTEDPATHS・SECURELOAD を書き換える行 $($violations.Count) 件:`n" + ($violations -join "`n")) }
"trusted_paths lint OK: $Root"
