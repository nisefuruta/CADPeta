# TESTCHART2_layout.dwg のレイアウト PETA_A3（A3 1:1）を PETA で切り出したときの、窓と SVG の寸法・viewBox の既知正解。
# crop_regression.ps1・peta_option_matrix.ps1・peta_option_smoke_compat.ps1 がドットソースで読み、値をほかに書かない。
#
# viewBox の Y は、プラグインが印刷する PDF のページの高さで変わるので、ページの高さごとに表を分ける。
# - PageHeight842pt: A3 の高さ 297mm を 842pt = 297.0389mm に丸めたページ。窓の上の縁からの位置が 0.0389mm 下がる。
#   AutoCAD の accoreconsole と ARES（ページの大きさだけを丸める）の出力で確かめた。
#   AutoCAD 2027 の M5 の PDF で、AutoCAD が書いた窓のクリップの中心とクロップの中心の差は Y 0.011mm だった（Y 108.5 なら 0.049mm。2026-10-31）
# - PageHeight297mm: ページを丸めない製品（IJCAD・BricsCAD）。実機の出力はまだ確かめていない
# どの製品がどの表を使うかは、製品の *PluginDefaults.PdfPointSnapping と食い違わないことを
# tests/CadPeta.Tests/Duplication/PdfPointSnappingExpectTests.cs が確かめる
$PetaA3Window = @{ P1 = '50,40'; P2 = '150,110'; MarginMm = '5' }
$PetaA3WindowExpect = @{
    PageHeight842pt = @{
        M0   = @{ W = '100'; H = '70'; VB = '160 113.5389 100 70' }
        M5   = @{ W = '110'; H = '80'; VB = '155 108.5389 110 80' }
        Full = @{ W = '420.1583'; H = '297.0389'; VB = '0 0 420.1583 297.0389' }
    }
    PageHeight297mm = @{
        M5   = @{ W = '110'; H = '80'; VB = '155 108.5 110 80' }
    }
}
