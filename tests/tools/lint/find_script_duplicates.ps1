#Requires -Version 7
# dist と tests/tools の .ps1 に、同じ目的のコードが 2 か所以上にあることを見つける。PowerShell の構文解析器で読み、次の 3 種類を 1 行ずつ出力する(タブ区切り)。
#   SAMENAME  <関数名>        <場所を ; でつないだもの>   同じ名前の関数が 2 つ以上のファイルにある
#   SAMEBODY  <関数名を , でつないだもの>  <場所>          空白とコメントを除いて本体が同じ関数が 2 つ以上ある(変数名や文字列が違えば別の本体)
#   SAMESEQ   <文の数>        <場所を ; でつないだもの>   空白とコメントを除いて同じ文が続く並びが 2 か所以上にある(関数にしていない手順の書き写し)
#   NESTED    <関数名>        <場所>                      別の関数の中に定義した関数(その関数の中でしか呼べない。関数を移したときの置き換えの誤りで起きる)
# 場所は「リポジトリの根からの相対パス:行」。何も見つからなければ何も出力しない。判定と許可の一覧は tests/CadPeta.Tests/Duplication/ScriptDuplicationTests.cs にある。
param(
    # 調べるフォルダ(.ps1 を再帰的に読む)。省略するとリポジトリの dist と tests/tools。
    [string[]]$Folder,
    # 場所を相対パスにする基準。省略するとリポジトリの根。
    [string]$Root,
    # 本体(と文の並び)とみなす最小のトークンの数。短い関数(1 行の補助)は偶然に同じ形になる。
    # TurnTrace の直す前のスクリプトで決めた値(20 にしても 40 にしても見つかる並びは同じ書き写しだけで、30 はその間の値)。
    [int]$MinimumTokens = 30,
    # 文の並びとみなす最小の文の数。TurnTrace の直す前のスクリプトの検出結果で決めた(3 では 2 か所に書いた 2 文の照合を見逃し、
    # 2 にしても偶然の一致は出なかった。トークンの最小の数で短い文の組を除くため)。
    [int]$MinimumStatements = 2
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lint_common.ps1')
$scope = Resolve-LintScope $Folder $Root
$Root = $scope.Root
$Folder = $scope.Folder

# 節の範囲にあるトークン。$Kept はコメントと改行を除いたトークン(ファイルの順)、$Offsets はその開始位置。
# 文ごとに全トークンを調べると 1,500 行のスクリプトで数十秒かかるので、開始位置を二分探索する。
function Get-OwnTokens($Kept, [int[]]$Offsets, $Extent) {
    $i = [Array]::BinarySearch($Offsets, $Extent.StartOffset)
    if ($i -lt 0) { $i = -bnot $i }
    $own = [Collections.Generic.List[object]]::new()
    while ($i -lt $Kept.Count -and $Kept[$i].Extent.EndOffset -le $Extent.EndOffset) { $own.Add($Kept[$i]); $i++ }
    $own.ToArray()
}

# .ps1 1 つの関数の定義(入れ子の関数を含む)と、文の並び(スクリプトの最上位と、関数・try・if などのブロックの中)。
function Read-ScriptFile([string]$Path) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "構文エラー: ${Path}: $($errors[0].Message)" }
    $relative = [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
    $kept = @($tokens | Where-Object { $_.Kind -notin @('Comment', 'NewLine') })
    [int[]]$offsets = @($kept | ForEach-Object { $_.Extent.StartOffset })
    $functions = foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        $own = @(Get-OwnTokens $kept $offsets $function.Body.Extent)
        # 別の関数の本体に直接定義した関数(入れ子の関数)は、その関数の中でしか呼べない。
        # { } のスクリプトブロック(ジョブに渡す本体など)の中の定義は、そのブロックの中で完結するので、入れ子にしない。
        $nested = $false
        for ($parent = $function.Parent; $null -ne $parent; $parent = $parent.Parent) {
            if ($parent -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) { break }
            if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $nested = $true; break }
        }
        [pscustomobject]@{
            Name   = $function.Name
            Place  = "${relative}:$($function.Extent.StartLineNumber)"
            File   = $relative
            Nested = $nested
            Tokens = $own.Count
            Shape  = ($own | ForEach-Object { $_.Text }) -join ' '
        }
    }
    $blocks = foreach ($block in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.NamedBlockAst] -or $node -is [System.Management.Automation.Language.StatementBlockAst] }, $true)) {
        $statements = foreach ($statement in $block.Statements) {
            # 関数の定義は本体の検査(SAMEBODY)が扱う。並びに入れると、同じ関数を 2 回数える。
            if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { continue }
            $own = @(Get-OwnTokens $kept $offsets $statement.Extent)
            [pscustomobject]@{ Line = $statement.Extent.StartLineNumber; Tokens = $own.Count; Shape = ($own | ForEach-Object { $_.Text }) -join ' ' }
        }
        # 関数の本体全体が同じなら SAMEBODY が出すので、関数の本体のブロックかどうかを持つ。
        [pscustomobject]@{ Statements = @($statements); IsFunctionBody = $block.Parent.Parent -is [System.Management.Automation.Language.FunctionDefinitionAst] }
    }
    [pscustomobject]@{ File = $relative; Functions = @($functions); Blocks = @($blocks) }
}

$files = foreach ($directory in $Folder) { Get-ScriptFiles $directory | Sort-Object FullName }
$scripts = @($files | ForEach-Object { Read-ScriptFile $_.FullName })
$functions = @($scripts | ForEach-Object { $_.Functions })

foreach ($group in ($functions | Group-Object Name | Sort-Object Name)) {
    if (@($group.Group | Select-Object -ExpandProperty File -Unique).Count -ge 2) {
        "SAMENAME`t$($group.Name)`t$(($group.Group | Select-Object -ExpandProperty Place) -join ';')"
    }
}
foreach ($function in ($functions | Where-Object { $_.Nested } | Sort-Object Place)) {
    "NESTED`t$($function.Name)`t$($function.Place)"
}
foreach ($group in ($functions | Where-Object { $_.Tokens -ge $MinimumTokens } | Group-Object Shape | Sort-Object { $_.Group[0].Place })) {
    if ($group.Count -ge 2) {
        "SAMEBODY`t$(($group.Group | Select-Object -ExpandProperty Name) -join ',')`t$(($group.Group | Select-Object -ExpandProperty Place) -join ';')"
    }
}

# 文の並び: 各ブロックの連続する $MinimumStatements 個の文を窓にして、形が同じ窓を集める。
# 同じ並びが長く続くと窓が 1 文ずつずれて何組も出るので、どの場所も 1 文前の窓から続いている組は、続きとして出さずに最初の組の文の数へ足す。
$windows = [Collections.Generic.Dictionary[string, Collections.Generic.List[object]]]::new()
foreach ($script in $scripts) {
    foreach ($block in $script.Blocks) {
        for ($i = 0; $i + $MinimumStatements -le $block.Statements.Count; $i++) {
            $slice = $block.Statements[$i..($i + $MinimumStatements - 1)]
            if (($slice | Measure-Object Tokens -Sum).Sum -lt $MinimumTokens) { continue }
            $shape = ($slice | ForEach-Object { $_.Shape }) -join "`n"
            if (-not $windows.ContainsKey($shape)) { $windows[$shape] = [Collections.Generic.List[object]]::new() }
            $windows[$shape].Add([pscustomobject]@{ File = $script.File; Block = $block; Index = $i; Place = "$($script.File):$($block.Statements[$i].Line)" })
        }
    }
}
# 組(窓の一覧)は番号で扱う。パイプラインに組を流すと、組が 1 つだけのときに PowerShell が一覧をほどき、窓が 1 つずつの組に見える。
$repeated = [Collections.Generic.List[object]]::new()
foreach ($list in $windows.Values) { if ($list.Count -ge 2) { $repeated.Add($list) } }
# 窓の場所(ブロックと位置)から、その窓が属する組の番号を引く。
$groupAt = @{}
$keyOf = { param($each, $offset) "$([Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($each.Block)):$($each.Index + $offset)" }
for ($g = 0; $g -lt $repeated.Count; $g++) { foreach ($each in $repeated[$g]) { $groupAt[(& $keyOf $each 0)] = $g } }
# 組の全員を $offset 文ずらした窓が、そろって同じ 1 つの組(大きさも同じ)に属するか。
$shiftedTogether = {
    param($group, $offset)
    $ids = @($group | ForEach-Object { $groupAt[(& $keyOf $_ $offset)] })
    if (@($ids | Where-Object { $null -eq $_ }).Count -gt 0) { return $false }
    $unique = @($ids | Select-Object -Unique)
    return $unique.Count -eq 1 -and $repeated[$unique[0]].Count -eq $group.Count
}
foreach ($g in @(0..($repeated.Count - 1) | Where-Object { $repeated.Count -gt 0 } | Sort-Object { $repeated[$_][0].Place })) {
    $group = $repeated[$g]
    if (& $shiftedTogether $group -1) { continue }
    $length = $MinimumStatements
    while (& $shiftedTogether $group ($length - $MinimumStatements + 1)) { $length++ }
    if (@($group | Where-Object { $_.Block.IsFunctionBody -and $_.Index -eq 0 -and $length -eq $_.Block.Statements.Count }).Count -eq $group.Count) { continue }
    "SAMESEQ`t$length`t$(($group | Sort-Object Place | Select-Object -ExpandProperty Place) -join ';')"
}
