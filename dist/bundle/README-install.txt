CADPeta — AutoCAD 版インストール
================================

CADPeta は、AutoCAD の図面を Word / Excel / PowerPoint へ、拡大しても劣化しない
ベクタ形式で貼り付けるフリーソフトウェアです。

■ インストール
  1. この zip を任意のフォルダへ展開します。
  2. install.bat をダブルクリックします（管理者権限は不要）。
     CADPeta.bundle が %APPDATA%\Autodesk\ApplicationPlugins\ へコピーされます。
  3. AutoCAD を起動し、コマンド行に PETA と入力します。

  対応バージョン: AutoCAD 2021〜2024 / 2025〜2026 / 2027 以降
  （bundle が版に応じて .NET Framework 4.8 / .NET 8 / .NET 10 の DLL を自動で選びます）

■ 使い方（最初の一歩）
  - PETA      : 指定した範囲を、印刷（プロット）したとおりの見た目のベクタ図にします。
                [Enter] で現在空間の全体、2点の指定でその窓の切り出しになります。
                EMF/SVG をファイル出力し、あわせてクリップボードへ載せるので、
                Office 側で Ctrl+V すると図として貼り付きます。
                オプション O（オブジェクト）で、範囲内の出力対象を選んだ図形だけに
                絞り込めます。
  - PETADATA  : CTB/STB を通さず、TrueColor、線種、文字、レイヤを保ったままの SVG を
                保存します。Inkscape 等で CAD の素材として編集し直したいときに使います。

  各コマンドの残りのオプションは、コマンド行のプロンプトに一覧が出ます。

■ アンインストール
  uninstall.bat をダブルクリックします。

■ SECURELOAD の警告が出て読み込めない場合
  AutoCAD のセキュリティ設定（システム変数 SECURELOAD）が有効な環境では、
  %APPDATA% 配下のプラグインが信頼されず読み込まれないことがあります。
  次のいずれかで解決します。

  (a) 信頼できる場所に追加する（推奨・管理者権限不要）
      AutoCAD のコマンド行で OPTIONS を実行し、[ファイル] タブの
      「信頼できる場所」（システム変数 TRUSTEDPATHS）に次のフォルダを追加します:
        %APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle\Contents
      追加後に AutoCAD を再起動します。

  (b) 一時的に SECURELOAD を下げる
      SECURELOAD を 0 または 1 に設定します（既定は 2）。
      セキュリティを弱めるため、(a) を推奨します。

  (c) 発行元の証明書を信頼登録する
      CADPeta.bundle の DLL は配布元のコード署名証明書で署名しています。同梱の
      WATANABE_Kazufumi.cer を信頼登録すると、署名済み・信頼された発行元として扱われ、
      (a) の「信頼できる場所」の追加なしに読み込めることがあります（環境に依存）。
      手順は同梱の README-signing.txt を参照してください（管理者権限は不要）。

■ ライセンス
  本体は MIT ライセンスです（同梱の LICENSE を参照）。
  依存ライブラリの表示は THIRD-PARTY-NOTICES.txt にまとめています。
