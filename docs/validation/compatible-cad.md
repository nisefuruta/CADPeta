# 検証記録: 互換CAD 印刷経路

## 画面の接続が切れているときの ARES（2026-10-25）

利用者のセッションがリモート デスクトップの切断の状態のとき、ARES は画面を Standard VGA の扱いにし、起動の途中で「ドライバが対応していません」（ビデオ カード ドライバ … Standard VGA: Insufficent support for OpenGL …）を出して、閉じるまで COM の呼び出しに応答しなかった。
ARES 2023 のダイアログ（Qt 5）は UI オートメーションに [無視] のボタンを見せ、ARES 2027 のダイアログ（Qt 6.8）は題名と [閉じる] しか見せない。どちらも既定のボタンは [無視] で、2027 はダイアログのウィンドウへ Enter を送ると閉じて ARES が動き続けた。
互換 CAD の試験は、自分が起動した製品のこのダイアログに [無視] を押す（ボタンが見えなければ Enter を送る）。押した内容は profile.xml に残っても、試験の後で元のバイト列へ戻す。
この状態で、ARES 2020・2023 の 8 本は合格した。ARES 2027 は、ダイアログの後に KERNELBASE.dll のアクセス違反（0xc0000005）で起動の途中で終了し（23:22 から 7 回）、試験は 120 秒待ってから原因の分からない表示で止まっていた。製品のプロセスが無くなったら、待たずに「起動の途中で終了した」と止めるようにした。
画面に接続している間（同日の 21 時台）は、ARES 2027 の 5 本は合格していた。この節の変更（ダイアログの扱い、復元の保留、OSMODE の関数）を ARES 2027 で確かめるのは、画面に接続した後に行う。
2026-10-31 にも、セッションが 00:30 に切断された後、ARES 2027 は起動のたびに KERNELBASE.dll のアクセス違反（オフセット 0xc408a）で終了した（00:50 から 4 回）。切断の前の 00:15・00:17 は起動できた。
デバッガの下で起動すると、同じ状態で「ドライバが対応していません」（Matched blacklist id(8) : Standard VGA）を出して止まった。ARES 2020・2023・2024 は、切断中も試験に合格した（ARES 2024 は起動の 3 秒後に同じオフセットのアクセス違反をイベント ログに残し、動き続けた）。
CADPeta のプラグインを読み込む前に起きるので、試験の側で、ARES 2027 を起動する前にセッションの接続の状態（WTSConnectState）を確かめ、切断なら環境を変えずに理由を書いて止めるようにした（`tests/tools/common/compat_cad_com.ps1` の `Assert-CompatLaunchReady`）。
ARES 2023 は、終了するときに Qt5WebEngineCore.dll でアクセス違反を起こすことがあった（20:56・21:10・22:09。試験は合格し、クラッシュの報告のウィンドウが残った）。

## 互換 CAD の COM の試験の SECURELOAD（2026-10-25）

互換 CAD の COM の試験 4 本は、SECURELOAD を 0 にして終了の前に戻していた。この処理を取り除いた。
ARES では、SECURELOAD を 0 にしても確認の画面は出ていた（2026-07-21 の ARES 2020 の実測）。画面が出なかったのは、DLL を `%AppData%\ARES Standard Edition x64` の下へコピーしていたためだった。
ほかの製品では、DLL のフォルダを TRUSTEDPATHS へ一時的に追加し、終了の前に戻して読み戻す（`common/compat_cad_com.ps1` の Add-CadTrustedPluginFolder）。
SECURELOAD を変えない形で、ARES 2020・2023・2027 の `gradient_compat_e2e.ps1`（Print と DataExport）、ARES 2027 の `peta_option_smoke_compat.ps1`・`peta_window_rotation_compat.ps1`・`window_contract_compat.ps1` が合格した。
AutoCAD 2026 の `peta_window_rotation_compat.ps1` も合格し、TRUSTEDPATHS は元の値へ戻って読み戻しが一致した。IJCAD と BricsCAD はこの PC に無く、確かめていない。

2026-10-26 に、TRUSTEDPATHS も変えない形にした（一時的に追加して戻す形も使わない）。AutoCAD では、DLL のフォルダを信頼済みのフォルダ
（`C:\Tools\ACAD\dotnet`）の直下へコピーし、終了後にコピーしたものだけを削除する。ARES は今までどおり、ARES が信頼するフォルダへコピーする。
IJCAD と BricsCAD では、DLL のフォルダが TRUSTEDPATHS にあることを読むだけで確かめ（`Assert-CadPluginFolderTrusted`）、無ければ何も変えずに止まる。

ARES 2020・2023 の `peta_option_smoke_compat.ps1` と `peta_window_rotation_compat.ps1` は、変更の前も後も、プラグインが読み込まれないとして不合格になった。
ARES 2020 のプロファイル（`20.3.4078\Profiles\__unnamed profile__\profile.xml`）のスタートアップに、インストール済みの CADPeta を読み込む `CADPeta\2020\load_cadpeta.lsp` が登録されている（2023 も同じ形と見ている）。
起動時にインストール済みの DLL が読み込まれるので、試験が読み込む DLL は使われない。
`gradient_compat_e2e.ps1` は読み込まれた DLL の場所を照合しないので、ARES 2020・2023 の合格は、インストール済みの DLL を試験した結果の可能性がある。
試験の間だけスタートアップの登録を外して戻すようにした（同日）。ARES は終了時に profile.xml を書き直し、最後に読み込んだ DLL のフォルダ（`lastapploadfolder`）に試験が削除したフォルダを残していたので、全部の版で profile.xml の全体を保存して、終了後に元のバイト列へ戻す。
13 回の実行の前後で、ARES 2020・2023・2024・2027 の profile.xml のハッシュは一致した。それより前の実行で ARES 2027 の profile.xml に書かれた `lastapploadfolder` は、元の値を記録していないので戻していない。
ビルドした DLL が読み込まれるようになると、ARES 2020・2023 の `peta_option_smoke_compat.ps1` のケース b（余白 5mm と窓）と `peta_window_rotation_compat.ps1` の 4 ケースで、窓の出力の寸法が期待（110×80mm、100×70mm）と違った（260×185mm、250×175mm など）。
PETA は既定の用紙へ進み、用紙合わせの尺度で窓を拡大していた。理由を表示に含めるように直すと、「PDF の用紙 0 件に同じ名前と寸法の用紙が無い」だった。
ARES 2020 の .NET の API は、出力先を `PDF` にした後の用紙の一覧を 0 件で返す。COM の `GetCanonicalMediaNames` では、同じ出力先に `ISO_full_bleed_A3_(420.00_x_297.00_MM)` を含む 101 件がある。
一覧が空のときは、ページ設定の用紙名をそのまま設定するように直した（docs/02_設計書.md の 4.4）。
直した後、ARES 2020・2023・2027 の 13 回（グラデーションの Print と DataExport、smoke、rotation、2027 の window_contract）がすべて合格した。

## ARES 2027 の無人E2Eと選択絞り込みの範囲の照合（2026-10-25）

ARES の COM 駆動の E2E は、読み込む DLL を `%AppData%\ARES Standard Edition x64` の下の実行ごとのフォルダへコピーして読み込む。
Graebert のヘルプ「Controlling Safety Options Against Malicious Files」は、使っているソフトの `%AppData%\<ソフト名>` を暗黙に信頼する場所として挙げている。
リポジトリの bin の DLL を直接読むと、ARES 2027 は「信頼できるフォルダにありません」の確認の画面を毎回出した。
コピーしたフォルダから読むと画面は出ず、`peta_option_smoke_compat.ps1`、`peta_window_rotation_compat.ps1`、`window_contract_compat.ps1`、`gradient_compat_e2e.ps1 -Mode DataExport` が人の操作なしで完了した。

ARES 2024 と 2027 には、インストール済みの CADPeta を起動時に読み込む登録（`HKCU\Software\Graebert GmbH\ARES Standard <版>\Applications\CADPeta`）があった。
登録があると、試験が後から読み込む DLL は使われない。
2026-10-24 と 2026-10-25 の修正前の ARES の結果は、2026-07-21 にインストールした DLL を試験したものだった。
E2E は登録を書き出して外し、終了時に戻して一致を確かめる。
読み込まれた DLL の場所は PromptTrace の `Plugin.Load` で照合し、試験の前後で書き出した `Applications` のキーが一致することも確かめた。

ARES 2027 で、レイアウトで PETA を実行した後にモデル空間へ切り替えて O で選ぶと、PETA は側DBが元の選択と一致しないとして中止した。
差は X の最大値で、元図面 390.0665、側DB 390 だった。
原因は幅 0.6 のポリラインで、元図面の `GeometricExtents` はレイアウトでの印刷の後だけ線の幅を含んで 337.905〜390.066 になり、表示していない側DBでは 338〜390 だった。
スプラインの範囲も同じ条件で変わった。
モデル空間で保存した図面から始めると、元図面の範囲も 338〜390 で、O は成功した。
元図面と側DBを比べる範囲には、ポリラインの頂点とスプラインの制御点を使うように直した。
修正前は既定実行のケース o が不合格、修正後は既定実行の 8 ケース（出力オプション 2、O の選択経路 5、画層 0 と現在の画層の図形の後選択 1）がすべて合格した。

`gradient_compat_e2e.ps1 -Mode DataExport` の構造SVGは、2026-07-16 の記録と同じ SHA-256（`ce1594b80251bcf6114937f55193e65673c1c7e72a54d40a8136d029665cdff1`）だった。
キー入力（SendKeys）で応答を送っていた間は、「_ALL」が「\all」としてコマンドラインに届いて選択が無効になり、成果物が作られずに 300 秒で打ち切りになることがあった（6 回のうち 4 回。前面のウィンドウと日本語入力・Shift の状態で届く文字が変わる）。
応答を LISP の `(command ...)` で 1 回の SendCommand にまとめて送る方式に変え、3 回続けて合格し、構造SVG は 3 回とも同じ SHA-256 だった。
既定の `-Mode Print` は、ARES 2027 の PDF でグラデーションが画像 9 件・パス 10 件になり、パス 3000 件以上という期待と合わなかった（2026-10-25）。
2026-07-15 の記録では同じ試験がパス 3050 件で合格していたが、ARES 2027 はそのあと 2026-07-17 にインストールし直されていた（今は 27.1.1.2150、PDF は ODA PDF Export 26.8 が作る）。
2026-07-15 のコミット（3d894b7）のプラグインを今の ARES 2027 で実行しても画像 9 件になったので、変わったのは ARES の PDF の印刷で、CADPeta ではない。
O を使わずに全体を印刷しても画像 9 件だった。
PETA は印刷と同じ仕上がりを出すので、ARES 2027 の期待値を画像 9 件に変え、合格した。
ARES 2020・2023 の期待（パス 3000 件以上）は、対応を加えた 2026-07-21 に 2024 の実測を写したもので、実機で確かめた記録が無かった。2026-10-25 に実行すると、どちらも画像 9 件・パス 1 件だったので、期待値を画像 9 件に変え、合格した（PETADATA の経路も合格）。2024 はライセンスの制約で起動しないので、パス 3000 件以上の期待のままで、今の版では確かめていない。
互換 CAD 版のインストールとアンインストール（install.bat・uninstall.bat、`-AresYear 2027`）を ARES 2027 の実機で確かめた（2026-10-25）。
インストール済みの CADPeta をバックアップしてから行い、最後に元のインストールへ戻して全ファイルのハッシュと登録の一致を確かめた。
インストールは、前の版にしか無いファイルを残さずに 2027 のフォルダを配布物と同じ内容へ入れ替え、2020・2023・2024 のフォルダを変えなかった。
ARES 2027 は開始ページの状態では登録したプラグインを読み込まず、最初の図面を開いたときにインストール先の DLL を読み込んだ。
ARES の起動中のアンインストールは、DLL が使用中であることを表示し、登録もフォルダも変えずに失敗で終わった。
ARES の終了後のアンインストールは、2027・2027.new・2027.old のフォルダと登録を削除し、ほかの年式を変えなかった。
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
コーナー指定プロンプトが出た。プラグインの読み込みと PETA の登録が成立している。（当時の出力の引用。Fileoutput（F）は 2026-10-28 に削除したので、今のプロンプトには出ない）

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
