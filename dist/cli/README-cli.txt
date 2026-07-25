CADPeta CLI — コマンドライン版
==============================

CADPeta の CLI（cadpeta.exe）は、ベクタ PDF を入力に、SVG / EMF / PDF / PNG を出力し、
クリップボードへ EMF を載せるコマンドライン版です。AutoCAD の導入は不要です
（PDF印刷経路の単独入口。プラグイン未対応の CAD でもベクタ PDF を出力できれば処理できます）。

■ 前提（必須）
  .NET 10 ランタイム（Microsoft.NETCore.App 10.0 以上）が必要です。
  この CLI は framework-dependent（ランタイム非同梱）版のため、未導入の環境では動きません。

  導入（いずれか）:
    - winget install Microsoft.DotNet.Runtime.10
    - https://dotnet.microsoft.com/download/dotnet/10.0 から「.NET Runtime」を入手
  （Windows Desktop ランタイムは不要。基本ランタイムのみで動作します）

■ 使い方（例）
  受領した PDF を SVG と EMF にする:
    cadpeta.exe 図面.pdf out.svg --emf out.emf

  出力先を指定しないとクリップボードへ EMF を登録します（Office で Ctrl+V）:
    cadpeta.exe 図面.pdf

  モノクロ化して貼り付け用にクリップボードへ:
    cadpeta.exe 受領図面.pdf --mono

  PDF / PNG も出す:
    cadpeta.exe 図面.pdf out.svg --pdf out.pdf --png out.png --png-dpi 150

  色付きの貼付先に合わせて EMF の半透明背景を指定する（既定は白）:
    cadpeta.exe 図面.pdf out.svg --emf out.emf --emf-bg "#102030"
  --emf-bg は --emf またはクリップボード出力と組み合わせます。

  範囲クロップ・余白・診断ダンプ:
    cadpeta.exe 図面.pdf out.svg --crop 100 100 300 250 --margin 5 --diag diag_dir

  すべてのオプションは引数なしで実行すると表示されます:
    cadpeta.exe

■ 発行元不明の警告が出る／実行が止まる場合（署名の信頼登録）
  cadpeta.exe は配布元のコード署名証明書で署名しています。ダウンロードした実行ファイルに
  「Windows によって PC が保護されました」や発行元不明の警告が出る環境、または署名済み
  実行のみを許可する環境（AppLocker 等）では、同梱の WATANABE_Kazufumi.cer を信頼登録すると
  警告が消え、実行できるようになります。手順は README-signing.txt を参照してください。

■ ライセンス
  本体は MIT ライセンスです（同梱の LICENSE を参照）。
  依存ライブラリの表示は THIRD-PARTY-NOTICES.txt にまとめています。
