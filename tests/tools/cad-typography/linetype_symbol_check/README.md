# 複雑線種の記号（シェイプ・文字）展開の検証ハーネス

2026-07-10 の実装検証で使った再現一式。すべて accoreconsole 2026（PowerShell から起動、
stdout はファイルへ捕捉、脚本は `_.QUIT _Y` 終端・FILEDIA を書かない）で実行した。
作業ディレクトリは `%TEMP%\cadpeta_p3b_ltsym\` に隔離した（.scr 内の絶対パスはそのまま）。
.lsp は UTF-8 BOM 付き、.scr は ASCII/CP932。
.scr は SECURELOAD を変えず、読み込む .lsp と DLL のフォルダだけを TRUSTEDPATHS へ一時追加して、読み込んだ後に元の値へ戻す。
NETLOAD する DLL は `pwsh -File tests/tools/build_e2e.ps1 -Products Acad` が作る Release の出力（配布物と同じビルド）。

## 意味論の実測（entget プローブ）

- `probe_ltype.lsp` — acadiso.lin の記号線種7種（GAS_LINE・HOT_WATER_SUPPLY・FENCELINE1/2・
  TRACKS・BATTING・ZIGZAG）をロードし LTYPE テーブルの生 DXF グループを書き出す
- `test_symbols.lin` + `probe_test_symbols.lsp` — R=/A=/U= の回転指定を1軸ずつ切り分けた
  自作線種で group 74 のビット意味を確定する

実測で確定した LTYPE ダッシュ要素の DXF 意味論（AutoCAD 2026 実機）:

| グループ | 意味 |
|---|---|
| 49 | ダッシュ長（正=実線、負=間隙、0=ドットまたは記号キャリア） |
| 74 | 記号種別・回転種別のビット束: bit1(2)=文字、bit2(4)=シェイプ、bit0(1)=A=（絶対回転、C# の ShapeIsUcsOrientedAt）、bit3(8)=U=（正立、C# の ShapeIsUprightAt）。R=（既定・相対回転）はビットを立てない |
| 75 | シェイプ番号（文字記号では 0） |
| 340 | シェイプ/文字スタイル（TEXTSTYLE レコード）への参照 |
| 46 | 尺度 S=（シェイプ設計単位への直接倍率。above 正規化ではない） |
| 50 | 回転角（ラジアン。R=/A=/U= のどれで解釈するかは 74 のビットが決める） |
| 44 | X オフセット（接線方向、線種尺度でスケールされる） |
| 45 | Y オフセット（左法線方向、同上） |

実測値の例: TESTSHAPE_R（r=45°）は 74=4・50=0.785398、TESTSHAPE_A（a=45°）は 74=5、
TESTSHAPE_U（u=45°）は 74=12、TESTTEXT_BASE は 74=2・(9 . "T")。

## 検証チャートと抽出

- `gen_test_chart.lsp` / `gen_test_chart.scr` — acadiso.lin の記号線種を直線・ポリライン
  （Plinegen 両状態）・円・円弧・ブロック内（挿入尺度1の回転挿入＝E7、挿入尺度2の
  等方挿入＝E8）へ適用し、LTSCALE=2 と個別線種尺度（CELTSCALE=2）の変化を含む DWG を生成する。
  E8 はブロック挿入尺度2で破線パターン・記号寸法・記号出現位置がブロック拡大率と二重に
  スケールされる欠陥（2026-07-10 是正）の回帰ケース。gen_block_scale2.lsp は同欠陥の
  単体再現に使った最小構成で、E8 と同じ BATTING・尺度2・回転なしの構成
- `gen_zero_case.lsp` / `gen_zero_case.scr` — 記号が長さ0のダッシュに乗る偽ドット検証専用
  （test_symbols.lin の TESTSHAPE_ZERO）
- `run_peta_extract.ps1` — 生成DWGに対しNETLOAD→PETADATA→ALLで構造SVG書き出しを実行する
  （-Repo でワークツリー切替、-Ver で 2024/2026/2027 切替）

## 構造SVG書き出し／PDF印刷結果の突き合わせ

- `run_petadata_scale2.scr` — PETADATAを尺度1:2で実行（PETADATA→ALL→尺度2）
- `plot_print_reference.scr` — PDF印刷経路: -PLOT（Model・窓 (-1,-6)..(481,192)・尺度1:2・A3・CP932）→ PDF。
  PDF は worktree の CLI で SVG へ変換する
- `compare_data_print.ps1` — CAD(0,0)を同一ピクセルへ位置合わせしてChromeヘッドレス150dpiで
  PNG 化し、render_diff.py で並置・差分・異画素率を出す

突き合わせで確定した意味論（2026-07-10 実測）:

- **記号の基準点は「記号を運ぶダッシュ要素の終端」**。GAS_LINE の B 実測文字位置
  21.246 CAD mm = (12.7+5.08)×2（運搬要素終端）＋X オフセット(−5.08) − 端部整列シフト
  (9.23) が代数的に厳密一致。要素始端基準だと運搬要素長ぶん（この例で 10.16）ずれる
- **AutoCAD のプロットは破線パターンを端部整列する**（直線: 始端・終端が実線片になる
  よう位相シフト＋端の実線片を短縮。閉曲線: 整数周期へ伸長）。記号もパターンに乗って
  同じシフトを受ける。構造SVG書き出しのstroke-dasharrayは位相0固定（承認済み設計）のため、
  この整列は破線にも記号にも再現されない（記号↔破線の相対位置は B と厳密一致する）

## エラー経路

- `gen_missing_shape_font.lsp` / `run_missing_shape_font.scr` — 正常ロード済み線種の
  シェイプスタイル（群340 が指す TEXTSTYLE）のフォント名を entmod で実在しない SHX へ
  書き換え、「線種記号の展開省略（シェイプフォントを解決できず）」ログと破線のみ出力の
  フォールバックを実機確認する。
  注意: -LINETYPE の L（ロード）はフォント不在の線種定義を読み込み時に拒否するため、
  不在ケースは必ず「正常ロード→entmod で事後破壊」の手順で作る（entmake での LTYPE 直接
  構築も 340 の実 ObjectId が必要で失敗する。実測）
