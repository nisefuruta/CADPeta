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
# 署名の前に、staging の自作のファイルを build_dist.ps1 が書いた記録（staging\sign-record.json）と照合する。
# 途中から再開するときは、未署名のファイルは作ったときの値と、署名済みのファイルは記録した署名後の値と一致しなければ止める
# （記録の無い署名済みのファイルを、署名の検証が通ることだけで受け入れない）。署名済みと記録したファイルの署名を検証できないときも止める。
# staging のファイル（署名するファイル、複製先、同梱文書、記録）はその場で書き換えない。署名は staging\sign-work の一時ファイルで行い、
# 検証が通ったら、先に署名後の SHA256 を記録に書いてから、名前の変更で置き換える（sign_record.ps1 の Invoke-SignStaging）。
# プロセスが止まった場合（例外、Ctrl+C、強制終了）は、同じコマンドで続きから再開できる。電源断と OS のクラッシュでは、
# 名前の変更がディスクに書かれたかを保証しないので、止まったら build_dist.ps1 で staging を作り直す。
# 同じ staging への同時の実行は、staging\sign.lock を排他で開いて止める（別の実行が開いていれば、何も変えずに止まる）。
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
#   -WhatIf                      署名する対象と照合の結果を表示するだけで、署名も zip の作り直しもしない
#                                （確認の画面を出せない実行では、-WhatIf か -Confirm:$false が要る）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [ValidateSet("all", "bundle", "cli", "gui", "compat")]
    [string]$Only = "all",
    [switch]$NoTimestamp,
    [switch]$NoReZip,
    [ValidateRange(1, 10)]
    [int]$RetryCount = 3,
    # zip の名前の版。渡さなければ、build_dist が artifacts\manifest.txt に書いた版を使う
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
# 既定の署名証明書の拇印（YubiKey の slot 9c。姉妹リポジトリと同じ証明書）
$DefaultThumbprint = "A4850D199DDC8851E3795554C9616B499FC89E36"
if (-not $Thumbprint) { $Thumbprint = $DefaultThumbprint }
# 再試行の待ち時間の基数（秒）。試行回数に比例して延ばす。スマートカードのセッションが
# 立ち直る猶予を与えるためで、長く待つ性質のものではない。
$SignRetryWaitSeconds = 3
$dist = $PSScriptRoot
$repo = Split-Path -Parent $dist
$staging = Join-Path $dist "staging"
$artifacts = Join-Path $dist "artifacts"
# 署名しない同梱ファイルの定義（build_dist.ps1 と共通）
. (Join-Path $PSScriptRoot "dist_docs.ps1")
# 署名する自作のファイルの定義と内容の記録（build_dist.ps1 と共通）
. (Join-Path $PSScriptRoot "sign_record.ps1")
# 実行の確認（-WhatIf と -Confirm。E2E と共通）
. (Join-Path $PSScriptRoot "script_confirm.ps1")
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
# zip を作り直すときの版は、build_dist が manifest.txt に書いた版（PackageContents.xml の AppVersion）。違う版を渡されたら、
# 署名の後ではなく、何も変えないうちに止める（署名の後で止まると、署名し直しになる）
if (-not $NoReZip) {
    $builtManifest = Join-Path $artifacts "manifest.txt"
    $builtLine = if (Test-Path -LiteralPath $builtManifest) { @(Get-Content -LiteralPath $builtManifest | Where-Object { $_ -like "Version: *" }) | Select-Object -First 1 }
    if (-not $builtLine) { throw "組立時 manifest の Version が無い: $builtManifest。build_dist.ps1 を先に実行すること" }
    $builtVersion = $builtLine.Substring("Version: ".Length)
    if (-not $Version) { $Version = $builtVersion }
    elseif ($Version -ne $builtVersion) { throw "署名引数の版 ($Version) が組立時 manifest の版 ($builtVersion) と違う。何も署名していない" }
}
# 同じ staging への同時の実行を止める。署名の終わりまで staging\sign.lock を排他で開いたままにし、別の実行が開いていれば何も変えずに止まる。
# -WhatIf はロックを取らず作らず、別の実行が開いているかを確かめるだけにする。
$signLock = Open-SignLock $staging ([bool]$WhatIfPreference)
try {


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
        # 拡張キー使用法の拡張が無い証明書は用途を制限しない（RFC 5280 4.2.1.12）が、配布物に署名する証明書がコード署名の用途を
        # 明示していないのは、証明書の作り間違い（別の用途の証明書、CA 証明書など）の可能性が高い。既定の証明書には拡張キー使用法が
        # 無いので、拡張キー使用法の無い証明書は、拇印が既定の証明書と一致するときだけ使い、それ以外は止める。
        $eku = @($cert.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.37" })
        if ($eku.Count) {
            if (-not @($eku.EnhancedKeyUsages | Where-Object { $_.Value -in @("1.3.6.1.5.5.7.3.3", "2.5.29.37.0") }).Count) {
                throw "コード署名に使用できる証明書ではない（拡張キー使用法にコード署名が無い）: $Thumbprint"
            }
        }
        elseif ($Thumbprint -ne $DefaultThumbprint) {
            throw "拡張キー使用法の無い証明書は、既定の証明書（拇印 $DefaultThumbprint）のときだけ使う: $Thumbprint"
        }
    }

    # ---- 署名対象の収集 ----
    # PE（自作アセンブリ CadPeta.*.dll と cadpeta*.exe）と、互換 CAD 用の PowerShell セットアップ
    # スクリプト（register.ps1／unregister.ps1／compat_common.ps1）を signtool で署名する。対象の定義は
    # build_dist.ps1 と共通（sign_record.ps1 の Get-SignTargets）。第三者 DLL（PdfPig・System.* 等）は署名しない。
    # install.bat／uninstall.bat は Authenticode の署名機構が Windows に無いため対象外
    # （信頼の要は SECURELOAD が検証する署名済み PE）。
    $allTargets = @(Get-SignTargets $staging $groups)
    foreach ($g in $groups) {
        if (-not ($allTargets | Where-Object { $_.Group -eq $g })) { Write-Host "[sign] 対象なし（未 staging?）: $g" -ForegroundColor Yellow }
    }
    $targets = @($allTargets | Where-Object { $_.Kind -eq "pe" } | ForEach-Object { $_.Path })
    $scriptTargets = @($allTargets | Where-Object { $_.Kind -eq "script" } | ForEach-Object { $_.Path })
    if ($targets.Count -eq 0 -and $scriptTargets.Count -eq 0) {
        throw "署名対象が1件も無い。先に build_dist.ps1 で staging を作ること"
    }
    # 署名する前に、staging の自作のファイルが build_dist.ps1 の作ったものと同じ内容であることを確かめる。
    # 未署名のファイルは作ったときの値と、前回までに署名したファイルは署名後に記録した値と一致しなければ止める。
    $recordFailures = @(Test-SignRecord $staging $groups)
    if ($recordFailures.Count -gt 0) {
        throw ("staging の自作のファイルが build_dist.ps1 の記録と一致しない（$($recordFailures.Count) 件）。build_dist.ps1 で staging を作り直すこと:`n" + ($recordFailures -join "`n"))
    }
    Write-Host "[sign] staging の自作のファイル $($allTargets.Count) 件が build_dist.ps1 の記録と一致" -ForegroundColor Cyan

    Write-Host "[sign] 署名対象 PE $($targets.Count) 件 / スクリプト $($scriptTargets.Count) 件" -ForegroundColor Cyan
    $targets | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }
    $scriptTargets | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }

    # ---- 署名の実施（成否はファイルの署名実体で判定し、残りを再試行する） ----
    # 判定基準は後段の検証と同じく「署名者の拇印が一致し、タイムスタンプが付いた」こと。
    # 自己署名のためチェーン信頼（$sig.Status）は問わない。
    function Test-SignedFile([string]$path) {
        $sig = Get-AuthenticodeSignature -FilePath $path
        # 内容が署名と違う（HashMismatch）・署名が無い・読めない形式は受け入れない。チェーンの未信頼（NotTrusted、.ps1 の UnknownError）は許容する
        if ("$($sig.Status)" -in @("HashMismatch", "NotSigned", "NotSupportedFileFormat", "Incompatible")) { return $false }
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
    # ---- 確認: ここより後で、秘密鍵を使って署名し、staging の同梱文書を差し替えて zip と manifest を作り直す ----
    # 署名済みかどうかは記録の値だけで決める。署名済みと記録したファイルは、署名も検証できなければ止める（-WhatIf でも確かめる）。
    $signStates = @(Get-SignStates $staging $allTargets)
    $unverified = @($signStates | Where-Object { $_.Signed -and -not (Test-SignedFile $_.Path) } | ForEach-Object { $_.Path })
    if ($unverified.Count -gt 0) {
        throw ("署名済みと記録したファイルの署名を検証できない（$($unverified.Count) 件）。build_dist.ps1 で staging を作り直すこと:`n" + ($unverified -join "`n"))
    }
    $unsignedCount = @($signStates | Where-Object { -not $_.Signed }).Count
    $work = Measure-SignWork $signStates
    $changePlan = New-ChangePlan "staging の自作のファイル $($allTargets.Count) 件と配布物の zip" "署名して zip を作り直す（-Only $Only）" @(
        "未署名 $unsignedCount 件のうち、作ったときに同じ内容のファイルは代表だけを署名して残りへ複製する: 秘密鍵で署名 $($work.Sign) 件、複製 $($work.Copy) 件（拇印 $Thumbprint の証明書。YubiKey の PIN／タッチを求められる）",
        "署名済み $($allTargets.Count - $unsignedCount) 件は飛ばす",
        $(if ($NoTimestamp) { "タイムスタンプは付けない" } else { "タイムスタンプ: $TimestampUrl" }),
        $(if ($NoReZip) { "zip は作り直さない（-NoReZip）" } else { "同梱文書を差し替え、$artifacts の zip と manifest.txt を作り直す" }))
    Write-ChangePlan $changePlan
    Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)

    # ---- signtool を解決（最新の x64 を採る）。確認の後に行う（-WhatIf は Windows SDK の無い PC でも動く） ----
    $signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "signtool.exe が見つからない（Windows SDK を導入すること）" }
    Write-Host "[sign] signtool: $signtool" -ForegroundColor DarkGray

    # .ps1 を先に署名する（トークンや証明書の問題は少数の .ps1 で先に分かる）。続けて PE（.dll/.exe）を署名する。
    # Core 等の netstandard 出力は複数の製品・TFM にそのまま複製されるので、作ったときに同じ内容だったファイルは代表だけを署名して
    # 複製する。未署名の代表は 1 回の signtool の呼び出しにまとめる（YubiKey の PIN／タッチを署名の回数だけ求めないように）。
    # staging のファイルの置き換えと記録の順は Invoke-SignStaging（sign_record.ps1）が決める。
    $signFiles = { param([string[]]$files) Invoke-SignWithRetry $files "署名" }
    $isSigned = { param([string]$path) Test-SignedFile $path }
    foreach ($kind in "script", "pe") {
        $kindTargets = @($allTargets | Where-Object { $_.Kind -eq $kind })
        if ($kindTargets.Count -eq 0) { continue }
        $result = Invoke-SignStaging $staging $kindTargets $signFiles $isSigned
        Write-Host "[sign] $(if ($kind -eq 'pe') { 'PE' } else { 'スクリプト' }): 署名 $($result.Signed) 件 / 複製 $($result.Copied) 件" -ForegroundColor Cyan
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
    # 同梱ファイルも staging でその場で書き換えず、作業フォルダの一時ファイルから置き換える。
    $docsWork = Get-SignWorkDir $staging
    if (Test-Path -LiteralPath $docsWork) { Remove-Item -LiteralPath $docsWork -Recurse -Force }
    New-Item -ItemType Directory -Path $docsWork | Out-Null
    $refreshedDocs = @()
    foreach ($g in $groups) {
        $changed = @(Copy-DistDocs $g $stagingRoots[$g] $dist $repo $docsWork)
        $refreshedDocs += $changed | ForEach-Object { "$g/$_" }
    }
    Remove-Item -LiteralPath $docsWork -Recurse -Force
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
        # 署名したファイル（DLL・exe・.ps1）を、zip を作る直前の staging と、作った zip の中の両方で、記録した署名済みの値と照合する
        # （検証の後に staging のファイルが同じ署名者の別の版へ置き換えられても、その版を zip に入れて成功にしない）。
        # zip は一時の名前で作り、照合が通ってから成功の名前にする。前の zip は先に削除し、失敗したときに成功の名前の zip を残さない。
        $expected = Get-SignedZipExpectations $staging $grp
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Assert-SignedHashes $expected (Get-FolderFileHashes $src @($expected.Keys)) "zip を作る直前の staging（$grp）"
        $partial = [IO.Path]::ChangeExtension($zip, ".partial.zip")
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        try {
            Compress-Archive -Path "$src\*" -DestinationPath $partial
            Assert-SignedHashes $expected (Get-ZipFileHashes $partial @($expected.Keys)) "作った zip（$grp）"
        }
        catch {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
            throw
        }
        Move-StagingFileReplacing $partial $zip
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
}
finally { if ($signLock) { $signLock.Dispose() } }
