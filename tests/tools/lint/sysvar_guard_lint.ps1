# 永続sysvarの裸変更を検出するlint。
#
# 対象: tests/tools 配下の *.ps1 が FILEDIA / OSMODE / SECURELOAD / TRUSTEDPATHS /
# PLOTTRANSPARENCYOVERRIDE を setvar（.scr生成文字列）または COM SetVariable で変更する場合、
# 次のどちらかを満たさなければ違反とする。
#   1. CadRegGuard.ps1 を dot-source している（AutoCAD 系: レジストリスナップショット→復元検証）
#   2. 例外リストに載る互換CAD GUI ハーネスで、読み戻し検証付きの in-session 復元を実装している
#      （互換CADの永続先は AutoCAD と別ハイブのため CadRegGuard の対象外）
#
# 例外リストは「登録すれば通る」台帳にしないため、lint が復元実装の読み戻し検証
# （$readBack 照合）の存在まで機械検査する。
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$script:CadPetaSysvarNames = 'FILEDIA|OSMODE|SECURELOAD|TRUSTEDPATHS|PLOTTRANSPARENCYOVERRIDE'
$script:CadPetaSysvarWritePattern =
    "(setvar\s+[`"']($script:CadPetaSysvarNames)[`"']|SetVariable\s*\(\s*[`"']($script:CadPetaSysvarNames)[`"'])"
# CadRegGuard の dot-source（AutoCAD 系の合格条件）
$script:CadPetaGuardPattern = 'CadRegGuard\.ps1'
# in-session 復元の読み戻し検証（互換CAD例外の合格条件）。peta_window_rotation_compat.ps1 の
# RESTORE ループが正のパターンで、SetVariable 後に GetVariable の再読値を照合していること。
$script:CadPetaVerifiedRestorePattern = '\$readBack"?\s+-ne\s+'

# 互換CAD GUI ハーネス。value は例外の根拠（違反表示にも使う）。
$script:CadPetaCompatExceptions = @{
    'regression/peta_window_rotation_compat.ps1' = '互換CAD GUI。別レジストリハイブのため in-session 読み戻し検証復元'
    'regression/peta_option_smoke_compat.ps1' = '互換CAD GUI。別レジストリハイブのため in-session 読み戻し検証復元'
    'regression/window_contract/window_contract_compat.ps1' = '互換CAD GUI。別レジストリハイブのため in-session 読み戻し検証復元'
    'pdf-emf/gradient_compat_e2e.ps1' = '互換CAD GUI。別レジストリハイブのため in-session 読み戻し検証復元'
}

function Get-CadPetaSysvarLintViolations([string]$rootDir) {
    $rootFull = [IO.Path]::GetFullPath($rootDir)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "lint対象ディレクトリがない: $rootFull"
    }
    $violations = @()
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -Filter '*.ps1' -File) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -like 'lint/*') { continue }                     # lint 自身
        if ($relative -eq 'regression/CadRegGuard.ps1') { continue }   # ガード実装本体
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text -notmatch $script:CadPetaSysvarWritePattern) { continue }
        if ($text -match $script:CadPetaGuardPattern) { continue }
        if ($script:CadPetaCompatExceptions.ContainsKey($relative)) {
            if ($text -match $script:CadPetaVerifiedRestorePattern) { continue }
            $violations += "${relative}: 例外リスト（$($script:CadPetaCompatExceptions[$relative])）だが読み戻し検証付き復元が見つからない"
            continue
        }
        $violations += "${relative}: 永続sysvarを変更するのに CadRegGuard.ps1 を使っていない（互換CAD GUI なら例外リストへ根拠付きで登録し、読み戻し検証復元を実装する）"
    }
    # 例外リストの空振り（ファイル改名・削除で台帳が腐るのを防ぐ）
    foreach ($exception in $script:CadPetaCompatExceptions.Keys) {
        if (-not (Test-Path -LiteralPath (Join-Path $rootFull $exception) -PathType Leaf)) {
            $violations += "例外リストのファイルが存在しない: $exception"
        }
    }
    return $violations
}

function Invoke-CadPetaSysvarLintSelfTest {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("CadPeta_sysvar_lint_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path (Join-Path $work 'regression') | Out-Null
    try {
        # ガード実装本体のダミー（除外対象パス）
        Set-Content -LiteralPath (Join-Path $work 'regression\CadRegGuard.ps1') -Value '# guard' -Encoding UTF8
        # 1) 裸変更 → 違反
        Set-Content -LiteralPath (Join-Path $work 'bare.ps1') -Encoding UTF8 -Value @'
$scr = '(setvar "SECURELOAD" 0)'
'@
        # 2) ガード付き → 合格
        Set-Content -LiteralPath (Join-Path $work 'guarded.ps1') -Encoding UTF8 -Value @'
. "$PSScriptRoot\regression\CadRegGuard.ps1"
$scr = '(setvar "SECURELOAD" 0)'
'@
        # 3) 読取だけ → 対象外
        Set-Content -LiteralPath (Join-Path $work 'readonly.ps1') -Encoding UTF8 -Value @'
$scr = '(princ (getvar "SECURELOAD"))'
'@
        $violations = @(Get-CadPetaSysvarLintViolations $work)
        $expectedMissing = @($script:CadPetaCompatExceptions.Keys).Count   # 一時dirには例外ファイルが無い
        $bare = @($violations | Where-Object { $_ -like 'bare.ps1:*' })
        if ($bare.Count -ne 1) { throw "自己試験失敗: 裸変更を1件として検出しなかった: $($violations -join '; ')" }
        if (@($violations | Where-Object { $_ -like 'guarded.ps1:*' -or $_ -like 'readonly.ps1:*' }).Count -ne 0) {
            throw "自己試験失敗: ガード付き・読取のみを誤検出した: $($violations -join '; ')"
        }
        if (@($violations | Where-Object { $_ -like '例外リストのファイルが存在しない:*' }).Count -ne $expectedMissing) {
            throw "自己試験失敗: 例外リストの空振り検出が $expectedMissing 件でない: $($violations -join '; ')"
        }
        "sysvar lint 自己試験 OK: 裸変更=検出 ガード付き=合格 読取のみ=対象外 例外空振り=検出"
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    Invoke-CadPetaSysvarLintSelfTest
    return
}

if (-not $Root) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$found = @(Get-CadPetaSysvarLintViolations $Root)
if ($found.Count -gt 0) {
    $found | ForEach-Object { "VIOLATION: $_" }
    throw "sysvar lint 違反 $($found.Count) 件。永続sysvarの変更は CadRegGuard（AutoCAD系）または読み戻し検証復元（互換CAD GUI）で保全すること"
}
"sysvar lint OK: 違反0件（$Root）"
