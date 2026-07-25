# CADPeta

CADPeta は、AutoCAD（および互換 CAD）の図面を Word / Excel / PowerPoint へ、拡大しても劣化しないベクタ形式で貼り付ける AutoCAD プラグインです。コマンド `PETA` を実行し、Office で貼り付けるだけで、印刷（プロット）と同じ仕上がりの図が用紙どおりの実寸で使用できます。MIT ライセンスのフリーソフトウェアです。

## 背景

CAD 図面をドキュメントの説明図として使う方法として、2000 年代初頭には、Windows に付属していた Adobe PostScript（Level 1 Plus）プリンタドライバで EPS ファイルへ出力する手段がありました。しかし 2017 年、Microsoft がセキュリティ対策として Office への EPS 挿入を無効化し、この経路は使えなくなりました。以後は CAD の WMF 書き出しなどで代用するしかなく、作図画面の背景色まで出力に含まれるなど、印刷結果と一致しない図を手作業で整える手間が続いていました。CADPeta は、印刷（プロット）と同じ経路でベクターデータを取得することで、この手間なしに印刷どおりの図を Office へ貼り付けることができます。

## 主な機能

- **印刷と同じ仕上がりで貼る** — 全体・2点窓・選択オブジェクトをすべてPDF印刷経由で処理し、CTB/STBの色・線幅を反映する。
- **選択オブジェクトを実寸尺度で貼る** — 元図面を変更せず側DBから尺度1:Nで印刷する。非印刷図形も明示選択時は出力できる。
- **CAD構造をSVGへ書き出す** — `PETADATA` で文字・レイヤ・元の色を保った外部編集用SVGを作る。
- **4 形式で出力する** — EMF（Enhanced Metafile＝拡張メタファイル。Office 貼り付け用の主形式）、SVG（Scalable Vector Graphics）、PDF（変換元の印刷イメージ）、PNG（画像）。
- **Office 上で図形に分解できる** — 貼り付けた図を Excel / PowerPoint で線・塗り・文字へ分解し、個別に編集できる。
- **CAD なしで PDF を貼る** — コマンドライン版 `cadpeta.exe` で、受け取ったベクタ PDF を同じ品質で貼り付けられる（色変換・範囲切り出しにも対応）。
- **CAD なしでPDFを画面操作する** — GUI版 `cadpeta-gui.exe` へPDFをドロップし、プレビューを見ながら色調整してOfficeへ貼るか4形式で保存できる。

## 対応環境

- **AutoCAD** 2024 / 2025 / 2026 / 2027（実行基盤: 2024 以前 = .NET Framework 4.8、2025・2026 = .NET 8、2027 = .NET 10）
- **互換 CAD** ARES Standard 2020 / 2023 / 2024 / 2027、IJCAD 2026 PRO、BricsCAD V26
- **Office** Word / Excel / PowerPoint（Windows デスクトップ版）
- **OS** Windows 11
- コマンドライン版 `cadpeta.exe` は CAD のインストールを必要としない
- GUI版 `cadpeta-gui.exe` はCADを必要としない（.NET 10 Desktop Runtime x64が必要）

## 入手とインストール

配布物は用途別の 4 種類の zip で提供します（GitHub の Releases から入手）。

**AutoCAD**

1. `CADPeta-AutoCAD-x.y.z.zip` を任意のフォルダへ展開する
2. `install.bat` を実行する（管理者権限は不要。`CADPeta.bundle` が `%APPDATA%\Autodesk\ApplicationPlugins\` へコピーされます）手動で読み込みたい場合は `NETLOAD` も使えます。
3. AutoCAD を起動し、コマンド行に `PETA` と入力する（bundle が版に合った DLL を自動で選び、初回実行時に自動ロードされます。以降 `NETLOAD` は不要）

アンインストールは同梱の `uninstall.bat`。

**互換 CAD（ARES / IJCAD / BricsCAD）**

`CADPeta-CompatCAD-x.y.z.zip` を展開し、同梱の `register.ps1` で製品へ登録します。

**コマンドライン版（CAD 不要）**

`CADPeta-CLI-x.y.z.zip` を展開し、`cadpeta.exe` を実行します（要 .NET 10 ランタイム）。受け取ったベクタ PDF を貼り付ける用途です。

**GUI版（CAD不要）**

`CADPeta-GUI-x.y.z.zip` を展開して `cadpeta-gui.exe` をダブルクリックします。PDFを画面へドロップし、色調整、クロップ、プレビュー、Office貼り付け、EMF/SVG/PDF/PNG保存を操作できます。`.NET 10 Desktop Runtime (x64)` 環境が必要です。

版ごとのフォルダ、SECURELOAD の対処、貼り付け先ごとの注意などの詳細は[利用者向け文書](docs/04_利用者向け文書.md)を参照してください。

## 基本操作

1. 貼りたいレイアウト（またはモデル空間）で `PETA` を実行し、全体を貼るなら Enter、範囲を切り出すなら 2 点、現在のモデル空間またはペーパー空間の図形を貼るなら `O` を指定します（ペーパー空間のビューポート自体は対象外です）
2. Word / Excel / PowerPoint 等で Ctrl+V(ペースト) します

## ソースからビルド

- 必要環境は .NET SDK（10 系）。AutoCAD 版プラグインと CLI は CAD 本体をインストールしなくてもビルドできます（AutoCAD 参照は NuGet の compile 専用）。
- **AutoCAD 版・CLI・コア一式だけをビルド**: `dotnet build CadPeta.Core.slnf`
- **全体（互換 CAD 含む）とテストのビルド**: `dotnet build CadPeta.sln`。互換 CAD（ARES / IJCAD / BricsCAD）のプラグインは各社 SDK（本体インストール）の参照 DLL を必要とします。
- 本体をアンインストールしても、別マシンでも互換プラグインをビルドし続けたい場合は、各社の参照 DLL を `lib/vendor-refs/{bricscad,ijcad,ares/2020,ares/2023,ares/2024,ares/2027}/` へ退避してください。存在すればそちらが本体より優先されます（`lib/vendor-refs/` は Git 管理外。ベンダー SDK の再配布可否は各社ライセンスに従うこと）。
- 配布物の組み立ては `dist/build_dist.ps1`、コード署名は `dist/sign_dist.ps1`。

## ドキュメント

製品に同梱する利用者向け文書:

- [利用者向け文書](docs/04_利用者向け文書.md) — インストール、操作手順、貼り付け先ごとの注意、トラブルシュート
- [機能ガイド](docs/05_機能ガイド.md) — できることの一覧と精度の実測値

仕様・設計・検証の基準文書:

- [要求仕様書](docs/01_要求仕様書.md)
- [設計書](docs/02_設計書.md)
- [技術解説](docs/03_技術解説.md)
- [検証根拠](docs/validation/)

## ライセンス

MIT ライセンス（[LICENSE](LICENSE)）。同梱ライブラリの著作権表示は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
