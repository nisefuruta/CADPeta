# CADPeta リリース手順（runbook）

配布 zip を作り、YubiKey で署名し、GitHub Releases へ公開するまでの手順。判断の分岐が多い
署名段を取り違えないための基準。バージョンは `X.Y.Z`（初回は `1.0.0`）。
版番号は `dist/bundle/CADPeta.bundle/PackageContents.xml` の `AppVersion` の 1 か所で決める。DLL の版はビルドのときにこの値から決まり、
`build_dist.ps1` と `sign_dist.ps1` は `-Version` を省くとこの値（sign_dist は build_dist が manifest.txt に書いた値）を使う。
違う `-Version` を渡すと、何も作らず、何も署名せずに止まる。

すべての `.ps1` は **pwsh（PowerShell 7）** で実行する。`powershell.exe`（5.1）は日本語コメントの
UTF-8 を CP932 誤読してパースエラーになる。

## 前提

- **ビルド端末**: dotnet SDK（10 系）。互換 CAD zip を作るには ARES / IJCAD / BricsCAD の本体、
  または `lib/vendor-refs/` の参照 DLL キャッシュが必要（AutoCAD 版と CLI は本体不要）。
- **署名端末**: YubiKey（コード署名証明書。既定は slot 9c）、Windows SDK（`signtool`）。
- 生成物 `dist/staging/`・`dist/artifacts/`・`lib/vendor-refs/`・`lib/CADPeta-vendor-refs.zip` は
  すべて `.gitignore` 済み。ベンダー SDK と未署名成果物を公開リポへ含めない。

## 0. 証明書の信頼配布（初回のみ・署名者が設定）

署名は自己署名証明書（`Subject == Issuer`）で行うため、利用者の端末では既定でチェーンが
信頼されない。利用者が発行元を信頼登録できるよう、**公開証明書**を配布物へ用意する。

1. 公開証明書を書き出す（YubiKey Manager の PIV slot 9c → Export でも同じ物が得られる）。
   ファイル名は証明書の Subject（署名者の身元）に合わせる:

   ```
   Export-Certificate -Cert Cert:\CurrentUser\My\A4850D199DDC8851E3795554C9616B499FC89E36 `
     -FilePath dist/common/WATANABE_Kazufumi.cer -Type CERT
   ```

   ファイル名は表示上の便宜で、信頼は拇印で成立する。Subject を変えたら README とファイル名も揃える。

2. **拇印を配布物とは別の経路で公開する**（HTTPS のプロジェクトサイトや Release ノート）。
   `dist/common/WATANABE_Kazufumi.cer` を zip に同梱しても、それだけでは信頼の起点にならない。
   zip を差し替えられる攻撃者は証明書ごと差し替えられるため、利用者は取り込み前に拇印
   `A4850D19 9DDC8851 E3795554 C9616B49 9FC89E36` を独立経路で照合する必要がある。

3. `dist/common/WATANABE_Kazufumi.cer` と `dist/common/README-signing.txt` は
   `build_dist.ps1` が 4 zip すべてへ同梱する。証明書を更新したら拇印の公開値も更新する。

証明書自体は秘密ではないので、公開 `.cer` と信頼登録手順はリポジトリにコミットしてよい。

## 1. ビルド（ビルド端末）

worktree をクリーン（未コミット変更なし）にしてから:

```
pwsh -NoProfile -File dist/build_dist.ps1 -Only all -Version X.Y.Z
```

`dist/staging/`（未署名 PE）、`dist/artifacts/*.zip`（未署名）、`manifest.txt`（HEAD と SHA-256）を出力する。
`dist/staging/sign-record.json` には、署名する自作のファイルごとの SHA-256 を記録する（署名の段で照合する）。

互換 CAD 本体が無い端末でビルドする場合は、先に参照 DLL キャッシュを用意する:

1. 本体のある端末で `pwsh -NoProfile -File dist/extract_vendor_refs.ps1` を実行 →
   `lib/CADPeta-vendor-refs.zip`（互換 CAD の compile 専用参照 DLL 9 件）。
2. その zip をビルド端末の `<repo>/lib/vendor-refs/` へ展開する（**GitHub 経由で運ばない**。各社 SDK
   ライセンス下の私的コピー）。`src/Directory.Build.props` がキャッシュを本体より優先して参照する。

## 2. 署名（署名端末）

ビルド端末から `dist/`（`dist/staging/` を含む）を署名端末へ運ぶ。再ビルドは不要（署名は staging の
PE を対象にする）。署名する前に `-WhatIf` で、署名する対象と照合の結果を確かめられる（秘密鍵を使わず、何も変えない）:

```
pwsh -NoProfile -File dist/sign_dist.ps1 -Only all -Version X.Y.Z -WhatIf
```

YubiKey を挿して次を実行すると、計画を表示してから確認を求める（確認の画面を出せない実行では `-Confirm:$false` が要る）:

```
pwsh -NoProfile -File dist/sign_dist.ps1 -Only all -Version X.Y.Z
```

`CadPeta.*.dll`、`cadpeta.exe`、`cadpeta-gui.exe`、および互換 CAD の `register.ps1`／`unregister.ps1` を
署名する（第三者 DLL と `install.bat`／`uninstall.bat` は対象外。`.bat` は Authenticode の署名機構が
Windows に無い）。PE も `.ps1` も `signtool` で署名し、いずれも拇印一致とタイムスタンプを検証した
うえで `dist/artifacts/` の zip を署名済みで作り直す。既定の拇印は slot 9c
（`A4850D199DDC8851E3795554C9616B499FC89E36`）、タイムスタンプは DigiCert。

再 zip の前に、署名しない同梱ファイル（`README-*.txt`、`install.bat`／`uninstall.bat`、`LICENSE`、
`THIRD-PARTY-NOTICES.txt`、信頼登録ファイル）を `dist/` 配下の複製元の現在の内容へ差し替える。
差し替えたファイルは `[sign] 同梱文書を差し替え` として表示し、`manifest.txt` にも差替え元の HEAD
とともに記録する。署名済みの PE と `register.ps1`／`unregister.ps1` には触れない。

署名の途中で YubiKey が一過性のエラー（`SignerSign() failed`、`0x8010001f` 等）を返しても、
未署名のファイルだけを対象に既定 3 回まで試行を繰り返す（`-RetryCount` で変えられる）。
それでも残った場合はファイル名を挙げて止まる。YubiKey を挿し直して同じコマンドを実行すれば、
署名済みのファイルを飛ばして続きから再開するので、済んだ分の PIN／タッチはやり直さない。

署名の前に、staging の自作のファイルを `sign-record.json` と照合する。未署名のファイルは組み立てたときの値と、
署名済みのファイルは署名の後に記録した値と一致しなければ、何も署名せずに止まる（staging のファイルが
組み立てた後に置き換えられた場合）。署名済みと記録したファイルの署名を検証できないときも止まる。

staging のファイルはその場で書き換えない。署名は `dist/staging/sign-work` の一時ファイルで行い、検証が通ったら
署名後の値を記録に書いてから、名前の変更で置き換える。同梱文書の差し替えと記録の保存も、一時ファイルに書いて
ディスクへ書き出してから置き換える。そのため、署名の途中でプロセスが止まった場合（例外、Ctrl+C、強制終了）は、
同じコマンドを実行すれば続きから再開できる。電源断と OS のクラッシュでは、名前の変更がディスクに書かれたかを
保証しないので、`build_dist.ps1` で staging を作り直してから署名し直す。

zip は一時の名前で作り、zip を作る直前の staging と作った zip の中の署名したファイル（DLL・exe・.ps1）が、記録した署名済みの値と一致してから
成功の名前にする。一致しなければ、成功の名前の zip を残さずに止まる（検証の後に、staging のファイルが同じ署名者の別の版へ置き換えられた場合）。

同じ staging に対して `sign_dist.ps1` を同時に実行することはできない。実行の間は `dist/staging/sign.lock` を
排他で開いておき、別の実行が開いていれば、何も変えずに止まる。

## 2-1. 署名の後で文書だけを直したとき

`README-install.txt` などの文言や `LICENSE`、`THIRD-PARTY-NOTICES.txt` の直しだけなら、
`dist/` 配下の複製元を直して署名段（2）と同じコマンドをもう一度実行する。**再ビルドは不要**。

```
pwsh -NoProfile -File dist/sign_dist.ps1 -Only all -Version X.Y.Z
```

`build_dist.ps1` は `dist/staging/` を作り直すため、再ビルドすると署名がすべて失われて
全件の再署名になる。上のコマンドは staging をそのまま使い、署名済みのファイルを飛ばして
文書だけを差し替え、zip と `manifest.txt` を作り直す。署名すべき未署名ファイルが1件も
無ければ秘密鍵を使わないので、この実行に **YubiKey は要らない**（未署名ファイルが残っていた
場合はそこで証明書を要求して止まる）。

同梱ファイルの一覧は `dist/dist_docs.ps1` が持ち、組立（`build_dist.ps1`）と差し替え
（`sign_dist.ps1`）が同じ定義を使う。同梱物を増やすときはこのファイルだけを直す。

## 3. 公開（gh のある端末）

署名済み zip で Release を作成する。タグは対象コミットを指す:

```
gh release create vX.Y.Z --target <commit> --title "CADPeta vX.Y.Z" \
  dist/artifacts/CADPeta-AutoCAD-X.Y.Z.zip \
  dist/artifacts/CADPeta-CLI-X.Y.Z.zip \
  dist/artifacts/CADPeta-GUI-X.Y.Z.zip \
  dist/artifacts/CADPeta-CompatCAD-X.Y.Z.zip
```

## 注意

- **未署名の zip を公開しない。** 署名段（2）を必ず通す。
- 公開する 4 zip は同一コミットのビルドであること（`manifest.txt` の HEAD と一致）。
- `lib/vendor-refs/` と `lib/CADPeta-vendor-refs.zip` はライセンス下のベンダー DLL。自分の
  開発端末間でのみ私的に運び、公開リポへは含めない。
