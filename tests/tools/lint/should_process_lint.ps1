# 環境や履歴を変えるスクリプトの確認の形を、構文木で検査する(dist/script_confirm.ps1 の規則)。
# - param の CmdletBinding に SupportsShouldProcess = $true と ConfirmImpact = 'High' がある
# - 最初の ShouldProcess より前に、スクリプトの最上位の $ErrorActionPreference = 'Stop' がある
# - ShouldProcess はスクリプトの本体(関数の外)から 1 回だけ呼ぶ
# 使い方: pwsh -File tests/tools/lint/should_process_lint.ps1 <スクリプト>...
param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Paths)
$ErrorActionPreference = 'Stop'

$failures = New-Object System.Collections.Generic.List[string]
foreach ($path in $Paths) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { $failures.Add("${path}: 構文の誤り: $($errors[0].Message)"); continue }

    $binding = @($ast.ParamBlock.Attributes | Where-Object { $_.TypeName.Name -eq 'CmdletBinding' })
    $named = @{}
    foreach ($argument in @($binding | ForEach-Object { $_.NamedArguments })) { $named[$argument.ArgumentName] = $argument.Argument.Extent.Text }
    if ($named['SupportsShouldProcess'] -ne '$true') { $failures.Add("${path}: CmdletBinding に SupportsShouldProcess = `$true が無い") }
    if ($named['ConfirmImpact'] -notin @("'High'", '"High"')) { $failures.Add("${path}: CmdletBinding に ConfirmImpact = 'High' が無い") }

    $calls = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Extent.Text -eq 'ShouldProcess' }, $true))
    if ($calls.Count -ne 1) { $failures.Add("${path}: ShouldProcess の呼び出しが $($calls.Count) 個(1 個にする)"); continue }
    $call = $calls[0]
    for ($parent = $call.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [Management.Automation.Language.FunctionDefinitionAst]) { $failures.Add("${path}: ShouldProcess を関数 $($parent.Name) の中で呼んでいる(本体から呼ぶ)"); break }
    }
    $stop = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$ErrorActionPreference' -and
            $_.Right.Extent.Text -in @("'Stop'", '"Stop"')
        } | Select-Object -First 1)
    if ($stop.Count -eq 0 -or $stop[0].Extent.StartOffset -gt $call.Extent.StartOffset) {
        $failures.Add("${path}: 最初の ShouldProcess より前に、最上位の `$ErrorActionPreference = 'Stop' が無い")
    }
}
if ($failures.Count -gt 0) { throw ("確認の形の違反 $($failures.Count) 件:`n" + ($failures -join "`n")) }
"should_process lint OK: $($Paths.Count) 本"
