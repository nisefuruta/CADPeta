#Requires -Version 7
# finally で AutoCAD のプロファイルを戻す(Restore-CadRegState・Restore-CadRegStateAndReport)試験が、本体の例外と復元の失敗を
# 両方報告することを確かめる。本体が例外を投げると finally の後の文は実行されないので、finally の後で復元の結果を判定する形では、
# 復元の失敗(戻せなかった設定、残した記録)が利用者に届かない。次の 2 つを満たさない try を違反にする。
#   1. 本体の例外を catch で変数に入れる(catch { $runError = $_ })。catch { Write-Warning $_ } のように捨てる形は違反
#   2. その try の後(同じ関数の中、関数の外ならスクリプトの中)で、1. の変数を最初の引数にして Assert-CadRunAndRegRestore を呼ぶ
# 使い方: pwsh -File tests/tools/lint/reg_restore_report_lint.ps1 [-Folder <調べるフォルダ>] [-Root <相対パスの基準>]
param([string[]]$Folder, [string]$Root)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lint_common.ps1')

$restoreCommands = @('Restore-CadRegState', 'Restore-CadRegStateAndReport')

# 構文木 $ast の違反を「行: 理由」の配列で返す
function Find-RegRestoreReportViolations($ast) {
  $violations = New-Object System.Collections.Generic.List[string]
  $tries = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $n.Finally }, $true)
  foreach ($try in $tries) {
    $restores = @($try.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in $restoreCommands }, $true))
    if ($restores.Count -eq 0) { continue }
    $line = $try.Extent.StartLineNumber
    # catch で $_ を入れた変数の名前
    $caught = @($try.CatchClauses | ForEach-Object {
        $_.Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $n.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.Right.Extent.Text -eq '$_' }, $true)
      } | ForEach-Object { $_.Left.VariablePath.UserPath })
    if ($caught.Count -eq 0) { $violations.Add("${line}: finally で設定を戻す try に、本体の例外を変数に入れる catch が無い(本体が例外を投げると、復元の失敗が報告されない)"); continue }
    $scope = $ast
    for ($parent = $try.Parent; $null -ne $parent; $parent = $parent.Parent) {
      if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $scope = $parent; break }
    }
    $asserts = @($scope.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Assert-CadRunAndRegRestore' -and
          $n.Extent.StartOffset -gt $try.Extent.EndOffset -and $n.CommandElements.Count -gt 1 -and
          $n.CommandElements[1] -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.CommandElements[1].VariablePath.UserPath -in $caught }, $true))
    if ($asserts.Count -eq 0) { $violations.Add("${line}: finally で設定を戻す try の後で、catch の変数(`$$($caught -join ', $'))を渡して Assert-CadRunAndRegRestore を呼んでいない") }
  }
  return @($violations)
}

# 検出器の自己確認(壊れて何も見つけなくなったら止める)
$bad = [System.Management.Automation.Language.Parser]::ParseInput('try { Run } finally { $r = Restore-CadRegState $g }; if (-not $r.ok) { throw "x" }', [ref]$null, [ref]$null)
$good = [System.Management.Automation.Language.Parser]::ParseInput('try { Run } catch { $e = $_ } finally { Restore-CadRegStateAndReport $g ([ref]$r) }; Assert-CadRunAndRegRestore $e $r', [ref]$null, [ref]$null)
$inFunction = [System.Management.Automation.Language.Parser]::ParseInput('function F { try { Run } catch { $e = $_ } finally { $r = Restore-CadRegState $g } }; Assert-CadRunAndRegRestore $e $r', [ref]$null, [ref]$null)
$swallow = [System.Management.Automation.Language.Parser]::ParseInput('try { Run } catch { Write-Warning $_ } finally { $r = Restore-CadRegState $g }; Assert-CadRunAndRegRestore $e $r', [ref]$null, [ref]$null)
$otherVariable = [System.Management.Automation.Language.Parser]::ParseInput('try { Run } catch { $e = $_ } finally { $r = Restore-CadRegState $g }; Assert-CadRunAndRegRestore $null $r', [ref]$null, [ref]$null)
if (@(Find-RegRestoreReportViolations $bad).Count -ne 1) { throw '自己確認: catch の無い try を違反にしなかった' }
if (@(Find-RegRestoreReportViolations $swallow).Count -ne 1) { throw '自己確認: 本体の例外を捨てる catch を違反にしなかった' }
if (@(Find-RegRestoreReportViolations $otherVariable).Count -ne 1) { throw '自己確認: catch の変数を渡さない Assert-CadRunAndRegRestore を合格にした' }
if (@(Find-RegRestoreReportViolations $good).Count -ne 0) { throw '自己確認: 規則どおりの try を違反にした' }
if (@(Find-RegRestoreReportViolations $inFunction).Count -ne 1) { throw '自己確認: 関数の外の Assert-CadRunAndRegRestore を、関数の中の try の判定に数えた' }

$scope = Resolve-LintScope $Folder $Root
$found = New-Object System.Collections.Generic.List[string]
foreach ($directory in $scope.Folder) {
  foreach ($file in (Get-ScriptFiles $directory | Sort-Object FullName)) {
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$errors)
    if ($errors.Count -gt 0) { throw "構文エラー: $($file.FullName): $($errors[0].Message)" }
    $relative = [IO.Path]::GetRelativePath($scope.Root, $file.FullName).Replace('\', '/')
    foreach ($v in (Find-RegRestoreReportViolations $ast)) { $found.Add("${relative}:$v") }
  }
}
if ($found.Count -gt 0) {
  $found | ForEach-Object { "NG: $_" }
  throw "本体の例外と復元の失敗を両方報告しない試験が $($found.Count) 件ある(catch { `$runError = `$_ } と Assert-CadRunAndRegRestore を使う)"
}
"reg_restore_report_lint OK"
