# dist_docs.ps1: 配布 zip へ入れる「署名しない同梱ファイル」の定義と複製。
# build_dist.ps1（組立時）と sign_dist.ps1（署名後の再 zip 時）が同じ定義を使う。
#
# ここに載せてよいのは Authenticode 署名を持たないファイルだけである。署名済みの PE と
# 互換 CAD の register.ps1／unregister.ps1 を混ぜると、署名済みバイト列を複製元の未署名
# バイト列で上書きしてしまう。

# 配布物ごとの同梱ファイル（複製元の絶対パス）。複製先のファイル名は複製元と同じ。
function Get-DistDocSource([string]$Group, [string]$DistRoot, [string]$RepoRoot) {
    $perGroup = switch ($Group) {
        "bundle" { @("bundle\install.bat", "bundle\uninstall.bat", "bundle\README-install.txt") }
        "cli"    { @("cli\README-cli.txt") }
        "gui"    { @("gui\README-gui.txt") }
        "compat" { @("compat\install.bat", "compat\uninstall.bat", "compat\README-compat.txt") }
        default  { throw "未知の配布物: $Group" }
    }
    @($perGroup | ForEach-Object { Join-Path $DistRoot $_ }) +
    @((Join-Path $RepoRoot "LICENSE"), (Join-Path $RepoRoot "THIRD-PARTY-NOTICES.txt"))
}

# 署名の信頼登録ファイル（公開 .cer と手順 txt）。署名者が用意する（RELEASE.md §0）ので、
# 開発ビルドを止めないため未用意なら警告してスキップする。リリースでの入れ忘れは
# sign_dist.ps1 が再 zip 前に必須検査する。
function Get-DistTrustSource([string]$DistRoot) {
    $common = Join-Path $DistRoot "common"
    $cer = @(Get-ChildItem -LiteralPath $common -Filter *.cer -ErrorAction SilentlyContinue)
    if ($cer.Count -eq 0) {
        Write-Host "[dist] 署名証明書(dist/common/*.cer)が未用意のため信頼登録ファイルを同梱しない（開発ビルド）" -ForegroundColor Yellow
        return @()
    }
    @($cer | Select-Object -ExpandProperty FullName) + @(Join-Path $common "README-signing.txt")
}

# 同梱ファイルを $ZipRoot へ複製し、バイト列が変わったファイル名だけを返す。
# 返り値は差し替えの報告に使う（同一内容なら複製もしないので空になる）。
function Copy-DistDocs([string]$Group, [string]$ZipRoot, [string]$DistRoot, [string]$RepoRoot) {
    if (-not (Test-Path -LiteralPath $ZipRoot -PathType Container)) {
        throw "[$Group] 同梱ファイルの複製先が無い: $ZipRoot"
    }
    $changed = @()
    $sources = @(Get-DistDocSource $Group $DistRoot $RepoRoot) + @(Get-DistTrustSource $DistRoot)
    foreach ($src in $sources) {
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
            throw "[$Group] 同梱ファイルの複製元が無い: $src"
        }
        $dst = Join-Path $ZipRoot (Split-Path -Leaf $src)
        if (Test-Path -LiteralPath $dst -PathType Leaf) {
            $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
            $dstHash = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
            if ($srcHash -eq $dstHash) { continue }
        }
        Copy-Item -LiteralPath $src -Destination $dst -Force
        $changed += (Split-Path -Leaf $src)
    }
    return $changed
}
