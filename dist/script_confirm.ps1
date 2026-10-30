# script_confirm.ps1: 開発用の PC の環境や履歴を変えるスクリプト(AutoCAD・互換 CAD を起動する試験、その記録から戻すだけのスクリプト、
# sign_dist.ps1)の確認。PowerShell の標準の -WhatIf と -Confirm に従う。
# スクリプトは先頭を [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')] と param にし、直後に
# $ErrorActionPreference = 'Stop' を置く(確認の画面を出せない実行で ShouldProcess が投げる例外を、スクリプト全体の失敗にするため。
# Stop が無いと、例外はその文だけを止め、スクリプトは次の文から変更を続けて終了コード 0 で終わる)。
# 最初の変更の前に、本体から次のように 1 回だけ確認する。ここの関数は ShouldProcess を呼ばない。
#   Write-ChangePlan $plan
#   Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($plan.Target, $plan.Action)
# 自動の実行は -Confirm:$false を付けて呼ぶ。Windows PowerShell 5.1 の -File は -Confirm:$false の値を文字列として渡し、
# SwitchParameter に変換できずに止まるので、5.1 で動くスクリプトは -Command "& '<スクリプト>' ... -Confirm:`$false" で呼ぶ。

# 確認する変更の内容(計画)。Target と Action は ShouldProcess に渡す文、Lines は表示する行。
function New-ChangePlan([string]$Target, [string]$Action, [string[]]$Lines) {
    [pscustomobject]@{ Target = $Target; Action = $Action; Lines = @($Lines | Where-Object { $_ }) }
}

function Write-ChangePlan($Plan) {
    Write-Host "[計画] $($Plan.Action)" -ForegroundColor Cyan
    foreach ($line in $Plan.Lines) { Write-Host "  $line" }
}

# ShouldProcess の結果を受け取り、断られたら何も変えずにスクリプトを終える。終了コードは -WhatIf のとき 0、確認で断ったとき 1。
function Exit-UnlessConfirmed([bool]$Approved) {
    if (-not $Approved) { if ($WhatIfPreference) { exit 0 } else { exit 1 } }
}
