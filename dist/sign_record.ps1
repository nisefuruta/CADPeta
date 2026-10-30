# sign_record.ps1: 署名する自作のファイルの定義と、その内容の記録(staging の sign-record.json)。
# build_dist.ps1 が staging を作ったときに自作のファイルごとの SHA256 を書き、sign_dist.ps1 が署名の前にその値と照合して、
# 署名したファイルの署名後の SHA256 を、staging のファイルを置き換える前に追記する(Invoke-SignStaging)。途中から再開するときは、
# 未署名のファイルは作ったときの値と、署名済みのファイルは追記した値と一致することを求める。staging のファイルを別のファイルに置き換えると、
# どちらとも一致しないので止まる。
# 記録は staging の直下に置き、zip にするフォルダ(bundle、cli\pkg、gui\pkg、compat)には入れない。

. (Join-Path $PSScriptRoot "staging_files.ps1")

$SignRecordFileName = "sign-record.json"

function Get-SignRecordPath([string]$Staging) {
    return Join-Path $Staging $SignRecordFileName
}

# 署名する自作のファイル。PE は CadPeta.*.dll、cadpeta.exe、cadpeta-gui.exe、スクリプトは compat の .ps1。
# 第三者の DLL(PdfPig・System.* など)は署名しない(自作かどうかを署名の有無で判定しない)。
# 返り値は Group(配布物)、Kind(pe か script)、Path(絶対パス)を持つ。
function Get-SignTargets([string]$Staging, [string[]]$Groups) {
    foreach ($group in $Groups) {
        $pe = switch ($group) {
            "bundle" { @{ Dir = "bundle\CADPeta.bundle\Contents"; Filter = "CadPeta.*.dll"; Recurse = $true } }
            "cli"    { @{ Dir = "cli\pkg"; Filter = "cadpeta.exe"; Recurse = $false } }
            "gui"    { @{ Dir = "gui\pkg"; Filter = "cadpeta-gui.exe"; Recurse = $false } }
            "compat" { @{ Dir = "compat"; Filter = "CadPeta.*.dll"; Recurse = $true } }
            default  { throw "未知の配布物: $group" }
        }
        $dir = Join-Path $Staging $pe.Dir
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $dir -File -Filter $pe.Filter -Recurse:$pe.Recurse)) {
            [pscustomobject]@{ Group = $group; Kind = "pe"; Path = $file.FullName }
        }
        if ($group -eq "compat") {
            foreach ($file in @(Get-ChildItem -LiteralPath $dir -File -Filter "*.ps1" -Recurse)) {
                [pscustomobject]@{ Group = $group; Kind = "script"; Path = $file.FullName }
            }
        }
    }
}

function Get-SignRecordHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# 記録は一時ファイルに書いてディスクへ書き出してから置き換える(書き込みの途中で止まっても、前の記録が残る)。
function Save-SignRecord([string]$Staging, [object[]]$Entries) {
    $json = ConvertTo-Json -InputObject @{ files = @($Entries) } -Depth 4
    Write-StagingFileReplacing (Get-SignRecordPath $Staging) $json
}

# 記録を読む。読めなければ例外を投げる(呼び出し元が失敗として扱う)。
function Read-SignRecord([string]$Staging) {
    $path = Get-SignRecordPath $Staging
    $record = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    return @($record.files)
}

# build_dist.ps1 が staging を作った直後に呼ぶ。$Groups の自作のファイルごとに、作ったときの SHA256 を書く。
function Write-SignRecord([string]$Staging, [string[]]$Groups) {
    $entries = @(Get-SignTargets $Staging $Groups | ForEach-Object {
        [ordered]@{
            group = $_.Group
            path = [IO.Path]::GetRelativePath($Staging, $_.Path)
            built = Get-SignRecordHash $_.Path
            signed = $null
        }
    })
    if ($entries.Count -eq 0) { throw "署名する自作のファイルが staging に無い: $Staging" }
    Save-SignRecord $Staging $entries
}

# 記録と staging の $Groups の自作のファイルを照合し、失敗の一覧を返す(空なら照合に合格)。
# 未署名のファイルは作ったときの値と、署名済みのファイルは追記した署名後の値と一致しなければ失敗にする。
# 記録が無い・読めない、記録に無いファイルがある、記録にあるファイルが無い、も失敗にする。
function Test-SignRecord([string]$Staging, [string[]]$Groups) {
    $failures = New-Object System.Collections.Generic.List[string]
    $recordPath = Get-SignRecordPath $Staging
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        $failures.Add("署名の記録が無い: $recordPath")
        return $failures.ToArray()
    }
    try { $entries = Read-SignRecord $Staging }
    catch {
        $failures.Add("署名の記録を読めない: $recordPath ($($_.Exception.Message))")
        return $failures.ToArray()
    }
    $byPath = @{}
    foreach ($entry in $entries) {
        if ($Groups -contains $entry.group) { $byPath[$entry.path.ToLowerInvariant()] = $entry }
    }
    foreach ($target in @(Get-SignTargets $Staging $Groups)) {
        $relative = [IO.Path]::GetRelativePath($Staging, $target.Path)
        $entry = $byPath[$relative.ToLowerInvariant()]
        if (-not $entry) {
            $failures.Add("記録に無いファイル: $($target.Path)")
            continue
        }
        $byPath.Remove($relative.ToLowerInvariant())
        $hash = Get-SignRecordHash $target.Path
        if ($hash -eq $entry.built) { continue }
        if ($entry.signed -and $hash -eq $entry.signed) { continue }
        $state = if ($entry.signed) { "作ったときの値とも署名後に記録した値とも違う" } else { "作ったときの値と違い、署名後の値は記録されていない" }
        $failures.Add("内容が記録と違う($state): $($target.Path)")
    }
    foreach ($missing in $byPath.Values) {
        $failures.Add("記録にあるファイルが無い: $(Join-Path $Staging $missing.path)")
    }
    return $failures.ToArray()
}

# 配布物ごとの zip にするフォルダ(staging からの相対パス)
function Get-SignZipRootRelative([string]$Group) {
    switch ($Group) {
        "bundle" { "bundle" }
        "cli"    { "cli\pkg" }
        "gui"    { "gui\pkg" }
        "compat" { "compat" }
        default  { throw "未知の配布物: $Group" }
    }
}

# 配布物 $Group の署名したファイルの、zip の根からの相対パス(/ 区切り)→ 記録した署名済みの SHA256 の表。
# 署名済みの値が記録に無いファイルがあれば例外にする(zip を作る前に、すべて署名して記録してあること)。
function Get-SignedZipExpectations([string]$Staging, [string]$Group) {
    $root = Get-SignZipRootRelative $Group
    $expected = [ordered]@{}
    foreach ($entry in @(Read-SignRecord $Staging | Where-Object { $_.group -eq $Group })) {
        if (-not $entry.signed) { throw "署名済みの値が記録に無い(署名していない): $($entry.path)" }
        $expected[$entry.path.Substring($root.Length).TrimStart('\').Replace('\', '/')] = $entry.signed
    }
    return $expected
}

# $Root の下の $RelativePaths(/ 区切り)の SHA256 の表。無いファイルは値を $null にする。
function Get-FolderFileHashes([string]$Root, [string[]]$RelativePaths) {
    $hashes = @{}
    foreach ($relative in $RelativePaths) {
        $path = Join-Path $Root $relative.Replace('/', '\')
        $hashes[$relative] = if (Test-Path -LiteralPath $path -PathType Leaf) { Get-SignRecordHash $path } else { $null }
    }
    return $hashes
}

# zip の中の $RelativePaths(/ 区切り)の SHA256 の表を、zip から読んで求める。zip に無いファイルは値を $null にする。
function Get-ZipFileHashes([string]$Zip, [string[]]$RelativePaths) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $hashes = @{}
    foreach ($relative in $RelativePaths) { $hashes[$relative] = $null }
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if (-not $hashes.ContainsKey($name)) { continue }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hashes[$name] = [Convert]::ToHexString($sha.ComputeHash($stream)) }
            finally { $sha.Dispose(); $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return $hashes
}

# 署名したファイルの SHA256($Actual)が、記録した署名済みの値($Expected)とすべて一致することを確かめる。違えば $Label を添えて例外にする。
# zip を作る直前の staging の照合と、作った zip の照合の両方がこの関数を使う。
function Assert-SignedHashes([Collections.IDictionary]$Expected, [Collections.IDictionary]$Actual, [string]$Label) {
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($relative in $Expected.Keys) {
        if ($null -eq $Actual[$relative]) { $problems.Add("無い: $relative") }
        elseif ($Actual[$relative] -ne $Expected[$relative]) { $problems.Add("記録した署名済みの値と違う: $relative") }
    }
    if ($problems.Count) { throw "$Label の署名したファイルが記録と一致しない（$($problems.Count) 件）。build_dist.ps1 で staging を作り直すこと: $($problems -join '; ')" }
}

# staging のファイル(絶対パス)→ 署名後の SHA256 の表を、記録に追記する。記録に無いファイルがあれば、何も書かずに例外を投げる。
# 署名した一時ファイルで staging のファイルを置き換える前に呼ぶ(置き換えの後に記録を書けずに止まると、記録の無い署名済みのファイルが残り、再開できない)。
function Set-SignRecordSigned([string]$Staging, [Collections.IDictionary]$SignedHashes) {
    if ($SignedHashes.Count -eq 0) { return }
    $entries = Read-SignRecord $Staging
    foreach ($file in $SignedHashes.Keys) {
        $relative = [IO.Path]::GetRelativePath($Staging, $file)
        $entry = $entries | Where-Object { $_.path -ieq $relative } | Select-Object -First 1
        if (-not $entry) { throw "署名の記録に無いファイル: $file" }
        $entry.signed = $SignedHashes[$file]
    }
    Save-SignRecord $Staging $entries
}

# 署名を確かめたファイルの今の SHA256 を、署名後の値として記録に追記する。
function Add-SignRecordSigned([string]$Staging, [string[]]$Paths) {
    $hashes = [ordered]@{}
    foreach ($file in @($Paths | Where-Object { $_ })) { $hashes[$file] = Get-SignRecordHash $file }
    Set-SignRecordSigned $Staging $hashes
}

# 署名するファイル($Targets は Get-SignTargets の結果)ごとに、記録と照合した状態を返す。
# Built は作ったときの SHA256、SignedHash は記録した署名後の SHA256(未記録なら $null)、Signed は今の内容が記録した署名後の値と一致するか。
# 署名済みかどうかは記録の値だけで決める(署名の検証が通ることだけでは、同じ証明書で署名した別の版への置き換えを通してしまう)。
# どちらの値とも違うファイルは例外にする(署名の前に Test-SignRecord が止めるので、通常は起きない)。
function Get-SignStates([string]$Staging, [object[]]$Targets) {
    $byPath = @{}
    foreach ($entry in (Read-SignRecord $Staging)) { $byPath[$entry.path.ToLowerInvariant()] = $entry }
    foreach ($target in $Targets) {
        $entry = $byPath[[IO.Path]::GetRelativePath($Staging, $target.Path).ToLowerInvariant()]
        if (-not $entry) { throw "署名の記録に無いファイル: $($target.Path)" }
        $hash = Get-SignRecordHash $target.Path
        $signed = $false
        if ($hash -eq $entry.built) { $signed = $false }
        elseif ($entry.signed -and $hash -eq $entry.signed) { $signed = $true }
        else { throw "署名するファイルが記録と一致しない: $($target.Path)" }
        [pscustomobject]@{ Path = $target.Path; Kind = $target.Kind; Built = $entry.built; SignedHash = $entry.signed; Signed = $signed }
    }
}

# 作ったときに同じ内容だったファイル(拡張子と作ったときの値が同じもの)の組。署名は組ごとに代表の 1 件だけを行う
function Get-SignGroups([object[]]$States) {
    @($States | Group-Object -Property { "$([IO.Path]::GetExtension($_.Path).ToLowerInvariant())|$($_.Built)" })
}

# 署名の前に、秘密鍵で署名する件数(署名済みの代表が無い組の数)と、署名せずに代表から複製する未署名のファイルの件数を数える
# (確認の画面の計画に出す。Invoke-SignStaging と同じ組で数える)
function Measure-SignWork([object[]]$States) {
    $sign = 0
    $copy = 0
    foreach ($group in (Get-SignGroups $States)) {
        $unsigned = @($group.Group | Where-Object { -not $_.Signed }).Count
        if ($unsigned -eq 0) { continue }
        if ($unsigned -eq $group.Count) { $sign++; $copy += $unsigned - 1 } else { $copy += $unsigned }
    }
    [pscustomobject]@{ Sign = $sign; Copy = $copy }
}

# 署名して staging のファイルを置き換える。作ったときに同じ内容だったファイルを 1 つの組にし、代表だけを署名して残りへ複製する
# (組は作ったときの値と拡張子で決める。代表を署名した後、複製の前に止まっても、再開のときに同じ組のまま複製する)。
# $SignFiles は一時ファイルのパスの配列を受け取って署名する処理(sign_dist.ps1 は signtool。未署名の代表を 1 回の呼び出しでまとめて渡す)、
# $IsSigned はパスを受け取って署名を検証する処理。署名と複製の件数を { Signed; Copied } で返す。
# staging のファイルはその場で書き換えない。署名は作業フォルダの一時ファイルで行い、検証が通ってから、署名後の SHA256 を記録に書き、
# その後に名前の変更で置き換える。複製も一時ファイルに書いてから置き換える。プロセスが止まった場合(例外、Ctrl+C、強制終了)は、
# staging のファイルが作ったときの内容か記録した署名後の内容のどちらかなので、続きから再開できる(置き換えの前に止まったファイルは、
# 作ったときの内容のままなので、もう一度署名する)。電源断と OS のクラッシュでは、名前の変更がディスクに書かれたかを保証しないので、build_dist から作り直す。
# 再開のときは、署名済みのファイルを代表にする(もう一度署名しない)。署名済みの代表も一時ファイルへコピーし、記録した署名後の値との一致と
# 署名を確かめてから複製に使う(照合の後に、同じ証明書で署名した別の版へ置き換えられても複製しない)。
# 署名済みと記録したファイルの署名を検証できなければ、何も変えずに止まる。
function Invoke-SignStaging([string]$Staging, [object[]]$Targets, [scriptblock]$SignFiles, [scriptblock]$IsSigned) {
    $states = @(Get-SignStates $Staging $Targets)
    $unverified = @($states | Where-Object { $_.Signed -and -not (& $IsSigned $_.Path) })
    if ($unverified.Count) { throw "署名済みと記録したファイルの署名を検証できない。build_dist.ps1 で staging を作り直すこと: $($unverified[0].Path)" }
    $work = Get-SignWorkDir $Staging
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    [void](New-Item -ItemType Directory -Path $work)
    $groups = @(Get-SignGroups $states)
    $plans = New-Object System.Collections.Generic.List[object]
    $index = 0
    foreach ($group in $groups) {
        $members = @($group.Group)
        $representative = @($members | Where-Object { $_.Signed }) | Select-Object -First 1
        # 一時ファイルは元の名前のままにする(signtool は拡張子で署名の形式を選ぶ)
        $temp = Join-Path (Join-Path $work ("{0:D4}" -f $index++)) ([IO.Path]::GetFileName(@($members)[0].Path))
        if ($representative) {
            $hash = Copy-StagingFileSynced $representative.Path $temp
            if ($hash -ne $representative.SignedHash) { throw "署名済みの代表のファイルが記録した署名後の値と一致しない。build_dist.ps1 で staging を作り直すこと: $($representative.Path)" }
            if (-not (& $IsSigned $temp)) { throw "署名済みと記録したファイルの署名を検証できない。build_dist.ps1 で staging を作り直すこと: $($representative.Path)" }
            $plans.Add([pscustomobject]@{ Members = $members; Representative = $representative; Temp = $temp; Fresh = $false; SignedHash = $hash })
        }
        else {
            $representative = $members[0]
            # 署名の直前に、一時ファイルが作ったときの内容であることを確かめ直す(照合の後に staging のファイルが置き換えられていたら署名しない)
            if ((Copy-StagingFileSynced $representative.Path $temp) -ne $representative.Built) { throw "署名するファイルが照合の後に変わった。build_dist.ps1 で staging を作り直すこと: $($representative.Path)" }
            $plans.Add([pscustomobject]@{ Members = $members; Representative = $representative; Temp = $temp; Fresh = $true; SignedHash = $null })
        }
    }
    $fresh = @($plans | Where-Object { $_.Fresh })
    # 署名の失敗は 1 つの一覧に集め、署名と検証に成功した代表をすべて記録して置き換えてから投げる(一部のファイルの署名が失敗しても、
    # 成功したファイルは staging に残り、再開のときに署名し直さない。YubiKey の PIN／タッチを求め直さない)
    $failures = New-Object System.Collections.Generic.List[string]
    if ($fresh.Count) {
        try { & $SignFiles ([string[]]@($fresh | ForEach-Object { $_.Temp })) }
        catch { $failures.Add($_.Exception.Message) }
    }
    # 署名したすべての代表を先に置き換えてから複製する(複製で止まっても、署名した代表は staging に残り、再開のときに署名し直さない)
    $signed = 0
    $copied = 0
    foreach ($plan in $fresh) {
        if (-not (& $IsSigned $plan.Temp)) {
            $failures.Add("署名者・内容・タイムスタンプを検証できない(staging のファイルは変えていない): $($plan.Representative.Path)")
            continue
        }
        Sync-StagingFile $plan.Temp
        $plan.SignedHash = Get-SignRecordHash $plan.Temp
        $hashes = [ordered]@{}
        foreach ($member in $plan.Members) { $hashes[$member.Path] = $plan.SignedHash }
        Set-SignRecordSigned $Staging $hashes
        Move-StagingFileReplacing $plan.Temp $plan.Representative.Path
        $signed++
    }
    # 署名か検証に失敗した組(署名後の値が無い)は複製しない。成功した組は複製する
    foreach ($plan in @($plans | Where-Object { $_.SignedHash })) {
        # 複製の元: 署名した代表は置き換えた staging のファイル(複製の内容を署名後の値と照合する)、署名済みの代表は確かめた一時ファイル
        $source = if ($plan.Fresh) { $plan.Representative.Path } else { $plan.Temp }
        foreach ($member in @($plan.Members | Where-Object { $_.Path -ne $plan.Representative.Path })) {
            if ((Get-SignRecordHash $member.Path) -eq $plan.SignedHash) { continue }
            $copy = Join-Path (Join-Path $work ("{0:D4}" -f $index++)) ([IO.Path]::GetFileName($member.Path))
            if ((Copy-StagingFileSynced $source $copy) -ne $plan.SignedHash) { throw "署名したファイルを複製できない(複製の内容が違う): $($member.Path)" }
            Set-SignRecordSigned $Staging ([ordered]@{ $member.Path = $plan.SignedHash })
            Move-StagingFileReplacing $copy $member.Path
            $copied++
        }
    }
    Remove-Item -LiteralPath $work -Recurse -Force
    if ($failures.Count) { throw "署名に失敗した($($failures.Count) 件。署名と検証に成功した $signed 件は staging に反映した。再実行すると、失敗したファイルだけを署名する): $($failures -join '; ')" }
    return [pscustomobject]@{ Signed = $signed; Copied = $copied }
}
