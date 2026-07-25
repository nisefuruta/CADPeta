CADPeta — 互換CAD（ARES / IJCAD / BricsCAD）版
===============================================

CADPeta は AutoCAD 互換の CAD でも動きます（PETAのPDF印刷経路とPETADATAの構造SVG書き出しの両方に対応します）。
本 zip には3製品ぶんのプラグイン DLL と、自動読込を登録するスクリプトを同梱しています。

  ares\2020\ … ARES Standard 2020 用（.NET Framework 4.8 版）
  ares\2023\ … ARES Standard 2023 用（.NET Framework 4.8 版）
  ares\2024\ … ARES Standard 2024 用（.NET Framework 4.8 版）
  ares\2027\ … ARES Standard 2027 用（.NET 8 版）
  ijcad\     … IJCAD 用（.NET 8）
  bricscad\  … BricsCAD 用（.NET 8）
  install.bat / uninstall.bat  … 自動読込の登録／解除（ダブルクリックで実行）
  register.ps1 / unregister.ps1  … 上記の本体（PowerShellから直接実行する場合のみ使用）
  README-compat.txt（本ファイル）
  WATANABE_Kazufumi.cer / README-signing.txt … 発行元の信頼登録用（下記参照）


■ スクリプトの署名について（実行ポリシーが厳しい環境では先に登録）
  register.ps1 / unregister.ps1 と各プラグイン DLL は配布元のコード署名証明書で署名しています。
  install.bat はプロセス限定の実行許可でregister.ps1を起動するため、通常の環境では
  実行ポリシーの個人設定に関係なく動作します。
  組織がグループポリシーで実行ポリシー（AllSigned等）を強制している環境では、
  先に同梱の WATANABE_Kazufumi.cer を信頼登録しないと register.ps1 が実行できません。
  手順は README-signing.txt を参照してください（管理者権限は不要）。

  コード署名は、PowerShell自体をAppLocker、WDAC、組織ポリシーなどで禁止している環境では実行許可になりません。
  PowerShellの使用が完全に禁止されている場合はinstall.batを実行せず、本書の「登録なしで使う」に従ってCAD内から手動で読み込んでください。
  起動時の常駐が必要な場合は、システム管理者へ各製品のDemandLoad登録を依頼してください。


■ 自動読込を登録する
  zipを展開し、install.batをダブルクリックしてください（AutoCAD版のinstall.batと同じ操作です）。
  開いた窓は、成功でも失敗でもキーを押すまで閉じません。
  表示された番号からARES、IJCAD、BricsCADのいずれかを選びます。
  選んだ製品が複数の版でインストールされている場合は、検出したすべての版へ登録します。
  ARESは版ごとのフォルダ（ares\2020、ares\2023、ares\2024、ares\2027）のDLLを自動で対応づけます。
  ARESの場合、プラグイン一式は登録時に、ARESが既定で信頼するユーザー領域
  %AppData%\ARES Standard Edition x64\CADPeta\<版>\ へコピーしてから登録します。
  この場所はARESの信頼フォルダのため、読み込み時のセキュリティ確認（信頼できるフォルダにありません）が
  出ず、管理者権限も要りません。zipはどこへ展開しても構いません（Downloads等でも可）。

  [DONE]と登録件数が表示されたら、レジストリへの書込みと読み戻し確認は成功です。
  続けて、表示された対象CADをいったん終了してから起動し、コマンド行にPETAと入力してください。
  PETAコマンドが開始した時点で、CAD上の自動読込確認まで完了します。
  [FAILED]と表示された場合は、直後の理由と「登録できない場合」を確認してください。

  PowerShellから実行する場合は、展開先で次のいずれかを実行します。

    powershell -File register.ps1 -Product bricscad
    powershell -File register.ps1 -Product ijcad
    powershell -File register.ps1 -Product ares

  -Productを省略すると、ダブルクリック実行と同じ製品選択を表示します。
  ARESの1版だけへ登録する場合に限り、-AresYear 2020|2023|2024|2027 のいずれかを指定します。
  ARESの自動読込の口は版で異なります（下の「■ 登録の内容と手動での解除」を参照）。

■ 解除する
  uninstall.batをダブルクリックし、解除する製品を選んでください。
  選んだ製品で検出したすべての版からCADPetaの登録を削除します。

    powershell -File unregister.ps1 -Product bricscad
    powershell -File unregister.ps1 -Product ijcad
    powershell -File unregister.ps1 -Product ares

  install.bat が作ったものだけを削除します（他の設定には触れません）。詳しくは次節を参照してください。


■ 登録の内容と手動での解除（uninstall.batが使えないときの逃げ道）
  install.bat（register.ps1）がARESに対して行うことは次の2つです。どちらもユーザー領域だけで、
  管理者権限もレジストリの機微な領域も使いません。手動で消したいときはここを消せば元に戻ります。

  (1) プラグイン一式のコピー（全版共通）
      zip内の ares\<版>\ 一式を、ARESが既定で信頼するユーザーフォルダへコピーします。
        %AppData%\ARES Standard Edition x64\CADPeta\<版>\
      この場所はARESの信頼フォルダなので、読み込み時のセキュリティ確認（信頼できるフォルダに
      ありません）が出ません。手動解除：このフォルダを削除します。

  (2) 起動時に自動で読み込ませる登録（版で口が異なる）
      ・ARES 2024以降：レジストリのApplicationsキーに登録します。
          キー: HKCU\SOFTWARE\Graebert GmbH\ARES Standard <版>\Applications\CADPeta
          値  : LOADER=(上記コピー先のCadPeta.Plugin.Ares.dll)、LOADCTRLS=2、MANAGED=1、DESCRIPTION=CADPeta
          手動解除：regeditで上記CADPetaキーを削除します。
      ・ARES 2023以前（2020/2023）：レジストリでは自動読込されないため、ARES本来のstartup機構
          （OPTIONSダイアログのAdd-Ins）に、上記コピー先のDLLを読み込むローダLISPを登録します。
          - ローダ: %AppData%\ARES Standard Edition x64\CADPeta\<版>\load_cadpeta.lsp
          - 登録先: %AppData%\ARES Standard Edition x64\<内部版番号>\Profiles\<プロファイル>\profile.xml
                    の applications グループへ、name="load_cadpeta.lsp" の item を1つ追加します。
          手動解除：ARESのOPTIONS＞Add-Ins（またはAPPLOADの一覧）から load_cadpeta.lsp を外すか、
                    上記profile.xmlの当該itemブロックを削除します。加えて(1)のフォルダを削除します。

  IJCAD／BricsCADは各製品のApplicationsレジストリキー（HKCU）だけを使います。手動解除はそのCADPetaキーの削除です。


■ 登録できない場合
  [FAILED]と表示された場合は、その直後の理由（英語）を確認してください。
  「登録先が見つかりません」の場合は、対象CADを一度起動して終了し、install.batを再実行します。
  「対象DLLがありません」の場合は、zip内のファイルを個別に移動せず、zipを展開したフォルダーで実行します。
  [DONE]後もPETAが不明なコマンドになる場合は、次節の手動読込で同じ版のDLLを指定してください。
  手動読込でエラーが表示された場合は、そのメッセージとinstall.batが表示した対象、DLL、登録先を添えて報告してください。


■ 登録なしで使う（NETLOAD＝確実な逃げ道・管理者権限不要）
  自動読込を登録せず、その場かぎりでプラグインを読み込む方法です。製品ごとに
  読込コマンドが異なります（実測）。DLL は各 product フォルダの
  CadPeta.Plugin.<製品>.dll です。

  - BricsCAD : コマンド行で NETLOAD と入力し、bricscad\CadPeta.Plugin.Bricscad.dll を選ぶ
  - IJCAD    : コマンド行で  NETLOAD    と入力し、ijcad\CadPeta.Plugin.Ijcad.dll を選ぶ
               （IJCAD に -NETLOAD は無い。素の NETLOAD がファイル選択ダイアログを出す）
  - ARES     : コマンド行で  LoadDotNetApplication  を実行し、所有する版のフォルダの
               CadPeta.Plugin.Ares.dll を指定（ARES 2020=ares\2020\・2023=ares\2023\・
               2024=ares\2024\・2027=ares\2027\）（ARES Standard で動作確認済み）

  読み込めたら PETA / PETADATA が使えます。NETLOAD はその製品を閉じると解除されます。

  - PETA     : 指定した範囲を、印刷（プロット）したとおりの見た目のベクタ図にします。
               [Enter] で現在空間の全体、2点の指定でその窓の切り出しです。
               オプション O（オブジェクト）で出力対象を選んだ図形だけに絞り込めます。
               EMF/SVG をファイル出力し、クリップボードへも載せます（Office で Ctrl+V）。
  - PETADATA : CTB/STB を通さず、TrueColor、線種、文字、レイヤを保ったままの SVG を
               保存します。Inkscape 等で編集し直したいときに使います。


■ 製品別の注記
  - IJCAD の自動読込は検出したすべての版へ登録します。ユーザー単位（HKCU）へ登録できる環境では管理者権限は不要です。うまく常駐しない
    場合は、上記 NETLOAD か、IJCAD の APPLOAD ダイアログの「スタートアップに登録」
    （プロファイルに保存）を使ってください。
  - ARES Standard は版でホストランタイムが異なります（2020/2023/2024=.NET Framework 4.8／
    2027=.NET 8）。ARES 2020/2023 で使うときも、PC に .NET Framework 4.8 が入っている必要が
    あります（Windows 10/11 には標準で入っています）。
    register.ps1 は検出したすべての版へ、適合フォルダ（ares\2020〜ares\2027）を対応づけて登録します。
    1版だけへ登録する場合は -AresYear 2020|2023|2024|2027 で明示してください。自動読込はユーザー単位（HKCU）へ
    登録します（管理者権限不要）。うまく常駐しない場合は NETLOAD（LoadDotNetApplication）を
    使ってください。
  - BricsCAD は検出したすべての版と言語キーへ登録します。既定でセキュリティ制限（SECURELOAD）が緩く、HKCU への登録で常駐できます。


■ ライセンス
  本体は MIT ライセンスです（同梱の LICENSE を参照）。
  依存ライブラリの表示は THIRD-PARTY-NOTICES.txt にまとめています。
