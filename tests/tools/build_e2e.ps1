# CAD を起動する試験（E2E・画面の確認）が読み込むプラグインの DLL を、配布物と同じビルド（dist/dist_build.ps1）で作る。
# 試験のスクリプトは Get-E2ePluginPath（tests/tools/common/test_helpers.ps1）で DLL のパスを受け取り、
# 開発機のパスが残る普段のビルドの DLL なら CAD を起動せずに止まる。単体試験は普段のビルドのまま。
#
# 使い方: pwsh -File tests/tools/build_e2e.ps1 [-Products Acad,Ares,Ijcad,Bricscad]
# 互換 CAD（Ares・Ijcad・Bricscad）は各社の SDK の参照 DLL が要る（README.md のビルドの節）。
# DLL は、配布物と同じく、開発機のパスと失敗の注入が無いことと、net48 の依存の版の一致を確かめる。
param(
    [ValidateSet('Acad', 'Ares', 'Ijcad', 'Bricscad')][string[]]$Products = @('Acad', 'Ares', 'Ijcad', 'Bricscad')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
. "$repo\dist\dist_build.ps1"
. "$repo\dist\dll_path_check.ps1"
. "$repo\tests\tools\common\test_helpers.ps1"   # Get-E2eBuildFingerprint・Write-E2eBuildRecord

$config = $DistBuildConfig
foreach ($product in $Products) {
    $project = Join-Path $repo "src\CadPeta.Plugin.$product\CadPeta.Plugin.$product.csproj"
    # ARES の 2020・2023 は SDK の違う別の出力（bin\ares<年>）。既定（2024・2027）と合わせて作る
    $variants = if ($product -eq 'Ares') { @(@{ Properties = @(); Bin = 'bin' }, @{ Properties = @('-p:AresSdkYearNet48=2020'); Bin = 'bin\ares2020' }, @{ Properties = @('-p:AresSdkYearNet48=2023'); Bin = 'bin\ares2023' }) }
                else { @(@{ Properties = @(); Bin = 'bin' }) }
    foreach ($v in $variants) {
        "[$product] dotnet $((Get-DistBuildArguments $project $config $v.Properties) -join ' ')"
        # ビルドの前に入力の値を求める（ビルドの間にソースが変わっても、記録は古い側になり、試験は作り直すまで止まる）
        $fingerprint = Get-E2eBuildFingerprint $repo
        Invoke-DistBuild $project $config $v.Properties
        $outRoot = Join-Path $repo "src\CadPeta.Plugin.$product\$($v.Bin)\$config"
        foreach ($dir in @(Get-ChildItem -LiteralPath $outRoot -Directory)) {
            $label = "$product $($v.Bin) $config $($dir.Name)"
            Assert-ReleaseOwnAssemblies $dir.FullName $label
            # net48 は同梱の DLL の版が参照と一致しないと読み込めない（配布物と同じ検査）
            if ($dir.Name -eq 'net48') { Assert-ConsistentAssemblyReferences $dir.FullName $label }
        }
        Write-E2eBuildRecord $outRoot $fingerprint
    }
}
"試験の DLL を配布物と同じビルドで作った（$($Products -join ', ')、$config）"
