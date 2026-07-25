CADPeta — 署名の確認と発行元の信頼登録
======================================

CADPeta の実行ファイル（.exe / .dll）と互換 CAD 用セットアップスクリプト
（register.ps1 / unregister.ps1）は、配布元のコード署名証明書で署名しています。
この証明書は自己署名のため、初期状態の Windows ではチェーンが信頼されず、
「発行元不明」の警告が出たり、署名の検証が有効になりません。

同梱の WATANABE_Kazufumi.cer を信頼登録すると、これらの警告が消え、
署名が有効（Valid）として扱われます。管理者権限は不要です。

署名済みファイルの実行や読み込みのみを許す設定の環境（SmartScreen、AppLocker、
PowerShell の実行ポリシー AllSigned など）では、この信頼登録が実行の前提になります。
その場合は、各パッケージのインストール手順より先に本手順を済ませてください。


■ 先に拇印（フィンガープリント）を照合する
  信頼登録は「この証明書を信頼する」という宣言です。zip の中身だけを見て信頼せず、
  必ず配布元が別途公開している拇印と一致することを確認してください。

    拇印（SHA-1）: A4850D19 9DDC8851 E3795554 C9616B49 9FC89E36

  確認方法（どちらでも可）:
    ・WATANABE_Kazufumi.cer をダブルクリック →[詳細]タブ →「拇印」
    ・PowerShell:
        Get-PfxCertificate .\WATANABE_Kazufumi.cer | Select-Object Thumbprint

  上記と一致しない証明書は登録しないでください。


■ 信頼登録の手順（2 か所に登録する）
  自己署名証明書はチェーンの根が自分自身のため、次の 2 か所へ登録します。
    ・信頼されたルート証明機関 … 署名の検証を有効（Valid）にする
    ・信頼された発行元         …「発行元不明」の警告を抑止する

  方法A: PowerShell（推奨・管理者権限不要）
    zip を展開したフォルダで pwsh または Windows PowerShell を開き、次を実行します。

      Import-Certificate -FilePath .\WATANABE_Kazufumi.cer `
        -CertStoreLocation Cert:\CurrentUser\Root
      Import-Certificate -FilePath .\WATANABE_Kazufumi.cer `
        -CertStoreLocation Cert:\CurrentUser\TrustedPublisher

  方法B: 証明書インポートウィザード（GUI）
    1. WATANABE_Kazufumi.cer をダブルクリックし、[証明書のインストール]を押します。
    2. 保存場所は「現在のユーザー」を選びます。
    3.「証明書をすべて次のストアに配置する」を選び、[参照]で
       「信頼されたルート証明機関」を指定して完了します。
    4. もう一度 1〜3 を行い、今度は「信頼された発行元」を指定します。


■ 登録できたかの確認
  署名済みファイル（例: cadpeta.exe）を右クリック →[プロパティ]→[デジタル署名]で
  署名者を選び[詳細]を開くと、「このデジタル署名は問題ありません」と表示されます。


■ 解除したいとき
  次のいずれかで登録を取り消せます（登録した 2 か所の両方から削除します）。

    Get-ChildItem Cert:\CurrentUser\Root, Cert:\CurrentUser\TrustedPublisher |
      Where-Object Thumbprint -eq 'A4850D199DDC8851E3795554C9616B499FC89E36' |
      Remove-Item

  GUI の場合は certmgr.msc を開き、「信頼されたルート証明機関」と
  「信頼された発行元」の各[証明書]から CADPeta の証明書を削除します。


■ 補足
  ・AutoCAD 版で読み込みが SECURELOAD に阻まれる場合は、証明書の信頼登録とは別に、
    同梱の README-install.txt にある「信頼できる場所（TRUSTEDPATHS）」の追加を行ってください。
  ・register.ps1 の実行が PowerShell の実行ポリシーで止まる場合、この信頼登録により
    署名済みスクリプトとして実行できるようになります。
