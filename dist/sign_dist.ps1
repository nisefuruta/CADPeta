# sign_dist.ps1: 組み立て済みの配布物 staging を YubiKey のコード署名証明書で署名し、
# 影響する zip を作り直す。実行は利用者（YubiKey の PIN/タッチを伴う）。
# 先に build_dist.ps1 で dist\staging\ を作ってから走らせる。
#
#   -Only bundle|cli|compat|all  署名対象の配布物（既定 all）
#   -Thumbprint <TP>             署名証明書の拇印（既定=YubiKey slot 9c）
#   -TimestampUrl <URL>          RFC3161 タイムスタンプ（既定 DigiCert）
#   -NoTimestamp                 タイムスタンプなしで署名（オフライン時）
#   -NoReZip                     署名だけ行い zip は作り直さない（検証用途）
#   -RetryCount <N>              署名の試行回数（既定 3。YubiKey の一過性エラー対策）
#
# 署名対象は自作アセンブリ（CadPeta.*.dll）、cadpeta.exe、cadpeta-gui.exe、および互換 CAD 用
# セットアップスクリプト（compat の register.ps1／unregister.ps1）。第三者 DLL
# （PdfPig・System.* 等）と install.bat／uninstall.bat は署名しない（.bat は Authenticode の
# 署名機構が Windows に無い）。同一バイト列の自作アセンブリが複数の製品・TFM フォルダに
# ある場合は代表だけを署名し、署名済みバイト列を各配置先へ複製する。
#
# 再 zip の前に、署名しない同梱ファイル（README・.bat・LICENSE・信頼登録ファイル）を
# 複製元の現在の内容へ差し替える（dist_docs.ps1 が build_dist.ps1 と共通の定義を持つ）。
# README の文言だけを直したときに、再ビルドと再署名をやり直さずに済ませるためである。
# 署名済みファイルが1件も無ければ秘密鍵を使わないので、この場合は YubiKey も要らない。
#
# 注意: 既定の証明書は自己署名である。signtool verify /pa は信頼された発行元へ
# 登録していない端末ではチェーン検証に失敗する。本スクリプトの検証は「署名者の拇印が
# 一致し、タイムスタンプが付いた」ことで判定する（チェーン信頼は SECURELOAD の
# 実機対照で別途確認する）。
param(
    [string]$Thumbprint = "A4850D199DDC8851E3795554C9616B499FC89E36",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [ValidateSet("all", "bundle", "cli", "gui", "compat")]
    [string]$Only = "all",
    [switch]$NoTimestamp,
    [switch]$NoReZip,
    [ValidateRange(1, 10)]
    [int]$RetryCount = 3,
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
# 再試行の待ち時間の基数（秒）。試行回数に比例して延ばす。スマートカードのセッションが
# 立ち直る猶予を与えるためで、長く待つ性質のものではない。
$SignRetryWaitSeconds = 3
$dist = $PSScriptRoot
$repo = Split-Path -Parent $dist
$staging = Join-Path $dist "staging"
$artifacts = Join-Path $dist "artifacts"
# 署名しない同梱ファイルの定義（build_dist.ps1 と共通）
. (Join-Path $PSScriptRoot "dist_docs.ps1")
$groups = if ($Only -eq "all") { @("bundle", "cli", "gui", "compat") } else { @($Only) }
$stagingRoots = @{
    bundle = Join-Path $staging "bundle"
    cli = Join-Path $staging "cli\pkg"
    gui = Join-Path $staging "gui\pkg"
    compat = Join-Path $staging "compat"
}
# -Only all は4配布物すべてを署名する契約である。一部だけの staging を警告で通すと、
# 「all 成功」に見える部分リリースを作れるため、署名環境の確認より先に不足を確定させる。
foreach ($g in $groups) {
    if (-not (Test-Path -LiteralPath $stagingRoots[$g] -PathType Container)) {
        throw "署名対象 $g の staging が無い: $($stagingRoots[$g])。build_dist.ps1 -Only $Only を先に実行すること"
    }
}

# ---- signtool を解決（最新の x64 を採る） ----
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { throw "signtool.exe が見つからない（Windows SDK を導入すること）" }
Write-Host "[sign] signtool: $signtool" -ForegroundColor DarkGray

# ---- 証明書の確認 ----
# 秘密鍵を必要とするのは未署名ファイルが残っているときだけである。全件が署名済みで
# 同梱文書の差し替えと再 zip しかしない実行では YubiKey を要求しない（署名の検証は
# ファイル内の署名者拇印を見るだけで、ストアの秘密鍵を使わない）。
$cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
if (-not $cert) { $cert = Get-Item "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction SilentlyContinue }
if ($cert) {
    $selfSigned = [string]::Equals($cert.Subject, $cert.Issuer)
    Write-Host "[sign] 証明書: $($cert.Subject)" -ForegroundColor Cyan
    Write-Host "[sign]   拇印=$Thumbprint  失効=$($cert.NotAfter)  自己署名=$selfSigned" -ForegroundColor Cyan
}
else {
    Write-Host "[sign] 署名証明書が My ストアに無い（拇印 $Thumbprint）。未署名ファイルがあれば中止する。" -ForegroundColor Yellow
}
# 実際に署名する直前に呼ぶ。ここで初めて YubiKey の実在を必須にする。
function Assert-SigningCertificate {
    if (-not $cert) { throw "署名証明書が My ストアに無い: thumbprint $Thumbprint（YubiKey を挿しているか確認）" }
    if (-not $cert.HasPrivateKey) { throw "証明書に秘密鍵が無い: $Thumbprint（YubiKey を挿しているか確認）" }
}

# ---- 署名対象の収集 ----
# PE（自作アセンブリ CadPeta.*.dll と cadpeta*.exe）は signtool、互換 CAD 用の
# PowerShell セットアップスクリプト（register.ps1／unregister.ps1）は
# Set-AuthenticodeSignature で署名する。第三者 DLL（PdfPig・System.* 等）は署名しない。
# install.bat／uninstall.bat は Authenticode の署名機構が Windows に無いため対象外
# （信頼の要は SECURELOAD が検証する署名済み PE）。
function Get-Targets([string]$grp) {
    switch ($grp) {
        "bundle" {
            $d = Join-Path $staging "bundle\CADPeta.bundle\Contents"
            if (Test-Path $d) { Get-ChildItem $d -Recurse -Filter "CadPeta.*.dll" }
        }
        "cli" {
            $d = Join-Path $staging "cli\pkg"
            if (Test-Path $d) { Get-ChildItem $d -Filter "cadpeta.exe" }
        }
        "gui" {
            $d = Join-Path $staging "gui\pkg"
            if (Test-Path $d) { Get-ChildItem $d -Filter "cadpeta-gui.exe" }
        }
        "compat" {
            $d = Join-Path $staging "compat"
            if (Test-Path $d) { Get-ChildItem $d -Recurse -Filter "CadPeta.*.dll" }
        }
    }
}
# .ps1 セットアップスクリプトは compat のみが持つ（register.ps1／unregister.ps1）。
function Get-ScriptTargets([string]$grp) {
    if ($grp -eq "compat") {
        $d = Join-Path $staging "compat"
        if (Test-Path $d) { Get-ChildItem $d -Recurse -Filter "*.ps1" }
    }
}
$targets = @()
$scriptTargets = @()
foreach ($g in $groups) {
    $t = Get-Targets $g
    if ($t) { $targets += $t }
    $s = Get-ScriptTargets $g
    if ($s) { $scriptTargets += $s }
    if (-not $t -and -not $s) { Write-Host "[sign] 対象なし（未 staging?）: $g" -ForegroundColor Yellow }
}
$targets = @($targets | Select-Object -ExpandProperty FullName -Unique)
$scriptTargets = @($scriptTargets | Select-Object -ExpandProperty FullName -Unique)
if ($targets.Count -eq 0 -and $scriptTargets.Count -eq 0) {
    throw "署名対象が1件も無い。先に build_dist.ps1 で staging を作ること"
}
Write-Host "[sign] 署名対象 PE $($targets.Count) 件 / スクリプト $($scriptTargets.Count) 件" -ForegroundColor Cyan
$targets | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }
$scriptTargets | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

# ---- 署名の実施（成否はファイルの署名実体で判定し、残りを再試行する） ----
# 判定基準は後段の検証と同じく「署名者の拇印が一致し、タイムスタンプが付いた」こと。
# 自己署名のためチェーン信頼（$sig.Status）は問わない。
function Test-SignedFile([string]$path) {
    $sig = Get-AuthenticodeSignature -FilePath $path
    if (-not $sig.SignerCertificate) { return $false }
    if ($sig.SignerCertificate.Thumbprint -ne $Thumbprint) { return $false }
    if (-not $NoTimestamp -and -not $sig.TimeStamperCertificate) { return $false }
    return $true
}

# YubiKey は連続署名中にスマートカード層の一過性エラー（SCARD_E_UNEXPECTED 0x8010001f 等）を
# 返すことがある。signtool は失敗した時点で残りを処理せず終了するが、それまでに署名した
# ファイルはディスク上に残る。そこで毎回「まだ署名されていないファイル」だけを渡して
# 試行を繰り返す。すでに済んだ分へ PIN/タッチを再要求しないので、別プロセスで中断した
# 実行の再開もこの仕組みで賄える。
function Invoke-SignWithRetry([string[]]$files, [string]$label) {
    if ($files.Count -eq 0) { return }
    $signArgs = @("sign", "/sha1", $Thumbprint, "/fd", "sha256")
    if (-not $NoTimestamp) { $signArgs += @("/tr", $TimestampUrl, "/td", "sha256") }
    for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
        $remaining = @($files | Where-Object { -not (Test-SignedFile $_) })
        if ($remaining.Count -eq 0) { break }
        Assert-SigningCertificate
        $done = $files.Count - $remaining.Count
        if ($attempt -eq 1) {
            if ($done -gt 0) { Write-Host "[sign] ${label}: 署名済み $done 件を飛ばす（前回の続きから）" -ForegroundColor Yellow }
            Write-Host "[sign] $label $($remaining.Count) 件を署名（YubiKey の PIN/タッチを求められる）..." -ForegroundColor Cyan
        }
        else {
            Write-Host ("[sign] $label 再試行 {0}/{1}: 残り {2} 件（済 {3} 件）" -f `
                $attempt, $RetryCount, $remaining.Count, $done) -ForegroundColor Yellow
            Start-Sleep -Seconds ($SignRetryWaitSeconds * ($attempt - 1))
        }
        # signtool の異常終了は再試行の対象なのでここでは中断しない。理由は表示し、
        # 成否の判定は次周回の Test-SignedFile（拇印＋タイムスタンプ）へ委ねる。
        try { & $signtool @signArgs @remaining }
        catch { Write-Host "[sign] signtool 例外: $($_.Exception.Message)" -ForegroundColor Yellow }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[sign] signtool が exit $LASTEXITCODE で終了（残りを再試行する）" -ForegroundColor Yellow
        }
    }
    $failed = @($files | Where-Object { -not (Test-SignedFile $_) })
    if ($failed.Count -gt 0) {
        $list = $failed -join ', '
        $hint = "YubiKey を挿し直して再実行すれば、署名済みの分は飛ばして続きから再開する"
        throw "$label の署名が $RetryCount 回とも失敗（$($failed.Count) 件）: $list — $hint"
    }
}

# ---- PowerShell スクリプト（.ps1）の署名（PE より先に行う） ----
# .ps1 も PE と同じく signtool で署名する。PowerShell 用 SIP により signtool は .ps1 へ
# 署名ブロック（# SIG # ...）を埋め込める。signtool は Set-AuthenticodeSignature と違い
# 証明書の Code Signing EKU を要求しないため、EKU 拡張を持たない既存のコード署名証明書でも
# PE と同じ拇印指定で .ps1 を署名できる。事故防止のため PE より先に署名する
# （トークンや証明書の問題は少数の .ps1 で先に露見する）。内容が異なるため重複排除はしない。
# 自己署名証明書ではチェーン未信頼で Status=UnknownError になるが署名ブロックは埋め込まれ、
# 拇印とタイムスタンプの一致は後段でまとめて検証する。
Invoke-SignWithRetry $scriptTargets "スクリプト"

# ---- PE（.dll/.exe）の署名（同一バイト列は重複排除） ----
# Core 等の netstandard 出力は複数の製品・TFM にそのまま複製される。未署名時点の
# SHA-256 が同じなら Authenticode の署名結果も共用できるため、各ハッシュの代表だけを
# YubiKey で署名する。署名後に代表ファイルを同一ハッシュの全配置先へ複製し、
# 後段では従来どおり全ファイルを検証する。
# 前回が途中で落ちた場合、署名済みのファイルはハッシュが変わるため別の群に分かれる。
# 署名済みの群は Invoke-SignWithRetry が飛ばし、未署名の群は代表を1件だけ署名して
# 複製する。結果は正しく、余分な秘密鍵操作は中断した群の数に留まる。
if ($targets.Count -gt 0) {
    $targetGroups = @($targets | Group-Object -Property {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    })
    $signTargets = @($targetGroups | ForEach-Object { $_.Group[0] })
    $duplicateCount = $targets.Count - $signTargets.Count
    Write-Host ("[sign] 秘密鍵操作 {0} 件（同一バイト列の複製 {1} 件を省略）" -f `
        $signTargets.Count, $duplicateCount) -ForegroundColor Cyan

    # YubiKey の PIN ポリシー次第で代表ファイルごとにタッチ/PIN を要求されることがある。
    Invoke-SignWithRetry $signTargets "PE"

    # 署名済みの代表を、署名前に同一ハッシュだったすべての配置先へ複製する。
    foreach ($targetGroup in $targetGroups) {
        $signedSource = [string]$targetGroup.Group[0]
        foreach ($destination in @($targetGroup.Group | Select-Object -Skip 1)) {
            Copy-Item -LiteralPath $signedSource -Destination ([string]$destination) -Force
        }
    }
    if ($duplicateCount -gt 0) {
        Write-Host "[sign] 署名済みファイルを $duplicateCount 配置先へ複製" -ForegroundColor Cyan
    }
}

# ---- 検証: 署名者拇印の一致とタイムスタンプの実在で判定（自己署名のため
#      チェーン信頼は問わない。それは SECURELOAD 実機で確認する） ----
$verifyTargets = @($targets) + @($scriptTargets)
$bad = @()
foreach ($f in $verifyTargets) {
    $sig = Get-AuthenticodeSignature -FilePath $f
    $signerTp = if ($sig.SignerCertificate) { $sig.SignerCertificate.Thumbprint } else { "(none)" }
    $hasTs = [bool]$sig.TimeStamperCertificate
    $ok = ($signerTp -eq $Thumbprint) -and ($NoTimestamp -or $hasTs)
    $mark = $ok ? "OK" : "NG"
    Write-Host ("   [$mark] {0}  status={1} signer={2} ts={3}" -f (Split-Path $f -Leaf), $sig.Status, $signerTp, $hasTs)
    if (-not $ok) { $bad += $f }
}
if ($bad.Count -gt 0) { throw "署名検証に失敗: $($bad -join ', ')" }
Write-Host "[sign] 全 $($verifyTargets.Count) 件を署名・検証（PE $($targets.Count) / スクリプト $($scriptTargets.Count)）" -ForegroundColor Green

# ---- 署名済みファイルで zip を作り直す ----
if ($NoReZip) { Write-Host "[sign] -NoReZip: zip は作り直さない" -ForegroundColor Yellow; return }

# 署名しない同梱ファイルを複製元の現在の内容へ差し替える。README の文言だけを直した場合、
# この工程があるので build_dist.ps1 の再実行（＝staging の作り直しと全件の再署名）は要らない。
# 差し替えるのは署名を持たないファイルだけで、署名済みの PE と .ps1 には触れない。
# staging を書き換えてから zip を作るので、後段の package_verify.ps1 の全バイト照合も通る。
$refreshedDocs = @()
foreach ($g in $groups) {
    $changed = @(Copy-DistDocs $g $stagingRoots[$g] $dist $repo)
    $refreshedDocs += $changed | ForEach-Object { "$g/$_" }
}
if ($refreshedDocs.Count -gt 0) {
    Write-Host "[sign] 同梱文書を差し替え（$($refreshedDocs.Count) 件）: $($refreshedDocs -join ', ')" -ForegroundColor Cyan
}
else {
    Write-Host "[sign] 同梱文書は staging と同一（差し替えなし）" -ForegroundColor DarkGray
}

New-Item -ItemType Directory -Force $artifacts | Out-Null
function Test-CompatComplete {
    # 3製品そろってはじめて配布 zip とする（部分 zip を配布物に見せない）
    $c = Join-Path $staging "compat"
    $need = @("ares", "ijcad", "bricscad")
    foreach ($p in $need) { if (-not (Test-Path (Join-Path $c $p))) { return $false } }
    (Test-Path (Join-Path $c "register.ps1"))
}
function ReZip([string]$grp) {
    switch ($grp) {
        "bundle" { $src = Join-Path $staging "bundle"; $zip = Join-Path $artifacts "CADPeta-AutoCAD-$Version.zip" }
        "cli" { $src = Join-Path $staging "cli\pkg"; $zip = Join-Path $artifacts "CADPeta-CLI-$Version.zip" }
        "gui" { $src = Join-Path $staging "gui\pkg"; $zip = Join-Path $artifacts "CADPeta-GUI-$Version.zip" }
        "compat" {
            if (-not (Test-CompatComplete)) {
                Write-Host "[sign] compat は3製品そろっていないため zip を作らない（ARES のみ等）。署名は済み。" -ForegroundColor Yellow
                return
            }
            $src = Join-Path $staging "compat"; $zip = Join-Path $artifacts "CADPeta-CompatCAD-$Version.zip"
        }
    }
    if (-not (Test-Path $src)) { throw "署名済み ZIP の組立元が無い: $src" }
    # リリース関門: 署名の信頼登録ファイル（公開 .cer と手順 txt）の同梱を必須にする。
    # build_dist.ps1 は証明書未用意なら同梱をスキップするため、未署名リリースだけでなく
    # 未同梱リリースもここで止める（RELEASE.md §0）。
    if (-not (Test-Path (Join-Path $src "*.cer"))) {
        throw "信頼登録用の公開証明書(*.cer)が $grp の zip 直下に無い。RELEASE.md §0 で用意し build_dist.ps1 を再実行すること"
    }
    if (-not (Test-Path (Join-Path $src "README-signing.txt"))) {
        throw "信頼登録手順(README-signing.txt)が $grp の zip 直下に無い。build_dist.ps1 を再実行すること"
    }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$src\*" -DestinationPath $zip
    $mb = [Math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Host "[sign] 署名済み zip: $zip ($mb MB)" -ForegroundColor Green
}
foreach ($g in $groups) { ReZip $g }

# 署名後の最終 ZIP を開き直し、staging とのファイル集合・全バイト一致を確認する。
$packageVerifier = Join-Path $repo "tests\tools\distribution\package_verify.ps1"
& $packageVerifier -Only $Only -Version $Version -StagingRoot $staging -ArtifactsDir $artifacts -RequireTrustFiles

# build_dist の manifest は未署名 ZIP のハッシュである。署名と再圧縮でバイト列が変わるため、
# 公開対象となる最終 ZIP の SHA-256 へ必ず更新する。
function Write-FinalManifest {
    $manifestPath = Join-Path $artifacts "manifest.txt"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "組立時 manifest が無い: $manifestPath。build_dist.ps1 を先に実行すること"
    }
    $previous = Get-Content -LiteralPath $manifestPath
    $head = $previous | Where-Object { $_ -like "HEAD: *" } | Select-Object -First 1
    $config = $previous | Where-Object { $_ -like "Config: *" } | Select-Object -First 1
    $builtVersion = $previous | Where-Object { $_ -like "Version: *" } | Select-Object -First 1
    if (-not $head -or -not $config -or -not $builtVersion) {
        throw "組立時 manifest に HEAD/Config/Version が揃っていない: $manifestPath"
    }
    if ($builtVersion.Substring("Version: ".Length) -ne $Version) {
        throw "組立時 manifest の Version ($builtVersion) と署名引数 ($Version) が一致しない"
    }
    $zipMap = @{
        bundle = Join-Path $artifacts "CADPeta-AutoCAD-$Version.zip"
        cli = Join-Path $artifacts "CADPeta-CLI-$Version.zip"
        gui = Join-Path $artifacts "CADPeta-GUI-$Version.zip"
        compat = Join-Path $artifacts "CADPeta-CompatCAD-$Version.zip"
    }
    $lines = @(
        $head
        "生成日時: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
        "Only: $Only"
        $config
        "Version: $Version"
        "署名済み: true"
    )
    # 同梱文書を差し替えた場合、PE は組立時の HEAD、文書は差替え時の作業ツリーに由来する。
    # 両者が同じとは限らないので、差し替えた事実と差替え時の HEAD を残す。
    if ($refreshedDocs.Count -gt 0) {
        $docHead = try { (& git -C $repo rev-parse HEAD 2>$null).Trim() } catch { "" }
        if (-not $docHead) { $docHead = "(不明)" }
        $lines += "署名時に差し替えた同梱文書: $($refreshedDocs -join ', ')"
        $lines += "差替え元 HEAD: $docHead"
    }
    $lines += ""
    foreach ($g in $groups) {
        $zip = $zipMap[$g]
        if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "最終 ZIP が無い: $zip" }
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        $lines += "$(Split-Path -Leaf $zip)  SHA256:$hash"
    }
    Set-Content -LiteralPath $manifestPath -Value $lines -Encoding UTF8
    Write-Host "[manifest] 署名後の最終 SHA-256 へ更新: $manifestPath" -ForegroundColor Green
}
Write-FinalManifest
