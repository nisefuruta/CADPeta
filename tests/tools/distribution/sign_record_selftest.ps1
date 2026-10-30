# dist/sign_record.ps1 の約束を、一時フォルダに作った staging で確かめる。証明書と秘密鍵は使わない(署名はバイト列の追加で模擬する)。
# build_dist が書いた記録と staging の自作のファイルを照合し、作ったときの内容とも署名後に記録した内容とも違うファイルがあれば失敗を返す。
# 使い方: pwsh -File tests/tools/distribution/sign_record_selftest.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\..\dist\sign_record.ps1')

$work = Join-Path ([IO.Path]::GetTempPath()) ("cadpeta_sign_record_" + [guid]::NewGuid().ToString('N'))
$staging = Join-Path $work 'staging'
$checked = New-Object System.Collections.Generic.List[string]

function Write-Bytes([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($text))
}
# 署名の模擬: 署名は自作のファイルの末尾に署名のデータを足して内容を変える
function Add-FakeSignature([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path) + [Text.Encoding]::UTF8.GetBytes('#signature')
    [IO.File]::WriteAllBytes($path, $bytes)
}
# 照合が失敗を返し、その中に $expectedPath を含むことを確かめる
function Assert-Fails([string[]]$groups, [string]$expectedPath, [string]$label) {
    $failures = @(Test-SignRecord $staging $groups)
    if ($failures.Count -eq 0) { throw "${label}: 照合が失敗を返さなかった" }
    if (-not ($failures | Where-Object { $_ -like "*$expectedPath*" })) { throw "${label}: 失敗に $expectedPath が無い: $($failures -join ' / ')" }
    $checked.Add($label)
}
function Assert-Passes([string[]]$groups, [string]$label) {
    $failures = @(Test-SignRecord $staging $groups)
    if ($failures.Count -ne 0) { throw "${label}: 照合が失敗を返した: $($failures -join ' / ')" }
    $checked.Add($label)
}

try {
    $all = @('bundle', 'cli', 'gui', 'compat')
    $core = Join-Path $staging 'bundle\CADPeta.bundle\Contents\net48\CadPeta.Core.dll'
    $plugin = Join-Path $staging 'bundle\CADPeta.bundle\Contents\net8\CadPeta.Plugin.Acad.dll'
    $thirdParty = Join-Path $staging 'bundle\CADPeta.bundle\Contents\net8\UglyToad.PdfPig.dll'
    $cli = Join-Path $staging 'cli\pkg\cadpeta.exe'
    $gui = Join-Path $staging 'gui\pkg\cadpeta-gui.exe'
    $aresPlugin = Join-Path $staging 'compat\ares\2024\CadPeta.Plugin.Ares.dll'
    $register = Join-Path $staging 'compat\register.ps1'
    Write-Bytes $core 'core'
    Write-Bytes $plugin 'plugin'
    Write-Bytes $thirdParty 'third-party'
    Write-Bytes $cli 'cli'
    Write-Bytes $gui 'gui'
    Write-Bytes $aresPlugin 'ares'
    Write-Bytes $register 'register'

    # 署名する対象は自作のファイル(CadPeta.*.dll、cadpeta*.exe、compat の .ps1)だけで、第三者の DLL は含まない
    $targets = @(Get-SignTargets $staging $all | ForEach-Object { $_.Path })
    foreach ($expected in $core, $plugin, $cli, $gui, $aresPlugin, $register) {
        if ($targets -notcontains $expected) { throw "署名する対象に $expected が無い: $($targets -join ', ')" }
    }
    if ($targets -contains $thirdParty) { throw "第三者の DLL を署名する対象に含めた: $thirdParty" }
    $checked.Add('署名する対象は自作のファイルだけ')

    # build_dist が記録を書く前は、署名を始めない
    Assert-Fails $all (Get-SignRecordPath $staging) '記録が無ければ失敗する'

    Write-SignRecord $staging $all
    # 記録は zip にするフォルダ(bundle、cli\pkg、gui\pkg、compat)の外に置く
    if ((Split-Path -Parent (Get-SignRecordPath $staging)) -ne $staging) { throw "記録が staging の直下に無い: $(Get-SignRecordPath $staging)" }
    $checked.Add('記録は zip にするフォルダの外')
    Assert-Passes $all '作ったときのままなら通る'

    # staging の DLL を別の DLL に置き換えると止まる
    $original = [IO.File]::ReadAllBytes($plugin)
    Write-Bytes $plugin 'another plugin'
    Assert-Fails $all $plugin '別の DLL に置き換えると失敗する'
    [IO.File]::WriteAllBytes($plugin, $original)

    # 署名したのに署名後の値を記録していない DLL は、置き換えたものと区別できないので止まる
    Add-FakeSignature $core
    Assert-Fails $all $core '署名後の値の記録が無い DLL は失敗する'

    # 署名後の値を記録した DLL は、途中から再開しても通る(ほかの未署名の DLL は作ったときの値と照合する)
    Add-SignRecordSigned $staging @($core)
    Assert-Passes $all '署名済みの DLL で再開すると通る'

    # 署名後の値を記録した DLL を、さらに別の内容に置き換えると止まる
    Write-Bytes $core 'replaced after signing'
    Assert-Fails $all $core '署名済みの DLL を置き換えると失敗する'
    Write-SignRecord $staging $all

    # 記録に無い自作のファイルが増えたとき、記録にあるファイルが無くなったときも止まる
    $extra = Join-Path $staging 'bundle\CADPeta.bundle\Contents\net10\CadPeta.Extra.dll'
    Write-Bytes $extra 'extra'
    Assert-Fails $all $extra '記録に無いファイルは失敗する'
    Remove-Item -LiteralPath $extra
    Remove-Item -LiteralPath $gui
    Assert-Fails $all $gui '記録にあるファイルが無ければ失敗する'

    # -Only で選んだ配布物だけを照合する(署名しない配布物のファイルが無くても止まらない)
    Assert-Passes @('bundle', 'compat') '選んだ配布物だけを照合する'

    # 記録に無いファイルへ署名後の値を追記しようとしたら止まる(記録の外のファイルを署名済みとして扱わない)
    $added = $false
    try { Add-SignRecordSigned $staging @((Join-Path $staging 'bundle\CadPeta.Unknown.dll')); $added = $true } catch { }
    if ($added) { throw '記録に無いファイルの署名後の値を追記した' }
    $checked.Add('記録に無いファイルへ追記しない')

    # 自作のファイルが 1 つも無ければ記録を書かずに止まる
    $empty = Join-Path $work 'empty'
    New-Item -ItemType Directory -Force (Join-Path $empty 'bundle') | Out-Null
    $written = $false
    try { Write-SignRecord $empty @('bundle'); $written = $true } catch { }
    if ($written) { throw '自作のファイルが無いのに記録を書いた' }
    $checked.Add('自作のファイルが無ければ記録を書かない')

    Write-Host "OK: 署名の記録の照合 $($checked.Count) 件($($checked -join '、'))" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
