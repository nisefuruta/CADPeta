CADPeta GUI版

事前に .NET 10 Desktop Runtime (x64) をインストールし、cadpeta-gui.exe をダブルクリックして起動します。
受領したベクタPDFをドロップし、ページを選んで、色・背景・クロップ・文字・レイヤの設定を調整し、
クリップボードにコピーするか、EMF/SVG/PDF/PNGへ保存できます。プレビューはPNGですが、
クリップボードにコピーする図は劣化しないEMFで、Word・Excel・PowerPointなどに貼り付けられます。

クリップボードへのコピーに失敗した場合も、画面に表示されたEMFを「挿入 > 図」から選択できます。
開発者向けの段階診断ダンプは従来のcadpeta.exe --diagを使用してください。

cadpeta-gui.exe は配布元のコード署名証明書で署名しています。起動時に「WindowsによってPCが
保護されました」や発行元不明の警告が出る環境、または署名済み実行のみを許可する環境では、
同梱の WATANABE_Kazufumi.cer を信頼登録すると警告が消えます。手順は
README-signing.txt を参照してください。
