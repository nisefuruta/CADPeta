# リリース関門拡充の検証結果（2026-07-19）

PR #5で残っていた互換CAD実機、Officeとクリップボードの競合、配布ZIPの検査を再実行可能な試験へ固定した。
続く同日の追補で、ページ設定pairwiseの実機駆動系と永続sysvar lintも完了した（後述）。
残る関門は実鍵による署名済みZIPの最終検査だけである。

## 互換CADとAutoCADの実機結果

`peta_window_rotation_compat.ps1`をAutoCAD 2026／2027、ARES 2024／2027、IJCAD 2026、BricsCAD V26で実行した。
各製品でPlotRotation 0／90／180／270の100×70 mm窓を照合し、クロップ後の意味的前景差は承認上限0.01%以内だった。
画像の平行移動比較は端を循環させず、有効重複領域だけを比較する。
negative controlは線分欠落を検出する。

`window_contract_compat.ps1`はBricsCAD V26、IJCAD 2026、ARES 2024、ARES 2027でA／G／J／M／T90／T180／T270／T360／U90／U180／U270／U360を実行した。
ネイティブ`-PLOT`がページ設定へ格納した窓は、製品コードを参照しない計測器のWCS→DCS候補と一致した。
ARESが現在UCS成分で返すTARGETとVIEWDIRは、CAD自身の`trans`でWCSへ戻して入力条件と照合した。
ARES 2024が直角ねじれで`(trans p 1 2)`を恒等値として返す製品差は、独立計測器の行列結果で判定した。
PDFの受理は任意の1,024 byte下限ではなく、ヘッダー、`startxref`の範囲、`%%EOF`を検査する。
各実行でFILEDIA、OSMODE、SECURELOAD、TRUSTEDPATHSの復元、入力DWGのSHA-256不変、所有CADプロセスの終了を確認した。

## Officeとクリップボード

`clipboard_contention.ps1`は別プロセスが`OpenClipboard`を保持した状態でCLIを実行した。
CLIの非0終了、保持PIDを含む診断、検証済みEMFの保持、既存クリップボード内容の不変を確認した。
保持解放後の再実行は成功した。

`office_paste_regression.ps1`はExcel、PowerPoint、Wordのファイル挿入とクリップボード貼付を実行した。
寸法の正解はOfficeアプリ間の一致ではなく、EMFヘッダーのFrameから算出した独立値である。
Excelは幅と高さを独立値へ照合した。
PowerPointとWordはページ内へ縮小されるため、縦横比を既存の承認済み許容0.24%で照合した。
この許容値は今回の失敗を通すために拡大していない。
分解後はファイル経路とクリップボード経路で図形3,066個、文字75個が一致した。
所有権は文書作成直後のウィンドウPIDで確定し、終了対象をそのPIDへ限定した。
既存Officeを使う実行は、利用者が保護不要を明示した場合に限り`-AllowExistingOffice`で許可する。

## 配布物

`package_verify.ps1`を追加し、4種類のZIPを展開せずに再度開いて検査した。
ZIP内の相対パス集合と各SHA-256はステージングと一致した。
必須ファイル、TFM、言語ファイル、bundle XMLのModuleName、署名素材を検査する。
パストラバーサル、重複、0 byte、CAD SDK DLL混入を拒否する。
自己試験は内容改変とGUI ZIP欠落を検出した。
`dist/build_dist.ps1 -Only all -Version 1.0.0 -AllowDirty`はAutoCAD bundle、CLI、GUI、互換CAD bundleの4 ZIPを生成し、検査を通過した。
署名鍵と証明書を用いる実署名は実行していない。

## ページ設定pairwiseの実機駆動（同日追補）

`page_setup_pairwise_matrix.ps1`を新設し、12ケースをAutoCAD 2026のaccoreconsole実機で完走した。
計測器`CPSETPAIRWISE`がケースの因子（空間・単位・尺度・回転・用紙探索）を対象レイアウトへ焼き、対比側レイアウトはA2固定として空間の取り違えを寸法で識別する。
用紙探索の水準は実在デバイスの列挙から決定的に解決した。
異名同寸法は実在プリンタ（Canon iPF680）のA3（`ns0000:BORDERLESS_ISOA3`、297×420mm）、該当寸法なしは同プリンタの13×22インチ（329×558mm）が選ばれ、作為的なモックを使っていない。

判定は独立オラクルで行った。
PDFのMediaBoxと`/Rotate`を独立パーサで読み、A3系9ケースは420.1583×297.0389mm、フォールバック3ケースはISO A4縦209.9028×297.0389mmへ、期待用紙クラスと1.5mm以内で一致した。
SVG寸法は全12ケースで「MediaBox×`/Rotate`の表示寸法」と0.5mm以内で一致した。
回転による向きの正規化は製品・ドライバ差があるため、その意味論は`peta_window_rotation_compat.ps1`の描画一致が担い、本駆動系は用紙寸法クラスとSVG＝PDF表示の整合を担う。
用紙縮退（A4フォールバック）の診断は該当寸法なしの3ケースだけが報告し、「既定用紙で進める」は全ケースで出なかった。
各ケースでLayout DBObjectの前後スナップショット完全一致、レジストリ復元検証、入力DWG不変、残留CADプロセスなしを確認した。

## 永続sysvar lintとハーネス移行（同日追補）

`lint/sysvar_guard_lint.ps1`を新設した。
tests/tools配下のPowerShellがFILEDIA・OSMODE・SECURELOAD・TRUSTEDPATHS・PLOTTRANSPARENCYOVERRIDEを変更する場合、CadRegGuardのdot-sourceを要求し、互換CAD GUIハーネスは例外リストへ根拠付きで登録したうえで読み戻し検証付き復元の実装まで機械検査する。
自己試験（裸変更の検出、ガード付きの合格、読取のみの対象外、例外リスト空振りの検出）はWindows PowerShell 5.1とPowerShell 7の両方で成功した。
裸変更だったAutoCAD系ハーネス15本をCadRegGuardの実行前スナップショット→復元検証で包み、互換CAD GUI 2本（gradient_compat_e2e、peta_option_smoke_compat）の復元を読み戻し検証付きへ強化した。
全走査は違反0件だった。

## CAD不要試験と未完範囲

全solutionのビルドは警告0、エラー0だった。
xUnitはCore系net48が552件、net8が556件、net10が560件、Appが12件、GUIが15件すべて成功した。
`page_setup_pairwise_selftest.ps1`は5因子、10因子対、77水準対を12ケースで覆うことをWindows PowerShell 5.1とPowerShell 7の両方で確認した。
`package_verify.ps1 -SelfTest`、`cad_reg_guard_selftest.ps1`、`measure_shift_selftest.py`も成功した。
`dotnet build CadPeta.sln`後の`dist/build_dist.ps1 -Only all -Version 1.0.0 -AllowDirty`は4ZIPを生成し、圧縮後検査を通過した。
署名済みZIPの実鍵による最終検査だけが未実施である。
