# dist_docs.ps1: 配布 zip へ入れる「署名しない同梱ファイル」の定義と複製。
# build_dist.ps1（組立時）と sign_dist.ps1（署名後の再 zip 時）が同じ定義を使う。
#
# ここに載せてよいのは Authenticode 署名を持たないファイルだけである。署名済みの PE と
# 互換 CAD の register.ps1／unregister.ps1 を混ぜると、署名済みバイト列を複製元の未署名
# バイト列で上書きしてしまう。

# staging のファイルの置き換え（Copy-StagingFileSynced、Move-StagingFileReplacing）
. (Join-Path $PSScriptRoot "staging_files.ps1")

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

# install.bat が展開の欠けを確かめるための、CADPeta.bundle の全ファイルの一覧を $ZipRoot\bundle-files.txt に書く。
# 1 行に 1 ファイルで、CADPeta.bundle からの相対パス（区切りは \）、ASCII、CRLF。
# 組み立て（build_dist.ps1）と bat の試験が同じこの関数で作る（書式を試験に書き写さない）。
# bat はファイルの名前を ASCII でしか確かめられないので、ASCII 以外の名前があれば止める。
function Write-BundleFileList([string]$ZipRoot) {
    $bundle = Join-Path $ZipRoot "CADPeta.bundle"
    $files = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($bundle, $_.FullName) } | Sort-Object)
    if ($files.Count -eq 0) { throw "bundle のファイルが無い: $bundle" }
    $nonAscii = @($files | Where-Object { $_ -match '[^\x20-\x7E]' })
    if ($nonAscii.Count -gt 0) { throw "bundle に ASCII 以外の名前のファイルがある: $($nonAscii -join ', ')" }
    [IO.File]::WriteAllText((Join-Path $ZipRoot "bundle-files.txt"), (($files -join "`r`n") + "`r`n"), [Text.Encoding]::ASCII)
}

# 同梱ファイルを $ZipRoot へ複製し、バイト列が変わったファイル名だけを返す。
# 返り値は差し替えの報告に使う（同一内容なら複製もしないので空になる）。
# $WorkDir を渡すと（sign_dist.ps1 の署名の後の差し替え）、staging のファイルをその場で書き換えず、$WorkDir の一時ファイルに複製して
# ディスクへ書き出してから、名前の変更で置き換える（途中で止まっても、同梱ファイルは前か後の内容のどちらかになる）。
function Copy-DistDocs([string]$Group, [string]$ZipRoot, [string]$DistRoot, [string]$RepoRoot, [string]$WorkDir = "") {
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
        if ($WorkDir) {
            $temp = Join-Path $WorkDir ("doc-" + (Split-Path -Leaf $src))
            [void](Copy-StagingFileSynced $src $temp)
            Move-StagingFileReplacing $temp $dst
        }
        else { Copy-Item -LiteralPath $src -Destination $dst -Force }
        $changed += (Split-Path -Leaf $src)
    }
    return $changed
}
