# 配布物の自作の DLL に、開発機のパスが残っていないことを検査する（build_dist.ps1 がドットソースで読み込む）。
# .pdb を配らなくても、DLL の CodeView には PDB のパスが残り、埋め込み PDB があればソースのパスも残る。
# 配布のビルドは -p:ContinuousIntegrationBuild=true でパスを /_/ に置き換え、ここで残っていないことを確かめる。
# 第三者の DLL には配布元のビルドの PC のパスが入っていて変えられないので、自作の DLL（CadPeta.*.dll）だけを検査する。
# 検査は PowerShell 7（.NET）の System.Reflection.Metadata を使う。

# ドライブ文字で始まる絶対パス（例 C:\Users\...）を開発機のパスとして扱う
$DevPathPattern = '^[A-Za-z]:[\\/]'
$OwnAssemblyFilter = 'CadPeta.*.dll'

# DLL の CodeView の PDB のパスと、埋め込み PDB のソースの文書のパスを返す。読めなければ例外を投げる。
function Get-AssemblyDebugPaths([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $pe.HasMetadata) { throw "マネージドのアセンブリではありません" }
            $paths = New-Object System.Collections.Generic.List[string]
            foreach ($entry in $pe.ReadDebugDirectory()) {
                switch ($entry.Type) {
                    ([System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
                        $paths.Add($pe.ReadCodeViewDebugDirectoryData($entry).Path)
                    }
                    ([System.Reflection.PortableExecutable.DebugDirectoryEntryType]::EmbeddedPortablePdb) {
                        $provider = $pe.ReadEmbeddedPortablePdbDebugDirectoryData($entry)
                        try {
                            $pdb = $provider.GetMetadataReader()
                            foreach ($handle in $pdb.Documents) {
                                $paths.Add($pdb.GetString($pdb.GetDocument($handle).Name))
                            }
                        }
                        finally { $provider.Dispose() }
                    }
                }
            }
            return , $paths.ToArray()
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
}

# $dir の下の自作の DLL を検査し、開発機のパスが残っているか、読めない DLL があれば止める。
function Assert-NoDevPathsInOwnAssemblies([string]$dir, [string]$label) {
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($dll in @(Get-ChildItem -LiteralPath $dir -Filter $OwnAssemblyFilter -Recurse -File)) {
        try {
            $devPaths = @(Get-AssemblyDebugPaths $dll.FullName | Where-Object { $_ -match $DevPathPattern })
        }
        catch {
            $failures.Add("$($dll.FullName): 読めない（$($_.Exception.Message)）")
            continue
        }
        foreach ($p in $devPaths) { $failures.Add("$($dll.FullName): $p") }
    }
    if ($failures.Count -gt 0) {
        throw "[$label] 自作の DLL に開発機のパスが残っているか、DLL を読めない（ContinuousIntegrationBuild のビルドか確認）:`n" + ($failures -join "`n")
    }
}

# 失敗の注入（試験だけで処理を失敗させる仕組み）は Debug のビルドだけに入れる（#if DEBUG）。注入を切り替える環境変数の名前は
# CADPETA_TEST_ で始め、配布物の自作の DLL にその名前（DLL の中では UTF-16 の文字列）があれば止める
$FailureInjectionPrefix = 'CADPETA_TEST_'

function Assert-NoFailureInjectionInOwnAssemblies([string]$dir, [string]$label) {
    $pattern = [Text.Encoding]::Unicode.GetBytes($FailureInjectionPrefix)
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($dll in @(Get-ChildItem -LiteralPath $dir -Filter $OwnAssemblyFilter -Recurse -File)) {
        $bytes = [IO.File]::ReadAllBytes($dll.FullName)
        for ($i = [Array]::IndexOf($bytes, $pattern[0]); $i -ge 0 -and $i -le $bytes.Length - $pattern.Length; $i = [Array]::IndexOf($bytes, $pattern[0], $i + 1)) {
            $match = $true
            for ($j = 1; $j -lt $pattern.Length; $j++) { if ($bytes[$i + $j] -ne $pattern[$j]) { $match = $false; break } }
            if ($match) { $failures.Add($dll.FullName); break }
        }
    }
    if ($failures.Count -gt 0) {
        throw "[$label] 自作の DLL に失敗の注入（$FailureInjectionPrefix で始まる環境変数）が入っている（Debug のビルドか、#if DEBUG の外に注入がある）:`n" + ($failures -join "`n")
    }
}

# net48 の配布フォルダの DLL が、同じフォルダに同梱した DLL を、同梱したものと同じ名前・AssemblyVersion・公開鍵トークン・Culture で
# 参照していることを確かめる。.NET Framework は厳密な名前のアセンブリを版まで一致させて束縛し、プラグインは bindingRedirect を
# 持てないので、ずれていると読み込みに失敗する（System.Text.Json 9 の System.Memory 4.0.1.2 と同梱の 4.0.2.0。2026-10-28）。
# .NET 8・10 は同じ名前の上位の版で束縛するので、この検査は net48 の配布フォルダだけに使う。読めない DLL があれば止める
function Assert-ConsistentAssemblyReferences([string]$dir, [string]$label) {
    $failures = New-Object System.Collections.Generic.List[string]
    $bundled = @{}
    $readers = New-Object System.Collections.Generic.List[object]
    try {
        foreach ($dll in @(Get-ChildItem -LiteralPath $dir -Filter '*.dll' -File)) {
            $stream = [IO.File]::OpenRead($dll.FullName)
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            $readers.Add($pe); $readers.Add($stream)
            if (-not $pe.HasMetadata) { continue }   # ネイティブの DLL は .NET の参照を持たない
            try {
                $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                if (-not $md.IsAssembly) { continue }
                $bundled[$md.GetAssemblyDefinition().GetAssemblyName().Name] = [pscustomobject]@{ Name = $md.GetAssemblyDefinition().GetAssemblyName(); Reader = $md; File = $dll.Name }
            }
            catch { $failures.Add("$($dll.FullName): 読めない（$($_.Exception.Message)）") }
        }
        foreach ($entry in $bundled.Values) {
            foreach ($handle in $entry.Reader.AssemblyReferences) {
                $ref = $entry.Reader.GetAssemblyReference($handle).GetAssemblyName()
                $target = $bundled[$ref.Name]
                if (-not $target) { continue }   # 同梱していない（ホストか .NET Framework が提供する）アセンブリ
                $want = $target.Name
                $sameToken = [Convert]::ToHexString([byte[]]@($ref.GetPublicKeyToken())) -eq [Convert]::ToHexString([byte[]]@($want.GetPublicKeyToken()))
                if ($ref.Version -ne $want.Version -or -not $sameToken -or $ref.CultureName -ne $want.CultureName) {
                    $failures.Add("$($entry.File) -> $($ref.FullName)（同梱: $($want.FullName)）")
                }
            }
        }
    }
    finally { foreach ($r in $readers) { $r.Dispose() } }
    if ($failures.Count -gt 0) {
        throw "[$label] 同梱の DLL を、同梱したものと違う名前・版・公開鍵トークン・Culture で参照している（net48 は読み込みに失敗する）:`n" + ($failures -join "`n")
    }
}

# 配布する（Release の）自作の DLL の検査: 開発機のパスと失敗の注入が無いこと。配布スクリプトと、CAD を起動する試験の DLL の確認が呼ぶ
function Assert-ReleaseOwnAssemblies([string]$dir, [string]$label) {
    Assert-NoDevPathsInOwnAssemblies $dir $label
    Assert-NoFailureInjectionInOwnAssemblies $dir $label
}
