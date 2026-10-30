# 検証記録: 配布物

| 項目 | 内容 |
|---|---|
| 日付 | 2026-07-12 |
| 範囲 | AutoCAD bundle・互換CAD登録スクリプト・CLI zip・LICENSE/THIRD-PARTY-NOTICES・PdfImageDecoder生RGB・PowerPoint分解閾値 |
| 前提 | origin/main = fcb00e6。決裁済み: 製品名=CADPeta・ライセンス=MIT・公開範囲=実物図面不可/テストチャート可 |
| 成果物の実体 | dist/（bundle テンプレート・compat スクリプト・CLI 用 README・build_dist.ps1）、THIRD-PARTY-NOTICES.txt、tests/tools/distribution/ppt_ungroup_threshold/ |

本記録は実装、実測、判定根拠、再現手順を残す。
実機（AutoCADまたは互換CADのGUI）で確認する項目は§9に分離する。

## 1. T0: PackageContents.xml 読込属性の記法（§9-1 の解消）

公式リファレンス（ComponentEntry Element Reference）と Autodesk 公式ブログ（AutoCAD 2025:
Update Your PackageContents.xml with RuntimeRequirements）の原文で確定した。

- 読込理由（`LoadOnAutoCADStartup` / `LoadOnCommandInvocation` / `LoadOnAppearance` /
  `LoadOnProxy`）は **ComponentEntry の真偽値属性として直接書ける**（簡潔形）。公式ブログの
  net48/net8 分割サンプルが `LoadOnAutoCADStartup="True"` を属性で使用している。
- **明示 `<LoadReasons>` 要素が要るのは、読込理由を False へ無効化する場合のみ**（原文:
  "If parameters need to be disabled (set to False), the LoadReasons element must be specified
  along with the parameters set to False"）。有効化だけなら属性形で足りる。
- **複数 ComponentEntry がそれぞれ独自の RuntimeRequirements（OS/SeriesMin/SeriesMax）で
  別 ModuleName を指す構成**は公式サンプルそのもの（net48=SeriesMax R24.3 /
  net8=SeriesMin R25.0）。

判定: CADPeta は `LoadOnCommandInvocation="True"` ＋ `<Commands>`（PETA / PETADIAG）の
デマンドロードを採る。毎起動での常駐（PdfPig/GDI の読込）を避ける利用者価値があり、公式が
起動時間の観点で推奨する形。読込理由を True の属性で書くため明示 LoadReasons 要素は使わない。

## 2. T1-a: PdfImageDecoder の生 RGB 誤読の修正

### 2.1 欠陥（現物確認）

`PdfImageDecoder.ComposePng` は、非 JPEG の色ストリームを ColorSpace に依らず
`GrayToBitmap`（1バイト/画素）で復元していた。自前 PDF 出力（PdfWriter）の画像は
`/ColorSpace /DeviceRGB /BitsPerComponent 8`（3バイト/画素）のため、先頭バイトのグレーと
誤読して色・寸法が総崩れになる。実プロット PDF の画像は DCT 系（JPEG 経路）のため通常経路
では顕在化せず、「自作 PDF 出力を CLI へ再入力する往復」でのみ壊れる。

### 2.2 修正

`DecodeColorBitmap` を新設し、非 JPEG は画像辞書の `/ColorSpace`・`/BitsPerComponent` を読んで
分岐する:
- DeviceRGB・8bpc → 新設 `RgbToBitmap`（3バイト/画素、R,G,B→GDI B,G,R,A へ写す）
- DeviceGray・8bpc → 既存 `GrayToBitmap`（1バイト/画素）
- それ以外（ビット深度≠8・未対応色空間）→ 握りつぶさず `NotSupportedException`（対象サイズ付き）

`GrayToBitmap`・`RgbToBitmap` の双方にバイト数不足の明示検査（`FormatException`）を足し、
誤読を確実に例外へ落とす。

### 2.3 検証

- **往復回帰テスト追加**（PdfImageRoundTripTests.cs）: 非対称・多色・一部半透明の 4×3 画像を
  PdfWriter で PDF 出力 → PdfIrAcquirer で再取得 → 埋め込み PNG を PdfPngDecoder で復号し、
  全画素の RGBA が原画素と一致。修正前は GrayToBitmap 経路で色が総崩れになる欠陥を捕える
  （色・上下向き・アルファの3点を同時に検査）。
- **無回帰（機械証明）**: 修正前後で CLI 基準9ケース33ファイル（SVG/EMF/diag）の SHA256 が
  **完全一致**。手法=同一作業ツリーで PdfImageDecoder.cs だけを `git stash` して前後ビルド・
  採取・比較（reg_base vs reg_new）。実プロット PDF の画像は JPEG 経路で不変、修正は非JPEG・
  非グレー経路のみに効くことを実証した。
- **3系全合格**: net10=221・net8=221・net48=217。

## 3. T1-b: PowerPoint「図形に変換」閾値の測定

### 3.1 ハーネス

phase3 の Excel 版（emf_ungroup_threshold）を PowerPoint COM へ移植した
（tests/tools/distribution/ppt_ungroup_threshold/: ungroup_case_ppt.ps1・run_case_ppt.ps1・test_n_ppt.ps1）。
コーパス生成器（gen_corpus.py）と二分探索の方針は Excel 版と共通。PowerPoint は不可視での
貼付が不安定なため Visible=msoTrue で走る（閾値は図形数だけに依存する内部制約であり可視・
不可視で不変）。貼付は Slide.Shapes.PasteSpecial(ppPasteEnhancedMetafile=2)。

### 3.2 結果（PowerPoint 16.0、Microsoft 365）

- **成功最大 N=10,000／失敗最小 N=10,001。Excel と完全に同一の閾値**（分解後の実図形数では
  10,001＝N＋PowerPoint が自動付与する全面背景図形1個。この +1 も Excel と同じ）。各3回で
  6/6 完全再現・フレークなし。
- ブラケット: N=8,000=成功、12,000/16,000/24,000=失敗。境界詰め: 10,000=成功、
  10,001/10,010/10,025/10,050=失敗。
- **支配変数は図形数のみ**。サイズ・幾何量との逆転対照で証明:
  N=10,000×50セグメント（EMF 3,947,212 bytes）=成功、
  N=10,001×50セグメント（3,956,420 bytes）=失敗、
  N=10,001×1セグメント（417KB 級）=失敗、N=500×200セグメント（682,436 bytes）=成功。
- エラー実体: `COMException 0x80004005 (E_FAIL)`。貼付・実寸表示は正常、影響は分解不可のみ。
  ハングは全ケース観測ゼロ（数秒で例外）。残留 POWERPNT プロセスゼロを確認。

### 3.3 反映

docs/04 §6 の該当箇所を「Excel と PowerPoint のどちらでも失敗する／閾値は両者で同じ」へ更新した
（利用者文書であり、基準の docs/01・02 ではない）。

## 4. T2: AutoCAD 向け CADPeta.bundle

- **dist/bundle/CADPeta.bundle/PackageContents.xml**: 3 Components×ComponentEntry。
  net48=SeriesMin R24.0/SeriesMax R24.3（AutoCAD 2021-2024）、net8=R25.0/R25.1（2025-2026）、
  net10=R26.0-（2027+）。各 ComponentEntry は `LoadOnAutoCADStartup="True"` ＋ Commands
  （PETA / PETADIAG、GroupName=CADPeta）。ProductCode={F79DD514-...}。整形式 XML を検証済み。
- **読込方式は起動時ロード（LoadOnAutoCADStartup）へ確定**（当初案の LoadOnCommandInvocation
  から変更）。理由: accoreconsole は ApplicationPlugins の AutoLoader を走らせず、
  コマンド起動時ロードも起動時ロードも不発（§9-1 で実測）。ヘッドレスで検証できる範囲を最大化し、
  かつ StationLabeler 実績のある起動時ロードを採る。起動時に読むのは小さいプラグイン DLL 本体
  だけで、PdfPig/GDI 等の依存は PETA 実行時に遅延ロードされるため常駐コストは無視できる。
- **install.bat / uninstall.bat**: %APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle へ
  xcopy（管理者権限不要＝NFR-1）。既存削除・コピー失敗・ソース不在のエラー処理付き。
- **README-install.txt**: 導入手順・最初の一歩・SECURELOAD 警告時の TRUSTEDPATHS 追加手順。
- 組立: dist/build_dist.ps1 -Only bundle が3系を Release ビルドし Contents/net48・net8・net10 を
  構成。**AutoCAD 参照 DLL（accoremgd/acdbmgd 等）の混入を機械検査**（Assert-NoForbidden、
  ExcludeAssets=runtime の裏取り）。成果物 CADPeta-AutoCAD-1.0.0.zip（6.89 MB）。

## 5. T4: CLI zip 配布

- framework-dependent 単一ファイル（win-x64）。**cadpeta.exe 6.46 MB**（計画の実測 6.5MB と一致）、
  zip 2.22 MB。self-contained のランタイム（coreclr.dll 等）が混入していないことを機械検査。
- **配布 exe = 開発ビルドの実証**: publish した exe で TESTCHART2-off を変換し、SVG・EMF とも
  開発ビルド（Debug）の基準と **SHA256 バイト一致**。
- README-cli.txt に .NET 10 ランタイム前提（Microsoft.NETCore.App、Desktop 不要）を明記。
- 成果物 CADPeta-CLI-1.0.0.zip。**self-contained（76.3MB）併載は行わない＝fd 単一ファイルのみ**で
  確定（2026-07-12 利用者決定）。README に .NET 10 ランタイム導入の1行を明記済み。

## 6. T5: LICENSE / THIRD-PARTY-NOTICES

- LICENSE（MIT・Copyright (c) 2026 WATANABE Kazufumi）は初版から tracked。名義は現行のまま
  （利用者承認済み）。
- **THIRD-PARTY-NOTICES.txt**（新規・ルート）: 実行時に再配布する依存のみ。PdfPig 0.1.15=
  Apache-2.0（UglyToad。PDFBox/FontBox・Adobe AFM/CMap の帰属を含む）、.NET Foundation/
  Microsoft の System.*・Microsoft.*=MIT。Apache-2.0 と MIT の全文を掲載。nuspec 実確認で
  ライセンスを裏取り（PdfPig=Apache-2.0、他は全て MIT）。全配布物（bundle・compat・CLI zip）へ
  LICENSE とともに同梱。Cp932Encode.bin は自前生成物のため対象外。

## 7. T3: 互換CAD 3製品の登録

- **register.ps1 / unregister.ps1**（-Product ares|ijcad|bricscad）: 選択した製品で検出したすべての版を列挙し、
  版ごとのHKCUのDemandLoadレジストリ（Applicationsキー）へLOADER、LOADCTRLS=2、MANAGED=1、DESCRIPTIONを書き、読み戻す。
  HKLMはインストール済み版の検出にだけ使い、登録と解除はHKCUで対称に行う。
  値構成と型は、本端末のIJCAD純正キー（Professional\R26\ja-jp\Applications\IJDIM）と一致する。
- **README-compat.txt**: NETLOAD の逃げ道（BricsCAD=_.-NETLOAD、IJCAD=NETLOAD、
  ARES=LoadDotNetApplication。読込コマンド名は製品ごとに実測で確定）と製品別注記
  （IJCAD・ARES とも HKCU＝ユーザー単位で登録・管理者権限不要）を記載。
- **BricsCAD の HKCU 往復を機械実証**（CAD 起動なし）: register で
  HKCU\...\V26x64\ja_JP\Applications\CADPeta に LOADER（配布 DLL 実パス）/LOADCTRLS=2/
  MANAGED=1/DESCRIPTION を作成 → 値を読み戻し一致 → unregister で削除 → reg query で消滅確認
  （exit=1）。検証で作った空の Applications 親キー（元不在）も除去し残留ゼロを確認。
- **IJCAD・BricsCAD の HKCU 自動読込を実機で確認**（2026-07-17・非管理者）: HKCU の
  Applications\CADPeta（register.ps1 と同じ LOADER/LOADCTRLS=2/MANAGED=1）を作成 → GUI 起動 →
  起動時自動読込 → PETA が EMF/SVG/PDF を生成 → キー削除、の往復が両製品で成立。
  IJCAD 2026 PRO の実効ハイブは HKCU（言語キーは HKCU=ja-JP／HKLM=ja-jp と大小文字が揺れる）。
  BricsCAD V26 は /Automation 起動でも読込され、セキュリティ警告なし。
- **ARES の HKCU 自動読込を実機で確認**（2026-07-17・非管理者、Standard 2024・2027 の両版）:
  実効パスは HKCU\SOFTWARE\Graebert GmbH\<製品名>\Applications（x64 サフィックスなしの製品キー。
  値構成は純正 XTRUN と同じ LOADER/LOADCTRLS=2/MANAGED=1）。HKLM の security（secureload=1、
  trustedpaths は Administrators のみ書込可）はそのままで、信頼パスに無いフォルダの DLL でも
  起動時自動読込→PETA が EMF/SVG/PDF を生成（信頼登録の有無を振った対照試験で、レジストリ
  自動読込は信頼パス門を通らないことを確認）。信頼確認ダイアログが出るのは対話的
  LOADDOTNETAPPLICATION のみ。なお HKCU の security\trustedpaths も有効だった（必要時の代替）。
- **配布スクリプトの複数版選択を再検証**（2026-07-20）:
  旧`register.ps1`はARES 2024と2027の同居時に2027だけを選び、`-AresYear 2024`も登録先を2024へ切り替えなかった。
  2026-07-17の試験は各版へ同じ値を個別配置したものであり、完成スクリプトの版選択を通していなかった。
  3製品とも全版列挙へ改め、ARESは2024にnet48、2027にnet8のDLLを対応づけた。
  隔離HKCUキーで各製品2版、HKCUとHKLMの重複、HKLMだけにある版のHKCU登録、途中失敗時の既存値復元と部分登録なし、全解除をWindows PowerShell 5.1から確認した。
  配布スクリプトで実機へ登録後、手動LOADを省いた起動でARES 2024、ARES 2027、BricsCAD V26、IJCAD 2026のPETAがSVG、EMF、PDFを生成した。
  ARES 2027の既知の二重起動回避で最初の非表示PIDが残ったため、自分が起動したPIDだけを承認後に停止し、最終的な残留CADプロセスはゼロだった。
- **標準入口をinstall.bat / uninstall.batへ変更**（2026-07-20）:
  `.ps1`の右クリック「PowerShellで実行」はWindowsのビルド、実行ポリシー、Mark of the Webの組合せにより、
  スクリプト本文が実行される前に窓が閉じ得る。この場合はps1側のtrapも報告も動かず、利用者には
  「何も表示せず閉じた」となる。入口はAutoCAD版zipと同名・同様式のinstall.bat / uninstall.batとし、
  結果表示の後にpauseで必ず待ち、powershell.exe（フルパス）をプロセス限定のBypassで起動して
  実行ポリシー起因の即時終了を排除した。register.ps1 / unregister.ps1はbatから-NoPauseで呼ぶ
  内部部品として同梱する（PowerShellからの直接実行も引き続き可能）。
  スクリプトの表示と注釈はAutoCAD版batと同じく英語とし、利用者向けの日本語案内はREADME-compat.txtに置く。
  英語化によりps1は純ASCIIとなったが、UTF-8 BOM付きCRLFの形式は維持する。
  隔離HKCUの自己試験へbat往復（登録、解除、失敗時のexit 1と[FAILED]表示、pause待ち）を追加し、
  実機ではzip展開フォルダのinstall.batでARES 2024へ登録後、手動LOADなしの起動でPETAが
  SVG、EMF、PDFを生成することを確認した。
- 互換プラグイン3種のビルド出力に CAD 参照 DLL の混入なしを機械検査済み。
- 成果物 CADPeta-CompatCAD-1.0.0.zip（1.33 MB、ares/ijcad/bricscad の3フォルダ＋スクリプト）。

## 8. 組立ハーネス

**dist/build_dist.ps1**（-Only all|bundle|cli|compat）: 3配布物を再現可能に組み立てる。
dotnet SDK のみ依存（AutoCAD/互換CAD 本体不要）。staging/（組立途中）と artifacts/（zip）は
git 管理外（.gitignore 追記）。committed はテンプレート・スクリプトのみ。

## 9. 実機検証（ヘッドレスで消化した分）

本端末で accoreconsole を使い、GUI 不要の範囲を消化した。

### 9-0. AutoCAD 2027 GUIで自動ロード成立

- **AutoCAD 2027 GUIでbundle自動ロード成立**（2026-07-12）。通常起動→
  TESTCHART2_layout.dwg→`PETA` で起動時ロードされたプラグインが動作し、PDF 発行→IR
  （paths=2989 texts=75）→SVG/EMF 出力→クリップボード登録（CF_ENHMETAFILE 153,252 bytes・
  読み戻し一致）まで全動線が成立。EMF frame=420.16×297.04mm。**LoadOnAutoCADStartup 方式の
  GUI 自動ロードを実機確認**。
- **AutoCAD 2026 は GUI 起動そのものが失敗**（環境設定ファイルを作成できない・WebView2 データ
  ディレクトリ書込不可・acad.cuix 見つからず）。原因は **CADPeta ではなく AutoCAD 2026 側の
  profile 未設定**と機械証明した:
  - レジストリ `HKLM/HKCU\...\AutoCAD\R25.1\ACAD-9101:411` の **RoamableRootFolder /
    LocalRootFolder がともに空**（2027 の R26.0 は
    `...\AppData\Roaming\Autodesk\AutoCAD 2027\R26.0\jpn\` 等が設定済み）。2026 は初回起動
    セットアップが未完で、ユーザー設定の置き場所が未定義。
  - そのため 2026 は設定を install 先 `C:\Program Files\Autodesk\AutoCAD 2026`（**BUILTIN\Users は
    ReadAndExecute のみ・Write なし**）へ作ろうとして致命失敗する。
  - CADPeta の関与の否定: %APPDATA% へ足したのは共有 ApplicationPlugins 配下の CADPeta.bundle
    のみで、**同じ bundle を 2027 は正常ロード**。2026 の失敗は profile/customization 読込段で、
    プラグイン自動ロードより前。残プロセスなし・私の操作が 2026 のユーザー設定を作成/削除した
    形跡なし（%APPDATA%\Autodesk に 2026 フォルダは元から不在）。本端末の 2026 はプロファイル
    未生成という既知の状態と符合する。
  - 対処（AutoCAD 2026 の環境側・利用者判断）: 管理者権限で 2026 を一度起動して初回セットアップを
    完了させる／Autodesk Access で修復／「AutoCAD の設定を既定にリセット」。CADPeta は本件に
    関与しないため、2026 の net8 自動ロード検証は 2026 の profile 修復後に消化する。

### 9-1. accoreconsole は bundle 自動ロードを走らせない（重要な実測）

クリーン条件（%APPDATA% へ install.bat 配置のみ・NETLOAD なし）で accoreconsole 2027 に
PETA を送ると「そのようなコマンド "PETA" はありません」＝**プラグインが読み込まれない**
（成果物ゼロ）。読込属性を LoadOnCommandInvocation → LoadOnAutoCADStartup へ変えても同じ。
一方、**同じ bundle 内の net10 DLL を NETLOAD すると PETA は正常動作**（成果物3件）。
判定: accoreconsole（コアコンソール）は ApplicationPlugins の AutoLoader を実行しない
（自動ロードは GUI の acad.exe 専用機構）。accoreconsole で常に NETLOAD を要した理由と
一致する。**bundle 自動ロードの検証は GUI（acad.exe）が必須**で、ヘッドレスでは
確認できない。この実測を受けて読込方式を起動時ロードへ確定した（§4）。

### 9-2. bundle ペイロードの正当性（GUI 不要・バイト一致）

自動ロードとは別に、bundle に入る DLL が正しい成果物を出すことを NETLOAD で検証した。
- **AutoCAD 2027（bundle net10 を NETLOAD）の PETA 出力＝開発ビルド（src Debug net10）と
  SVG・EMF ともバイト一致**（SVG=238A39B8777A…・EMF=8817A3D7FA6F…）。bundle に同梱した
  Release DLL が開発ビルドと同一出力であることを実証。
- AutoCAD 2026（net8）の同検証は、本端末の 2026 がプロファイル未生成のため accoreconsole が
  走らず未消化（既知の端末制約）。GUI 初回起動後に消化する。

### 9-3. install.bat / SECURELOAD 規律

- install.bat / uninstall.bat の往復が成立（%APPDATA% へ導入・削除）。当初 UTF-8/LF で cmd が
  解釈失敗したため、**ASCII メッセージ＋CRLF** へ修正して堅牢化（日本語案内は README-install.txt）。
- accoreconsole 脚本で使った SECURELOAD=0 は clean QUIT のため永続せず（R25.1・R26.0 の
  プロファイルとも SECURELOAD 残留なしを reg 点検）。

### 9-4. 残骸の削除と、配布物の開発機のパス（2026-10-25）

インストールの残骸は、フォルダの名前だけで判定し、中身を照らさない。
`CADPeta.bundle.old`、`CADPeta.bundle.new`、PackageContents.xml の無い `CADPeta.bundle` は、DLL の確認を通ったあとに、利用者が置いたファイルを含めて中身ごと削除する。
DLL が使用中なら、1 ファイルも削除せずに止まる。
InstallerBatchTests の 31 件（子プロセスの APPDATA だけを試験のフォルダへ向けて実行）が合格した。
変更の前の uninstall.bat では、PackageContents.xml の無い `CADPeta.bundle` を削除する 2 件が不合格だった。

配布のビルドは `-p:ContinuousIntegrationBuild=true` と `--no-incremental`（publish を除く）で行う。
build_dist -Only all で組み立てた staging の自作の DLL は、CodeView の PDB のパスが `/_/src/...` で、開発機のパスが無かった。
`tests/tools/distribution/dll_path_check_selftest.ps1` が、開発機のパスを持つ DLL（CodeView、埋め込み PDB）を見つけて止まること、ContinuousIntegrationBuild の DLL が通ること、第三者の DLL を検査しないこと、読めない自作の DLL で止まることを確かめる。
4 つの zip は `package_verify.ps1 -Only all` に合格した。
組み立てのために、v1.0.0 の署名済みの `dist/staging` と `dist/artifacts`（263 ファイル）を一時的に移し、組み立てのあとに戻して、SHA-256 がすべて一致することを確かめた。

## 10. GUI実機の確認項目

accoreconsole で代替できない、GUI（acad.exe / 各互換CAD）でしか確認できない項目:

1. **bundle 自動ロード（GUI）**: %APPDATA% に導入済みの CADPeta.bundle で AutoCAD 2027・2026 を
   GUI 起動し、PETA/PETADIAG が起動時ロードされて動くか。出力が基準（§9-2 の 238A…/8817…）と
   一致するか。本端末の 2026 は GUI 初回起動でプロファイルを生成すれば以後 accoreconsole でも
   net8 ペイロードを機械照合できる。
2. **SECURELOAD=1 での GUI 自動ロード**: %APPDATA% 配下のブロック有無と、README 記載の
   TRUSTEDPATHS 追加での復旧。変更前に reg export→検証後復元→reg query で残存ゼロを点検。
3. **AutoCAD 2024（net48枠）**: bundleの自動読込とPETAの動作を確認済み。
4. **互換CAD 3製品の登録往復＋自動読込**: 完了。ARES Std 2024/2027、IJCAD 2026、BricsCAD V26で、
   配布`register.ps1`によるHKCU登録、手動LOADなしの起動時自動読込、PETA、配布`unregister.ps1`による全解除の往復を確認した（§7）。

### 導入状態のメモ
検証のため CADPeta.bundle を %APPDATA%\Autodesk\ApplicationPlugins へ導入済み
（LoadOnAutoCADStartup 版）。GUI 検証はこの導入済み bundle でそのまま行える。不要なら
dist\bundle\uninstall.bat で削除できる。

## 11. 利用者判断（決着済み）

- **CLI の self-contained 併載**: 行わない＝fd 単一ファイルのみ（2026-07-12 決定）。

## 12. 再現手順

```powershell
# 配布物の組立（全部 / 個別）
pwsh -File dist\build_dist.ps1 -Only all
pwsh -File dist\build_dist.ps1 -Only bundle
pwsh -File dist\build_dist.ps1 -Only cli
pwsh -File dist\build_dist.ps1 -Only compat

# T1-a 往復回帰＋3系
dotnet test tests\CadPeta.Tests\CadPeta.Tests.csproj -m:1

# T1-a 無回帰（前後バイト比較）: PdfImageDecoder.cs を stash して前後採取
pwsh -File tests\tools\cad-typography\capture_baseline_repo.ps1 -OutDir <dir> -Config Debug

# T1-b PowerPoint 閾値（要 uv・.NET 10・PowerPoint）
pwsh -File tests\tools\distribution\ppt_ungroup_threshold\test_n_ppt.ps1 -N 10000 -Label repro
pwsh -File tests\tools\distribution\ppt_ungroup_threshold\test_n_ppt.ps1 -N 10001 -Label repro

# 互換CAD 登録の往復（BricsCAD=HKCU・管理者不要）
pwsh -File dist\staging\compat\register.ps1 -Product bricscad
pwsh -File dist\staging\compat\unregister.ps1 -Product bricscad
```
# GUI単一ファイル配布

- `CadPeta.Gui` を `win-x64`、framework-dependent、PublishSingleFileで発行する。
- publishディレクトリに `cadpeta-gui.exe` だけが存在し、管理DLLが分離配置されていないことを
  `dist/build_dist.ps1 -Only gui` が検査する。
- .NET 10 Desktop Runtime x64導入環境で起動スモークを行う。publishディレクトリへcoreclr等が混入しないことを
  配布ハーネスで検査する。最終サイズとハッシュは各リリースのmanifestを基準とする。

## CLI合成層抽出の回帰比較（2026-07-14）

合成層抽出直前の `e03faf2` と抽出後のCLIを、同一PDF・同一出力パス・同一引数
（SVG/EMF/PDF/PNG、`--diag`、`--clipboard`、PNG DPI 96）でRelease実行した。

| 成果物 | SHA-256（両実装で一致） |
|---|---|
| SVG | `A06372AC307DFA89C8D18D2C8769B478D76C1120206E3A2DCD50C42D8C308AEF` |
| diag.json | `C2BE24C0D3486D64DAB5CB2510FA505230ABE0DCA7E5A23BA6FA0ACB33ACCABA` |
| EMF | `99EA077DF7ABDC44C4E6EA757B516E87F175BF8874B54D70AA1CE3FC47F84312` |
| PDF | `86C76B7977C906C29BEA62F229E47C44B04F8627D7A6DB5D12A5806F859C836C` |
| PNG | `ADDB2B821284A162AD2B0B2B573EA55623A6E89EBA8E0B11018785797407682E` |

段階診断SVGも全ファイル一致した。stdoutは行内容と順序が一致し、Writerが実測して出力する経過時間
`Nms` の数値だけが異なった。経過時間を正規化した比較は完全一致した。生stdoutは同一バイナリの反復でも
経過時間が変わるため、バイト同一性の判定対象にはできない。
