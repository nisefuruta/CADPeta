# ページ設定pairwiseケースの純粋自己試験。CAD・レジストリ・外部ファイルへ触れない。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\page_setup_pairwise_cases.ps1"

function Assert-Throws([scriptblock]$Action, [string]$name) {
    try {
        & $Action
    } catch {
        return
    }
    throw "負例を拒否しなかった: $name"
}

$cases = @(Get-CadPetaPageSetupPairwiseCases)
$summary = Assert-CadPetaPageSetupPairwiseCoverage $cases
if ($summary.CaseCount -ne 12 -or $summary.FactorCount -ne 5 -or
    $summary.FactorPairCount -ne 10 -or $summary.CoveredLevelPairCount -ne 77) {
    throw "coverage集計が不正: $($summary | ConvertTo-Json -Compress)"
}

foreach ($case in $cases) {
    if ($case.ScaleKind -eq 'Fit' -and $null -ne $case.CustomScale) {
        throw "FitケースのCustomScaleはnullでなければならない: $($case.Id)"
    }
    if ($case.ScaleKind -eq 'Custom' -and $null -eq $case.CustomScale) {
        throw "CustomケースのCustomScaleがない: $($case.Id)"
    }
}

# coverage検証が単なる現状追認になっていないことを、欠落ケースと未知水準で確かめる。
$missingCase = @($cases | Select-Object -First 11)
Assert-Throws { Assert-CadPetaPageSetupPairwiseCoverage $missingCase } 'ケース欠落'

$unknownLevel = @($cases | ForEach-Object { $_ | Select-Object * })
$unknownLevel[0].Units = 'Points'
Assert-Throws { Assert-CadPetaPageSetupPairwiseCoverage $unknownLevel } '未知の用紙単位'

"ページ設定pairwise自己試験 OK: cases=$($summary.CaseCount) factors=$($summary.FactorCount) factorPairs=$($summary.FactorPairCount) levelPairs=$($summary.CoveredLevelPairCount)"
