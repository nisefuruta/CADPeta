# 配布物に入れてはいけない CAD 本体の参照アセンブリの判定の唯一の定義。
# build_dist.ps1（組み立ての前の検査）と tests/tools/distribution/package_verify.ps1（ZIP の最終の検査）の両方が読み込む。
# compile 専用の参照は ExcludeAssets=runtime で出力から外れる前提で、混入は配布の事故になる。

# src のプロジェクトが実際に参照する DLL（Private=false の Reference と、ExcludeAssets=runtime のパッケージの compile の DLL）を
# 全部含むことは、単体試験 CadReferenceDllTests が csproj と project.assets.json から求めて確かめる
$CadReferenceDllNames = @(
    # AutoCAD（NuGet の AutoCAD.NET・AutoCAD.NET.Core・AutoCAD.NET.Model）
    "AcCoreMgd.dll", "AcDbMgd.dll", "acdbmgdbrep.dll", "AcMgd.dll",
    "AcCui.dll", "AcDx.dll", "AcMr.dll", "AcSeamless.dll", "AcTcMgd.dll", "AcWindows.dll",
    "AdUIMgd.dll", "AdUiPalettes.dll", "AdWindows.dll", "AutoCAD.NET.dll",
    "TD_Mgd.dll", "FxCoreMgd.dll", "FxProperties.dll",                         # ARES(Graebert)
    "GcDbMgd.dll", "GcCoreMgd.dll", "GcMgd.dll",                               # IJCAD
    "GcaddMgd.dll", "GrxMgd.dll", "Itjp.Ijcad.dll",
    "BrxMgd.dll",                                                              # BricsCAD
    "Teigha.Runtime.dll", "Teigha.TD.dll", "Bricscad.dll", "TD_DbMgd.dll"     # BricsCAD/ODA
)
# ARES の SDK は年式ごとの版の付いた名前（TD_Mgd_4.03_14.dll・FxCoreMgd_23.1_16.dll・
# TD_Mgd_Net8_26.8_17.dll 等）なので、名前の列挙では漏れる。パターンでも判定する
$CadReferenceDllPatterns = @("TD_Mgd_*.dll", "FxCoreMgd_*.dll")

# ファイル名（パスを含まない）が CAD 本体の参照アセンブリなら $true。大文字と小文字は区別しない
function Test-CadReferenceDllName([string]$Name) {
    return ($CadReferenceDllNames -contains $Name) -or [bool]($CadReferenceDllPatterns | Where-Object { $Name -like $_ })
}
