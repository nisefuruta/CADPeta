# 配布物と、CAD を起動する試験（tests/tools/build_e2e.ps1 で作る DLL）のビルドの唯一の定義。
# build_dist.ps1 と build_e2e.ps1 の両方がドットソースで読み込んで呼び、試験した DLL と配布する DLL を同じビルドで作る。
# 単体試験は普段のビルド（この定義を使わない）のまま。
#   -m:1: 同じ netstandard の出力を複数のターゲットが同時に作ると競合する。
#   -p:ContinuousIntegrationBuild=true: DLL のデバッグ情報のパスを /_/ に置き換え、開発機のパスを残さない
#     （dll_path_check.ps1 で検査する）。props に置かないのは、[CallerFilePath] でソースのフォルダを読む試験の
#     ビルドまで置き換えないため。
#   --no-incremental: 普段のビルドの出力が新しいとコンパイルが飛ばされ、パスが置き換わらない。毎回作り直す
#     （dotnet publish はこの指定を受け付けない。CLI と GUI は DebugType=None で、DLL にパスが入らない）。
# 既定の構成は Release（配布物と同じ）。

$DistBuildConfig = 'Release'
# CLI と GUI の dotnet publish も同じ置き換えを使う（build_dist.ps1）
$DistCiProperty = '-p:ContinuousIntegrationBuild=true'

function Get-DistBuildArguments([string]$Project, [string]$Config = $DistBuildConfig, [string[]]$Properties = @()) {
    return @('build', $Project, '-c', $Config, '-m:1', $DistCiProperty, '--no-incremental') + $Properties
}

# 上の定義で $Project をビルドする。成功したときは出力を表示せず、失敗したときは出力を例外の文に入れる。
function Invoke-DistBuild([string]$Project, [string]$Config = $DistBuildConfig, [string[]]$Properties = @()) {
    $arguments = Get-DistBuildArguments $Project $Config $Properties
    $output = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "ビルドに失敗: dotnet $($arguments -join ' ')`n" + (($output | Select-Object -Last 30) -join "`n")
    }
}
