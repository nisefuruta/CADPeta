# PdfBoxProbe — PDFBox による正解データ生成器（Phase 0 第3段）

PDFBox は「正解データ生成器」であり本実装候補ではない（`docs/validation/architecture-decisions.md` §3）。
PdfPigProbe の抽出結果をオブジェクト単位で照合するための基準 JSON を生成する。

## 依存とライセンス

- **Apache PDFBox 3.0.7**（pdfbox-app 単体 jar、依存同梱）。ライセンスは **Apache-2.0**。
  jar 同梱の `META-INF/LICENSE` で確認済み。NOTICE 記載の同梱物
  （Adobe Glyph List・Unicode データ・TwelveMonkeys ImageIO）もすべて許容的ライセンス（NFR-1 適合）。
- 実行環境: OpenJDK 21（Microsoft Build 21.0.11、javac 同梱）

jar・クラスファイルはコミットしない（ルート `.gitignore` で `lib/` と `out/` を除外）。

## 再現手順

```powershell
# 1. jar の取得（Maven Central。SHA1 = 157f1bb4064cec282e64d1a7c2f8ca7a8d0f71cd）
cd tests\tools\pdf-emf\PdfBoxProbe
New-Item -ItemType Directory -Force lib | Out-Null
curl.exe -sSL -o lib\pdfbox-app-3.0.7.jar `
  https://repo1.maven.org/maven2/org/apache/pdfbox/pdfbox-app/3.0.7/pdfbox-app-3.0.7.jar
(Get-FileHash lib\pdfbox-app-3.0.7.jar -Algorithm SHA1).Hash  # 上記と一致を確認

# 2. ビルド
javac -encoding UTF-8 -cp lib\pdfbox-app-3.0.7.jar -d out PdfBoxProbe.java

# 3. 実行（リポジトリルートから）
cd ..\..\..
java -cp "tests\tools\pdf-emf\PdfBoxProbe\lib\pdfbox-app-3.0.7.jar;tests\tools\pdf-emf\PdfBoxProbe\out" `
  PdfBoxProbe tests\assets\testcharts\TESTCHART2-off.pdf out\pdfbox_off.json

# 4. PdfPig 側と照合
dotnet run --project tests/tools/pdf-emf/PdfPigProbe -- tests\assets\testcharts\TESTCHART2-off.pdf out\pdfpig_off.json
uv run --project tests/tools/pdf-emf python tests/tools/pdf-emf/compare_pdfbox.py out\pdfbox_off.json out\pdfpig_off.json [--cells]
```

## 抽出内容

- **パス**: `PDFGraphicsStreamEngine` のコールバック（moveTo/lineTo/curveTo/appendRectangle/クリップ/塗り/線）。
  座標はデバイス空間 = PDF ユーザー空間（pt、y-up）。bbox は制御点包含（PdfPig と同じ規約。
  Java 17+ の `Path2D.getBounds2D()` は曲線の厳密 bbox を返すため自前集計に変更済み）
- **文字**: `PDFTextStripper` + `TextPosition`（グリフ単位）。色オペレータは既定未登録のため追加登録。
  サイズは TRM のスケール係数から算出（`getFontSizeInPt()` は CTM を反映しない）
- **状態**: 色（色空間→RGB 解決）・線幅（生値と CTM 補正値）・透過 ca/CA・クリップ bbox・
  レイヤ（BDC/EMC の /OC を processOperator でフックしリソースの Properties から名前解決）
- **画像・注釈**: 寸法・配置・SMask 有無、/Square 注釈の文字列と矩形
