# lint の検出器(find_script_duplicates.ps1、find_ref_without_variable.ps1)が共有する、調べる .ps1 の集め方。

# 省略された -Folder と -Root を決める。-Folder を省略するとリポジトリの dist と tests/tools、-Root を省略するとリポジトリの根。
# 調べるフォルダが無ければ止める（読み飛ばすと、何も検査せずに合格する）。
function Resolve-LintScope([string[]]$Folder, [string]$Root) {
    # このファイルは tests/tools/lint にあるので、根は 3 つ上
    $repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    $folders = if ($Folder) { $Folder } else { @((Join-Path $repoRoot 'dist'), (Join-Path $repoRoot 'tests/tools')) }
    $missing = @($folders | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) })
    if ($missing.Count) { throw "調べるフォルダがありません: $($missing -join ', ')" }
    @{
        Root   = if ($Root) { $Root } else { $repoRoot }
        Folder = $folders
    }
}

# フォルダの下の .ps1 を再帰で集める。作業の出力のフォルダ(artifacts、staging)と、Python の仮想環境(.venv)、ビルドの出力(bin、obj)には入らない。
# 他の試験がその中に作業フォルダを作っては削除するので、-Recurse でたどると途中でフォルダが消えて失敗する。
function Get-ScriptFiles([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -Filter '*.ps1' -File
    foreach ($child in (Get-ChildItem -LiteralPath $Directory -Directory)) {
        if ($child.Name -in @('artifacts', 'staging', '.venv', 'bin', 'obj')) { continue }
        Get-ScriptFiles $child.FullName
    }
}