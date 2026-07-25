# 検証記録: 互換CAD 印刷経路

## ARES 2020・2023 への対応（2026-07-21）

ARES プラグインの net48 ターゲットへ、SDKアセンブリ名の版差を吸収する年式選択を入れ、
ARES Standard 2020 と 2023 を対応に加えた。
既定の年式は 2024 のままで、日常のソリューションビルドと 2024／2027 の実機経路は変えていない。
版とDLLサフィックスの対応、ProgIDは `docs/03_技術解説.md` §3.3 に記す。

対応の可否は、本機の実SDKに対する実コンパイルで確かめた。
ARES 向けの全ソース（取得アダプタ、プラグイン、共有ソース）を 2020 と 2023 の各SDKへ実際にコンパイルし、
警告0・エラー0で通ることを確認した。
version の差は `CadHostLanguage` の1点だけで、2020 のSDKに無い `UserRegistryProductRootKey` を、
全世代のSDKにある `RegistryProductRootKey` へ替えて解消した。

配布と登録は自動試験で確かめた。
配布ハーネスは `ares\2020` と `ares\2023` を含む互換CAD ZIP（162ファイル）を組み、`package_verify` が
収録物一致を確認した。
登録スクリプトの自己試験は、隔離レジストリへ4年式（2020／2023／2024／2027）のキー形状を作り、
全件登録、年式限定登録、途中失敗時のロールバック、全解除の往復を確認した。

ARES 2020 の実機で、PETA コマンドの起動までを確認した。
`LoadDotNetApplication` でプラグインを読み込み、`PETA` を実行すると、コマンド行に
「オプション: All, Object, Margin, Layer, Textoutline, Fileoutput, Background」の
コーナー指定プロンプトが出た。プラグインの読み込みと PETA の登録が成立している。

ただしこの読み込みには、ARES 2020 のフォルダ信頼の確認を越える必要がある。
ARES 2020 は、信頼済みでないフォルダにあるDLLを読み込むとき「実行ファイル …\CadPeta.Plugin.Ares.dll は、
信頼できるフォルダにありません。このまま実行ファイルをロードしますか？」というダイアログを出す。
これはフォルダの信頼可否の確認であって、コード署名の確認ではない。
手動読み込みではこのダイアログで「はい」を選べば読み込めるが、`SECURELOAD=0` を COM で設定しても
このダイアログは抑止されなかった（2026-07-21実測）。
レジストリ自動読み込みで起動すると、ARES 2020 は信頼済みでないフォルダのDLLをダイアログも出さずに
読み込まず、PETA が未登録のままになる（配布ZIPを `Downloads` 配下へ展開した状態で再現）。
このフォルダ信頼の扱いは ARES 2024 では緩く、同じ未信頼フォルダのDLLを自動読み込みできる。

## 0. 印刷単一経路への移行（2026-07-15）

PETAの先選択・オブジェクト指定をDB直接貼付から側DB PDF印刷へ移行した。IJCAD 2026 PROと
BricsCAD V26は `TESTCHART2.dwg` の全図形先選択をPETADIAGで実GUI実行し、PDF→SVG/EMF・診断JSON、
FILEDIA/SECURELOAD復元、未保存終了まで完走した。IJCADはSVG 198163 bytes / EMF 151832 bytes、
BricsCADはSVG 433427 bytes / EMF 128504 bytes。製品表示版（2027の27.1）とCOM API版（26.8）は
別体系であり、ProgIDを製品表示版から推測しない。ハーネスの事前条件と切り分け手順は
`tests/tools/README.md` の「グラデーション実機E2E」に固定した。

2026-07-15、ARES Standard 2024評価版で本試験を完走した。版別ProgID接続、
`LoadDotNetApplication`、PETADIAGのオブジェクト全選択、PDF→内容クロップ→SVG/EMF/診断JSON、
`SECURELOAD`復元、未保存終了、残留プロセス0を確認した。最終SVGは457102 bytes、EMFは133492 bytes、
診断は4件で縮退0件。ARESのPDFドライバは9グラデーションを画像ではなく3050個のベクターパスへ展開するため、
ハーネスはARESのみ `images=0 && paths>=3000`、他製品は従来どおり画像9件を合格条件とする。

ARESでは.NETコマンド開始後のCOM呼出しが拒否され、`SendCommand`へ同梱した後続プロンプト回答も保持されない。
ハーネスはアイドル中に`WScript.Shell`を作成し、PETADIAG開始後はUIへ
`O, _ALL, Enter, Enter`を送り、COMへ触れず成果物の安定生成を待ってからCOMの後始末へ戻る。

2026-07-15、ARES Standard 2027（製品27.1／COM API 26.8、net8.0-windows版プラグイン）で本試験を完走した。
版別ProgID `PCAD_AC_X.AcadApplication.26.8` へのCOM接続、`LoadDotNetApplication`、PETADIAGのオブジェクト
全選択、PDF→SVG/EMF/診断JSON、`SECURELOAD`復元、未保存終了、残留プロセス0を確認した。最終SVGは
457119 bytes（画像0件・ベクターパス3050件）、EMFは133492 bytes、診断は4件で縮退0件であり、2024と同一の
合格条件（`images=0 && paths>=3000`）を充足した。初回起動は初期化でROT登録が120秒を超えることがあり、
その場合は同条件で再試行する。

2026-07-16、ARES Standard 2027で構造SVG書き出し（PETADATADIAG、`gradient_compat_e2e.ps1 -Mode DataExport`）
の実機E2Eを完走した。9形状のグラデーションハッチを9枚のPNG画像へラスタ化（150dpi・縮退0件）し、
最終SVG（155,312 bytes）はAutoCAD 2026 accoreconsoleの同一フィクスチャに対するPETADATA成果物と
SHA-256で完全一致した。詳細は `gradient-rasterization.md` §8 に記す。

2026-07-15、選択印刷ポートの4製品統合（CadPlotEngineRunner・CadSelectionPlotPort）後の実装で、
ARES 2024・ARES 2027・IJCAD 2026 PRO・BricsCAD V26 の本試験を完走した。IJCADは最終SVG 198163 bytes・
EMF 151832 bytes・診断2件、BricsCADは最終SVG 433427 bytes・EMF 128504 bytes・診断2件で、
統合前の記録とバイト一致した。

BricsCAD V26.2 は .NET ランタイム 8.0.29 が入った環境で NETLOAD のマネージドホスト初期化に失敗する
（`BRXLOADMANAGEDDLL` 未定義、または coreclr のアクセス違反でプロセスが落ちる。プラグインの読込前に
発生するため CADPeta 側の版とは無関係）。通常の .NET ホスティングでは 8.0.29 は正常であり、
BricsCAD 同梱の C++/CLI ホスト（Ijwhost）との組み合わせに固有の非互換である。8.0.28 環境では正常に
動作することを確認済み。恒久対処は 8.0.29 系ランタイムの巻き戻し、または Bricsys の修正版適用であり、
E2E が上記症状で失敗したときは `dotnet --list-runtimes` でこの非互換に該当しないかを確認する。

構造保持の旧DB取得は削除せず、独立コマンドPETADATAへ移した。以下の2026-07-13記録にある
「構造SVG書き出し」は当時のPETA動作を示す履歴であり、現在のPETA仕様ではない。

実施日: 2026-07-13。対象図面: `tests/assets/testcharts/TESTCHART2.dwg` / `TESTCHART2_layout.dwg`。

## 1. 実装結果

3製品すべてでサイレント PlotEngine 経路が成立した。Shared の `PlotOrchestrator` と
`PlotWindowMapper` は変更せず、製品固有 `IPlotPort`、窓pick、`PdfIrAcquirer` の配線だけを追加した。

| 製品 | 実機 | Plot API | PDFデバイス | 用紙実寸 |
|---|---|---|---|---|
| ARES | Standard 2024 24.3 | `Teigha.PlottingServices` | `PDF` | `GetMediaBounds` の印刷可能領域0×0を検出し、検証後 `PlotPaperSize` / `PlotPaperMargins` を使用 |
| IJCAD | 2026 PRO | `Itjp.Ijcad.PlottingServices` | `DWG To PDF.pc3` | 同上 |
| BricsCAD | V26 | `Bricscad.PlottingServices`（DB型は `Teigha.DatabaseServices`） | `Print As PDF.pc3` | `CurrentConfig` 未初期化/NREまたは0×0を検出し、同じページ設定値を使用 |

ARES 2027は§0のとおり、2026-07-15に印刷経路の本試験を、2026-07-16に構造SVG書き出しのE2Eを完走した。

## 2. GUI実機結果

各製品で全体（PDF印刷経路）、窓 0,0–200,140（PDF印刷経路）、全図形先選択（構造SVG書き出し）、PETADIAGを実行した。
全ケースでSVG/EMF、PDF印刷経路ではPDF、診断ではstage SVGとdiag.jsonを確認した。NETLOAD時の
`1.0.0+1b41aac3 / Debug` 表示も3製品で確認した。

| 製品 | 全体 | 窓 | 構造SVG書き出し | PETADIAG | 窓SVG実寸 |
|---|---|---|---|---|---|
| ARES | 131506/132025 | 143537 | 144105 | 144310 | 420×294mm |
| IJCAD | 131958 | 133913 | 145209 | 145300 | 418.4125×292.8888mm |
| BricsCAD | 132009 | 134737 | 135259 | 135327 | 418.4125×292.8887mm |

ARES と他2製品の窓差は幅1.59mm・高さ1.11mmで、各PDFデバイスの印刷可能領域差に一致する。
3製品とも最終SVG/EMFが生成され、PDF解析は透過・OCGを含め完走した。通常PETAでは、互換APIの
既知戻り値0×0からページ設定へ切り替える情報表示を出さない。最終寸法が非正なら従来どおり例外にする。

構造SVG書き出し出力はPDFを持たず、PDF印刷経路出力と経路が混同されていない。PETADIAGは各段ファイルを生成した。
クリップボード登録は既存の3製品実機記録でOffice読み戻しまで成立済みで、本セッションでも
EMF生成後のコマンド完了を確認した。

## 3. AutoCAD PDF印刷経路の無回帰

AutoCAD 2026 accoreconsoleで旧 `acabc14` と現行を比較した。

- model/layout × PETA/PETADIAG: SVG、EMF、stage SVG、diag.json は全ケースSHA256一致。
- 窓50,40–150,110、余白5mm、ゼロ面積後A: SVG/EMF 6組がSHA256一致。
- 窓は117×71mm / viewBox `151.5 113 117 71`、余白は127×81mm、全体は420.1583×297.0389mmで一致。
- 中間PDFは旧新でバイト数一致。PDFハッシュは同一ビルドのPETA/PETADIAG間でも生成時刻と辞書順で変化するため、
  バイト比較対象にできない。PDFから得た全派生成物のバイト一致とPDF構造等価で無回帰を判定した。

## 4. ビルド・テスト・衛生

- ARES / IJCAD / BricsCAD: Debugビルド成功、各0警告0エラー。
- 全TFMテスト: net48 269、net8 273、net10 273、合計815件成功。参照方向テストを含む。
- テスト時のNU1900は隔離環境から脆弱性情報サービスへ接続できない警告で、テスト失敗ではない。
- SharedはCAD SDK参照ゼロを維持。3製品とも `CadPeta.Acquire.Pdf` を外殻から参照する。
- GUI終了時に ARES / IJCAD / BricsCAD の残留プロセス0、`FILEDIA` 復元を確認。自動ロード用レジストリは使用せず、既に作成した一時キーも不存在。

エラー境界は、別プロット進行中、PDFデバイス設定失敗、設定検証失敗、PlotEngine段階失敗、PDF未生成を
それぞれ別メッセージで例外化する。実機の正常構成を意図的に破壊するエラー注入は行わず、既存単体テストと
コード境界の検査で確認した。
