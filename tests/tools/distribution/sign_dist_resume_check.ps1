# sign_dist.ps1 の署名の再開の約束を、証明書と秘密鍵を使わずに、sign_dist.ps1 を外から実行したときの振る舞いで確かめる。
# 偽の signtool.exe(ファイルの末尾に署名の印を足す)、偽の Get-AuthenticodeSignature と Get-Item(証明書のストア)、
# 試験用の staging・記録・ファイルのロックで、止まる場面を 1 つずつ作る。止まった後に、staging の署名するファイルが
# 「作ったときの値」か「記録した署名後の値」のどちらかと一致すること(Test-SignRecord)と、再実行で署名済みのファイルを
# 署名し直さずに完了することを確かめる。作業は dist\artifacts\sign-resume-check-<実行ごとの名前> で行い、最後に削除する。
# -DistSource に直す前の dist のスクリプトのフォルダを渡すと、その版を試験する(直す前のコードで不合格になることを確かめるため)。
# 使い方: pwsh -File tests/tools/distribution/sign_dist_resume_check.ps1 [-DistSource <dist のスクリプトのフォルダ>]
param([string]$DistSource = "")
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $DistSource) { $DistSource = Join-Path $repo 'dist' }
$DistSource = (Resolve-Path $DistSource).Path
$work = Join-Path $repo ("dist\artifacts\sign-resume-check-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
# 試験の署名の拇印(実際の証明書とは別の値)
$thumb = 'AB' * 20
$marker = "`n#FAKESIG:$thumb`n"
$failures = New-Object System.Collections.Generic.List[string]
$checked = New-Object System.Collections.Generic.List[string]
function Assert-Check([bool]$ok, [string]$message) { if ($ok) { $checked.Add($message) } else { $failures.Add($message) } }

# ---- 偽の署名の道具 ----
# 署名の検証の結果(Status)と、検証の直後に対象のファイルを別の内容へ置き換える仕掛け(AfterVerify)
# 証明書の拡張(既定はコード署名の拡張キー使用法を持つ。拡張キー使用法の無い証明書の場面では空にする)
$codeSigningEku = @([pscustomobject]@{ Oid = [pscustomobject]@{ Value = '2.5.29.37' }; EnhancedKeyUsages = @([pscustomobject]@{ Value = '1.3.6.1.5.5.7.3.3' }) })
$global:SignResumeState = [pscustomobject]@{ Status = 'Valid'; AfterVerify = $null; Extensions = $codeSigningEku }
function global:Get-AuthenticodeSignature {
    [CmdletBinding()]
    param([string]$FilePath, [string]$LiteralPath)
    $target = if ($LiteralPath) { $LiteralPath } else { $FilePath }
    $text = [IO.File]::ReadAllText($target)
    $match = [regex]::Match($text, '\n#FAKESIG:([0-9A-F]{40})\n$')
    $result = [pscustomobject]@{
        Status = if ($match.Success) { $global:SignResumeState.Status } else { 'NotSigned' }
        SignerCertificate = if ($match.Success) { [pscustomobject]@{ Thumbprint = $match.Groups[1].Value } } else { $null }
        TimeStamperCertificate = $null
    }
    $hook = $global:SignResumeState.AfterVerify
    if ($hook -and [IO.Path]::GetFullPath($target) -eq [IO.Path]::GetFullPath($hook.Path)) {
        $global:SignResumeState.AfterVerify = $null
        [IO.File]::WriteAllText($hook.Path, $hook.Text)
    }
    return $result
}
function global:Get-Item {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath)
    $target = if ($LiteralPath) { $LiteralPath } else { $Path }
    if ($target -like 'Cert:*') {
        $requested = Split-Path -Leaf $target
        return [pscustomobject]@{ Subject = 'CN=試験の証明書'; Issuer = 'CN=試験の証明書'; NotAfter = (Get-Date).AddDays(30); HasPrivateKey = $true; Thumbprint = $requested; Extensions = @($global:SignResumeState.Extensions) }
    }
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}

# 偽の signtool.exe。sign の後の /sha1 の拇印を読み、オプションの後のファイルごとに末尾へ署名の印を足して、署名したパスを記録に書く。
# 環境変数 CADPETA_FAKE_SIGNTOOL_FAIL のファイル名(拡張子を含む)のファイルは署名せず、記録にも書かずに、最後に終了コード 1 を返す。
# 本物の signtool は失敗したところで止まるが、偽の signtool は残りのファイルも署名する(失敗のファイルより後に署名できたファイルを作る)。
function New-FakeSignTool([string]$Root) {
    $exe = Join-Path $Root 'Windows Kits\10\bin\10.0.99999.0\x64\signtool.exe'
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exe))
    $source = @'
using System; using System.IO; using System.Collections.Generic;
public static class FakeSignTool {
    public static int Main(string[] args) {
        string thumb = null; var files = new List<string>();
        var withValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/sha1", "/fd", "/tr", "/td" };
        for (int i = 1; i < args.Length; i++) {
            if (withValue.Contains(args[i])) { if (args[i] == "/sha1") thumb = args[i + 1]; i++; continue; }
            if (args[i].StartsWith("/")) continue;
            files.Add(args[i]);
        }
        var log = Environment.GetEnvironmentVariable("CADPETA_FAKE_SIGNTOOL_LOG");
        var fail = Environment.GetEnvironmentVariable("CADPETA_FAKE_SIGNTOOL_FAIL");
        bool failed = false;
        try {
            foreach (var f in files) {
                if (!string.IsNullOrEmpty(fail) && string.Equals(Path.GetFileName(f), fail, StringComparison.OrdinalIgnoreCase)) { failed = true; continue; }
                using (var s = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.None)) {
                    var b = System.Text.Encoding.UTF8.GetBytes("\n#FAKESIG:" + thumb + "\n"); s.Write(b, 0, b.Length);
                }
                if (log != null) File.AppendAllText(log, f + Environment.NewLine);
            }
        } catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        return failed ? 1 : 0;
    }
}
'@
    $script = "Add-Type -TypeDefinition `$env:CP_FAKE_SOURCE -OutputAssembly `$env:CP_FAKE_EXE -OutputType ConsoleApplication"
    $env:CP_FAKE_SOURCE = $source; $env:CP_FAKE_EXE = $exe
    try { & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command $script }
    finally { Remove-Item Env:CP_FAKE_SOURCE, Env:CP_FAKE_EXE }
    if (-not (Test-Path -LiteralPath $exe)) { throw "偽の signtool.exe を作れなかった: $exe" }
}

# ---- 試験用の staging ----
function Write-Text([string]$Path, [string]$Text) {
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path))
    [IO.File]::WriteAllText($Path, $Text)
}

# dist のスクリプトと、4 つの配布物の staging(同じ内容の Core.dll が 3 つ、系列で違う Plugin.Acad.dll、exe、.ps1)と記録を作る。
function New-SigningFixture([string]$Name) {
    $fixture = Join-Path $work $Name
    $dist = Join-Path $fixture 'dist'
    foreach ($file in @(Get-ChildItem -LiteralPath $DistSource -File -Filter '*.ps1')) { Write-Text (Join-Path $dist $file.Name) ([IO.File]::ReadAllText($file.FullName)) }
    $staging = Join-Path $dist 'staging'
    $contents = Join-Path $staging 'bundle\CADPeta.bundle\Contents'
    foreach ($tfm in 'net48', 'net8', 'net10') {
        Write-Text (Join-Path $contents "$tfm\CadPeta.Core.dll") 'core'
        Write-Text (Join-Path $contents "$tfm\UglyToad.PdfPig.dll") 'third-party'
    }
    Write-Text (Join-Path $contents 'net48\CadPeta.Plugin.Acad.dll') 'plugin net48'
    Write-Text (Join-Path $contents 'net8\CadPeta.Plugin.Acad.dll') 'plugin net8'
    Write-Text (Join-Path $staging 'gui\pkg\cadpeta-gui.exe') 'gui'
    Write-Text (Join-Path $staging 'compat\ares\2024\CadPeta.Plugin.Ares.dll') 'ares'
    Write-Text (Join-Path $staging 'compat\register.ps1') '# register'
    # cli は zip を作り直せる形にする(同梱文書の場面で -NoReZip なしに実行するため)
    $cli = Join-Path $staging 'cli\pkg'
    Write-Text (Join-Path $cli 'cadpeta.exe') 'cli'
    Write-Text (Join-Path $cli 'lang\1041.lang') 'ja'
    Write-Text (Join-Path $cli 'lang\1033.lang') 'en'
    foreach ($doc in @(@('dist\cli\README-cli.txt', 'readme'), @('LICENSE', 'license'), @('THIRD-PARTY-NOTICES.txt', 'notices'),
            @('dist\common\test.cer', 'cer'), @('dist\common\README-signing.txt', 'signing'))) {
        Write-Text (Join-Path $fixture $doc[0]) $doc[1]
        Write-Text (Join-Path $cli (Split-Path -Leaf $doc[0])) $doc[1]
    }
    Write-Text (Join-Path $fixture 'tests\tools\distribution\package_verify.ps1') ([IO.File]::ReadAllText((Join-Path $repo 'tests\tools\distribution\package_verify.ps1')))
    # package_verify.ps1 は SHA256 を試験の補助(test_helpers.ps1)で求める
    Write-Text (Join-Path $fixture 'tests\tools\common\test_helpers.ps1') ([IO.File]::ReadAllText((Join-Path $repo 'tests\tools\common\test_helpers.ps1')))
    Write-Text (Join-Path $dist 'artifacts\manifest.txt') "HEAD: 0000000`r`nConfig: Release`r`nVersion: 1.0.0`r`n"
    # 記録は試験する版の Write-SignRecord で作る(関数の名前が版で同じなので、試験のプロセスに読み込まず、子プロセスで呼ぶ)
    [void](Invoke-RecordScript $fixture "Write-SignRecord `$env:CP_STAGING @('bundle', 'cli', 'gui', 'compat')")
    return $fixture
}

function Invoke-RecordScript([string]$Fixture, [string]$Command) {
    $env:CP_SIGN_RECORD = Join-Path $Fixture 'dist\sign_record.ps1'
    $env:CP_STAGING = Join-Path $Fixture 'dist\staging'
    try { $output = & pwsh -NoProfile -NonInteractive -Command "`$ErrorActionPreference = 'Stop'; . `$env:CP_SIGN_RECORD; $Command" 2>&1 | Out-String }
    finally { Remove-Item Env:CP_SIGN_RECORD, Env:CP_STAGING }
    if ($LASTEXITCODE -ne 0) { throw "記録の処理に失敗した: $output" }
    return $output.Trim()
}

# staging の署名するファイルが、作ったときの値か記録した署名後の値のどちらかと一致するか(Test-SignRecord の失敗の数が 0 か)
function Test-Consistent([string]$Fixture) {
    return ((Invoke-RecordScript $Fixture "@(Test-SignRecord `$env:CP_STAGING @('bundle', 'cli', 'gui', 'compat')).Count") -eq '0')
}

# 記録の組(拡張子と作ったときの値)のうち、どのファイルも作ったときの内容のままの組の数(再実行で署名する数)
function Get-UnsignedGroupCount([string]$Fixture, [string[]]$Groups) {
    $staging = Join-Path $Fixture 'dist\staging'
    $entries = @((Get-Content -LiteralPath (Join-Path $staging 'sign-record.json') -Raw | ConvertFrom-Json).files)
    $sets = $entries | Where-Object { $Groups -contains $_.group } | Group-Object { "$([IO.Path]::GetExtension($_.path))|$($_.built)" }
    return @($sets | Where-Object {
            @($_.Group | Where-Object { (Get-FileHash -LiteralPath (Join-Path $staging $_.path) -Algorithm SHA256).Hash -ne $_.built }).Count -eq 0
        }).Count
}

function Test-AllSigned([string]$Fixture, [string[]]$Groups = @('bundle', 'cli', 'gui', 'compat')) {
    $staging = Join-Path $Fixture 'dist\staging'
    $entries = @((Get-Content -LiteralPath (Join-Path $staging 'sign-record.json') -Raw | ConvertFrom-Json).files)
    return @($entries | Where-Object { $Groups -contains $_.group -and -not [IO.File]::ReadAllText((Join-Path $staging $_.path)).EndsWith($marker) }).Count -eq 0
}

# staging の一覧(ファイルとフォルダの名前と SHA256。ロックのファイルは除く)の文字列。比べる処理は test_helpers.ps1 の 1 系統を使う
. (Join-Path $repo 'tests\tools\common\test_helpers.ps1')
function Get-StagingListingText([string]$Fixture) {
    $listing = Get-FolderListing (Join-Path $Fixture 'dist\staging')
    $listing.Items.Remove('sign.lock')
    return Format-FolderListing $listing
}
$signLog = Join-Path $work 'signtool.log'
function Get-SignCount { if (Test-Path -LiteralPath $signLog) { @([IO.File]::ReadAllLines($signLog)).Count } else { 0 } }

# sign_dist.ps1 を確認なしで実行する。止まったら $false と理由を返す。
function Invoke-SignDist([string]$Fixture, [hashtable]$Arguments, [string]$Thumbprint = $thumb) {
    $before = Get-SignCount
    $ok = $true
    $text = ''
    try { $text = & (Join-Path $Fixture 'dist\sign_dist.ps1') -Thumbprint $Thumbprint -NoTimestamp -Confirm:$false @Arguments *>&1 | Out-String }
    catch { $ok = $false; $text = $_.Exception.Message }
    return [pscustomobject]@{ Ok = $ok; Text = $text; Signed = (Get-SignCount) - $before }
}

# $Path を読み取りの共有だけを許して開いたまま $Action を実行する(置き換え・書き込みを失敗させる)
function Invoke-WhileHeld([string]$Path, [IO.FileShare]$Share, [scriptblock]$Action) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Read, $Share)
    try { & $Action } finally { $stream.Dispose() }
}

# 止まった後の約束と、再実行で完了することを確かめる。再実行で署名する数は、既定では止まった後の staging の未署名の組の数と比べる。
# $ExpectedResign を渡すと、その数と比べる(止まる前に署名と検証に成功したファイルを、staging に反映せずに失っていないかを確かめる)
function Assert-StopAndResume([string]$Label, [string]$Fixture, $Stopped, [hashtable]$Arguments, [int]$ExpectedResign = -1) {
    Assert-Check (-not $Stopped.Ok) "${Label}: 止まる"
    Assert-Check (Test-Consistent $Fixture) "${Label}: 止まった後、staging のファイルは作ったときの値か記録した署名後の値と一致する"
    $runGroups = if ($Arguments.Only -eq 'all') { @('bundle', 'cli', 'gui', 'compat') } else { @($Arguments.Only) }
    $expected = if ($ExpectedResign -ge 0) { $ExpectedResign } else { Get-UnsignedGroupCount $Fixture $runGroups }
    $resumed = Invoke-SignDist $Fixture $Arguments
    Assert-Check $resumed.Ok "${Label}: 再実行で完了する$(if (-not $resumed.Ok) { "($($resumed.Text))" })"
    Assert-Check ($resumed.Signed -eq $expected) "${Label}: 再実行は署名済みのファイルを署名し直さない(署名 $($resumed.Signed) 件、未署名の組 $expected 件)"
    Assert-Check (Test-AllSigned $Fixture $runGroups) "${Label}: 再実行の後、すべてのファイルが署名されている"
}

$previousProgramFiles = ${env:ProgramFiles(x86)}
try {
    [void](New-Item -ItemType Directory -Force -Path $work)
    $toolRoot = Join-Path $work 'pf86'
    New-FakeSignTool $toolRoot
    ${env:ProgramFiles(x86)} = $toolRoot
    $env:CADPETA_FAKE_SIGNTOOL_LOG = $signLog
    $all = @{ Only = 'all'; NoReZip = $true }

    # 0. 止まらない実行: 同じ内容の Core.dll は 1 回だけ署名し、再実行は何も署名しない
    $f = New-SigningFixture 'baseline'
    # 確認の画面の計画は、秘密鍵で署名する件数(代表の数)と複製する件数を示し、署名する件数は実際の署名の件数と同じ
    $plan = Invoke-SignDist $f ($all + @{ WhatIf = $true })
    $planSign = if ($plan.Text -match '秘密鍵で署名 (\d+) 件') { [int]$Matches[1] } else { -1 }
    $planCopy = if ($plan.Text -match '複製 (\d+) 件') { [int]$Matches[1] } else { -1 }
    $r = Invoke-SignDist $f $all
    Assert-Check ($plan.Ok -and $plan.Signed -eq 0 -and $planSign -eq $r.Signed -and $planCopy -eq 2) "計画の件数: 署名 $planSign 件(実際 $($r.Signed) 件)、複製 $planCopy 件(同じ内容の Core.dll 3 か所のうち 2 件)"
    Assert-Check ($r.Ok -and (Test-AllSigned $f)) "止まらない実行: すべて署名する$(if (-not $r.Ok) { "($($r.Text))" })"
    Assert-Check ($r.Signed -eq 7) "止まらない実行: 同じ内容のファイルは代表だけを署名する(署名 $($r.Signed) 件、組 7 件)"
    Assert-Check ((Invoke-SignDist $f $all).Signed -eq 0) "止まらない実行: 再実行は何も署名しない"

    # 1. 代表の署名の後の複製の失敗(複製先を読み取りの共有だけで開いておく)
    $f = New-SigningFixture 'copy-failure'
    $dup = Join-Path $f 'dist\staging\bundle\CadPeta.bundle\Contents\net8\CadPeta.Core.dll'
    $held = $null; Invoke-WhileHeld $dup ([IO.FileShare]::Read) { $script:held = Invoke-SignDist $f $all }
    Assert-StopAndResume '複製の失敗' $f $held $all

    # 2. 署名の後の検証の失敗(内容が署名と違う)
    $f = New-SigningFixture 'verify-failure'
    $global:SignResumeState.Status = 'HashMismatch'
    try { $r = Invoke-SignDist $f $all } finally { $global:SignResumeState.Status = 'Valid' }
    Assert-StopAndResume '署名の後の検証の失敗' $f $r $all

    # 2b. 一部のファイルの署名が、再試行の上限まで失敗する(CadPeta.Plugin.Ares.dll だけを失敗させる。組は拡張子と作ったときの値の順に
    #     並ぶので、.dll の組の後に 2 つの .exe の組があり、失敗の後に署名に成功する組ができる)。署名と検証に成功したファイルは記録して
    #     staging に反映し、再実行では失敗した組だけを署名する。期待値は、偽の signtool が成功を記録した数から決める
    #     (止まった後の staging から決めると、成功を失っても同じだけ増えて合格する)
    $f = New-SigningFixture 'partial-sign-failure'
    $env:CADPETA_FAKE_SIGNTOOL_FAIL = 'CadPeta.Plugin.Ares.dll'
    try { $r = Invoke-SignDist $f ($all + @{ RetryCount = 1 }) } finally { $env:CADPETA_FAKE_SIGNTOOL_FAIL = '' }
    Assert-Check ($r.Signed -eq 6) "一部の署名の失敗: 失敗させた 1 組のほかは署名に成功する(署名 $($r.Signed) 件、組 7 件)"
    Assert-StopAndResume '一部の署名の失敗' $f $r $all -ExpectedResign (7 - $r.Signed)

    # 3. 置き換えの失敗(署名する exe を読み取りの共有だけで開いておく)
    $f = New-SigningFixture 'replace-failure'
    $exe = Join-Path $f 'dist\staging\gui\pkg\cadpeta-gui.exe'
    Invoke-WhileHeld $exe ([IO.FileShare]::Read) { $script:held = Invoke-SignDist $f $all }
    Assert-StopAndResume '置き換えの失敗' $f $held $all

    # 4. 説明書の書き込みの失敗(-Only cli で zip を作り直す。staging の説明書を複製元と違う内容にして、読み取りの共有だけで開いておく)
    $f = New-SigningFixture 'notice-failure'
    $readme = Join-Path $f 'dist\staging\cli\pkg\README-cli.txt'
    [IO.File]::WriteAllText($readme, 'old readme')
    Invoke-WhileHeld $readme ([IO.FileShare]::Read) { $script:held = Invoke-SignDist $f @{ Only = 'cli' } }
    $readmeText = [IO.File]::ReadAllText($readme)
    Assert-Check ($readmeText -in @('old readme', 'readme')) "説明書の書き込みの失敗: 説明書は前か後の内容のどちらか(実際: $readmeText)"
    Assert-StopAndResume '説明書の書き込みの失敗' $f $held @{ Only = 'cli' }
    Assert-Check ([IO.File]::ReadAllText($readme) -eq 'readme') '説明書の書き込みの失敗: 再実行で説明書を差し替える'

    # 5. 記録の書き込みの失敗(記録を読み取りの共有だけで開いておく)
    $f = New-SigningFixture 'record-failure'
    $recordPath = Join-Path $f 'dist\staging\sign-record.json'
    Invoke-WhileHeld $recordPath ([IO.FileShare]::Read) { $script:held = Invoke-SignDist $f $all }
    Assert-StopAndResume '記録の書き込みの失敗' $f $held $all

    # 6. 照合の後に、署名済みの代表を、同じ証明書で署名した別の版へ置き換える(複製の前に止まった続きの再開で)
    $f = New-SigningFixture 'swapped-representative'
    $core = @('net10', 'net48', 'net8') | ForEach-Object { Join-Path $f "dist\staging\bundle\CadPeta.bundle\Contents\$_\CadPeta.Core.dll" }
    # 複製先を 1 つ開いておき、代表の署名と、もう 1 つの複製が済んだところで止める
    Invoke-WhileHeld $core[2] ([IO.FileShare]::Read) { [void](Invoke-SignDist $f $all) }
    $builtCore = (Get-FileHash -LiteralPath $core[2] -Algorithm SHA256).Hash
    $signedRep = @($core | Where-Object { [IO.File]::ReadAllText($_).EndsWith($marker) })[0]
    $global:SignResumeState.AfterVerify = [pscustomobject]@{ Path = $signedRep; Text = "another version$marker" }
    try { $r = Invoke-SignDist $f $all } finally { $global:SignResumeState.AfterVerify = $null }
    Assert-Check (-not $r.Ok) '照合の後の代表の置き換え: 止まる'
    Assert-Check ((Get-FileHash -LiteralPath $core[2] -Algorithm SHA256).Hash -eq $builtCore) '照合の後の代表の置き換え: 置き換えた版を複製しない(複製先は作ったときの内容のまま)'

    # 7. ロックのファイルを別のプロセスが開いている
    $f = New-SigningFixture 'locked'
    $before = Get-StagingListingText $f
    Invoke-WhileHeld (Join-Path $f 'dist\staging\sign.lock') ([IO.FileShare]::None) { $script:held = Invoke-SignDist $f $all }
    $after = Get-StagingListingText $f
    Assert-Check (-not $held.Ok -and $held.Signed -eq 0 -and $before -eq $after) 'ロックのファイルを別のプロセスが開いている: 何も変えずに止まる'

    # 8. 記録の無い署名済みのファイル(署名の検証は通る)を受け入れない
    $f = New-SigningFixture 'unrecorded-signed'
    $ares = Join-Path $f 'dist\staging\compat\ares\2024\CadPeta.Plugin.Ares.dll'
    [IO.File]::WriteAllText($ares, "other ares$marker")
    $r = Invoke-SignDist $f $all
    Assert-Check (-not $r.Ok -and $r.Signed -eq 0) '記録の無い署名済みのファイル: 何も署名せずに止まる'

    # 9. 署名済みと記録したファイルの署名を検証できない
    # (すべて署名して記録した後に、署名の検証が内容の不一致を返す)
    $f = New-SigningFixture 'recorded-unverifiable'
    [void](Invoke-SignDist $f $all)
    $global:SignResumeState.Status = 'HashMismatch'
    try { $r = Invoke-SignDist $f $all } finally { $global:SignResumeState.Status = 'Valid' }
    Assert-Check (-not $r.Ok) '署名済みと記録したファイルの署名を検証できない: 止まる'

    # 12. 署名の検証の後に、staging の署名済みのファイルを同じ署名者の別の版へ置き換えても、その版を ZIP に入れて成功にしない
    #     (ZIP を作る直前の staging と、作った ZIP の中の署名したファイルを、記録した署名済みの値と照合する。ZIP は成功の名前で残さない)
    $f = New-SigningFixture 'swapped-before-zip'
    $exe = Join-Path $f 'dist\staging\cli\pkg\cadpeta.exe'
    $zip = Join-Path $f 'dist\artifacts\CADPeta-CLI-1.0.0.zip'
    $global:SignResumeState.AfterVerify = [pscustomobject]@{ Path = $exe; Text = "another cli$marker" }
    try { $r = Invoke-SignDist $f @{ Only = 'cli' } } finally { $global:SignResumeState.AfterVerify = $null }
    $leftZips = @(Get-ChildItem -LiteralPath (Split-Path -Parent $zip) -Filter '*.zip' | ForEach-Object { $_.Name })
    Assert-Check (-not $r.Ok -and $leftZips.Count -eq 0) "検証の後の置き換え: 止まり、ZIP を残さない(ZIP: $($leftZips -join ', '))"

    # 13. 版: -Version を渡さなければ、build_dist が manifest.txt に書いた版の zip を作る。manifest.txt と違う版を渡したら、
    #     何も署名せずに止まる(署名の後で止まると、署名し直しになる)
    $f = New-SigningFixture 'version-from-manifest'
    $manifestFile = Join-Path $f 'dist\artifacts\manifest.txt'
    [IO.File]::WriteAllText($manifestFile, ([IO.File]::ReadAllText($manifestFile) -replace 'Version: 1\.0\.0', 'Version: 9.9.9'))
    $r = Invoke-SignDist $f @{ Only = 'cli'; Version = '1.0.0' }
    Assert-Check (-not $r.Ok -and $r.Signed -eq 0) "manifest.txt と違う版: 何も署名せずに止まる(署名 $($r.Signed) 件)"
    $r = Invoke-SignDist $f @{ Only = 'cli' }
    Assert-Check ($r.Ok -and (Test-Path -LiteralPath (Join-Path $f 'dist\artifacts\CADPeta-CLI-9.9.9.zip'))) "版を渡さない: manifest.txt の版(9.9.9)の zip を作る$(if (-not $r.Ok) { "($($r.Text))" })"

    # 10. 拡張キー使用法の無い証明書: 既定の拇印のときだけ使い、別の拇印なら署名せずに止まる
    $defaultThumbprint = 'A4850D199DDC8851E3795554C9616B499FC89E36'
    $global:SignResumeState.Extensions = @()
    try {
        $f = New-SigningFixture 'no-eku-default'
        $r = Invoke-SignDist $f $all $defaultThumbprint
        Assert-Check ($r.Ok -and $r.Signed -eq 7) "拡張キー使用法の無い証明書: 既定の拇印なら署名する$(if (-not $r.Ok) { "($($r.Text))" })"
        $f = New-SigningFixture 'no-eku-other'
        $r = Invoke-SignDist $f $all
        Assert-Check (-not $r.Ok -and $r.Signed -eq 0 -and (Get-UnsignedGroupCount $f @('bundle', 'cli', 'gui', 'compat')) -eq 7) '拡張キー使用法の無い証明書: 別の拇印なら署名せずに止まる'
    }
    finally { $global:SignResumeState.Extensions = $codeSigningEku }
    # 11. 拡張キー使用法にコード署名が無い証明書は、拇印にかかわらず止まる
    $global:SignResumeState.Extensions = @([pscustomobject]@{ Oid = [pscustomobject]@{ Value = '2.5.29.37' }; EnhancedKeyUsages = @([pscustomobject]@{ Value = '1.3.6.1.5.5.7.3.1' }) })
    try {
        $f = New-SigningFixture 'eku-without-code-signing'
        $r = Invoke-SignDist $f $all $defaultThumbprint
        Assert-Check (-not $r.Ok -and $r.Signed -eq 0) '拡張キー使用法にコード署名が無い証明書: 署名せずに止まる'
    }
    finally { $global:SignResumeState.Extensions = $codeSigningEku }
}
finally {
    ${env:ProgramFiles(x86)} = $previousProgramFiles
    Remove-Item Env:CADPETA_FAKE_SIGNTOOL_LOG -ErrorAction SilentlyContinue
    Remove-Item Function:\global:Get-AuthenticodeSignature, Function:\global:Get-Item -ErrorAction SilentlyContinue
    Remove-Variable -Name SignResumeState -Scope Global -ErrorAction SilentlyContinue
    # 自分が作った作業フォルダだけを、名前を指定して削除する
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}

foreach ($c in $checked) { Write-Host "  OK  $c" }
foreach ($c in $failures) { Write-Host "  NG  $c" -ForegroundColor Red }
if ($failures.Count -gt 0) { throw "署名の再開の約束の不合格 $($failures.Count) 件(合格 $($checked.Count) 件)" }
"sign_dist_resume_check OK(確かめた約束 $($checked.Count) 件)"
