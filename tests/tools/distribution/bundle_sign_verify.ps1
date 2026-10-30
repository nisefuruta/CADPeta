# bundle_sign_verify.ps1: 指定した bundle Contents の版別 DLL を accoreconsole で
# NETLOAD し、TESTCHART2（レイアウト/モデル）に PETA をかけて成果物（SVG/EMF）の
# SHA256 を出す。署名前後で同一ハッシュなら「署名はバイナリ末尾に付くだけで
# PETA 出力に影響しない」ことの機械証明になる。
#
#   -BundleContents <path>  .../CADPeta.bundle/Contents（net48/net8/net10 を含む）
#   -Ver 2024|2026|2027     使用する accoreconsole の版（TFM 対応は Directory.Build.props）
#   -Label <name>           出力ラベル（unsigned / signed 等）
#
# 前提: 対話 AutoCAD は終了しておく（DLL ロック回避）。SECURELOAD は変えず、DLL のフォルダだけを
# 信頼済みのフォルダ（C:\Tools\ACAD\dotnet）へコピーして NETLOAD し、終了後にコピーしたものだけを削除する。末尾でレジストリ残留を点検する。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$BundleContents,
    [string]$Ver = "2027",
    [Parameter(Mandatory)][string]$Label,
    [string]$OutRoot = "$env:TEMP\CadPeta_bundle_sign_verify"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'bundle_sign_verify.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
. "$PSScriptRoot\..\common\test_helpers.ps1"

$tfm = switch ($Ver) {
    "2024" { "net48" }
    "2026" { "net8" }
    "2027" { "net10" }
    default { throw "未知の版: $Ver" }
}
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
if (-not (Test-Path $acad)) { throw "accoreconsole がない: $acad" }
$plugin = Join-Path $BundleContents "$tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグインがない: $plugin" }

$sig = Get-AuthenticodeSignature $plugin
Write-Host "[$Label] plugin=$plugin" -ForegroundColor Cyan
Write-Host "[$Label]   署名状態=$($sig.Status) signer=$($sig.SignerCertificate.Thumbprint)" -ForegroundColor Cyan
Write-Host "[$Label]   sha256(dll)=$((Get-FileHash $plugin -Algorithm SHA256).Hash)" -ForegroundColor DarkGray

$drawings = @(
    @{ Key = "layout"; Src = "$repo\tests\assets\testcharts\TESTCHART2_layout.dwg" },
    @{ Key = "model";  Src = "$repo\tests\assets\testcharts\TESTCHART2.dwg" }
)

$results = @()
# CAD のタイムアウト・強制終了でも永続 sysvar（SECURELOAD 等）を利用者プロファイルへ残さない包括ガード
# （スナップショットは最初の起動前、復元は全図面ループの完了後に1回だけ）
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$runError = $null
try {
    foreach ($d in $drawings) {
        $work = Join-Path $OutRoot "$Label`_$($d.Key)"
        New-Item -ItemType Directory -Force $work | Out-Null
        Remove-Item "$work\*" -Force -Recurse -ErrorAction SilentlyContinue
        $dwgCopy = Join-Path $work ([IO.Path]::GetFileName($d.Src))
        Copy-Item $d.Src $dwgCopy -Force

        # NETLOAD→PETA→空行(Enter=全体) の .scr（ASCII）
        $scr = Join-Path $work "run.scr"
        @"
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
PETA

_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

        $stdout = Join-Path $work "accore_stdout.log"
        Push-Location -LiteralPath (Split-Path -Parent $scr)  # accoreconsole は図面ごとのログを作業ディレクトリへ書く
        try { & $acad /i $dwgCopy /s $scr /l ja-JP 2>&1 | Out-File $stdout -Encoding utf8 } finally { Pop-Location }
        $exit = $LASTEXITCODE

        foreach ($ext in @("svg", "emf")) {
            $f = Get-ChildItem $work -Filter "*.$ext" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($f) {
                $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
                $results += [pscustomobject]@{ Case = $d.Key; Ext = $ext; Sha256 = $h; File = $f.Name }
                Write-Host ("[{0}] {1,-6} {2}  {3}" -f $Label, $d.Key, $ext.ToUpper(), $h) -ForegroundColor Green
            } else {
                $results += [pscustomobject]@{ Case = $d.Key; Ext = $ext; Sha256 = "(missing)"; File = "" }
                Write-Warning "[$Label] $($d.Key) の .$ext が出ていない（exit=$exit）。$stdout を確認"
            }
        }
    }
}
catch { $runError = $_ }
finally {
    $regReport = $null # [ref] で受け取る変数を先に作る
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
Assert-CadRunAndRegRestore $runError $regReport

# SECURELOAD のレジストリ残留点検（版のプロファイルキー）
Write-Host "[$Label] SECURELOAD 残留点検:" -ForegroundColor DarkGray
$secResidue = reg query "HKCU\SOFTWARE\Autodesk\AutoCAD" /s /v SECURELOAD 2>$null | Select-String "0x0"
if ($secResidue) { Write-Warning "SECURELOAD=0 の残留がある。要復元: $secResidue" } else { Write-Host "  残留なし" -ForegroundColor DarkGray }

$results | Format-Table -AutoSize
$results | ConvertTo-Json | Out-File (Join-Path $OutRoot "$Label.hashes.json") -Encoding utf8
Write-Host "[$Label] ハッシュを $(Join-Path $OutRoot "$Label.hashes.json") に保存" -ForegroundColor Cyan
