# dist/dll_path_check.ps1 の約束を、一時フォルダで小さなクラスライブラリを実際にビルドして確かめる。
# 配布物の DLL に開発機のパス（CodeView の PDB のパス、埋め込み PDB のソースのパス）が残っていれば止め、
# ContinuousIntegrationBuild で作った DLL は通し、第三者の DLL は検査せず、読めない自作の DLL は止める。
# 使い方: pwsh -File tests/tools/distribution/dll_path_check_selftest.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\..\dist\dll_path_check.ps1')

$work = Join-Path ([IO.Path]::GetTempPath()) ("cadpeta_dll_path_check_" + [guid]::NewGuid().ToString('N'))
$checked = New-Object System.Collections.Generic.List[string]

function Build-Sample([string]$name, [string[]]$properties, [string]$source = 'namespace Sample { public static class S { public static int F() => 1; } }') {
    $proj = Join-Path $work "src\$name"
    New-Item -ItemType Directory -Force $proj | Out-Null
    Set-Content -LiteralPath (Join-Path $proj "$name.csproj") -Encoding utf8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>$name</AssemblyName>
  </PropertyGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $proj 'Sample.cs') -Encoding utf8 -Value $source
    $out = Join-Path $work "out\$name"
    & dotnet build (Join-Path $proj "$name.csproj") -c Release -o $out -nologo -v q @properties | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "試験用のライブラリをビルドできなかった: $name" }
    return Join-Path $out "$name.dll"
}

function Assert-Stops([string]$dir, [string]$expectedText, [string]$label) {
    try { Assert-NoDevPathsInOwnAssemblies $dir 'selftest' }
    catch {
        if ($_.Exception.Message -notlike "*$expectedText*") { throw "${label}: 止まったが理由が違う: $($_.Exception.Message)" }
        return
    }
    throw "${label}: 止まらなかった"
}

function New-PackageDir([string]$label, [string[]]$files) {
    $dir = Join-Path $work "pkg\$label"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($f in $files) { Copy-Item -LiteralPath $f -Destination $dir }
    return $dir
}

try {
    New-Item -ItemType Directory -Force $work | Out-Null
    # ContinuousIntegrationBuild のパスの置き換えは、Git のリポジトリの根を基準（SourceRoot）にする。
    # 配布のビルドと同じく、Git のリポジトリの中でビルドする
    # コミットの無いリポジトリでは基準が決まらず置き換わらなかったので、空のコミットを 1 つ作る
    & git -C $work init -q
    if ($LASTEXITCODE -eq 0) { & git -C $work -c user.name=selftest -c user.email=selftest@example.invalid commit -q --allow-empty -m selftest }
    if ($LASTEXITCODE -ne 0) { throw "試験のフォルダを Git のリポジトリにできなかった: $work" }

    # 1. 普通のビルドの DLL は CodeView に開発機の PDB のパスを持つ。埋め込み PDB はソースのパスも持つ
    $plain = Build-Sample 'CadPeta.Sample' @()
    Assert-Stops (New-PackageDir 'plain' @($plain)) 'CadPeta.Sample.dll' '1(CodeView)'
    $embedded = Build-Sample 'CadPeta.Embedded' @('-p:DebugType=embedded')
    Assert-Stops (New-PackageDir 'embedded' @($embedded)) 'Sample.cs' '1(埋め込み PDB)'
    $checked.Add('開発機のパスを CodeView に持つ DLL と、埋め込み PDB にソースのパスを持つ DLL を見つけて止まる')

    # 2. ContinuousIntegrationBuild で作った DLL は通る（埋め込み PDB でも）
    $ci = Build-Sample 'CadPeta.Ci' @('-p:ContinuousIntegrationBuild=true')
    $ciEmbedded = Build-Sample 'CadPeta.CiEmbedded' @('-p:ContinuousIntegrationBuild=true', '-p:DebugType=embedded')
    Assert-NoDevPathsInOwnAssemblies (New-PackageDir 'ci' @($ci, $ciEmbedded)) 'selftest'
    $checked.Add('ContinuousIntegrationBuild で作った DLL は、埋め込み PDB でも通る')

    # 3. 第三者の DLL（自作の名前でない）は、開発機のパスを持っていても検査しない
    $third = Join-Path $work 'pkg\third'
    New-Item -ItemType Directory -Force $third | Out-Null
    Copy-Item -LiteralPath $plain -Destination (Join-Path $third 'ThirdParty.Library.dll')
    Copy-Item -LiteralPath $ci -Destination $third
    Assert-NoDevPathsInOwnAssemblies $third 'selftest'
    $checked.Add('第三者の DLL は検査しない')

    # 4. 読めない自作の DLL があれば止まる
    $broken = Join-Path $work 'pkg\broken'
    New-Item -ItemType Directory -Force $broken | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $broken 'CadPeta.Broken.dll'), [byte[]](1..64))
    Assert-Stops $broken 'CadPeta.Broken.dll' '4'
    $checked.Add('読めない自作の DLL があれば止まる')

    # 5. Debug ビルドだけに入れる失敗の注入（CADPETA_TEST_ で始まる環境変数を読む処理）が自作の DLL に入っていれば止まる。
    #    第三者の DLL は検査しない
    $injected = Build-Sample 'CadPeta.Injected' @('-p:ContinuousIntegrationBuild=true') 'namespace Sample { public static class S { public static string F() => System.Environment.GetEnvironmentVariable("CADPETA_TEST_FAIL_STARTUP"); } }'
    $injectedDir = New-PackageDir 'injected' @($injected)
    $stopped = $null
    try { Assert-NoFailureInjectionInOwnAssemblies $injectedDir 'selftest' } catch { $stopped = $_.Exception.Message }
    if ($null -eq $stopped) { throw '5: 失敗の注入の入った DLL で止まらなかった' }
    if ($stopped -notlike '*CadPeta.Injected.dll*' -or $stopped -notlike '*CADPETA_TEST_*') { throw "5: 止まったが理由が違う: $stopped" }
    Assert-NoFailureInjectionInOwnAssemblies (New-PackageDir 'no-injection' @($ci)) 'selftest'
    $thirdInjected = Join-Path $work 'pkg\third-injected'
    New-Item -ItemType Directory -Force $thirdInjected | Out-Null
    Copy-Item -LiteralPath $injected -Destination (Join-Path $thirdInjected 'ThirdParty.Injected.dll')
    Assert-NoFailureInjectionInOwnAssemblies $thirdInjected 'selftest'
    $checked.Add('失敗の注入（CADPETA_TEST_ の環境変数）が入った自作の DLL で止まり、入っていない DLL と第三者の DLL は通る')

    # 6. net48 の配布フォルダの DLL は、同梱の DLL を同じ名前・AssemblyVersion・公開鍵トークン・Culture で参照する。
    #    参照される側だけを別の AssemblyVersion で作り直して置き換えると止まり、そろっていれば通る
    $refProj = Join-Path $work 'src\RefA'
    $useProj = Join-Path $work 'src\RefB'
    New-Item -ItemType Directory -Force $refProj, $useProj | Out-Null
    Set-Content -LiteralPath (Join-Path $refProj 'CadPeta.RefA.csproj') -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><AssemblyName>CadPeta.RefA</AssemblyName><AssemblyVersion>$(RefVersion)</AssemblyVersion></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $refProj 'A.cs') -Encoding utf8 -Value 'namespace RefA { public static class A { public static int F() => 1; } }'
    Set-Content -LiteralPath (Join-Path $useProj 'CadPeta.RefB.csproj') -Encoding utf8 -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><AssemblyName>CadPeta.RefB</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="..\RefA\CadPeta.RefA.csproj" /></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $useProj 'B.cs') -Encoding utf8 -Value 'namespace RefB { public static class B { public static int G() => RefA.A.F(); } }'
    $consistent = Join-Path $work 'pkg\consistent'
    & dotnet build (Join-Path $useProj 'CadPeta.RefB.csproj') -c Release -o $consistent -nologo -v q -p:RefVersion=1.0.0.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '6: 試験用のライブラリをビルドできなかった' }
    Assert-ConsistentAssemblyReferences $consistent 'selftest'
    $newer = Join-Path $work 'out\RefA2'
    & dotnet build (Join-Path $refProj 'CadPeta.RefA.csproj') -c Release -o $newer -nologo -v q -p:RefVersion=2.0.0.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '6: 別の版の試験用のライブラリをビルドできなかった' }
    $mismatched = New-PackageDir 'mismatched' @((Join-Path $consistent 'CadPeta.RefB.dll'), (Join-Path $newer 'CadPeta.RefA.dll'))
    $stopped = $null
    try { Assert-ConsistentAssemblyReferences $mismatched 'selftest' } catch { $stopped = $_.Exception.Message }
    if ($null -eq $stopped) { throw '6: 同梱と違う版を参照する DLL で止まらなかった' }
    if ($stopped -notlike '*CadPeta.RefB.dll*' -or $stopped -notlike '*1.0.0.0*' -or $stopped -notlike '*2.0.0.0*') { throw "6: 止まったが理由が違う: $stopped" }
    $checked.Add('同梱の DLL を同梱と違う AssemblyVersion で参照する DLL があれば止まり、そろっていれば通る')

    "dll_path_check_selftest OK（確かめた約束 $($checked.Count) 件）"
    $checked | ForEach-Object { "  - $_" }
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
