# AutoCAD のレジストリ保存値（FILEDIA/OSMODE/SECURELOAD/TRUSTEDPATHS/PLOTTRANSPARENCYOVERRIDE）の
# 保全ガード。ハーネスは開始時に Get-CadRegSnapshot で記録し、正常終了・エラー終了を問わず
# finally で Restore-CadRegState を呼んで開始状態へ戻す（復元後に再取得して一致を証明する）。
#
# 背景: これらは HKCU の AutoCAD ハイブへ保存され、scr 内の復元行は「入力ずれ・タイムアウト・
# 強制終了」で実行されないことがある（accoreconsole の kill が FileDialog=0 を永続化する実測、
# scr の setvar "SECURELOAD" 0 がプロファイル変数として保存される実測、OSMODE の同種の実測あり）。
# scr 内の復元と、この外側ガードの二段で守る。
#
# 使い方（ドットソース）:
#   . "$PSScriptRoot\CadRegGuard.ps1"
#   $regGuard = Get-CadRegSnapshot
#   try { ...本体... }
#   finally {
#       $regReport = Restore-CadRegState $regGuard
#       if (-not $regReport.ok) { ...失敗として扱う... }
#   }

$script:CadRegGuardValueNames = @('FileDialog', 'SECURELOAD', 'TRUSTEDPATHS', 'PLOTTRANSPARENCYOVERRIDE', 'OSMODE')
$script:CadRegGuardDefaultBasePath = 'HKCU:\Software\Autodesk\AutoCAD'

# HKCU の AutoCAD ハイブ全体から対象値を集める。戻り値: キー「<レジストリキー>::<値名>」の hashtable
function Get-CadRegSnapshot(
    [string]$BasePath = $script:CadRegGuardDefaultBasePath,
    [string[]]$ValueNames = $script:CadRegGuardValueNames
) {
    $snap = @{}
    if (-not (Test-Path $BasePath)) { return $snap }
    # 部分スナップショットは「保全できた」という偽PASSと、開始時からあった値の誤削除を招く。
    # 列挙・読取失敗は開始前に顕在化し、状態を変更する本体へ進ませない。
    $keys = @(Get-Item $BasePath -ErrorAction Stop) + @(Get-ChildItem $BasePath -Recurse -ErrorAction Stop)
    foreach ($k in $keys) {
        foreach ($n in $ValueNames) {
            $v = $k.GetValue($n, $null)
            if ($null -ne $v) {
                $snap["$($k.Name)::$n"] = @{ Key = $k.Name; Name = $n; Kind = $k.GetValueKind($n).ToString(); Value = $v }
            }
        }
    }
    $snap
}

# 開始時スナップショットとの差分を復元し、再取得で一致を証明する。
# 戻り値: @{ actions=実施した復元の一覧; mismatch=復元後も一致しない項目; ok=bool }
function Restore-CadRegState(
    [hashtable]$before,
    [string]$BasePath = $script:CadRegGuardDefaultBasePath,
    [string[]]$ValueNames = $script:CadRegGuardValueNames,
    [bool]$EnforceFileDialogOne = $true
) {
    $actions = New-Object System.Collections.ArrayList
    $mismatch = New-Object System.Collections.ArrayList
    try { $after = Get-CadRegSnapshot $BasePath $ValueNames }
    catch {
        [void]$mismatch.Add("復元前のレジストリ再読に失敗: $($_.Exception.Message)")
        return @{ actions = @($actions); mismatch = @($mismatch); ok = $false }
    }
    foreach ($id in $before.Keys) {
        $b = $before[$id]
        $cur = if ($after.ContainsKey($id)) { $after[$id].Value } else { $null }
        $expected = if ($EnforceFileDialogOne -and $b.Name -eq 'FileDialog') { 1 } else { $b.Value }
        if ("$cur" -ne "$expected") {
            $psPath = $b.Key -replace '^HKEY_CURRENT_USER', 'HKCU:'
            $type = switch ($b.Kind) { 'DWord' { 'DWord' } 'QWord' { 'QWord' } 'ExpandString' { 'ExpandString' } default { 'String' } }
            try {
                Set-ItemProperty -Path $psPath -Name $b.Name -Value $expected -Type $type -ErrorAction Stop
                $reason = if ($EnforceFileDialogOne -and $b.Name -eq 'FileDialog' -and "$($b.Value)" -ne '1') { '安全値' } else { '復元' }
                [void]$actions.Add("${reason}: $id 「$cur」→「$expected」")
            }
            catch { [void]$mismatch.Add("書込失敗: $id 期待「$expected」: $($_.Exception.Message)") }
        }
    }
    foreach ($id in $after.Keys) {
        if (-not $before.ContainsKey($id)) {
            $a = $after[$id]
            try {
                Remove-ItemProperty -Path ($a.Key -replace '^HKEY_CURRENT_USER', 'HKCU:') -Name $a.Name -ErrorAction Stop
                [void]$actions.Add("削除: $id（開始時に存在しなかった値）")
            }
            catch { [void]$mismatch.Add("削除失敗: ${id}: $($_.Exception.Message)") }
        }
    }
    try { $verify = Get-CadRegSnapshot $BasePath $ValueNames }
    catch {
        [void]$mismatch.Add("復元後のレジストリ検証に失敗: $($_.Exception.Message)")
        return @{ actions = @($actions); mismatch = @($mismatch); ok = $false }
    }
    foreach ($id in $before.Keys) {
        $v = if ($verify.ContainsKey($id)) { $verify[$id].Value } else { $null }
        $expected = if ($EnforceFileDialogOne -and $before[$id].Name -eq 'FileDialog') { 1 } else { $before[$id].Value }
        if ("$v" -ne "$expected") { [void]$mismatch.Add("不一致: $id 期待「$expected」実「$v」") }
    }
    foreach ($id in $verify.Keys) { if (-not $before.ContainsKey($id)) { [void]$mismatch.Add("残留: $id") } }
    @{ actions = @($actions); mismatch = @($mismatch); ok = ($mismatch.Count -eq 0) }
}
