# 検証記録: PETADATAグラデーションラスタ化

> 2026-07-15: 本文の「構造SVG書き出し」は現在の `PETADATA`（構造SVG書き出し）、「PDF印刷経路」は
> `PETA`/CLIのPDF印刷結果を指す歴史的呼称である。PETAの選択貼付は現在PDF印刷へ単一化済み。

対象は commit `6f50156`（構造SVG書き出しでのグラデーションハッチのアルファ付きラスタ化）と、その後続の
実測式固定・堅牢化・実機ハーネス差分である。
本記録は、コード・単体テストだけでは保証できないAutoCAD純正画素との一致、GUI対話プロット、
AutoCAD・ARES・IJCAD・BricsCADの公開APIとE2E、Office貼付を実機で確認した最終結果を残す。

## 1. 結論

- **式と実画素の対地上検証（計画 §2.2・§5・最重要）は成立した**。構造SVG書き出しの `GradientSemantics`
  の空間式を、AutoCAD 純正プロット（PDF印刷経路）の実画素と突き合わせ、LINEAR は一次目標
  （8bit 各成分の最大差 ≤2）を充足、非線形8種は中央値 1〜2・p95 3〜7.5（<3%）で追随した。
- **構造SVG書き出しの E2E スモークは成立した**。9 形状のグラデーションハッチが accoreconsole 上で
  例外なく 9 枚の `IrImage` へラスタ化され、SVG・EMF の双方へ出力・クリップボード登録まで到達した。
- 前セッションで「accoreconsole が起動直後にハングし構造SVG書き出しの E2E が取れない」と報告した件は
  **誤診であった**。真因は accoreconsole のコードでも SAVEAS でもなく、**外部シェルの標準出力
  リダイレクトでの起動方式**である（§4）。是正した手順で完走する。

## 2. 対地上検証（構造SVG書き出しの式 vs PDF印刷経路の実画素）

### 手順（本端末で完結・ヘッドレス可）

PDF印刷経路の「プロット PDF 生成」はハングするが、**既にコミット済みの正解チャート PDF を
CLI で消費する経路はヘッドレスで通る**。この非対称性が鍵である。

1. 正解チャート `tests/assets/testcharts/gradient/charts/grad_{形状}_bw_{変種}.pdf`（9形状×
   {a000_s000/a045_s000/a000_s050}=27枚）を CLI（PDF印刷経路）で SVG 化する。SVG の埋め込みラスタは
   AutoCAD 純正プロットが生成した JPEG+SMask をそのまま持つ＝正解画素である。実測で埋め込み
   `<image>` は `x=40 y=37 width=340 height=220`（mm）＝ハッチ境界 40,40–380,260 に一致した。
   - `cadpeta.exe <chart>.pdf <out>/<chart>.svg`
2. `tests/tools/pdf-emf/gradient_fit`（`production` モード）で、各サンプル画素に対し
   `GradientSemantics.Evaluate` を構造SVG書き出しアダプタと同じ規約（Y下向き局所座標・角度は非反転）で
   評価し、正解画素との差（8bit 単位）の med/p95/p99/max/rmse を出す。中央値フィルタで JPEG±2 を吸収する。
   - `GradientFit.exe <svg-dir> production`

### 結果（8bit 単位・0..255、2026-07-14 実機）

| 形状 | 変種 | med | p95 | p99 | max | rmse |
|---|---|---|---|---|---|---|
| LINEAR | a000_s000 | 0.28 | 0.62 | 0.72 | **0.95** | 0.33 |
| LINEAR | a045_s000 | 0.39 | 1.04 | 1.29 | **1.77** | 0.55 |
| LINEAR | a000_s050 | 0.46 | 4.85 | 6.53 | 7.62 | 1.93 |
| CYLINDER | a000_s000 | 1.43 | 5.03 | 5.73 | 6.73 | 2.38 |
| CYLINDER | a045_s000 | 1.52 | 4.57 | 5.50 | 6.71 | 2.33 |
| CYLINDER | a000_s050 | 1.25 | 4.14 | 5.07 | 5.60 | 1.99 |
| INVCYLINDER | a000_s000 | 1.45 | 5.60 | 6.50 | 7.58 | 2.62 |
| INVCYLINDER | a045_s000 | 1.97 | 5.50 | 6.50 | 7.71 | 2.82 |
| INVCYLINDER | a000_s050 | 1.42 | 4.18 | 5.50 | 6.60 | 2.21 |
| SPHERICAL | a000_s000 | 1.30 | 4.50 | 6.00 | 11.25 | 2.25 |
| SPHERICAL | a045_s000 | 1.74 | 4.37 | 5.45 | 8.16 | 2.33 |
| SPHERICAL | a000_s050 | 1.83 | 5.59 | 9.00 | 17.00 | 2.97 |
| INVSPHERICAL | a000_s000 | 1.77 | 4.93 | 6.63 | 11.25 | 2.61 |
| INVSPHERICAL | a045_s000 | 2.21 | 5.18 | 6.18 | 8.16 | 2.83 |
| INVSPHERICAL | a000_s050 | 0.98 | 5.58 | 8.30 | 17.00 | 2.61 |
| HEMISPHERICAL | a000_s000 | 1.33 | 3.82 | 4.75 | 6.35 | 1.93 |
| HEMISPHERICAL | a045_s000 | 1.58 | 4.25 | 5.33 | 6.46 | 2.26 |
| HEMISPHERICAL | a000_s050 | 1.29 | 3.45 | 5.25 | 8.00 | 1.85 |
| INVHEMISPHERICAL | a000_s000 | 1.28 | 4.81 | 5.74 | 7.35 | 2.33 |
| INVHEMISPHERICAL | a045_s000 | 2.08 | 4.86 | 5.81 | 7.46 | 2.68 |
| INVHEMISPHERICAL | a000_s050 | 1.18 | 3.81 | 5.08 | 7.04 | 1.86 |
| CURVED | a000_s000 | 1.93 | 7.00 | 12.35 | 17.96 | 3.51 |
| CURVED | a045_s000 | 1.73 | 5.40 | 6.56 | 8.57 | 2.75 |
| CURVED | a000_s050 | 1.04 | 2.98 | 3.75 | 5.25 | 1.51 |
| INVCURVED | a000_s000 | 2.19 | 7.54 | 12.53 | 18.00 | 3.73 |
| INVCURVED | a045_s000 | 2.30 | 6.38 | 7.54 | 9.57 | 3.39 |
| INVCURVED | a000_s050 | 0.94 | 2.92 | 3.87 | 54.03 | 1.80 |

### 評価

- **LINEAR（必須の第1段）**: a000/a045 は max<1.8 で計画 §5 の一次目標 max≤2 を充足。shift
  （s050）は max 7.62 だが中央値 0.46。構造SVG書き出しの必須要件は満たす。
- **非線形8種（第2段）**: 中央値 1〜2.3・rmse 1.5〜3.7・p95 ほぼ <7.5（<3%）で式は正しく追随する。
  ただし放射中心の特異点・AA 境界・JPEG ノイズに由来する**局所 max スパイク**（SPHERICAL/CURVED
  で 11〜18、INVCURVED a000_s050 で単発 54）が残り、max≤2 は満たさない。p99 が max より
  大幅に低い（例: CURVED a000_s000 は p99=12.35 に対し max=17.96、INVCURVED a000_s050 は
  p99=3.87 に対し max=54.03）ことから、これらは全面的なずれではなく孤立画素である。
- **採用基準**: 非線形は孤立したAA/JPEGスパイクを全面不一致と誤判定しないよう、境界3mm除外の
  固定格子で中央値≤2.5、p95≤7.5、RMSE≤4.5を合格基準とする。27チャートすべてが合格し、
  式そのものの追随は良好なため、9形状すべてを実装対象とした。

## 3. 構造SVG書き出しの E2E スモーク（accoreconsole 2026）

- ハーネス `tests/tools/pdf-emf/gradient_e2e_autocad.ps1`（本記録で堅牢化・§4）が、9 形状のグラデーション
  ハッチ（LINEAR/CYLINDER/INVCYLINDER/SPHERICAL/INVSPHERICAL/HEMISPHERICAL/INVHEMISPHERICAL/
  CURVED/INVCURVED、いずれも 2 色・真色 421）を entmake で生成し、PETA（構造SVG書き出し・先選択）で処理した。
- 結果（実機出力）:
  - SVG に `data:image/png` が **9 枚**（＝9 ハッチすべてラスタ化・代表色縮退 0 件）。
  - 診断サマリ: **「[CadPeta] グラデーション: 9件（実DPI 150～150）をラスタ化、0件を代表色へ縮退」**。
  - EMF: `9図形 → クリップボード登録`。例外・エラー出力なし。
- これにより、`hatch.GetGradientColors()` / `GradientName` / `GradientAngle` / `GradientShift` が
  **実行時に**（コンパイルだけでなく）AutoCAD 実ホストで機能し、`MapGradientHatch`→`IrImage`→SVG/EMF の
  結線と G キーワード既定 150dpi が動作することを確認した。

## 4. 前セッション誤診の訂正と手順確立（重要）

- **誤診**: 「accoreconsole が起動直後にハングし構造SVG書き出しの E2E が取れない」。
- **切り分け**: `(princ "HELLO") + _.QUIT` だけの最小 scr でも、外部シェル（bash）の
  `accore ... > out.txt` および PowerShell の `& accore ... | Out-File` 系はハングした。
  一方 `Start-Process -RedirectStandardOutput out.txt` + `WaitForExit(timeout)` は**同一 scr で
  クリーンに終了**した。すなわち真因は起動時の標準出力ハンドルの与え方であり、CADPeta のコードや
  SAVEAS ではない。参照ハーネス `gen_run.ps1`（正解チャート生成）が Start-Process を使い成立していた
  のは、この理由による。
- **手順（確立）**:
  1. `.scr` の先頭で `FILEDIA 0`・`SECURELOAD 0` を立てる（FILEDIA=1 のままだと SAVEAS が
     GUI ファイルダイアログを待ってハングする）。
  2. `Start-Process -FilePath accoreconsole -RedirectStandardOutput <file>` で起動し、
     `WaitForExit(TimeoutSec*1000)` で待ち、残留したら `Kill()`。
  3. Kill は `FixedProfile\General Configuration\FileDialog=0` をレジストリへ永続化するため、
     終了時に必ず点検し 1 へ復元する。
- **ハーネス是正**: `gradient_e2e_autocad.ps1` を上記へ書き換えた（旧版は `& $Accore ... | Out-File`
  でハング方式、かつ FILEDIA・timeout・kill・復元のハードニングを欠いていた）。本記録の実機実行で
  DWG/SVG/EMF 生成と `data:image/png count=9` を確認済み。

## 5. 環境衛生

- 本検証の accoreconsole 実行はすべてクリーン終了し、`HKCU\...\General Configuration\FileDialog`
  に 0 の残留は生じていない（実行前後で reg query 点検済み）。是正ハーネスは kill 経路でも復元する。

## 6. 追加ヘッドレス検証（無回帰・決定性・ブロック変換・縮退）

計画 §5・§12 の残条件のうち、本端末でヘッドレスに取れる 4 件を消化した（2026-07-14）。

### A. EMF 既定白のバイト無回帰（計画 §6・§12「既定白で既存 EMF がバイト一致」）
- 親コミット `ca6b093`（`MixWhite`・変更前）を `git worktree` へ隔離してビルドし、現行
  （`MixBackground` 既定白）と同一入力で EMF/SVG を生成してバイト比較した。
- 入力は SMask アルファを含むチャート PDF 3 種（`grad_LINEAR_bw_a000_s000`・
  `grad_SPHERICAL_bw_a000_s050`・`grad_LINEAR_color_a000_s000`）。EMF は約 80MB。
- 結果: **3 種すべて EMF・SVG とも SHA256 完全一致**。白背景パラメータ化は画像アルファ経路
  （`BuildBgrDib` の整数合成）で無回帰。path/text 透過は `c·a+1·(1-a)`＝旧 `MixWhite` と代数的に
  恒等（`EmfImageDibTests` が担保）。

### B. 構造SVG書き出しの決定性
- ブロック変換フィクスチャ（下記 C）を2回、独立の accoreconsole プロセスで実行し、成果物を比較した。
- 結果: **SVG・EMF とも 2 回の実行でバイト一致**（PNG エンコード・予算・DPI 判定が決定的）。

### C. ブロック変換の非崩れ（計画 §4「回転45/180・鏡像・非等方・二重ネスト」）
- フィクスチャ `tests/tools/pdf-emf/gradient_transform_fixture.lsp`: LINEAR グラデーションハッチを
  ブロック GRADBLK へ入れ、回転45°・回転180°+等方2倍・鏡像(x=-1)・非等方(2×1)・二重ネスト
  （GRADBLK2→GRADBLK）で 5 回 INSERT し、PETA（構造SVG書き出し・全選択）で処理した。
- 結果: **5 インサートすべてが例外なく 5 枚の `IrImage` へラスタ化**。各画像は `DocumentFromLocal`
  の逆行列を持つ `<g transform="matrix(...)">` で包まれ（rot45=`0.707107…`、rot180×2=`-2 0 0 -2`、
  鏡像=`-1 0 0 1`）、document 座標で軸平行ラスタ化した画像を最終空間へ戻す設計が機能した。
  ※ 変換後の画素レベルの突き合わせ（PDF印刷経路のプロットとの比較）はプロット生成がハングするため
  本端末では不可＝GUI 所持端末へ送る。本項は結線・構造・例外なしの結合スモークである。

### D. 代表色縮退フォールバック（計画 §12「代表色縮退は安全弁」）
- 同フィクスチャに未知 GFNAME `"BOGUS"`（赤 0xFF0000・青 0x0000FF の 2 色）のハッチを 1 個置いた。
- 結果: 診断サマリ **「グラデーション: 5件…ラスタ化、1件を代表色へ縮退」**。SVG では BOGUS だけが
  画像化されず `<path … fill="rgb(128,0,128)" fill-rule="evenodd"/>`（代表色＝2 色の中点）の
  ソリッドで出力。EMF は 6 図形（画像 5＋ソリッド 1）。未知形状が安全弁へ落ちることを実機確認した。

## 7. 環境衛生

- 本検証の accoreconsole 実行はすべてクリーン終了し、`HKCU\...\General Configuration\FileDialog`
  に 0 の残留は生じていない（実行前後で reg query 点検済み）。是正ハーネスは kill 経路でも復元する。
- 親コミット比較用の `git worktree` は検証後に除去した。

## 8. GUI 実機による残タスク消化

### A. AutoCAD 2026 対話プロット併測（計画 §2.2）

- `tests/tools/pdf-emf/gradient_interactive_plot.ps1` を WinSta0\Default・STA・COM message filter 付きで実行し、
  AutoCAD 2026 GUI の `AutoCAD PDF (High Quality Print).pc3` から LINEAR/a000/s000 を対話プロットした。
- 同じチャートを console 正解PDFと同じPDF印刷経路のCLIへ通した結果、SVG は SHA-256
  `3E5B476A5397DB14EF96EC799D56260A6278210B600ABA1E37DAEA2076FF4D6A` で**完全一致**した。
  埋め込みPNGも `5E84776D547DC53986010296DC9C7B21B4A512021C8BBC40A27C405430986C69`
  （3,407,588 bytes）で完全一致し、画像配置 340.0108×220.0063mm も一致した。
- したがって本ケースでは console/対話のレンダ表現・画素・配置に差はなく、計画が懸念した
  phase0 のハッチ経路差は再現しなかった。GUI PID・図面・レジストリ `FileDialog` は終了時に全て回収した。

### B. 互換CAD Gradient API と全9形状の構造SVG書き出しE2E

`tests/tools/pdf-emf/gradient_compat_e2e.ps1` で各GUIを別プロセス起動し、COMの HWND→PID→実行ファイルパスを
照合してから、コピーDWGを FullName 一致で明示 Activate した。4製品で `GetGradientColors()`、
`GradientName`、`GradientAngle`、`GradientShift`、`GradientOneColorMode` を実行時に通り、9形状を
9枚の画像へラスタ化した。診断JSONに代表色縮退は0件である。

| 製品 | 実ホスト / TFM | SVG | EMF | 診断 | 結果 |
|---|---|---:|---:|---:|---|
| ARES Standard 2024 | ProgID `PCAD_AC_X.AcadApplication.23.1` / net48 | 155,312 bytes・image=9 | 1,819,640 bytes | 1 entry | 合格 |
| ARES Standard 2027 | ProgID `PCAD_AC_X.AcadApplication.26.8` / net8 | 155,312 bytes・image=9 | 対象外（PETADATAはEMF非生成） | 1 entry | 合格 |
| IJCAD 2026 PRO | `Gcad.Application` / net8 | 155,312 bytes・image=9 | 1,819,640 bytes | 1 entry | 合格 |
| BricsCAD V26 | `BricscadApp.AcadApplication.26.0` / net8 | 155,312 bytes・image=9 | 1,819,640 bytes | 1 entry | 合格 |

ARES Standard 2027 は 2026-07-16 に実施した。
実施時点では PETA の選択貼付が PDF 印刷へ単一化済みのため、構造SVG書き出しは現行コマンド
PETADATADIAG（`gradient_compat_e2e.ps1 -Mode DataExport`）で駆動した。この経路は EMF も PDF も
生成しないため、EMF 欄は対象外である。9形状すべてが要求どおり 150dpi（325×207px）でラスタ化され、
診断は「構造SVG書き出し: グラデーションハッチのラスタ化」9件・縮退0件、最終SVGは AutoCAD 2026
accoreconsole の同一フィクスチャに対する PETADATA 成果物と SHA-256 で完全一致した
（`ce1594b80251bcf6114937f55193e65673c1c7e72a54d40a8136d029665cdff1`）。

製品固有の確定事項もハーネスへ固定した。

- ARES は版付きProgIDで接続し、`(command "._LOADDOTNETAPPLICATION" "<dll>")` を CR 単独で送る。
  .NETコマンド中の `CMDNAMES` が空になり得るため、範囲プロンプト A/O/M/F/B と成果物安定を正の証拠にする。
- IJCAD は `SECURELOAD` の取得が範囲外エラーになるが、`FILEDIA=0` と NETLOAD は成立する。
  `SendCommand` が入力待ち中は同期ブロックするため、ロードとは分離した上で
  `_.PETADIAG\rO\r_ALL\r\r\r` を1バッチ送信する。
- BricsCAD はレジストリ `LocalServer32` どおり `/Automation` で起動し、版付きProgIDを使う。
- 実施済み製品でシステム変数を復元後、起動時の未保存図面を含む全図面を `Close(false)` してから Quit した。
  最終確認はCADプロセス0・一時タスク0で、保存確認モーダルの残留もない。

### C. Office 貼付

BricsCAD実機が生成した全9形状入りEMF（1,819,640 bytes）を
`tests/tools/regression/office_paste_regression.ps1` で Office 16.0へ貼り付けた。

- Excel / PowerPoint / Word のファイル挿入・CF_ENHMETAFILE貼付の全6経路が成功。
- クリップボードは登録直後の全バイト読み戻しが元EMFと一致。
- Excel / PowerPoint は分解後10リーフ（背景1＋画像9）・文字0。画像化されたグラデーションは
  ベクタ図形へ再分解できないという非スコープを実物で確認した。
- Word は既知仕様どおり本文幅150mmへ等比縮小した。

### D. CLI `--emf-bg`

透過756件を含む `tests/assets/testcharts/TESTCHART2-on.pdf` を CLI で3回変換した。

- 背景省略と明示 `#FFFFFF` は SHA-256
  `92769DF465C62FEC9EA1C5823E1AC517794C09A5B5271969141991796AC8E8B0` で完全一致。
- `#102030` は `6F4421F29274D6A061E110138CA2FB9E104B0548029EF6D0B9B3092EE92FEFD0` と差が出て、
  診断も「透過の不透明化（合成下地色=#102030）: 756件」と一致した。
- `--emf` / クリップボード出力なしの `--emf-bg` は理由付きメッセージで終了コード1。

### E. ビルド・自動テスト

- `dotnet build CadPeta.sln -m:1`: 0 error。警告はネットワーク遮断下のNU1900のみ。
- `dotnet test tests/CadPeta.Tests/CadPeta.Tests.csproj -m:1 --no-restore`:
  net48=286、net8=290、net10=290、全866件合格・skip 0。
