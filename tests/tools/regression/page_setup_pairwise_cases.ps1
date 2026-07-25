# ページ設定の実機 pairwise ケース定義（唯一の情報源）。
#
# 対象因子:
#   空間 Model/Paper/LayoutModel × 用紙単位 mm/inch × 尺度 Fit/custom
#   × 回転 0/90/180/270 × 用紙探索 同名/異名同寸法/該当寸法なし
#
# 最大の2因子直積は空間3水準×回転4水準（または用紙探索3水準×回転4水準）の12通りである。
# 下記12ケースはその下限と同数で全2因子水準組合せを覆うため、pairwiseとして最小である。

$script:CadPetaPageSetupPairwiseFactorLevels = [ordered]@{
    Space = @('Model', 'Paper', 'LayoutModel')
    Units = @('Millimeters', 'Inches')
    ScaleKind = @('Fit', 'Custom')
    Rotation = @(0, 90, 180, 270)
    MediaScenario = @('SameName', 'DifferentNameSameSize', 'NoMatchingSize')
}

function New-CadPetaPairwiseCustomScale([string]$units, [string]$scaleKind) {
    if ($scaleKind -eq 'Fit') { return $null }

    # customは単位によらず 1mm/図面単位になる値に揃え、単位換算だけを因子差にする。
    $denominator = if ($units -eq 'Inches') { 25.4 } else { 1.0 }
    return [pscustomobject]@{
        Numerator = 1.0
        Denominator = $denominator
    }
}

function New-CadPetaPageSetupPairwiseCase(
    [string]$id,
    [string]$space,
    [string]$units,
    [string]$scaleKind,
    [int]$rotation,
    [string]$mediaScenario) {
    [pscustomobject]@{
        Id = $id
        Space = $space
        Units = $units
        ScaleKind = $scaleKind
        CustomScale = New-CadPetaPairwiseCustomScale $units $scaleKind
        Rotation = $rotation
        MediaScenario = $mediaScenario
    }
}

$script:CadPetaPageSetupPairwiseCases = @(
    New-CadPetaPageSetupPairwiseCase 'PSP01' 'Model'       'Millimeters' 'Custom'   0 'SameName'
    New-CadPetaPageSetupPairwiseCase 'PSP02' 'Model'       'Millimeters' 'Fit'     90 'DifferentNameSameSize'
    New-CadPetaPageSetupPairwiseCase 'PSP03' 'Model'       'Inches'      'Fit'    180 'NoMatchingSize'
    New-CadPetaPageSetupPairwiseCase 'PSP04' 'Model'       'Inches'      'Custom' 270 'SameName'
    New-CadPetaPageSetupPairwiseCase 'PSP05' 'Paper'       'Inches'      'Fit'      0 'DifferentNameSameSize'
    New-CadPetaPageSetupPairwiseCase 'PSP06' 'Paper'       'Millimeters' 'Custom'  90 'NoMatchingSize'
    New-CadPetaPageSetupPairwiseCase 'PSP07' 'Paper'       'Millimeters' 'Fit'    180 'SameName'
    New-CadPetaPageSetupPairwiseCase 'PSP08' 'Paper'       'Inches'      'Custom' 270 'DifferentNameSameSize'
    New-CadPetaPageSetupPairwiseCase 'PSP09' 'LayoutModel' 'Inches'      'Custom'   0 'NoMatchingSize'
    New-CadPetaPageSetupPairwiseCase 'PSP10' 'LayoutModel' 'Inches'      'Fit'     90 'SameName'
    New-CadPetaPageSetupPairwiseCase 'PSP11' 'LayoutModel' 'Millimeters' 'Custom' 180 'DifferentNameSameSize'
    New-CadPetaPageSetupPairwiseCase 'PSP12' 'LayoutModel' 'Millimeters' 'Fit'    270 'NoMatchingSize'
)

function Get-CadPetaPageSetupPairwiseCases {
    # 配列自体の差し替えを防ぐため、呼び出し側には配列のコピーを返す。
    return @($script:CadPetaPageSetupPairwiseCases)
}

function Assert-CadPetaPageSetupPairwiseCoverage(
    [object[]]$Cases = (Get-CadPetaPageSetupPairwiseCases)) {
    $requiredProperties = @('Id', 'Space', 'Units', 'ScaleKind', 'CustomScale', 'Rotation', 'MediaScenario')
    $casesArray = @($Cases)
    if ($casesArray.Count -ne 12) {
        throw "pairwiseケース数が不正: 期待=12 実=$($casesArray.Count)"
    }

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($case in $casesArray) {
        if ($null -eq $case) { throw 'pairwiseケースにnullが含まれる' }
        foreach ($property in $requiredProperties) {
            if ($property -notin $case.PSObject.Properties.Name) {
                throw "ケースに必須プロパティがない: property=$property"
            }
        }
        if ([string]::IsNullOrWhiteSpace([string]$case.Id)) { throw 'ケースIdが空' }
        if (-not $ids.Add([string]$case.Id)) { throw "ケースIdが重複: $($case.Id)" }

        if ($case.ScaleKind -eq 'Fit') {
            if ($null -ne $case.CustomScale) { throw "FitケースにCustomScaleが設定されている: $($case.Id)" }
        } elseif ($case.ScaleKind -eq 'Custom') {
            if ($null -eq $case.CustomScale) { throw "CustomケースにCustomScaleがない: $($case.Id)" }
            $physicalScale = [double]$case.CustomScale.Numerator / [double]$case.CustomScale.Denominator
            if ($case.Units -eq 'Inches') { $physicalScale *= 25.4 }
            if ([Math]::Abs($physicalScale - 1.0) -gt 1e-12) {
                throw "CustomScaleが1mm/図面単位でない: $($case.Id) value=$physicalScale"
            }
        }
    }

    $factors = @($script:CadPetaPageSetupPairwiseFactorLevels.Keys)
    foreach ($factor in $factors) {
        $expectedLevels = @($script:CadPetaPageSetupPairwiseFactorLevels[$factor])
        $actualLevels = @($casesArray | ForEach-Object { $_.$factor } | Sort-Object -Unique)
        $unknown = @($actualLevels | Where-Object { $_ -notin $expectedLevels })
        $missing = @($expectedLevels | Where-Object { $_ -notin $actualLevels })
        if ($unknown.Count -gt 0 -or $missing.Count -gt 0) {
            throw "因子水準が不正: factor=$factor missing=[$($missing -join ',')] unknown=[$($unknown -join ',')]"
        }
    }

    $coveredLevelPairs = 0
    $factorPairCount = 0
    for ($leftIndex = 0; $leftIndex -lt $factors.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $factors.Count; $rightIndex++) {
            $left = $factors[$leftIndex]
            $right = $factors[$rightIndex]
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $separator = [char]31
            foreach ($case in $casesArray) {
                [void]$seen.Add("$($case.$left)$separator$($case.$right)")
            }
            $missingPairs = @()
            foreach ($leftLevel in $script:CadPetaPageSetupPairwiseFactorLevels[$left]) {
                foreach ($rightLevel in $script:CadPetaPageSetupPairwiseFactorLevels[$right]) {
                    $key = "$leftLevel$separator$rightLevel"
                    if (-not $seen.Contains($key)) { $missingPairs += "$leftLevel/$rightLevel" }
                }
            }
            if ($missingPairs.Count -gt 0) {
                throw "2因子水準組合せが不足: factors=$left/$right missing=[$($missingPairs -join ',')]"
            }
            $factorPairCount++
            $coveredLevelPairs += $seen.Count
        }
    }

    [pscustomobject]@{
        CaseCount = $casesArray.Count
        FactorCount = $factors.Count
        FactorPairCount = $factorPairCount
        CoveredLevelPairCount = $coveredLevelPairs
    }
}
