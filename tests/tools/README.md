# 実機・回帰検証ツール

製品コードの単体テストだけでは確認できない CAD、Office、配布物のE2Eを再実行するための資産である。

| ディレクトリ | 主な検証 |
|---|---|
| `pdf-emf/` | PDF構造、IR写像、EMF、Office、グラデーション式とCAD 4製品E2E |
| `regression/` | プラグイン、クロップ、クリップボード、Office貼付、窓の座標契約の回帰 |
| `data-export/` | PETADATAの構造SVG書き出しと、PETA印刷結果との比較 |
| `cad-typography/` | 互換CAD API、SHX、複雑線種、文字配置、Office分解限界 |
| `distribution/` | 配布ZIP、署名、PowerPoint分解限界 |

## 試験の DLL のビルドと AutoCAD の起動の回数

CAD を起動する試験（E2E・画面の確認）は、配布物と同じビルド（`dist/dist_build.ps1`。構成は Release、`-p:ContinuousIntegrationBuild=true --no-incremental`）で作った DLL を読み込む。
試験の前に次を実行する。

```powershell
pwsh -File tests/tools/build_e2e.ps1                  # Acad・Ares（2020・2023・2024・2027）・Ijcad・Bricscad
pwsh -File tests/tools/build_e2e.ps1 -Products Acad    # AutoCAD 版だけ
pwsh -Command "& ./tests/tools/build_e2e.ps1 -Products Acad,Ares"   # 複数の製品（-File は Acad,Ares を 1 つの文字列として渡す）
```

試験のスクリプトは `Get-E2ePluginPath`・`Assert-E2ePluginBuild`（`common/test_helpers.ps1`）で DLL を確かめる。
DLL に開発機のパスが残っている（普段の `dotnet build` の DLL）と、CAD を起動する前に何も変えずに止まる。
build_e2e.ps1 は、ビルドの入力（`src` と `dist/dist_build.ps1`）の作業ツリーの内容から求めた値を出力のフォルダ（`e2e-build-source.txt`）に書く。
試験は同じ値を求め直し、記録が無いか、DLL を作った後にビルドの入力が変わっていれば（コミットしていない変更と新しいファイルを含む）、
CAD を起動する前に何も変えずに止まる。この約束は `common/e2e_build_record_selftest.ps1` が確かめる。
単体試験は普段のビルドのままでよい。

## 実行の確認（-WhatIf と -Confirm）

AutoCAD・互換 CAD を起動する試験と、その記録から戻すだけのスクリプト（`cad_reg_restore.ps1`・`compat_autoload_restore.ps1`）は、
PowerShell の標準の `-WhatIf` と `-Confirm` に従う。引数なしで実行すると、変える内容（計画）を表示してから確認を求める。
`-WhatIf` は計画を表示するだけで、何も変えない。確認の画面を出せない実行（`-NonInteractive`、エージェント、ほかのスクリプトからの呼び出し）は、
確認を求めた時点で失敗して何も変えないので、`-Confirm:$false` を付けて呼ぶ。

```powershell
pwsh -File tests/tools/regression/selection_plot_matrix.ps1 -Ver 2026 -Standard -WhatIf          # 計画だけを表示する
pwsh -File tests/tools/regression/selection_plot_matrix.ps1 -Ver 2026 -Standard -Confirm:$false  # 確認せずに実行する
```

Windows PowerShell 5.1 で動く互換 CAD の試験は、`-File` に `-Confirm:$false` を渡せない（値が文字列として渡り、
SwitchParameter に変換できずに止まる）。自動で実行するときは `-Command` で呼ぶ。

```powershell
powershell -STA -Command "& 'tests\tools\regression\peta_option_smoke_compat.ps1' -Product Ares2027 -Confirm:`$false"
```

確認の形（先頭の `[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]`、直後の `$ErrorActionPreference = 'Stop'`、
本体から 1 回だけ呼ぶ `ShouldProcess`）は `dist/script_confirm.ps1` に書いてある。単体試験の `ScriptConfirmTests` が、対象のスクリプトごとに、
確認なしの非対話の実行で失敗して何も変えないことと、`-WhatIf` で何も残さないことを、AutoCAD を起動せずに確かめる。
`-Confirm:$false` で確認の後まで進むことは、確認の後の処理が試験のフォルダの中で終わるスクリプト（記録から戻すだけの 2 本と sign_dist）で確かめる。
AutoCAD・互換 CAD を起動するスクリプトは、確認の後に CAD を起動するので単体試験では実行せず、構文木の検査と、`-Confirm:$false` で実行する実機の試験で確かめる。
環境を変えるスクリプトを足したら、`ScriptConfirmTests` の一覧にも足す（一覧から漏れると試験が失敗する）。

accoreconsole を使う試験は、1 回の起動で複数のケースを続けて実行する（`common/accore_batch.ps1` の `Invoke-AccoreBatch`）。
各ケースの前後に区切りのマーカーを置き、ケースの終わりで LISP が完了のファイルを書いて合図を待つ。その間にハーネスが成果物をケースのフォルダへ移す。
標準出力が 90 秒増えないか、1 ケースの上限を超えたら止まったと判定し、そのケースを不合格にして次のケースから起動し直す。
ケースの最初に図面の全図形の数を出力し、起動の最初のケースと数が違えば不合格にする（前のケースが図面に図形を残すと、後のケースの出力に入るため）。

| 試験 | 起動の回数（1 ケース 1 起動 → まとめた実行） | 所要（AutoCAD 2026、2026-10-25） |
|---|---|---|
| `peta_option_matrix.ps1 -Set PetaFull` | 256 → 2 | 864 秒 → 214 秒 |
| `peta_option_matrix.ps1 -Set PetaEdge` | 16 → 2 | 23 秒 |
| `window_ucs_regression.ps1` | 7 → 1 | 8 秒 |
| `window_fit_scale_matrix.ps1` | 5 → 1 | 7 秒 |
| `crop_regression.ps1` | 3 → 1 | 5 秒 |
| `plugin_regression_matrix.ps1` | 4 → 2 | 10 秒 |
| `plot_transparency_matrix.ps1` | 7 → 2 | 10 秒 |

まとめた実行で見つかったこと: ペーパー空間の O の試験が作った長方形を消していなかった（後のケースの出力に入った）。
モデル空間で O を使った PETA の直後の 1 回だけ、TrueType の文字が別のフォントで印刷された（製品を直した。PetaEdge の `edge11-window-after-object` が確かめる）。
`window_ucs_regression.ps1` は、UCS とビューをコマンドで初期値へ戻すと、ねじれた画面の塗りつぶしの頂点の順番が変わったので、UNDO のマークで戻す。

## 窓の座標契約の計測

`regression/window_contract/` は、窓プロットへ渡す矩形がどの座標系かを実機で確定する。
先に `dotnet build tests/tools/regression/window_contract/WindowContractProbe/WindowContractProbe.csproj -c Release` を実行する。

```powershell
pwsh -File tests/tools/regression/window_contract/window_contract_matrix.ps1 -Case B
pwsh -File tests/tools/regression/window_contract/window_contract_oracle.ps1 -Case B
pwsh -File tests/tools/regression/window_contract/window_contract_oracle.ps1 -Case AsIs -Dwg <実図面> -P1 "-50,120" -P2 "-15,155"
```

`window_contract_matrix.ps1` は pick 2点から3候補（生値・UCS→WCS・WCS→DCS）を作り、座標以外の
条件を揃えて別々のPDFを発行し、判断材料（入力文字列とAPI戻り値、UCS行列、ビューのTarget・
ViewDirection・ViewTwist、システム変数、設定直後と検証後の窓の読み戻し）をJSONへ記録する。
`window_contract_oracle.ps1` はCAD自身の `-PLOT` に同じ2点を与え、ページ設定へ格納された窓
（DXF群 48/49/140/141）を読み戻す。これが正解の定義になる。

ケースは A〜N（対照／目標点単独／UCS原点／回転／ねじれ／傾斜UCS／3Dビュー／視線がUCS平面と平行）で、
1変数ずつ切り分ける。`-Case AsIs` は図面自身のUCS・ビューをそのまま使う。

AutoCADの契約はDCSと確定済み（`docs/validation/architecture-decisions.md`）。

互換CAD（ARES／IJCAD／BricsCAD）は accoreconsole を持たないため、COM で GUI を駆動する
`window_contract_compat.ps1` を使う（Windows PowerShell 5.1 Desktop／STA・対話デスクトップ必須）。
計測器は各製品 SDK へ移植した `WindowContractProbe.{Ares,Ijcad,Bricscad}`（製品コード非参照）を
先にビルドする。

```powershell
dotnet build tests/tools/regression/window_contract/WindowContractProbe.Bricscad/WindowContractProbe.Bricscad.csproj -c Release
powershell -Sta -File tests/tools/regression/window_contract/window_contract_compat.ps1 -Product BricscadV26 -ProbeOnly
powershell -Sta -File tests/tools/regression/window_contract/window_contract_compat.ps1 -Product BricscadV26 -Cases A,G,J,M
```

ケースごとに、先にオラクル（-PLOT・全応答既定）を完走させ、ページ設定へ格納された用紙の
正式名（DXF 4）を読み戻して計測器へ CPWIN_MEDIA で渡す（-PLOT の用紙プロンプトは表示名を
要求し、正式名との対応は製品ごとに違って名指しできない。IJCAD 実測）。計測条件は
パラメータファイル（CPWIN_PARAMS）で渡す（GUI は1プロセスで複数ケースを流すため、起動時に
固定されるプロセス環境変数では渡せない）。定常オブジェクトスナップは実行中だけ OSMODE=0 にする
（スクリプト・(command) 経由の座標入力が近傍頂点へ吸着し、-PLOT 格納値が入力とずれる。
BricsCAD・AutoCAD 2026 とも実測。AutoCAD は2点目＝GetCorner／右上コーナーだけが吸着する）。

ARES はさらに次の癖がある（2024 実測。ハーネスは対応済み）。

- 未署名 DLL の読み込みは信頼確認ダイアログに黙って阻まれる。SECURELOAD=0 でも
  TRUSTEDPATHS の一時設定でも出る。ハーネスは DLL のフォルダを、ARES が暗黙に信頼する
  `%AppData%\ARES Standard Edition x64` の下の実行ごとのフォルダへコピーして読み込み、終了後に削除する
  （common/compat_cad_com.ps1 の New-CompatPluginStage）。
- -PLOT は -PRINT の別名で、プロンプト列が AutoCAD 型と全く違う（範囲キーワードは `_Specify`、
  プリンタ既定値は DWG 保存名のままだと「見つかりません」を繰り返す）。応答列は製品表の
  `OracleResponses` に分離してある。
- AutoLISP `(trans p 1 2)` はねじれ付きビューで DCS でなく WCS を返すため、判定は trans 基準に
  加えて計測器（view 読み＋公式行列）基準を併記する。
- `GetCorner` はキーボード座標を基点相対と解釈し、取りこぼしもあるため、pick 点は
  `(command "_CPWINPROBE" (list ...) (list ...))` の点リストで与える。
- `TARGET` と `VIEWDIR` のシステム変数は現在UCS成分で返る版があるため、設定成功の判定では
  CAD自身の `trans` でWCSへ戻した値を入力条件と照合する。ARES 2024の直角ねじれでは
  `(trans p 1 2)` が恒等値を返すため、候補の差だけを設定成功の根拠にしない。
- ARES 2027 は単独起動だとメインウィンドウ非表示のままスピンし ROT 未登録に陥ることがある
  （約2コア消費）。両ハーネスは 30 秒 ROT 未登録で2つ目のプロセスを起動して1つ目の起動を
  促す（2起動診断で実測確認）。この経路は捨てプロセスを停止するため `-AllowForceStop` を付ける。

判定は生PDF同士のページ内容で行う。`compare_pages.py` が固定DPIで描画して画素比較し、
`measure_shift.py` が平行移動量を出す。viewBoxはページ全体の寸法になるため座標契約の判定に使わない。
実測結果（BricsCAD V26・IJCAD 2026・ARES 2024 とも DCS。-PLOT 格納値が厳密一致し、BricsCAD と
ARES 2024 は画素完全一致、IJCAD は描線の丸め差のみ）は各 `<製品>PluginDefaults.WindowPickContract`
とその根拠コメントへ反映済み。

## 窓の座標契約の恒久回帰

`regression/window_ucs_regression.ps1` は、UCS やビューの向きが変わっても窓が正しく切り出されることを
固定する。既存のテストチャートは世界UCS・目標点0・ねじれ0で、UCS・WCS・DCS がすべて一致する条件しか
持たず、座標系の誤りを2度素通しした。

```powershell
pwsh -File tests/tools/regression/window_ucs_regression.ps1 -Ver 2026
```

不変条件は「同じモデル上の2点を、同じ画面の向きで pick したら、UCS の原点・回転やビューの目標点が
どう置かれていても出力は同一」。UCS の置き方と目標点は同じ点の呼び方を変えるだけで、画面に見える矩形は
変わらないためである。ねじれは画面の向きそのものを変えるので別群として群内で比較し、群をまたいだ
一致（＝ねじれが無視されている）も失敗にする。

条件は DVIEW と UCS コマンドで実行時に作る（新しい図面資産を足さない）。回転角は 3-4-5 の角に
統一し、期待する pick 点がすべて有限小数になるようにしてある（無理数を桁で打ち切ると DCS が僅かに
ずれ、成果物のバイト一致が壊れる）。

判定は成果物（SVG）のハッシュ一致。モデル空間は用紙合わせが強制され viewBox が用紙全面になるため、
viewBox では領域の違いを判別できない。

この回帰が実際に不具合を検出することは確認済み。契約を旧実装（`WindowPickContract.UcsToWcs`）へ戻すと
`target`・`combined` が失敗して非0終了する。目標点0・ねじれ0では DCS=WCS のため `ucsrot` は旧実装でも
通ってしまう。目標点を振るケースが検出の要である。

## 窓フィット尺度の絶対サイズ回帰

`regression/window_fit_scale_matrix.ps1` は、モデル空間の窓プロット（用紙合わせ）で、
クロップ矩形が実機の印字内容と絶対サイズで一致することを固定する。
`window_ucs_regression.ps1` は同一条件間のハッシュ一致（相対）しか見ず、系統誤差を検出できない。

```powershell
pwsh -File tests/tools/regression/window_fit_scale_matrix.ps1 -Ver 2026
```

余白付き／フルブリードA4 × 縦横 × 窓縦横比の5ケースで、窓と同一の矩形を描いた図面へ
-PLOT（オラクル）と PETADIAG を流し、(1) AutoCAD 自身の「有効な印刷領域」＝窓×算出尺度、
(2) SVG viewBox＝窓×尺度、(3) 内容 bbox が viewBox の四辺へ届く（窓の縁の欠落なし）を照合する。

実測で確定した契約（2026-07-17、この回帰が固定する）: 窓フィットの基準は印刷可能領域
（用紙全体ではない）で、プロット回転が奇数四半のときは印刷可能領域の縦横を入れ替えて
`min(printH/winW, printW/winH)` でフィットする。回転前の寸法で計算する旧実装は、回転90の
横長窓でクロップが実内容の 1/√2 になり窓の中央しか残らない・縦長窓では逆に余白だらけになる
（`PaperMetrics.DisplayPrintable*` とその根拠コメントへ反映済み）。

## PETA・PETADATA オプション全組み合わせの実機マトリクス

`regression/peta_option_matrix.ps1` は、AutoCAD の accoreconsole でコマンドのオプションを
全因子で駆動し、成果物と表示文言を機械判定する。セットは次のとおり。

- `PetaFull` … PETA の 空間×範囲×絞り込み×余白×レイヤ×文字輪郭×ファイル出力×EMF背景 の 2^8=256。
  ファイル出力の軸は、ケースの直前に設定のファイル（CADPETA_DATA_DIR）の形式を 0 件か PDF・SVG・EMF に書き替えて切り替える
- `PetaEdge` … A=Enter一致、縮退窓の聞き直し、EMF背景の不正入力、Enter維持、トグル偶奇、
  絞り込み解除、先選択 の10件
- `PetaData` … PETADATA の 尺度×余白×レイヤ×文字輪郭×SHX代替 の 2^5=32＋グラデーションDPI 2＋空選択 1
- `DiagSmoke` … PETADIAG／PETADATADIAG の診断出力（diag.json・stage ダンプ。設定の形式が 0 件でも全形式を出力する）
- `Smoke` … 版差確認の固定14件（`-Ver 2027` 用。13 件目は -PETAOPT で形式を値の合計 13 にした後の PETA、14 件目はファイル名のコメント）
- `Trial` … 内容フラグと golden の採取（合否判定の材料作り）

```powershell
pwsh -File tests/tools/regression/peta_option_matrix.ps1 -Set PetaFull [-Ver 2026] [-CampaignId <ID>]
pwsh -File tests/tools/regression/peta_option_matrix.ps1 -Set Smoke -Ver 2027 -CampaignId <ID>
```

判定は、成果物集合の厳密個数、SVG ヘッダの既知正解（レイアウト窓 100×70／余白5で110×80）、
レイヤ群 `inkscape:groupmode="layer"` と `<text` の有無、絞り込みサマリと各トグル文言、
ケース間クロスチェック（EMF背景だけ違う対の SVG 一致と透過あり時の EMF 相違、ファイル出力オフの
クリップボード読み戻しとファイル出力オンの EMF のバイト一致、レイヤ対の描画要素数不変、
モデル空間の寸法一貫性と余白の算術）で行う。内容フラグと golden は
`peta_option_matrix_flags.json`（試走から人手承認で固定）を出典とし、未確定なら実行を拒否する。
一時領域（%TEMP%\CadPeta）残留ゼロ、入力 DWG 不変、完了マーカー
（CADPETA_CASE_END）も全ケースで確認する。キャンペーン記録（campaign.json）に実行時 HEAD・
差分・untracked の SHA256 を残す。

レイアウトの窓の既知正解は `regression/peta_a3_window_expect.ps1` の表にあり、`crop_regression.ps1` と `peta_option_smoke_compat.ps1` も同じ表から読む。
viewBox の Y は PDF のページの高さで変わるので、表はページの高さごとに分けてある。

`regression/petadata_size_measure.ps1`（1 回の起動）は、図形の多い図面で PETADATA を実行し、図面から開いたオブジェクトの数が選択の数の 2 倍を超えたら失敗にする（同じ画層・線種・ブロックを図形ごとに開き直すと、メモリと時間が図形の数に比例して増える）。
時間とメモリは版や PC で変わるので、判定せずに表示する。`-Plugin` に別のコミットで作った DLL を渡すと、同じ図面・同じ版で前後を比べられる。

CAD を起動する試験は、CADPeta の設定のフォルダ（`CADPETA_DATA_DIR`）を、起動する CAD のプロセスにだけ渡す
（`common/test_helpers.ps1` の `Write-E2eSettings`。既定は今までの試験と同じく、図面のフォルダに PDF・SVG・EMF を出力する）。
利用者の `%APPDATA%\CADPeta\settings.json` は読まず、作らない。互換 CAD の試験の起動の関数（`common/compat_cad_com.ps1` の `Start-CadVisibleAndConnect`・`Start-CadProcessHidden`）は、
`CADPETA_DATA_DIR` の無い起動を止める（自己試験 `common/compat_launch_selftest.ps1`）。

ARES 2027 は、利用者のセッションがリモート デスクトップの切断の状態だと起動の途中で終了するので、互換 CAD の試験は、環境を変える前に
接続の状態を確かめて止まる（`` の `NeedsConnectedSession`、`Assert-CompatLaunchReady`）。画面に接続してから実行する。

PETAOPT のダイアログとリボンは、`regression/petaopt_gui_check.ps1`（画面のある acad.exe の 1 回の起動）で確かめる。
PETAOPT でファイル名に `{comment}` を設定し、続く PETA のコメントのダイアログ（開く位置、フォーカス、使えない文字、ファイル名の表示、
Enter・[キャンセル]・Esc）も確かめる。互換 CAD では、`regression/peta_option_smoke_compat.ps1` の最後の 4 ケース
（settings-dialog、comment-dialog、comment-cancel、comment-escape）が同じことを確かめる。ダイアログの操作と判定は、
両方が `common/cad_dialog_ui.ps1` の同じ関数を使う。互換 CAD は製品によって SendCommand がダイアログを閉じるまで戻らないので、
ダイアログの操作は別のプロセス（Start-Job）で行う。互換 CAD のリボンは UI オートメーションに出ないので、リボンの確認は AutoCAD だけで行う。
インストール済みの CADPeta.bundle があると acad.exe がその DLL を読み込むので、アンインストールしてから実行する（あれば起動の前に止まる）。
前提を2つハーネス自身が守る。(1) AutoCAD のプロファイルは、開始時に `HKCU\Software\Autodesk\AutoCAD` と
`DWGCommon` の全部のキーと値、各バージョンの `Support\Profiles` の .aws を記録し、エラー終了を含む finally で
開始時の状態へ戻して、もう一度読んで一致を確かめる（共有実装は `regression/CadRegGuard.ps1`）。
accoreconsole の強制終了が FileDialog=0 を残し、scr の setvar がプロファイルへ保存され、accoreconsole が
起動時の作業ディレクトリを `Editor Configuration\LogFilePath` に残す（2024・2026・2027、2026-10-25）ためである。
記録は `%LOCALAPPDATA%\CadPeta-e2e\reg-backup\autocad.reg.txt` にも書き、戻して一致したときだけ消す。
記録が残っていれば次の試験は始まらないので、`pwsh -File tests/tools/regression/cad_reg_restore.ps1` で戻す（`-WhatIf` を付けると、戻す違いを表示するだけ）。
終了時にほかの AutoCAD が動いていれば、戻さずに記録を残して失敗にする。
開始時にあったキーが試験中に消えていれば、記録から戻したうえで失敗にして記録を残す（試験も AutoCAD もキーを消すことは確かめられていないので、
原因を調べるため）。原因を調べたら `cad_reg_restore.ps1` をもう一度実行すると、一致を確かめて記録を消す。
CADPeta のインストール先（`%APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle` と .old・.new、ARES の `%AppData%\ARES Standard Edition x64\CADPeta`）は、
ファイルの名前と内容の SHA256 を試験の前に記録し、試験の後に比べる。更新時刻とサイズは比べない（利用者の AutoCAD やほかのセッションが
bundle を移しただけでも変わる）。インストール先は戻さず、違いがあれば失敗にする（互換 CAD の試験も同じ）。
記録と前後の比較は 1 系統にしてある（`common/test_helpers.ps1`）。ハッシュは `Get-FileSha256Shared` だけで求め、一覧は `Get-FolderListing`（ファイルとフォルダの名前と、
ハッシュと失敗の欄）、比較は `Compare-FolderListing`（どちらかの側で読めなければ必ず違いにする）。記録する対象は表の行（`New-RecordRow`）に並べ、
行ごとに扱いを決める: `.aws` は戻して照合し（Restore）、インストール先と信頼済みのフォルダは戻さずに違えば失敗にする（Verify）。
実行前の記録は `Save-RecordBaseline` の 1 か所でまとめて取り、読めないファイルがあれば環境を変える前に止まる。環境を変えた後（戻す前）に
実行前の記録を取ろうとすると止まる。レジストリは値の形が違うので、`regression/CadRegGuard.ps1` の別の系統で記録して戻す。
accoreconsole は、作業ディレクトリを試験の作業フォルダにして起動する（`CadPeta.Tests` の TestScriptRulesTests が検査する）。
(2) OSMODE は scr 内でも
保存→0→復元する。accoreconsole のスクリプト座標入力は2点目（GetCorner／-PLOT の右上コーナー）が
定常オブジェクトスナップで近傍ジオメトリへ吸着し、窓が入力値からずれる（AutoCAD 2026 実測。
ネイティブ -PLOT の格納値も同値になるため製品の欠陥ではなく入力経路の性質。BricsCAD でも実測済み）。

互換CAD（ARES 2027／IJCAD 2026／BricsCAD V26）は `regression/peta_option_smoke_compat.ps1` で実測する。
既定実行は出力オプション2ケースに加え、裸`O`、`_Object`、先選択、後選択、空Enter、画層 0 と現在の画層の図形の後選択を確認する。
既定実行は、レイアウトで PETA を実行した後にモデル空間へ切り替えて O を実行する。ARES はレイアウトで印刷した後に
一部の図形の範囲を違う値で返すので、この順番はその条件を確かめるために残している。
選択経路だけを検証するときは `-ObjectCasesOnly` を指定し、モデル空間で保存済みの`TESTCHART2.dwg`から開始する。

互換CADの COM 駆動の 4 本（`peta_option_smoke_compat.ps1`、`pdf-emf/gradient_compat_e2e.ps1`、
`peta_window_rotation_compat.ps1`、`window_contract/window_contract_compat.ps1`）は、人の操作なしで次のように実行する。

- 読み込む DLL は、製品が暗黙に信頼するフォルダ（ARES は `%AppData%\ARES Standard Edition x64`）の下に
  実行ごとのフォルダを作ってコピーし、終了時に削除する。これで「信頼できるフォルダにありません」の確認の画面が出ない。
- ARES 2024 以降は、インストール済みの CADPeta の自動読み込みの登録（`HKCU\Software\Graebert GmbH\ARES Standard <版>\Applications\CADPeta`）を
  起動の前に `%LOCALAPPDATA%\CadPeta-e2e\autoload-backup\<製品>.reg` へ書き出して外し、終了時（エラー・中断を含む）に戻して一致を確かめる。
  登録が残っていると、試験はビルドした DLL ではなくインストール済みの DLL を試験してしまう。
  ARES 2023 以前は、profile.xml のスタートアップ（applications の `load_cadpeta.lsp`）に登録がある。
  ARES は終了時に profile.xml を書き直し、最後に読み込んだ DLL のフォルダ（`lastapploadfolder`）などを残すので、全部の版で、起動の前に
  profile.xml の全体を `%LOCALAPPDATA%\CadPeta-e2e\autoload-backup\<製品>.profile` へコピーし（2023 以前はそのあとで登録の項目を外し）、
  終了時にコピーのバイト列へ戻して一致を確かめる。登録を外す処理は、配布物の unregister.ps1 と同じ関数（dist/compat/compat_common.ps1）を使う。
  前回のバックアップが残っていれば実行を中止するので、`powershell -File tests\tools\common\compat_autoload_restore.ps1 -Product Ares2027` で戻してから実行する（`-WhatIf` を付けると、戻すものを表示するだけ）。
  ARES が動いている間は、試験の終了時も `compat_autoload_restore.ps1` も戻さずにバックアップを残す（ARES は終了時にメモリ上の設定で profile.xml とレジストリを書き直すため）。ARES を終了してから戻す。
  バックアップは一時の名前（`.partial`）で作り終えてから正式の名前に変えるので、作る途中で失敗しても、次の実行を止めるバックアップは残らない。
  この処理の約束は `common/compat_autoload_selftest.ps1` で CAD を使わずに確かめる。
- `peta_option_smoke_compat.ps1` は、PromptTrace の `Plugin.Load` で読み込まれた DLL がコピーした DLL だけであることを確かめ、
  失敗したケースでは `Message`（プラグインがコマンドラインへ出した行）を証跡へ書き出す。
各選択ケースは先選択を解除してから準備し、SDK生値と選択件数を`PromptTrace`で判定する。
選択SVG／EMFが空Enterの全体出力と異なることも負の対照として要求する。
ARESでは準備式の完了マーカーを確認してからPETAを送り、成果物なしの`Command.End`を検出したら15秒で後続ケースを打ち切る。
GUI COM 駆動の実行条件と所有PIDの終了規律は`window_contract_compat.ps1`と同じである。

`regression/peta_option_g8_verify.ps1` は代表8ケースについて、PETA と独立に CAD ネイティブ
-PLOT で正解 PDF を作り、PETA の PDF／EMF（GDI描画）／SVG（Chromium描画）を ROI 指標
（前景 bbox・recall/precision/IoU）で照合する三点検証（機械比較・可視再現・目視）の駆動系。
判定は negative control（空画像・図形欠落・0.3mm平行移動・誤色が全て FAIL）を単体試験で証明した
`regression/peta_option_matrix/compare_renders.py` に集約する。

## CLI（cadpeta.exe）のオプション結線と他所産 PDF の忠実性

`regression/cli_option_matrix.ps1` は、CAD 側マトリクスと共有されない層を検証する。
(1) CLI 引数から変換オプションへの結線（keepBg×色3種＋置換×レイヤ×文字輪郭×クロップ×EMF背景の
全因子 2×4×2×2×2×2=256）、(2) CAD 経路に存在しない PNG 出力、(3) 他所産 PDF
（合成フィクスチャ4種・実物図面・Word・Excel・Edge 印刷）の忠実性。CAD 不要で全て走る。

```powershell
pwsh -File tests/tools/regression/cli_option_corpus.ps1     # Word/Excel/Edge 産 PDF を生成（要 Office）
pwsh -File tests/tools/regression/cli_option_matrix.ps1 -Set All
```

判定は成果物集合、SVG の痕跡（色集合は rgb(R,G,B) 表記を正規化して照合、レイヤ群、`<text`、
クロップ寸法の算術）、PNG の画素寸法と非空白、EMF背景対の EMF 差、エラー経路7種の非0終了と文言。
忠実性は入力 PDF 自身の描画を正解として出力 SVG/EMF/PDF の描画と 300dpi・mm 基準許容で照合する
（判定は compare_renders.py へ集約）。keepBg の実効は `tests/assets/pdf-fixtures/bg_rect_fixture.pdf`
（`pdf-emf/gen_bg_fixture.py` 生成）で確認する。TESTCHART2 系 PDF には除去対象の背景矩形が無い。

解決できないフォント（Type3 等。Chromium 印刷 PDF で実測）を含む入力は、代替書体の字形差で
recall が下がるのが診断つきの文書化済み縮退のため、診断記録（フォント名を解決できず／フォント代替）の
実在を必須にした上で recall の合格下限を回帰床 0.90 へ切り替える。診断のない劣化は FAIL のまま。
cadpeta.exe のコンソール出力は CP932 で、stdout 照合はその前提で読む。

## 用紙の寸法対応付けとプロット回転の実機回帰

`regression/peta_window_rotation_compat.ps1` は、レイアウト（PETA_A3・A3・1:1）に 100×70mm の
窓を指定した PETADIAG を PlotRotation 0/90/180/270 で流し、続けてモデル空間の窓のケースを流して、次の4点を機械判定する。

- 用紙の寸法対応付け（PaperMediaMatcher／SelectMedia）: 出力 SVG の寸法が全回転で 100×70mm に
  なる。用紙が既定用紙へ落ちて用紙単位が変わると 1:1 の意味が壊れ、この寸法にならない
- プロット回転のクロップ（QuarterTurnsCw）: クロップ後の内容が全回転で同一になる。判定は
  EMF のバイト一致を第一とし、不一致なら GDI で描画して平行移動 0.2mm 以内・残差 1% 未満を
  要求する（回転経由の PDF は描線座標の丸めが変わり、バイトは一致しないことがある）
- モデル空間 × 窓 × 用紙合わせ × 回転の実効尺度とクロップの位置: PDF デバイスの縦の A4（余白のある用紙と
  フルブリード）× 横長・縦長の窓 × 回転 0/90/270 度の 12 件で、窓と同じ矩形と対角線を描いて出力し、
  最終 SVG の内容が四辺まで 1mm 以内で届くことと、SVG の縦横比が窓のままであることを確かめる
  （window_fit_scale_matrix.ps1 の互換 CAD 版。互換 CAD は「有効な印刷領域」を表示しないので、図面の内容を正解にする）
- 窓のクリップとクロップの中心（AutoCAD だけ）: AutoCAD の PDF の窓のクリップは印字した窓そのものなので、クロップの中心との
  差を 0.05mm まで許す（PDF ドライバの 1pt の丸めを扱わないと 0.07〜0.29mm ずれる）。ARES はクリップが窓より広いので、差を証跡
  （WINDOW_CLIP）に書くだけにする

PETA_A3 は余白の無い A3 なので、余白が非対称な用紙での回転は `-LayoutMedia` でレイアウトの用紙を替えて確かめる。
ARES 2020〜2024 の PDF デバイスの余白のある用紙は上の余白だけが大きく、回転 90 度の向き（ARES は用紙を反時計回りに回す）を
取り違えるとクロップが横へずれる（docs/validation/architecture-decisions.md、2026-10-30）。
`-LogMediaMargins` は、PDF デバイスの全部の用紙の余白を証跡へ書く。`-FitPapers` でモデル空間の用紙を替え、
`-SkipModelFit` でモデル空間のケースを省く（IJCAD・BricsCAD は、PDF デバイスの A4 の正式名を確かめていない）。
AutoCAD（`-Product Acad*`）は、ほかの AutoCAD の試験と同じく CadRegGuard でプロファイルを記録して戻し、インストール済みの bundle があれば起動の前に止まる。

```powershell
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Ares2020 -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Ares2023 -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Ares2024 -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Ares2027 -AllowForceStop -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product BricscadV26 -SkipModelFit -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Ijcad2026 -SkipModelFit -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Acad2026 -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' -Confirm:`$false"
powershell -Sta -Command "& 'tests\tools\regression\peta_window_rotation_compat.ps1' -Product Acad2027 -LayoutMedia 'ISO_A3_(420.00_x_297.00_MM)' -Confirm:`$false"
```

GUI COM 駆動のため実行条件は window_contract_compat.ps1 と同じ。AutoCAD も `-Product Acad2026` で
同観点を測れる。あわせて CVPORT の意味（ペーパー空間=1・ビューポート内≠1）を読み取って記録する。
IJCAD は PlotRotation=180 のとき PDF に像が逆さまに描かれるため、CadPlotPort が IJCAD（と ARES）では発行時に
180→0・270→90 へ正規化する（このハーネスで実測・是正確認済み）。

## Officeの図形変換での位置回帰

`regression/emf_ungroup_geometry.ps1` は、EMFをExcelへ貼って「図形に変換」した前後のレンダを
画素で比較する。既存の `office_paste_regression.ps1` は寸法・分解数・文字数を測るが、分解後の
見た目を見ていない。外接矩形の集計では枠の内側の移動を検出できないため、レンダで判定する。

```powershell
pwsh -File tests/tools/regression/emf_ungroup_geometry.ps1 -Emf <emf>              # ファイル挿入
pwsh -File tests/tools/regression/emf_ungroup_geometry.ps1 -Emf <emf> -Clipboard   # クリップボード貼付
```

`-Clipboard` は利用者の実操作と同じ経路。クリップボードは対話セッションで実行する必要がある
（自動実行の文脈では保持者と競合して `OpenClipboard` が失敗し得る）。

`regression/clipboard_contention.ps1` は別プロセスの隠しウィンドウでクリップボードを実際に保持し、
CLIが非0で終わること、保持PIDを含む診断を出すこと、検証済みEMFを残すこと、既存内容を
変更しないことを確認する。
保持を解放した後は同じ入力を再実行し、成功へ回復することまでを一つの試験として判定する。

```powershell
pwsh -File tests/tools/regression/clipboard_contention.ps1
```

`regression/office_paste_regression.ps1` はExcel、PowerPoint、Wordのファイル挿入と
クリップボード貼付を検査する。
物理寸法の正解はOfficeアプリ間の一致ではなく、入力EMFヘッダーのFrameから独立に算出する。
Excelは幅と高さ、ページ内へ縮小されるPowerPointとWordは縦横比を承認済み許容0.24%で照合し、
分解後の図形数と文字数もファイル経路とクリップボード経路で一致させる。
既存Officeプロセスは既定で操作しない。
利用者が既存文書を保護しなくてよいと明示した実行だけ、`-AllowExistingOffice`を指定する。

## ページ設定pairwiseケース

`regression/page_setup_pairwise_cases.ps1` は空間、単位、尺度、回転、用紙探索の5因子について、
全77個の2因子水準組合せを覆う最小12ケースを定義する。
`regression/page_setup_pairwise_selftest.ps1` は全組合せの被覆と、ケース欠落を検出する
negative controlを実行する。
この自己試験はケース設計だけを保証し、CAD実機での設定適用や成果物照合を保証しない。

```powershell
pwsh -File tests/tools/regression/page_setup_pairwise_selftest.ps1
powershell -File tests/tools/regression/page_setup_pairwise_selftest.ps1
```

`regression/page_setup_pairwise_matrix.ps1` は12ケースを `accoreconsole`（AutoCAD 2026）の実機へ適用する。
計測器 `CPSETPAIRWISE` がケースの因子を対象レイアウトへ焼き、用紙探索の水準（同名・異名同寸法・該当寸法なし）は
実在デバイスの列挙から決定的に解決する。対比側レイアウトはA2固定とし、空間の取り違えを寸法で識別する。
判定は独立オラクルで行う: PDFページ寸法（MediaBoxの独立パーサ）、SVG寸法とのページ一致、
用紙縮退診断の有無（NoMatchingSizeだけがA4フォールバックを報告）、Layout DBObject非変更、
レジストリ復元、入力DWG不変。先に計測器を `dotnet build -c Release` でビルドする。

```powershell
dotnet build tests/tools/regression/window_contract/WindowContractProbe/WindowContractProbe.csproj -c Release
pwsh -File tests/tools/regression/page_setup_pairwise_matrix.ps1
```

## 永続sysvarのlint

`lint/sysvar_guard_lint.ps1` は tests/tools 配下の PowerShell が FILEDIA / OSMODE / SECURELOAD /
TRUSTEDPATHS / PLOTTRANSPARENCYOVERRIDE を `setvar` または COM `SetVariable` で変更する場合、
`CadRegGuard.ps1`（AutoCAD系のレジストリスナップショット→復元検証）を使っていることを機械検査する。
互換CAD GUIハーネスは永続先が別レジストリハイブのため lint 内の例外リストへ根拠付きで登録するが、
例外にも読み戻し検証付きの in-session 復元が実装されていることまで検査する。
ハーネスを追加・変更したら、この lint と自己試験を通してからコミットする。

`lint/trusted_paths_lint.ps1` は、tests/tools の .ps1 と .scr に、TRUSTEDPATHS と SECURELOAD を書き換える行
（LISP の setvar、COM の SetVariable、レジストリへの書き込み、.scr のコマンドの入力）が無いことを確かめる（単体試験から実行する）。
試験はこの 2 つの値を一時的に変えて戻す形も使わない。読み込む DLL のフォルダ（依存の DLL と lang を含む）と LISP は、
利用者のプロファイルで信頼済みのフォルダ（`C:\Tools\ACAD\dotnet`・`C:\Tools\ACAD\lisp`）の直下へコピーして読み込み、
終了時にコピーしたものだけを削除する（`regression/CadRegGuard.ps1` の `Get-CadRegSnapshot -StageToTrusted`。
`.scr` の行は `Get-AccoreNetloadLines`・`Get-AccoreLoadLispLines` が作る）。始める前に、その版の現在のプロファイルの TRUSTEDPATHS に
このフォルダがあることを読むだけで確かめ、無ければ、またはコピー先に同じ名前のファイルがあれば、何も変えずに止まる。
コピー先は記録のファイルにも書くので、中断したときは `cad_reg_restore.ps1` がコピーしたものだけを削除する。
信頼済みのフォルダは前後の比較の対象で、コピーを削除した後に、元からあるファイルの名前と内容が同じことを確かめる。
`cad-typography` と `pdf-emf` の手で実行する .scr（probe*.scr、gen_*.scr、survey_realworld.scr など）は、`C:\Tools\ACAD\lisp` の LISP と `C:\Tools\ACAD\dotnet` の DLL を読み込むので、
使う LISP と DLL をそのフォルダへコピーしてから実行し、終わったら削除する（`lint/trusted_paths_lint.ps1` が、.scr の読み込み先がこの 2 つのフォルダであることも確かめる）。

`lint/reg_restore_report_lint.ps1` は、finally で AutoCAD のプロファイルを戻す（`Restore-CadRegState`・`Restore-CadRegStateAndReport`）try が、
本体の例外を `catch { $runError = $_ }` で受け、その後で `Assert-CadRunAndRegRestore` を呼ぶことを確かめる（単体試験から実行する）。
本体が例外を投げると finally の後の文は実行されないので、finally の後で復元の結果を判定するだけでは、戻せなかった設定と残した記録が報告されない。
`Assert-CadRunAndRegRestore` は、本体の例外と復元の失敗の両方があれば、両方の理由を 1 つの例外の文に入れて投げる。

```powershell
pwsh -File tests/tools/lint/sysvar_guard_lint.ps1
pwsh -File tests/tools/lint/sysvar_guard_lint.ps1 -SelfTest
```

## 配布ZIPの検査

`distribution/package_verify.ps1` は4種類のZIPを再度開き、ステージングとの相対パス集合と
SHA-256を一致させる。
パストラバーサル、重複、0バイト、必須ファイル欠落、bundle XMLのModuleName欠落、
CAD SDK DLL混入、署名済み配布物の署名素材欠落を非0終了にする。
`dist/build_dist.ps1` と `dist/sign_dist.ps1` はZIP作成後にこの検査を自動実行する。

```powershell
pwsh -File tests/tools/distribution/package_verify.ps1 -SelfTest
pwsh -File dist/build_dist.ps1 -Only all -Version 1.0.0
```

## 選択絞り込みのAutoCAD回帰

`regression/object_keyword_probe.ps1` は、PETA の `O` を含むキーワード入力と選択状態を AutoCAD のプロンプト境界で計測する。
短縮名 `O`、グローバル名 `_Object`、日本語と英語、先選択、後選択、空選択、再選択を別ケースとして実行する。
日本語表示名と全角 `Ｏ` は負例であり、キーワードとして受理されず入力待ちが続くことを確認する。
選択には TESTCHART2 の真部分集合を使い、全選択と同じ結果でも通る判定を避ける。

先にプラグインをビルドし、`-ProbeOnly` で12ケースのスクリプトを CP932 で往復検証してから実機計測を実行する。

```powershell
pwsh -File tests/tools/build_e2e.ps1 -Products Acad
pwsh -File tests/tools/regression/object_keyword_probe.ps1 -Ver 2026 -ProbeOnly
pwsh -File tests/tools/regression/object_keyword_probe.ps1 -Ver 2026
```

計測器は次の試験専用環境変数を子 CAD プロセスへ渡す。

- `CADPETA_PROMPT_TRACE`：UTF-8の JSON Lines 出力先。
- `CADPETA_PROMPT_TRACE_RUN_ID`：一回の実行を識別する値。
- `CADPETA_PROMPT_TRACE_CASE_ID`：計測ケースを識別する値。
- `CADPETA_DUMP_SIDE_DB`：側DBメタデータの出力先から拡張子を除いた部分。

プロンプトtraceの各行は、`runId`、`caseId`、`pid`、`command`、`operation`、SDKの生の`status`と`stringResult`、`valueIsNull`、選択`count`を持つ。
各コマンドの完了は `operation=Command.End` の行で確認する。
プラグインが読み込まれたときは `operation=Plugin.Load`（`stringResult` はアセンブリの場所）、
コマンドラインへ 1 行出すたびに `operation=Message`（`stringResult` はその行）を書く。
側DBの計測ケースは `<stem>.raw.json` と `<stem>.ready.json` を出力し、複製直後とプロット準備後の図形件数、型別件数、有効範囲、ビュー、レイアウト、プロット設定を比較する。

traceの欠落、不正なJSON、実行IDの不一致、コマンド終了行の欠落、選択件数の不一致、側DBメタデータの欠落や不整合は失敗になる。
入力DWGのSHA-256が変化した場合と、所有する `accoreconsole` が制限時間内に終了しない場合も失敗になる。
負例だけは入力待ちを期待し、計測器が所有PIDを終了して拒否を判定する。

計測器は子プロセス起動直後に親プロセスの環境変数を存在有無まで復元し、終了時に `CadRegGuard.ps1` で AutoCAD のレジストリを復元して読み戻す。
成功時の集約結果は出力先の `results.json` に残る。

`regression/selection_plot_matrix.ps1` は、ビルド済みのAutoCADプラグインを `accoreconsole` で読み込み、選択絞り込み（範囲内のオブジェクト絞り込み）と構造SVG書き出しを検査する。
PETA は「_Object → 選択 → Enter（範囲=全体）」の対話列で駆動する。
先に `pwsh -File tests/tools/build_e2e.ps1 -Products Acad` を実行する。

```powershell
pwsh -File tests/tools/regression/selection_plot_matrix.ps1 -Ver 2026 -Standard
pwsh -File tests/tools/regression/selection_plot_matrix.ps1 -Ver 2026 -Command PETA -Space Model -CurrentLayerObjects
```

`-Standard` は次の 12 シナリオを、図面と環境変数の組み合わせごとに 1 回の起動で続けて実行する（5 回の起動）: PETA の Model・Paper・PaperMixed・LayoutModel、PETADATA の Model・Paper、PETADATADIAG の Model、PETA の Model に `-ForceCloneFallback`・`-CurrentLayerObjects`（単独と `-ForceCloneFallback` 併用）・`-NamedPlotStyles`（単独と `-ForceCloneFallback` 併用）。
各シナリオの前に UNDO のマークを置き、後でマークまで戻す。スイッチでシナリオを 1 つ指定すると、そのシナリオだけを 1 回の起動で実行する。
`Model` はモデル空間、`Paper` はレイアウト上の20×10mm矩形、`PaperMixed` は同じ矩形、通常Viewport、制御Viewportを同時に選択し、制御Viewportだけを除外する。
`LayoutModel` はレイアウト内でMSPACEへ入り、モデル空間のページ設定と選択対象を取り違えないことを検査する。
ペーパー空間のシナリオは `TESTCHART2_layout.dwg` の `PETA_A3` レイアウトを明示し、絞り込みサマリ（選択件数と対象件数）、ビューポート除外、部分出力確認、入力DWG不変、成果物数を自動照合する。
`PETADATA` は側DB複製を通らないため、`PaperMixed` の対象外である。
`-ForceCloneFallback` は環境変数 `CADPETA_FORCE_SELECTION_CLONE_FALLBACK=1` で Wblock を飛ばし、
オブジェクト複製フォールバック（`WblockCloneObjects`）の実走をログ照合で確認する。互換CADでも
同じ環境変数を設定して `gradient_compat_e2e.ps1` を流せばフォールバック経路で実走できる。
ペーパー空間の側DBは元図面全体の Wblock から作り、複製の経路を持たないため、`-ForceCloneFallback` は `Model` だけで受け付ける。
`-CurrentLayerObjects` は、画層 0 の矩形と、元図面の現在の画層の矩形の 2 つだけを選び、
両方が出力されることを確かめる。側DB は元図面の現在の画層を引き継ぎ、作業中の図面を側DB に切り替えた状態では
その画層の凍結状態を書くと AutoCAD が eInvalidLayer を返すため、この組み合わせを固定する。
`-NamedPlotStyles` は STB 図面 `STB_RECT.dwg` を使う。通常経路は Wblock が STB モードを引き継いで
成功し、`-ForceCloneFallback` 併用時は側DB既定（CTB）とのモード不一致で成果物なしの中止になる
ことを固定する（既知の制限）。
このハーネスが保証するのはconsoleプロットであり、GUI対話プロットの同等性は別の可視E2Eで確認する。

## グラデーション実機E2E

中心となるハーネスは `pdf-emf/gradient_compat_e2e.ps1` である。AutoCAD単体は
`gradient_e2e_autocad.ps1`、GUI対話プロットは `gradient_interactive_plot.ps1` を使う。
選択絞り込みのAutoCAD回帰は `regression/selection_plot_matrix.ps1`、互換CADは
`gradient_compat_e2e.ps1 -ExpectedGradientImageCount 0` を使う。
既定の `-Mode Print` はPETADIAG（PDF印刷経路）を駆動する。構造SVG書き出しの互換CAD実機は
`-Mode DataExport`（PETADATADIAG）を使う。この場合EMFとPDFは生成されず、グラデーションを
`-ExpectedGradientImageCount`（既定9）枚のPNG画像として検査する。入力DWGは
`gradient_e2e_autocad.ps1` が生成する9形状フィクスチャを使うと、AutoCADの成果物と
SVGのバイト一致まで比較できる。コミット済みのスナップショットは
`tests/assets/testcharts/gradient/gradient_e2e.dwg`（生成経緯は同ディレクトリのREADME）。

ハーネスに固定した運用規則:

- CADは製品・版付きProgIDと実行ファイルパスを照合し、別インスタンスへの誤接続を防ぐ。
- プラグインロードとPETA入力を分離する。IJCADは入力待ちで`SendCommand`が戻らないため、
  `PETADIAG`と後続入力を1バッチで送る。
- ARESは`LOADDOTNETAPPLICATION`、IJCAD/AutoCAD/BricsCADは対応する.NETロード経路を使う。
- ARESは通常編集・プロット可能なライセンス状態、ARESS残留プロセス0、対話デスクトップが必須。
  Windows PowerShell 5.1 Desktop/STAで可視起動し、対象exeに登録されたProgIDを動的列挙してROTを探す。
  まず `-ProbeOnly` でCOM接続を確認する。ROTタイムアウト時は画面上のライセンス／版拒否モーダルを
  確認する。読み取り専用は本試験不可。
- ARESは.NETコマンド中にCOMを拒否するため、PETADIAGと応答（`O, _ALL, Enter, Enter`。`-Mode DataExport`のPETADATADIAGは範囲プロンプトを持たないため`_ALL, Enter, Enter`）を、
  LISPの`(command ...)`で1回のSendCommandにまとめて送り、成果物の安定生成までCOMを呼ばない。ARES 2024 の PDF のグラデーションは画像9件ではなく3000以上の
  ベクターパスとして検査する。ARES 2020・2023・2027 の PDF は画像 9 件を出すので、画像 9 件を検査する（docs/validation/compatible-cad.md）。
- `FILEDIA`等の変更値を必ず復元する。`SECURELOAD`と`TRUSTEDPATHS`は変えない。DLL は、ARES では信頼するフォルダへのコピーから読み込む。
  ほかの製品では、DLL のフォルダが`TRUSTEDPATHS`にあることを読むだけで確かめ、無ければ何も変えずに止まる
  （common/compat_cad_com.ps1 の Assert-CadPluginFolderTrusted。IJCAD・BricsCAD は、この PC に製品が無く確かめていない）。
- 起動時の未保存図面を含め、全図面を`Close(false)`してから`Quit`する。保存確認を残さない。
- 自分が起動し、実行ファイルまで照合したPIDだけを終了対象にする。既定では強制終了せず、残留を失敗証跡にする。
  `Quit`の後に CAD が、製品の表（common/compat_cad_com.ps1 の`QuitExitCode`）の正常な終了の終了コードと違う値で終了したら失敗にする
  （証跡は`EXIT owned PID=... code=0x...`）。ARES 2020・2023・2024 は 0 で終了した。AutoCAD は正常に終了しても 0xBEBEBABE・0xD2E2BABE の
  ような値を返すので判定せず、ARES 2027・IJCAD・BricsCAD は測っていないので判定しない。
  CAD は終了の処理の中で異常終了しても`Quit`に成功を返すので、終了コードを見ないと合格になる
  （ARES 2020・2023・2024 の net48 が、解放されなかった DBObject のラッパーの解放でアクセス違反になった）。
  異常終了がファイナライザの実行の時機で決まる場合は毎回は起きないので、合格は起きなかったことしか示さない。
- 成功条件はコマンド終了だけでなく、SVG内画像数、EMF、診断JSON、縮退件数、残留プロセス0で判定する。

製品のインストール先やProgIDは環境依存である。スクリプトのparam既定値を確認してから実行し、
成果物は`$env:TEMP`以下へ出す。実測済み構成と結果は `docs/validation/` を参照する。
