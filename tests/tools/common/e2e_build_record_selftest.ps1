# 試験の DLL が今のソースから作ったものかの確認（test_helpers.ps1 の Get-E2eBuildFingerprint・Write-E2eBuildRecord・
# Assert-E2eBuildRecordCurrent）の約束を、一時の git のリポジトリで確かめる。CAD は使わない。
# 互換 CAD の試験は Windows PowerShell 5.1 で確認するので、pwsh と 5.1 の両方で実行する（単体試験の ScriptSelfTests）。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\test_helpers.ps1"
Repair-WindowsPowerShellModulePath

$work = Join-Path ([IO.Path]::GetTempPath()) "E2eBuildRecord_$PID"
$repo = Join-Path $work 'repo'
$outRoot = Join-Path $repo 'src\Plugin\bin\Release'
$checked = New-Object System.Collections.Generic.List[string]

function Invoke-FixtureGit([string[]]$Arguments) {
    $output = & git -C $repo @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') に失敗: $output" }
}

# src と dist/dist_build.ps1 と、ビルドに関係しない docs を持つリポジトリ。bin は無視する
function New-TestRepo {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    foreach ($dir in 'src\Plugin', 'dist', 'docs') { New-Item -ItemType Directory -Force -Path (Join-Path $repo $dir) | Out-Null }
    [IO.File]::WriteAllText((Join-Path $repo 'src\Plugin\Command.cs'), "class Command {}`r`n")
    [IO.File]::WriteAllText((Join-Path $repo 'dist\dist_build.ps1'), "`$DistBuildConfig = 'Release'`r`n")
    [IO.File]::WriteAllText((Join-Path $repo 'docs\manual.md'), "# manual`r`n")
    [IO.File]::WriteAllText((Join-Path $repo '.gitignore'), "bin/`r`n")
    Invoke-FixtureGit @('init', '-q')
    Invoke-FixtureGit @('add', '-A')
    Invoke-FixtureGit @('-c', 'user.name=test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-q', '-m', 'test')
    New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
}

# 止めた理由の文に、利用者がすべきこと（build_e2e.ps1 で作り直す）が入っているか。ほかの例外（関数が無い など）は止めたことにしない
function Test-StaleBuildError($ErrorRecord) {
    if ($ErrorRecord.Exception.Message -like '*build_e2e.ps1*') { return $true }
    throw $ErrorRecord
}

# 今のソースの記録を書いた状態から、$Change でリポジトリを変えて、確認が止まるか（$ShouldStop）を確かめる
function Assert-Freshness([string]$Label, [scriptblock]$Change, [bool]$ShouldStop) {
    New-TestRepo
    Write-E2eBuildRecord $outRoot (Get-E2eBuildFingerprint $repo)
    & $Change
    $stopped = $false
    try { Assert-E2eBuildRecordCurrent $outRoot $repo } catch { $stopped = Test-StaleBuildError $_ }
    if ($stopped -ne $ShouldStop) { throw "${Label}: 止まるはず=$ShouldStop、実際=$stopped" }
    $checked.Add($Label)
}

try {
    # 記録が無ければ止める（build_e2e.ps1 で作っていない DLL）
    New-TestRepo
    $threw = $false
    try { Assert-E2eBuildRecordCurrent $outRoot $repo } catch { $threw = Test-StaleBuildError $_ }
    if (-not $threw) { throw '記録が無いのに止めなかった' }
    $checked.Add('記録が無ければ止める')

    Assert-Freshness '記録の後に何も変えなければ止めない' { } $false
    Assert-Freshness 'src のコミットしていない変更で止める' { [IO.File]::AppendAllText((Join-Path $repo 'src\Plugin\Command.cs'), "// changed`r`n") } $true
    Assert-Freshness 'src に新しいファイル（git に追加していない）を置くと止める' { [IO.File]::WriteAllText((Join-Path $repo 'src\Plugin\Added.cs'), "class Added {}`r`n") } $true
    Assert-Freshness 'src のファイルを削除すると止める' { Remove-Item -LiteralPath (Join-Path $repo 'src\Plugin\Command.cs') } $true
    Assert-Freshness 'ビルドの定義（dist/dist_build.ps1）の変更で止める' { [IO.File]::AppendAllText((Join-Path $repo 'dist\dist_build.ps1'), "# changed`r`n") } $true
    Assert-Freshness '変更をコミットした後も、記録の後に変えたので止める' {
        [IO.File]::AppendAllText((Join-Path $repo 'src\Plugin\Command.cs'), "// committed`r`n")
        Invoke-FixtureGit @('-c', 'user.name=test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-q', '-am', 'change')
    } $true
    Assert-Freshness 'ビルドの入力の外（docs）の変更では止めない' { [IO.File]::AppendAllText((Join-Path $repo 'docs\manual.md'), "more`r`n") } $false
    Assert-Freshness 'git が無視するファイル（bin の出力）の変更では止めない' { [IO.File]::WriteAllText((Join-Path $outRoot 'Plugin.dll'), 'built') } $false

    # 値を求めても、利用者の索引と、このプロセスの GIT_INDEX_FILE は変わらない
    New-TestRepo
    [IO.File]::AppendAllText((Join-Path $repo 'src\Plugin\Command.cs'), "// not staged`r`n")
    $indexPath = Join-Path $repo '.git\index'
    $indexBefore = (Get-FileSha256Shared $indexPath)
    $envBefore = $env:GIT_INDEX_FILE
    [void](Get-E2eBuildFingerprint $repo)
    if ((Get-FileSha256Shared $indexPath) -ne $indexBefore) { throw '値を求めたら、リポジトリの索引が変わった' }
    if ($env:GIT_INDEX_FILE -ne $envBefore) { throw "値を求めた後、GIT_INDEX_FILE が戻っていない: $env:GIT_INDEX_FILE" }
    $status = @(& git -C $repo status --porcelain)
    if (($status -join '|') -ne ' M src/Plugin/Command.cs') { throw "値を求めたら、git status が変わった: $($status -join '|')" }
    $checked.Add('値を求めても、利用者の索引と GIT_INDEX_FILE を変えない')

    "E2eBuildRecord自己試験 OK（確かめた約束 $($checked.Count) 件、PowerShell $($PSVersionTable.PSEdition)）"
    $checked | ForEach-Object { "  - $_" }
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
