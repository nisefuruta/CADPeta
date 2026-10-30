# staging_files.ps1: 署名の間に staging のファイルを置き換える関数(sign_record.ps1 と dist_docs.ps1 が使う)。
# staging のファイル(署名するファイル、複製先、同梱文書、署名の記録)はその場で書き換えない。一時ファイルに書いて
# ディスクへ書き出し(FlushFileBuffers)、同じボリュームの中の名前の変更で置き換える。プロセスが止まった場合(例外、Ctrl+C、
# 強制終了)、置き換える先のファイルは前か後の内容のどちらかになる。電源断と OS のクラッシュでは、名前の変更がディスクに
# 書かれたかは保証されない(.NET の File.Move は書き込みを即時に行う指定をしない)。
# 作業フォルダとロックのファイルは staging の直下に置き、zip にするフォルダ(bundle、cli\pkg、gui\pkg、compat)には入れない。

# 置き換えのやり直しの回数と間隔。ウイルス対策ソフトなどが置き換える先のファイルを一時的に開いていると失敗するため。
$StagingMoveTries = 10
$StagingMoveWaitSeconds = 1

function Get-SignWorkDir([string]$Staging) { return (Join-Path $Staging "sign-work") }

function Get-SignLockPath([string]$Staging) { return (Join-Path $Staging "sign.lock") }

# 同じ staging への sign_dist の同時の実行を止める。ロックのファイルを排他(FileShare.None)で開き、閉じると削除する(DeleteOnClose)。
# 戻り値の FileStream を、呼び出し側が署名の終わりまで開いたままにして finally で閉じる。別の実行が開いていれば、何も変えずに例外で止まる。
# $ReadOnly(-WhatIf)のときは、ロックを取らず作らず、ロックのファイルがあれば開けるかを確かめるだけで $null を返す。
function Open-SignLock([string]$Staging, [bool]$ReadOnly) {
    $path = Get-SignLockPath $Staging
    try {
        if (-not $ReadOnly) {
            return [IO.FileStream]::new($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
        }
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $probe = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $probe.Dispose()
        }
        return $null
    }
    catch [IO.IOException] { throw "別の sign_dist の実行中です(同じ staging を同時に署名できません): ${path}: $($_.Exception.Message)" }
}

# ファイルの内容をディスクへ書き出す(FlushFileBuffers)。名前の変更で置き換える前に呼ぶ。
function Sync-StagingFile([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    try { $stream.Flush($true) } finally { $stream.Dispose() }
}

# $Source を一時ファイル $Target へ複製してディスクへ書き出し、複製の SHA256 を返す。
function Copy-StagingFileSynced([string]$Source, [string]$Target) {
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) -WhatIf:$false)
    [IO.File]::Copy($Source, $Target, $true)
    Sync-StagingFile $Target
    return (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash
}

# 一時ファイル $Source で $Target を置き換える(MoveFileEx の REPLACE_EXISTING)。最後まで失敗したら例外にする($Target は前の内容のまま)。
function Move-StagingFileReplacing([string]$Source, [string]$Target) {
    for ($try = 1; ; $try++) {
        try { [IO.File]::Move($Source, $Target, $true); return }
        catch {
            if ($try -ge $StagingMoveTries) { throw "staging のファイルを置き換えられません(ほかのプログラムが開いている可能性。前の内容のままです): ${Target}: $($_.Exception.Message)" }
            Start-Sleep -Seconds $StagingMoveWaitSeconds
        }
    }
}

# 文字列を一時ファイルに書いてディスクへ書き出し、$Target を置き換える。
function Write-StagingFileReplacing([string]$Target, [string]$Text) {
    $temp = "$Target.tmp"
    [IO.File]::WriteAllText($temp, $Text, (New-Object Text.UTF8Encoding($false)))
    Sync-StagingFile $temp
    Move-StagingFileReplacing $temp $Target
}
