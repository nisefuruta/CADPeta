# CADPeta設計判断の検証根拠

## 採用判断

| 項目 | 決定 | 根拠と回帰手段 |
|---|---|---|
| 中間表現 | 制約付きSVG相当IRを出力形式共通の基準とする | 総合チャートでパス、文字、画像、クリップ、描画順をPDF正解レンダと比較 |
| PDF解析 | 本実装はPdfPig。PDFBox、pikepdf、pdfiumは検証補助 | オブジェクト抽出の相互照合とレンダ比較。ツールは`tests/tools/pdf-emf/` |
| EMF | クラシックGDIレコードを直書き | Officeでの実寸、分解、文字回収を最小標本と総合チャートで確認 |
| EMF座標 | 0.01mm論理単位、96dpi固定メタデータ、絶対座標レコード | 複数表示DPIとExcel/PowerPointで寸法を比較。現在位置依存レコードはOffice分解で崩れるため不採用 |
| クリップ | IRでは保持し、EMFでは描画ジオメトリへ事前適用 | Officeの図形変換がEMFクリップ状態を保持しないことを実測 |
| 画像 | PNGをIRへ埋込み、EMFでは背景合成済み24bpp BGR DIB | アルファ合成、ファイルサイズ、Office貼付を回帰 |
| PETA貼付 | 範囲（全体・窓）をCADの正規PDF印刷へ単一化 | AutoCADの尺度・用紙・OCG回帰、IJCAD/BricsCAD実GUI E2E |
| PETADATA書き出し | CAD DBから選択を直接取得し構造SVGだけを保存 | 4製品ビルド、AutoCAD実機、構造・色・実寸回帰 |
| 選択絞り込み | 範囲内の出力対象を選択図形だけに絞る。通常図形は側DBへ複製し、ビューポートは元DBの未確定トランザクションで非選択図形を隠す | 元DWGハッシュ、モデル／ペーパー空間の実寸、OLE実体、ビューポート表示内容、CTB/STB、部分出力の確認契約 |
| SHX・複雑線種 | 自前解釈器で輪郭化し、配置計算を共有 | AutoCAD正解PDF、DWG、固定配置表との画素・座標比較 |
| 互換CAD | 共通プラグイン層＋製品別の薄いAPIアダプタ | ARES、IJCAD、BricsCADのビルド行列と実機E2E |

## 恒久的な注意事項

- AutoCADの対話プロットとconsoleは同一とは限らない。正解値を追加するときは少なくとも1ケースをGUIでも併測する。
- CADの`SendCommand`はプロンプト入力待ちの間に呼出元をブロックし得る。必要な応答を同一バッチへ含める。
- CAD終了前に未保存図面を`Close(false)`しないと保存確認モーダルが残る。
- ACI 7は表示文脈で白/黒が変わり、グラデーションのTrueColorを上書きし得る。正解チャートは色ストップのACI群を省略する。
- EMFは真の半透明を保持しない。既定白または指定背景色と事前合成し、非可逆処理を診断へ記録する。

数値の正解値と再生成方法は `tests/assets/`、実行手順は `tests/tools/README.md` を基準とする。

## AutoCAD 2026の選択印刷回帰（2026-07-15）

`tests/tools/regression/selection_plot_matrix.ps1` をAutoCAD 2026の `accoreconsole` で実行した。
PETAは `Model`、`Paper`、`PaperMixed`、`LayoutModel`、`Model -RequireLayers` の5シナリオ、PETADATAは `Model` と `Paper` の2シナリオが成功した。
`Paper` は `PETA_A3` レイアウト上の20×10mm矩形を尺度1:1で選択し、ログの要求寸法20.0×10.0mm、PDF／SVG／EMF各1件、入力DWGのSHA-256不変を確認した。
`PaperMixed` は当時の仕様として、同じ矩形とビューポートを同時に選択し、`Viewport` の除外記録、部分出力確認への同意、成果物生成を確認した。
`LayoutModel` はレイアウト内MSPACEからモデル図形を選択し、現在のペーパーレイアウトではなくモデル空間のページ設定で選択印刷が完了することを確認した。

同日、`-ForceCloneFallback`（環境変数 `CADPETA_FORCE_SELECTION_CLONE_FALLBACK=1` で Wblock を飛ばす故障注入）で
オブジェクト複製フォールバックを実走させた。`Model` と `Paper` の両シナリオでフォールバック経路のログを確認のうえ
成果物・要求寸法・入力DWG不変の全照合に合格した。ARES 2024 でも同じ環境変数で `gradient_compat_e2e.ps1` を流し、
フォールバック経路で通常経路と同一バイトの EMF（133492 bytes）を得た。
STB 図面（`STB_RECT.dwg`、PSTYLEMODE=0）は通常経路（Wblock が STB モードを引き継ぐ）で成功し、
フォールバック併用時は側DB既定（CTB）とのモード不一致により成果物なしで中止することを固定した（既知の制限）。
この記録はconsoleプロット（ARESはGUI）の結果であり、GUI対話プロットの同等性は別途確認する。

## AutoCAD 2026のレイアウトOLE・ビューポート選択回帰（2026-07-19）

`samples/009-010_入間川橋_交通規制図(4)～(5).dwg`の`交通規制図（4）`レイアウトで、OLE単体の先選択と後選択をAutoCAD GUIから実行した。
両経路のPDFは701,468 bytes、SVGは7,363,151 bytes、EMFは8,389,372 bytesで一致し、選択完了マーカーと描画内容を確認した。
Core ConsoleはOLEを描画しないため、OLEの描画正解にはGUI成果物を用いる。

同じレイアウトの通常ビューポート単体は、先選択と後選択を別セッションで実行した。
両経路のSVGは7,338,824 bytes、`path`は32,286件、EMFは5,067,012 bytes、PDFは653,676 bytesで一致した。
側DBへビューポートを複製する旧試行はSVGが459 bytes、`path`が2件で、クリップと枠しか出力しなかった。
この負の対照に基づき、ビューポート回帰は成果物の有無だけでなく、SVGが2件を超える描画パスを持つことを必須とする。
元図面の一時非表示は未確定トランザクションを明示的に破棄し、変更した各図形の可視状態を破棄後に再読して復元を検査する。
`DBMOD`全値は、プロット中に一時変更して元へ戻す`BACKGROUNDPLOT`でも変更bitが残るため、可視状態の復元判定には使わない。

汎用ハーネス`tests/tools/regression/object_keyword_probe.ps1`は、任意の入力DWG、レイアウト、`OLE2FRAME`または`VIEWPORT`を実行時引数で受ける。
サンプル固有のパスとレイアウト名はハーネスへ固定しない。
既定のモデル空間12ケースも全件合格し、既存の選択経路に回帰がないことを確認した。

## AutoCAD 2026の選択絞り込み回帰（2026-07-16）

オブジェクト選択を「範囲（全体・窓）内の出力対象絞り込み」とする現仕様（FR-1）の実測記録。
`tests/tools/regression/selection_plot_matrix.ps1` の全11シナリオ（PETA の Model／Paper／
PaperMixed／LayoutModel／レイヤ(L)／fallback 2種／STB 2種、PETADATA の Model／Paper、
PETADATADIAG）が AutoCAD 2026 accoreconsole `/l ja-JP` で合格した。照合内容は従来同様に
成果物数、入力DWGのSHA-256不変、除外記録と部分出力確認、フォールバック実走ログ、
STB×フォールバックのモード不一致中止で、これに絞り込みサマリ（選択件数と対象件数）を加えた。

同値検証として、`TESTCHART2.dwg` の全図形を絞り込み指定した出力と絞り込みなしの出力を、
全体（Enter）と窓（0,0–200,140）の両方で突き合わせた。SVG の要素数は一致（全体3069行）し、
座標差は0.03mm級（側DBと元図面の図形範囲の丸め差による用紙合わせ尺度の微差）に収まる。
150dpi PNG の画素比較は窓=差分3画素（0.0001%）、全体=1.2%（全面のサブピクセルずれによる
線縁のアンチエイリアス画素）で、目視でも同一である。窓では境界ぎりぎりの要素1件の採否が
尺度微差で割れ得る（窓縁精度0.2mmの範囲内）。

ペーパー空間の絞り込みは、レイアウト全面（A3実寸）の範囲に選択図形が用紙上の実位置・
ページ設定尺度で載ることをSVG座標で確認した（20×10mm矩形が用紙座標(0,0)-(20,10)、1:1）。
実装制約として、レイアウトのプロット検証（PlotInfoValidator）は対象レイアウトが
そのDBのカレントであることを要求する（eLayoutNotCurrent を実測）。側DBでは対象の
レイアウトをカレント化してから検証する。

## 窓プロットの座標契約と用紙の対応付け（2026-07-16）

再現手順は `tests/tools/regression/window_contract/`（計測器 `WindowContractProbe`、
マトリクス `window_contract_matrix.ps1`、オラクル `window_contract_oracle.ps1`）。

### 窓の座標契約は DCS（AutoCAD 2026 実機で確定）

`PlotSettingsValidator.SetPlotWindowArea` へ渡す矩形は、pick 2点（現在UCS）をビューの DCS へ
写した値の min/max である。三つの独立した観測が一致する。

- CAD 自身の `-PLOT` に窓の2点を与え、ページ設定へ保存させると、DXF 群 48/49/140/141 が
  AutoLISP `trans` の DCS 値と厳密一致した。対照・目標点単独・UCS原点移動・UCS回転・
  ビューのねじれ・傾斜UCS・3Dビューの全ケースで成立する。
- 同一図面で窓の値だけを変えて発行すると、DCS へ写した値だけが、目標点0の対照と画素完全一致の
  ページを出した（生値・UCS→WCS はいずれも別領域）。
- 実物図面（モデル空間・UCS が Z 軸まわり約80度回転・目標点非ゼロ）で、`-PLOT` の格納値
  `(-348.996488,-5.454480)-(-313.996488,29.545520)` と PETA の送出値が一致する。

DCS はビューの向き（目標点・視線方向・ねじれ）で決まり、ズーム・パンでは変わらない
（実測: ZOOM 中心／倍率／範囲・PAN のいずれでも固定点の DCS 値が不変）。したがって窓の2点を
pick する間に表示を変えても窓は壊れない。実装は2点を pick し終えてから座標系を1回だけ読む。

視線が UCS 平面と平行な向きでは pick 点が DCS 上の1直線へ潰れ、CAD 自身も
`eInvalidWindowArea` で窓プロットを拒否する。この向きは誤った成果物を出さず中止する。

`ViewTableRecord` の Target・ViewDirection は WCS、システム変数 TARGET は UCS である
（実測: WCS 目標点 (60,50) に対しシステム変数は (78,4)＝その UCS 表現）。

互換CADは GUI を COM で駆動する `window_contract_compat.ps1` で同じオラクルを実行し、
次のとおり確定した（2026-07-17。判定は -PLOT 格納値の数値一致と、オラクル PDF と候補 PDF の
画素比較の両方）。

- **BricsCAD V26 = DCS**。格納値が UCS→DCS 値と厳密一致し、DCS 候補の PDF が全ケース画素完全一致。
- **IJCAD 2026 = DCS**。格納値が厳密一致。DCS 候補だけがオラクルと同内容
  （残差は描線の丸め差 0.63% 以下・平行移動なし。全候補同一窓の対照ケースでも同じ残差が出る）。
- **ARES 2024 = DCS**。格納値が計測器（view 読み＋公式行列）の DCS と厳密一致し、DCS 候補の
  PDF が全ケース画素完全一致（0 差分）。生値・UCS→WCS は別領域。
- **ARES 2027 = DCS**。格納値が全ケースで他製品と同一の DCS 値に厳密一致
  （G: 43.8,21,83,136.6 / J: -0.591968,64.028506,6.090205,185.911026 / M: -43.840620,…）。

ARES の実測で判明した製品差（計測と実装の両方を誤らせるため記録する）:

- **ARES 2027 の `GetCurrentView().Target` はビュー中心を畳み込んだ値を返し、真の目標点に
  ならない**（設定 (60,50)・中心 (40,25)・ねじれ 36.87° で (107,46)＝Target+R(−twist)·中心 が
  返る。2024 は設定値のまま）。この汚染値で DCS を組むと窓が別領域になり、
  「ARES 2027 で DCS を送ると空になる」（2026-07-13 観測)を再現する。当該観測は契約の反証では
  なく、この読み取りの版差が原因だった。
- ARES のシステム変数 TARGET は 2024・2027 とも **WCS の真値**を返す（AutoCAD・BricsCAD・
  IJCAD の「UCS 表現」と違う。回転 UCS のケース G で UCS 表現 (6.6,113.8) でなく WCS (60,50)
  を実測）。したがって ARES ポートの目標点はここから読む（CadEditorPort.ReadPickFrame の ARES の分岐）。
- ARES の AutoLISP `(trans p 1 2)` は、ねじれ（VIEWTWIST≠0）付きビューで DCS でなく WCS を
  そのまま返す（ねじれ 0 なら正しい DCS）。DCS の一次情報には使えない。-PLOT の格納値は正しい。
- ARES の `Editor.GetCorner` はキーボード入力の座標を基点からの相対と解釈する
  （基点 (50,40) に "150,110" と打つと (200,150) が返る。`GetPoint` は絶対。マウスは絶対）。
- ARES 2027 は単独起動だとメインウィンドウ非表示のままスピンし ROT 登録されないことがある。
  2つ目のプロセスを起動すると1つ目が表示・ROT 登録へ進む（2起動診断で実測）。両ハーネスは
  一定時間 ROT 未登録なら2つ目を起動して促し、捨てプロセスは所有として終了時に停止する。

### 用紙はデバイス間で名前ではなく寸法で対応付ける

用紙の正式名はデバイス固有で、デバイスを上書きすると一致しない。実測: レイアウトの `A3`
（DocuWorks Printer の名前）と `USER9` はいずれも `AutoCAD PDF (High Quality Print).pc3` に
存在せず、`SetCanonicalMediaName` が `eInvalidInput` で失敗する。一方その pc3 は 93 種の用紙を
持ち、同寸法の `ISO_A3_(297.00_x_420.00_MM)` を実際に備えている。

名前で引けないと既定用紙（ANSI A・279.4×215.9mm）へ落ち、同時に用紙単位が Inches へ変わる。
その結果レイアウトの 1:1（mm/単位）が 25.4 倍の意味に化け、窓が印刷可能領域に収まらず用紙合わせへ
強制的に切り替わる。実測: レイアウトで 100×70mm を指定した窓が 259.86×181.9mm で出力された。

したがって用紙は寸法で対応付け（判定は `CadPeta.Plugin.Shared.PaperMediaMatcher`）、
用紙単位と尺度をページ設定の値へ復元する。是正後、同じ指定が 100×70mm ちょうどで出力される。

`PlotConfig.GetMediaBounds` はインチ名の用紙でも mm を返す（実測: ANSI_A → 279.4×215.9mm）。
`PlotSettings.PlotPaperSize` も用紙単位に依らず mm である。

### プロット回転は製品でドライバ挙動が違い、180/270 を 0/90 へ正規化する

同じ窓を PlotRotation 0/90/180/270 で発行し、クロップ後の内容が回転間で一致するかを機械判定した
（`tests/tools/regression/peta_window_rotation_compat.ps1`。EMF バイト一致を第一とし、不一致は GDI で
描画して平行移動 0.2mm 以内・残差 1% 未満を要求）。用紙の寸法対応付けは全製品・全回転で成立し、
出力 SVG は 100×70mm ちょうどになる。回転の像の向きは製品で分かれた。

- **AutoCAD・BricsCAD は全回転で像が正立**に描かれ、クロップは回転数だけの対応で足りる。
- **ARES・IJCAD（Teigha 系ドライバ）は 180 度のとき像を用紙上で 180 度回したまま描く**
  （実測: 180/270 度の PDF が回転0の像を点対称にし、クロップが平行移動では一致しなかった。
  0/90 度は一致）。90 度は縦横が入れ替わった用紙へ正立で描かれる。

クロップは「発行 PDF の表示ページで像が正立」を前提とするため、ARES・IJCAD のポートは発行前に
用紙の向きが同じで上下反転だけが違う 180→0・270→90 へ写す（`NormalizePlotRotation`。出力は窓の
中身の貼付画像であり、上下反転を消しても窓指定の意味は変わらない）。是正後、両製品とも全回転で
クロップ内容が一致する。BricsCAD は正規化なしで一致するため写像しない。

### ペーパー空間判定の CVPORT は 4 製品で同一（=1）

窓 pick 時にペーパー空間へ切り替える判定に使う `PaperSpaceViewportNumber` は、AutoCAD の固定
割当てを踏襲していた。ARES 2024・IJCAD 2026・BricsCAD V26 の実機で CVPORT を読み、真のペーパー
空間で 1・MSPACE でビューポートに入ると 2・PSPACE で 1 へ戻ることを確認した（2026-07-17）。
4 製品とも同一で、各 `<製品>PluginDefaults.PaperSpaceViewportNumber` の根拠コメントへ反映した。

### 未解明: Office の「図形に変換」での図形移動

利用者が開発初期に見た「分解すると全体が飛ぶ」症状は、本時点で**再現していない**。
利用者の実行時に残った EMF を、クリップボード貼付・ファイル挿入の両経路で分解し、分解前後の
レンダを画素比較した結果、平行移動は 1 画素（0.127mm）で、目視でも崩れは出ない。是正前の
パッケージでも再現しない。原因は未特定であり、再発時に計測できるよう
`tests/tools/regression/emf_ungroup_geometry.ps1` を残す（分解前後のレンダ一致を非0終了で判定する。
既存の office_paste_regression.ps1 は寸法・分解数・文字数のみで、分解後の見た目を見ていない）。

## 窓フィット尺度は回転を映した印刷可能領域で決まる（2026-07-17）

再現手順は `tests/tools/regression/window_fit_scale_matrix.ps1`。
利用者の実物図面（モデル空間・ビューねじれ約80度・プロット回転90・窓 92.03×62.45 図面単位）で
「窓の中央付近しか貼り付けられない」報告があり、原因を実測で確定した。

モデル空間の窓プロットは用紙合わせ（ScaleToFit）になり、クロップ矩形の算出はフィット尺度を
自前で再計算する。この計算が回転前の印刷可能領域を使っていたのに対し、AutoCAD 実機は
回転後のフレームで窓をフィットさせる。プロット回転が奇数四半のとき、実効尺度は
`min(printH/winW, printW/winH)`（印刷可能領域の縦横を入れ替えた値）である。

判定材料は AutoCAD 自身が発行時に出力する「有効な印刷領域」（用紙フレームの印字内容寸法）で、
-PLOT（オラクル）と PlotEngine（PETADIAG）が全ケースで同値を出した。余白付き／フルブリード
A4 × 回転 0/90 × 窓縦横比の5ケースと実物図面の全6点が上式で説明でき、旧実装と食い違うのは
回転 90 の3点だけである（例: フルブリードA4・横長窓で実効 3.2273 に対し旧計算 2.2819。
比は√2で、クロップが実内容の 1/√2 になり窓の中央しか残らない。縦長窓では大小が逆転し、
クロップが実内容より大きく余白だらけになる）。

あわせて次の2点も確定した。

- フィットの基準は印刷可能領域であり、用紙全体ではない（余白付き A4・回転0で実効
  200/92.028。用紙全体基準なら 210/92.028 になるはずが、ならない）。
- ペーパー空間で「ページ設定の尺度のまま収まるか」の判定も同じ理由で回転後の
  印刷可能領域と比べる必要がある（従来の回帰は回転ケースを固定尺度 1:1・収まる窓でしか
  流しておらず、判定の誤りを検出できなかった）。

是正は `PaperMetrics.DisplayPrintableWidthMm/HeightMm`（Shared の単一箇所）に置き、
`PlotOrchestrator` の収まり判定とフィット尺度の両方がこれを使う。互換CAD（ARES／IJCAD／
BricsCAD）の窓フィットの実効尺度は未測であり、同じ契約かは実機で確認する。

## PDF の BaseFont 名からファミリ名を解決する（2026-07-16）

PDF の BaseFont は PostScript 名で、ファミリ名と一致しない書体が多い。GDI は未知の名前を渡されても
失敗せず既定書体へ黙って差し替えるため、生名のまま流すと字形も字送りも変わったまま出力される。
字送りは元書体のキャップハイト基準で算出しているため、差し替え後は寸法も噛み合わない。

実測（`System.Drawing.Font` に名前を与えて解決される書体を読む）:

| 与えた名前 | GDI が解決する実体 |
|---|---|
| TimesNewRomanPSMT | Microsoft Sans Serif |
| CourierNewPSMT | Microsoft Sans Serif |
| YuGothic-Regular | Microsoft Sans Serif |
| MS-Gothic | Microsoft Sans Serif |
| Arial／Times New Roman／Courier New | 同名で解決 |

したがって固定の対応表は使わない。PostScript 名から候補のファミリ名を組み立て、GDI に解決させて
name テーブルの英語ファミリ名を読み戻し、**候補と一致したものだけ採用する**
（`CadPeta.Acquire.Common.GdiFontMetrics.FamilyNameForPostScriptName`）。GDI が差し替えた場合は
読み戻しが候補と食い違うため、当て推量が通らない。解決できない書体は生名のまま出力し、
非可逆な劣化として診断へ残す。

実測（是正後）: ArialMT→Arial、Arial-BoldMT→Arial、MS-Gothic→MS Gothic、MS-PGothic→MS PGothic、
MS-Mincho→MS Mincho、TimesNewRomanPSMT→Times New Roman、CourierNewPSMT→Courier New、
YuGothic-Regular→Yu Gothic、未インストール書体→解決せず診断へ記録。
旧実装の対応表にあった6件は同じ結果になり無回帰。

## Viewport選択出力の復元と非印刷レイヤを直接検証する（2026-07-20）

Viewport選択出力は元図面の未確定Transactionで非選択ペーパー図形を一時非表示にする。
この復元判定に`DBMOD`全値を使うと、`BACKGROUNDPLOT`など処理中の別要因まで拾い、図形が復元済みでも失敗になる。
判定対象を実際に変更した図形の`Visible`と`LayerId`、および一時レイヤの消滅へ限定した。

選択したペーパー図形が非表示・非印刷・凍結・オフのレイヤにある場合は、その図形だけを未確定Transaction内の一時印刷レイヤへ移す。
元レイヤ自体は変更しないため、同じレイヤの非選択図形と、Viewport内で参照されるモデル図形の表示条件を変えない。
選択Viewportは枠の元レイヤ状態を尊重し、一時レイヤへ移さない。

AutoCAD 2026で`009-010_入間川橋_交通規制図(4)～(5).dwg`の`交通規制図（4）`を使い、
先選択、後選択、先選択から`O`で対象を置換して窓指定、Viewportと非印刷レイヤ図形の混在、の4ケースを実行した。
全ケースが成功し、混在ケースでは選択図形の固有色だけをSVGから検出し、同じレイヤの非選択図形の固有色がないことを確認した。
処理後は選択図形の`LayerId`と`Visible`が元値へ戻り、入力DWGのハッシュも変化しなかった。

BricsCAD V26の既定7ケースも実行し、選択4経路のSVG／EMFが空Enterによる全体出力とすべて異なることを確認した。
AutoCADのPetaEdgeは個別10/10、クロス15/15が合格した。
全solutionは警告0・エラー0、xUnitはnet48=574、net8=578、net10=582件が成功した。

### Viewport固有設定と表示状態の判定

オブジェクト(O)でViewportを選択しても、Viewport内のモデル図形を選択したことにはならない。
選択経路はViewportの`On`、尺度、視点、クリップ、VPフリーズ、画層overrideを書き換えず、元レイアウトを製品のネイティブプロットへ渡す。
非印刷レイヤから一時退避する対象も、直接選択した非Viewportのペーパー空間図形に限る。

`Viewport.Number`は保存値ではなく、非アクティブなViewportでは`-1`を返す。
`-1`には画面外または`MAXACTVP`超過によるOnのViewportも含まれ、`0`は番号未割当を表し得るため、`Number <= 1`を対象外条件にはできない。
制御Viewportは`Number == 1`、表示内容のないオフViewportは`On == false`で判定する。

AutoCAD 2026の報告図面と報告レイアウトで7ケースを実行した。
VPフリーズ回帰では、同じ選択Viewport内へ固有色のモデル図形を2個置き、一方のレイヤだけをVPフリーズした。
SVGには表示側の固有色があり、VPフリーズ側の固有色はなかった。
オフViewportは専用理由で除外され、成果物を生成しなかった。
`MAXACTVP=2`でOnのまま非アクティブになった2個のViewportは、選択2件中2件を出力対象として正常に印刷した。
既存の先選択、後選択、選択置換、非印刷レイヤ混在も含め、7ケースすべてが合格し、入力DWGとレジストリ設定は復元された。
PR #6レビュー後に`PaperMixed`も再実行し、言語不変Kind `Controller`の除外記録、部分出力確認、PDF／SVG／EMF生成が成功した。
全solutionは警告0・エラー0、xUnitはnet48=585、net8=589、net10=593件が成功した。

## 選択絞り込みの現在の画層と全シナリオの再実行（2026-10-25）

モデル空間の O で元図面の現在の画層にある図形を選ぶと、AutoCAD では PETA が eInvalidLayer で止まり、何も出力しなかった。
Wblock で作った側DBは元図面の現在の画層を引き継ぎ、作業中の図面を側DBへ切り替えている間は、その画層の `IsFrozen` に同じ false を書いても AutoCAD が拒否する。
この切り替えは 2026-07-19 のコミット 50e2e9a で入り、その直前の 832e6c0 では同じ操作が成功した。
画層の表示・凍結・印刷の状態は、値が違うときだけ書くように直した。
ARES 2027 は同じ書き込みを拒否しなかった。

`selection_plot_matrix.ps1 -CurrentLayerObjects` は、画層 0 の矩形と現在の画層の矩形の 2 つだけを選び、2 件が出力対象になり、SVG に矩形 2 つの線が出ることを確かめる。
修正前のコードでは Wblock とオブジェクト複製の両方の経路で不合格になり、修正後は AutoCAD 2024・2026・2027 で合格した。

同じ日に、試験の期待値が古くなっていた 2 件を直した。
PetaFull の O は窓と重ならない範囲の図形を選んでいたので、窓と O を組み合わせた 32 件は、範囲に選択図形が無いという仕様どおりの中止になっていた。
O で選ぶ範囲を、モデルの窓と同じ座標から作るようにした。
ペーパー空間の側DBは 2026-07-20 から元図面全体の Wblock で作り、オブジェクト複製へ切り替えないので、`-ForceCloneFallback` はモデル空間だけで受け付ける。

修正後に、AutoCAD 2026 の `selection_plot_matrix.ps1` の全 12 シナリオとペーパー空間の `-ForceCloneFallback` を受け付けないことの確認、PetaFull 256/256（クロスチェック 399/399）、PetaEdge 10/10（15/15）、PetaData 35/35（26/26）、DiagSmoke 2/2、AutoCAD 2027 の Smoke 12/12（12/12）を実行し、すべて合格した。
全solutionは警告0・エラー0、xUnitはnet48=589、net8=593、net10=663件、App 12件、GUI 15件が成功した。

`samples/支持層結果.dwg` のグループ由来 932 件を O で選ぶと、修正後も v1.0.0 も Wblock の側DB が一様な平行移動にならず、オブジェクト複製の側DBで同じ大きさの EMF（69,412 bytes、863 図形）を出力した。
Wblock の側DB を一様な平行移動として元の座標へ戻す経路（`Wblock+translation`）は、手元の図面で通る選択が無く、修正後は確かめていない。

## net48 の依存の版をそろえ、AssemblyResolve の処理を外す（2026-10-28）

AutoCAD 2024（net48）で PETADIAG が diag.json を書けず、`System.Memory, Version=4.0.1.2` の FileNotFoundException で止まった。
同梱の `System.Memory` は 4.0.2.0（PdfPig 0.1.15 の依存）で、`System.Text.Json` 9.0.0 とその依存（`System.Text.Encodings.Web`、`System.IO.Pipelines`、`System.Threading.Tasks.Extensions`）は 4.0.1.2 を参照していた。
`System.Buffers`・`System.Numerics.Vectors`・`System.Runtime.CompilerServices.Unsafe` も、同梱より古い版を参照していた。
PdfPig の一族と自作の DLL の参照は、同梱の版とすべて一致していた。互換 CAD の net48（ARES 2020・2023・2024）の出力も同じだった。

Windows PowerShell 5.1（.NET Framework 4.8）で net48 の出力を `Assembly.LoadFrom` で読み、`System.Text.Json` を使わせると、`System.Memory` 4.0.1.2 の要求が `AppDomain.AssemblyResolve` に届き、そのときの `e.RequestingAssembly` は null だった。
4e0e36a のハンドラは要求元が自分のフォルダのアセンブリのときだけ同梱の DLL を返すので、この要求には応えなかった。
要求元が null の要求に応えると、どのプラグインの要求かを区別できず、ほかのプラグインのハンドラの解決を先取りしうる。

版をそろえる方法を nuspec と DLL のメタデータで確かめた。`System.Text.Json` 9.0.10 も `System.Memory` 4.5.5（4.0.1.2）に依存し、10.0.14 の net462 向けの DLL は `System.Memory` 4.0.5.0 を参照する。
PdfPig は最新の 0.1.16 でも `System.Memory` 4.6.0（4.0.2.0）に依存する。版はどの組み合わせでもそろわない。
そこで、プラグインから `System.Text.Json` を外し、診断の diag.json、試験の記録、側 DB の記録を .NET 標準の `DataContractJsonSerializer` で書くようにした（8715111）。
`dist/dll_path_check.ps1` の `Assert-ConsistentAssemblyReferences` は、net48 の配布フォルダの DLL が、同梱の DLL を同梱したものと同じ名前・AssemblyVersion・公開鍵トークン・Culture で参照していることを確かめ、build_dist と build_e2e が呼ぶ。

`AssemblyResolve` の登録を外したビルドで、AutoCAD 2024 の `plugin_regression_matrix.ps1`（PETA と PETADIAG の 4 ケース）、ARES 2020・2023 の `peta_option_smoke_compat.ps1`（各 8 ケース）が合格したので、ハンドラと、読み込み時の欠かせない処理の仕組みを削除した（2391bc1）。
ARES 2024 は起動していない。

削除の後のコード（2391bc1）で、AutoCAD 2024・2026・2027 の `plugin_regression_matrix.ps1`（各 4 ケース）、AutoCAD 2026・2027 の PetaEdge（各 11/11、クロスチェック 18/18）、ARES 2020・2023・2027 の `peta_option_smoke_compat.ps1`（各 8 ケース）を実行し、すべて合格した。
どの実行でも、レジストリと .aws、ARES の登録と profile.xml は元に戻り、照合で一致した。
ARES 2020 では、用紙の一覧が空のときにページ設定の用紙名を寸法を確かめずに使ったことを示す診断（`plotport.media_size_unverified`）が出ることも確かめた。
全solutionは警告0・エラー0、xUnitはnet48=612、net8=616、net10=769件が成功した。
