#Requires -Version 7
# .ps1 の中で、まだ作っていない変数に [ref] を付けてコマンドへ渡している場所を見つける。
# PowerShell は存在しない変数の [ref] を作れず、「[ref] は、存在しない変数には適用できません」で止まる。
# 関数の呼び出しの引数の ([ref]$name) について、同じスクリプト(関数の中なら同じ関数)で、それより前に $name へ代入しているかを調べる。
# 代入には、= による代入、foreach の変数、param の引数を含める。見つけた場所を「相対パス:行<TAB>変数名」で 1 行ずつ出力する。
# 判定と試験は tests/CadPeta.Tests/Duplication/ScriptDuplicationTests.cs にある。
param(
    # 調べるフォルダ(.ps1 を再帰的に読む)。省略するとリポジトリの dist と tests/tools。
    [string[]]$Folder,
    # 場所を相対パスにする基準。省略するとリポジトリの根。
    [string]$Root
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lint_common.ps1')
$scope = Resolve-LintScope $Folder $Root
$Root = $scope.Root
$Folder = $scope.Folder

# 節がコマンドの引数か。([ref]$x) は丸括弧の中のパイプラインになるので、祖先をたどり、最初に見つかるのが
# コマンドの呼び出しなら引数、メソッドの呼び出し(.NET が受け取る)なら引数ではない。
function Test-CommandArgument($node) {
    for ($parent = $node.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [System.Management.Automation.Language.CommandAst]) { return $true }
        if ($parent -is [System.Management.Automation.Language.InvokeMemberExpressionAst]) { return $false }
        # 丸括弧の中のパイプライン(PipelineAst と CommandExpressionAst)を越えて、ほかの文(if や代入)に着いたら引数ではない
        if ($parent -is [System.Management.Automation.Language.StatementAst] -and $parent -isnot [System.Management.Automation.Language.PipelineAst] -and
            $parent -isnot [System.Management.Automation.Language.CommandExpressionAst]) { return $false }
    }
    return $false
}

# 節を含む一番内側の関数の本体(無ければスクリプト全体)。変数はその範囲で作られている必要がある。
function Get-Scope($node, $script) {
    for ($parent = $node.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $parent }
    }
    return $script
}

# 範囲の中で、$Offset より前に $Name を作っているか(= の代入、foreach の変数、param の引数)。
function Test-Defined($scope, [string]$Name, [int]$Offset) {
    $made = $scope.FindAll({
        param($n)
        ($n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.Left.VariablePath.UserPath -eq $Name) -or
        ($n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left -is [System.Management.Automation.Language.ConvertExpressionAst] -and $n.Left.Child -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.Left.Child.VariablePath.UserPath -eq $Name) -or
        ($n -is [System.Management.Automation.Language.ForEachStatementAst] -and $n.Variable.VariablePath.UserPath -eq $Name) -or
        ($n -is [System.Management.Automation.Language.ParameterAst] -and $n.Name.VariablePath.UserPath -eq $Name)
    }, $true) | Where-Object { $_.Extent.StartOffset -lt $Offset }
    return @($made).Count -gt 0
}

foreach ($directory in $Folder) {
    foreach ($file in (Get-ScriptFiles $directory | Sort-Object FullName)) {
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) { throw "構文エラー: $($file.FullName): $($errors[0].Message)" }
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        # コマンドの引数の [ref]$name(メソッドの呼び出しの [ref] は .NET が受け取るので、変数が無くても止まらない対象ではない)
        $refs = $ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.ConvertExpressionAst] -and $n.Type.TypeName.Name -eq 'ref' -and
            $n.Child -is [System.Management.Automation.Language.VariableExpressionAst] -and (Test-CommandArgument $n)
        }, $true)
        foreach ($ref in $refs) {
            $name = $ref.Child.VariablePath.UserPath
            if (Test-Defined (Get-Scope $ref $ast) $name $ref.Extent.StartOffset) { continue }
            "${relative}:$($ref.Extent.StartLineNumber)`t$name"
        }
    }
}
