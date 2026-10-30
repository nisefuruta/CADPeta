# build_dist.ps1: CADPeta の配布物を再現可能に組み立てるハーネス。
# 成果物は dist/staging/（組立途中）と dist/artifacts/（zip）へ出す（いずれも git 管理外）。
#
#   -Only bundle    : AutoCAD bundle zip のみ
#   -Only cli       : CLI zip のみ
#   -Only compat    : 互換CAD プラグイン zip のみ
#   （既定は全部）
#
# 依存: dotnet SDK。git は不要。
#   -Only bundle / cli : AutoCAD/互換CAD 本体は不要（AutoCAD 参照は NuGet の compile 専用＝
#                        ExcludeAssets=runtime。CLI は CAD 参照を持たない）。
#   -Only compat / all : 互換CAD（ARES/IJCAD/BricsCAD）本体の参照 DLL が要る（HintPath 直接参照）。
#                        本体が無い場合は lib/vendor-refs/<cad>/ に参照 DLL を退避すれば足りる
#                        （src/Directory.Build.props が自動で優先。README「ソースからビルド」参照）。
param(
    [ValidateSet("all", "bundle", "cli", "gui", "compat")]
    [string]$Only = "all",
    [string]$Version = "",          # 渡さなければ PackageContents.xml の AppVersion（版番号の原本）
    [string]$Config = "Release",
    [switch]$AllowDirty,           # dirty な worktree でも確認せず続行する（非対話・意図的なとき）
    [switch]$DiscardSignedStaging  # 署名済み staging を確認せず作り直す（非対話・意図的なとき）
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # リポジトリルート（dist/ の親）
$dist = $PSScriptRoot
$staging = Join-Path $dist "staging"
$artifacts = Join-Path $dist "artifacts"
$license = Join-Path $root "LICENSE"
$notices = Join-Path $root "THIRD-PARTY-NOTICES.txt"
# 版番号の原本は PackageContents.xml の AppVersion。DLL の版もビルドのときにこの値から決まる（src/Directory.Build.props）。
# 違う版を渡されたら、zip の名前と中身の版が食い違うので、何も作らずに止める
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $dist "bundle\CADPeta.bundle\PackageContents.xml") -Raw -Encoding UTF8)).DocumentElement.GetAttribute("AppVersion")
if (-not $appVersion) { throw "PackageContents.xml の AppVersion を読めない" }
if (-not $Version) { $Version = $appVersion }
elseif ($Version -ne $appVersion) { throw "-Version ($Version) が PackageContents.xml の AppVersion ($appVersion) と違う。版は AppVersion で決める" }

# README・.bat・LICENSE・信頼登録ファイルなど、署名しない同梱ファイルの定義と複製。
# sign_dist.ps1 が署名後の差し替えで同じ定義を使う（dist_docs.ps1）。
. (Join-Path $PSScriptRoot "dist_docs.ps1")
# 自作の DLL に開発機のパスと失敗の注入が残っていないことの検査（dll_path_check.ps1）。配布のビルドは
# -p:ContinuousIntegrationBuild=true でパスを /_/ に置き換える（props に置かないのは、[CallerFilePath] でソースの
# フォルダを読む試験のビルドまで置き換えないため）
. (Join-Path $PSScriptRoot "dll_path_check.ps1")
# プラグインのビルドの定義（build_e2e.ps1 と共有する。dist_build.ps1）
. (Join-Path $PSScriptRoot "dist_build.ps1")
# 署名する自作のファイルの定義と内容の記録（sign_dist.ps1 と共通。sign_record.ps1）
. (Join-Path $PSScriptRoot "sign_record.ps1")

# 実行時に再配布する DLL に AutoCAD/互換CAD の参照アセンブリが混入していないことを機械検査する
# （compile 専用参照は ExcludeAssets=runtime で除外されている前提の裏取り。混入は配布事故）
$forbiddenDlls = @(
    "accoremgd.dll", "acdbmgd.dll", "acmgd.dll", "AcCoreMgd.dll", "AcDbMgd.dll", "AcMgd.dll",
    "AutoCAD.NET.dll",
    "TD_Mgd.dll", "FxCoreMgd.dll", "FxProperties.dll",                         # ARES(Graebert)
    "GcaddMgd.dll", "GrxMgd.dll", "Itjp.Ijcad.dll",                            # IJCAD
    "Teigha.Runtime.dll", "Teigha.TD.dll", "Bricscad.dll", "TD_DbMgd.dll"     # BricsCAD/ODA
)
# ARES の SDK は年式ごとの版サフィックス付き名（TD_Mgd_4.03_14.dll・FxCoreMgd_23.1_16.dll・
# TD_Mgd_Net8_26.8_17.dll 等）なので、名前の列挙では漏れる。パターンでも検査する
$forbiddenDllPatterns = @("TD_Mgd_*.dll", "FxCoreMgd_*.dll")

function Assert-NoForbidden([string]$dir, [string]$label) {
    $hit = Get-ChildItem $dir -Filter *.dll -ErrorAction SilentlyContinue |
        Where-Object {
            $name = $_.Name
            ($forbiddenDlls -contains $name) -or
            [bool]($forbiddenDllPatterns | Where-Object { $name -like $_ })
        }
    if ($hit) {
        throw "[$label] CAD 本体の参照 DLL が混入している: $($hit.Name -join ', ')（ExcludeAssets=runtime を確認）"
    }
}

# ファイルの大きさを MB（小数第 2 位まで）で返す。完成メッセージの表示に使う。
function Get-FileSizeMb([string]$path) {
    [Math]::Round((Get-Item $path).Length / 1MB, 2)
}

function New-CleanDir([string]$p) {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    New-Item -ItemType Directory -Force $p | Out-Null
}

# 言語カタログ（コマンドライン文言の ja/en。src/CadPeta.UseCases/lang/ が原本）。
# プラグインは DLL の隣の lang フォルダを実行時に読む。CLI/GUI の文言切替は未対応だが、
# 収録物は4種 zip で共通に揃える。入れ忘れは配布事故なので実在を必須検査する。
$langFiles = @("1041.lang", "1033.lang")
function Copy-LangFiles([string]$srcLangDir, [string]$dstRoot, [string]$label) {
    $dstLang = Join-Path $dstRoot "lang"
    New-Item -ItemType Directory -Force $dstLang | Out-Null
    foreach ($lang in $langFiles) {
        $src = Join-Path $srcLangDir $lang
        if (-not (Test-Path $src)) { throw "[$label] 言語カタログの複製元が無い: $src" }
        Copy-Item $src $dstLang
    }
}
function Assert-LangFiles([string]$dir, [string]$label) {
    foreach ($lang in $langFiles) {
        if (-not (Test-Path (Join-Path $dir "lang\$lang"))) { throw "[$label] 言語カタログ lang\$lang が zip 内容に無い" }
    }
}

function Require-Notices {
    if (-not (Test-Path $license)) { throw "LICENSE が無い: $license" }
    if (-not (Test-Path $notices)) { throw "THIRD-PARTY-NOTICES.txt が無い: $notices（先に生成すること）" }
}

# worktree が dirty のまま配布ビルドを走らせると、成果物に何が混じっているか（作業中の
# 未コミット変更か、確定した HEAD の内容か）が再現できなくなる。ただし dirty のままビルドして
# よいかはその時々の判断なので、止めずに続行可否を確認する。dist/staging・dist/artifacts は
# .gitignore 済みでここには出てこない。
function Confirm-Worktree {
    & git -C $root diff --quiet
    $diffDirty = ($LASTEXITCODE -ne 0)
    & git -C $root diff --cached --quiet
    $stagedDirty = ($LASTEXITCODE -ne 0)
    $status = & git -C $root status --porcelain --untracked-files=all
    $statusDirty = [bool]$status
    if (-not ($diffDirty -or $stagedDirty -or $statusDirty)) { return }

    Write-Host "[dist] worktree が dirty です（未コミットの変更・未追跡ファイルあり）:" -ForegroundColor Yellow
    Write-Host ($status -join "`n")
    Write-Host "配布ビルドは通常 HEAD と一致した clean な状態で行うと、成果物に混じる内容を再現できる。" -ForegroundColor DarkGray

    if ($AllowDirty) {
        Write-Host "[dist] -AllowDirty 指定のため dirty のまま続行する。" -ForegroundColor Yellow
        return
    }
    # 入力がリダイレクトされている（非対話）と Read-Host が使えないので、意図を明示させる。
    if ([Console]::IsInputRedirected) {
        throw "worktree が dirty で、非対話実行のため確認できない。意図的に続行するなら -AllowDirty を付けて再実行すること。"
    }
    $ans = Read-Host "この状態のまま配布ビルドを続けますか？ (y/N)"
    if ($ans -notmatch '^(y|yes)$') {
        throw "利用者がビルド続行を選ばなかったため中止した。"
    }
    Write-Host "[dist] dirty のまま続行する（利用者確認済み）。" -ForegroundColor Yellow
}

# ---------------- AutoCAD bundle ----------------
function Build-Bundle {
    Require-Notices
    $proj = Join-Path $root "src\CadPeta.Plugin.Acad\CadPeta.Plugin.Acad.csproj"
    Write-Host "[bundle] プラグインを3系ビルド..." -ForegroundColor Cyan
    Invoke-DistBuild $proj $Config

    $tfmMap = @{ "net48" = "net48"; "net8" = "net8.0-windows"; "net10" = "net10.0-windows" }
    $bundleStage = Join-Path $staging "bundle\CADPeta.bundle"
    New-CleanDir $bundleStage
    Copy-Item (Join-Path $dist "bundle\CADPeta.bundle\PackageContents.xml") $bundleStage

    foreach ($short in $tfmMap.Keys) {
        $src = Join-Path $root "src\CadPeta.Plugin.Acad\bin\$Config\$($tfmMap[$short])"
        if (-not (Test-Path $src)) { throw "ビルド出力が無い: $src" }
        Assert-NoForbidden $src "bundle/$short"
        $dstContents = Join-Path $bundleStage "Contents\$short"
        New-CleanDir $dstContents
        Copy-Item "$src\*.dll" $dstContents
        Copy-LangFiles (Join-Path $src "lang") $dstContents "bundle/$short"
        Assert-LangFiles $dstContents "bundle/$short"
        Assert-ReleaseOwnAssemblies $dstContents "bundle/$short"
        if ($short -eq "net48") { Assert-ConsistentAssemblyReferences $dstContents "bundle/$short" }
        # PackageContents.xml が指す本体 DLL の実在を確認
        if (-not (Test-Path (Join-Path $dstContents "CadPeta.Plugin.Acad.dll"))) {
            throw "本体 DLL が Contents\$short に無い"
        }
    }

    # bundle と並べる install/uninstall/README/ライセンス/信頼登録ファイル
    $zipRoot = Join-Path $staging "bundle"
    Copy-DistDocs "bundle" $zipRoot $dist $root | Out-Null
    Write-BundleFileList $zipRoot

    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $zip = Join-Path $artifacts "CADPeta-AutoCAD-$Version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$zipRoot\*" -DestinationPath $zip
    $mb = Get-FileSizeMb $zip
    Write-Host "[bundle] 完成: $zip ($mb MB)" -ForegroundColor Green
}

# ---------------- CLI ----------------
function Build-Cli {
    Require-Notices
    $proj = Join-Path $root "src\CadPeta.Cli\CadPeta.Cli.csproj"
    $pubDir = Join-Path $staging "cli\publish"
    New-CleanDir $pubDir
    Write-Host "[cli] framework-dependent 単一ファイルで publish..." -ForegroundColor Cyan
    # framework-dependent（ランタイム非同梱）・単一ファイル。要 .NET 10 ランタイム
    # 参照グラフの複数経路が同じ netstandard 出力を同時生成すると deps.json が競合するため、
    # 他の配布ビルドと同じく単一ノードで実行する。
    & dotnet publish $proj -c $Config -r win-x64 --self-contained false -m:1 $DistCiProperty `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o $pubDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CLI の publish に失敗" }
    $exe = Join-Path $pubDir "cadpeta.exe"
    if (-not (Test-Path $exe)) { throw "cadpeta.exe が生成されなかった: $pubDir" }

    # 単一ファイルへ畳み込めなかった管理 DLL が publish 出力に残っていないこと
    $managed = Get-ChildItem $pubDir -Filter *.dll -ErrorAction SilentlyContinue
    if ($managed) { throw "[cli] 単一ファイル外に管理 DLL が残っている: $($managed.Name -join ', ')" }
    # 実行時に self-contained のランタイム DLL を巻き込んでいない（=fd である）ことの裏取り:
    # publish 出力に coreclr.dll 等の実行基盤が無いこと
    $runtimeDlls = Get-ChildItem $pubDir -Filter *.dll -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @("coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll") }
    if ($runtimeDlls) {
        throw "[cli] self-contained のランタイムが混入している（fd のはずが: $($runtimeDlls.Name -join ', ')）"
    }

    $zipRoot = Join-Path $staging "cli\pkg"
    New-CleanDir $zipRoot
    Copy-Item $exe $zipRoot
    Copy-LangFiles (Join-Path $root "src\CadPeta.UseCases\lang") $zipRoot "cli"
    Assert-LangFiles $zipRoot "cli"
    Copy-DistDocs "cli" $zipRoot $dist $root | Out-Null

    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $zip = Join-Path $artifacts "CADPeta-CLI-$Version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$zipRoot\*" -DestinationPath $zip
    $exeMb = Get-FileSizeMb $exe
    $zipMb = Get-FileSizeMb $zip
    Write-Host "[cli] 完成: $zip (zip $zipMb MB / exe $exeMb MB)" -ForegroundColor Green
}

# ---------------- GUI ----------------
function Build-Gui {
    Require-Notices
    $proj = Join-Path $root "src\CadPeta.Gui\CadPeta.Gui.csproj"
    $pubDir = Join-Path $staging "gui\publish"
    New-CleanDir $pubDir
    Write-Host "[gui] framework-dependent 単一ファイルで publish..." -ForegroundColor Cyan
    & dotnet publish $proj -c $Config -r win-x64 --self-contained false -m:1 $DistCiProperty `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o $pubDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "GUI の publish に失敗" }
    $exe = Join-Path $pubDir "cadpeta-gui.exe"
    if (-not (Test-Path $exe)) { throw "cadpeta-gui.exe が生成されなかった: $pubDir" }
    $managed = Get-ChildItem $pubDir -Filter *.dll -ErrorAction SilentlyContinue
    if ($managed) { throw "[gui] 単一ファイル外に管理 DLL が残っている: $($managed.Name -join ', ')" }
    $runtimeDlls = Get-ChildItem $pubDir -Filter *.dll -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @("coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll") }
    if ($runtimeDlls) { throw "[gui] self-contained ランタイムが混入している: $($runtimeDlls.Name -join ', ')" }

    $zipRoot = Join-Path $staging "gui\pkg"
    New-CleanDir $zipRoot
    Copy-Item $exe $zipRoot
    Copy-LangFiles (Join-Path $root "src\CadPeta.UseCases\lang") $zipRoot "gui"
    Assert-LangFiles $zipRoot "gui"
    Copy-DistDocs "gui" $zipRoot $dist $root | Out-Null
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $zip = Join-Path $artifacts "CADPeta-GUI-$Version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$zipRoot\*" -DestinationPath $zip
    $exeMb = Get-FileSizeMb $exe
    $zipMb = Get-FileSizeMb $zip
    Write-Host "[gui] 完成: $zip (zip $zipMb MB / exe $exeMb MB)" -ForegroundColor Green
}

# ---------------- 互換CAD ----------------
function Build-Compat {
    Require-Notices
    # ARES は年式でホストランタイムと SDK アセンブリ名が異なる（2020/2023/2024=.NET Framework
    # 4.8 ビルド・SDK は AresSdkYearNet48 で切替、2027=.NET 8）。年式ごとに正しいペイロードが
    # 要るため ares\<年> のサブフォルダへ分ける（register.ps1 が製品キーの年から適合フォルダを
    # 選ぶ）。既定年式（2024）以外のビルド出力は bin\ares<年>\ へ分離される
    # （src/Directory.Build.props）。IJCAD/BricsCAD は net8 単一。
    $prods = @(
        @{ Name = "ares";     Sub = "ares\2020"; Proj = "CadPeta.Plugin.Ares";     Tfm = "net48";           Dll = "CadPeta.Plugin.Ares.dll"; AresYear = "2020" },
        @{ Name = "ares";     Sub = "ares\2023"; Proj = "CadPeta.Plugin.Ares";     Tfm = "net48";           Dll = "CadPeta.Plugin.Ares.dll"; AresYear = "2023" },
        @{ Name = "ares";     Sub = "ares\2024"; Proj = "CadPeta.Plugin.Ares";     Tfm = "net48";           Dll = "CadPeta.Plugin.Ares.dll" },
        @{ Name = "ares";     Sub = "ares\2027"; Proj = "CadPeta.Plugin.Ares";     Tfm = "net8.0-windows";  Dll = "CadPeta.Plugin.Ares.dll" },
        @{ Name = "ijcad";    Sub = "ijcad";     Proj = "CadPeta.Plugin.Ijcad";    Tfm = "net8.0-windows";  Dll = "CadPeta.Plugin.Ijcad.dll" },
        @{ Name = "bricscad"; Sub = "bricscad";  Proj = "CadPeta.Plugin.Bricscad"; Tfm = "net8.0-windows";  Dll = "CadPeta.Plugin.Bricscad.dll" }
    )
    $zipRoot = Join-Path $staging "compat"
    New-CleanDir $zipRoot
    foreach ($p in $prods) {
        $proj = Join-Path $root "src\$($p.Proj)\$($p.Proj).csproj"
        Write-Host "[compat] $($p.Sub) をビルド..." -ForegroundColor Cyan
        $binBase = "bin"
        $properties = @()
        if ($p.AresYear) {
            $properties += "-p:AresSdkYearNet48=$($p.AresYear)"
            $binBase = "bin\ares$($p.AresYear)"
        }
        Invoke-DistBuild $proj $Config $properties
        $src = Join-Path $root "src\$($p.Proj)\$binBase\$Config\$($p.Tfm)"
        if (-not (Test-Path $src)) { throw "ビルド出力が無い: $src" }
        Assert-NoForbidden $src "compat/$($p.Sub)"
        $dst = Join-Path $zipRoot $p.Sub
        New-CleanDir $dst
        Copy-Item "$src\*.dll" $dst
        Copy-LangFiles (Join-Path $src "lang") $dst "compat/$($p.Sub)"
        Assert-LangFiles $dst "compat/$($p.Sub)"
        Assert-ReleaseOwnAssemblies $dst "compat/$($p.Sub)"
        if ($p.Tfm -eq "net48") { Assert-ConsistentAssemblyReferences $dst "compat/$($p.Sub)" }
        if (-not (Test-Path (Join-Path $dst $p.Dll))) { throw "本体 DLL が $($p.Sub) に無い" }
    }
    # register.ps1／unregister.ps1 と、両者が読み込む compat_common.ps1 は署名対象なので、
    # 署名後に差し替えない dist_docs.ps1 の外側でここだけが複製する。
    foreach ($script in "register.ps1", "unregister.ps1", "compat_common.ps1") {
        Copy-Item (Join-Path $dist "compat\$script") $zipRoot
    }
    Copy-DistDocs "compat" $zipRoot $dist $root | Out-Null

    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $zip = Join-Path $artifacts "CADPeta-CompatCAD-$Version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$zipRoot\*" -DestinationPath $zip
    $mb = Get-FileSizeMb $zip
    Write-Host "[compat] 完成: $zip ($mb MB)" -ForegroundColor Green
}

# ---------------- manifest ----------------
# 今回の実行で実際に生成された zip（$builtZips）の SHA-256 と、どの HEAD・どのパラメータで
# ビルドしたかを記録する。artifacts ディレクトリを素朴に走査すると、別バージョンや
# -Only 対象外の旧 zip まで今回の HEAD 扱いで記録してしまうため、対象を明示的に絞る。
# 実際に zip が作られたときだけ意味を持つ（zip が1つも無ければ manifest は書かない）。
function Write-Manifest([string]$headCommit, [string[]]$builtZips) {
    $existing = $builtZips | Where-Object { Test-Path $_ }
    if (-not $existing) {
        Write-Host "[manifest] zip が無いため manifest は書かない" -ForegroundColor Yellow
        return
    }
    $manifestPath = Join-Path $artifacts "manifest.txt"
    $lines = @(
        "HEAD: $headCommit"
        "生成日時: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
        "Only: $Only"
        "Config: $Config"
        "Version: $Version"
        ""
    )
    foreach ($zipPath in ($existing | Sort-Object)) {
        $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
        $lines += "$(Split-Path -Leaf $zipPath)  SHA256:$hash"
    }
    Set-Content -Path $manifestPath -Value $lines -Encoding UTF8
    Write-Host "[manifest] 記録: $manifestPath" -ForegroundColor Green
}

# dist/staging を作り直すと、そこにある署名は消えて全件の再署名（YubiKey の PIN／タッチ）に
# なる。README 等の文言直しだけなら sign_dist.ps1 が staging を保ったまま差し替えるので、
# 署名済み staging を消す前に意図を確認する。
function Confirm-StagingDiscard {
    if (-not (Test-Path $staging)) { return }
    # 署名するのは自作のファイルだけ。Microsoft などの第三者の DLL は配布元の署名を持つので、判定に含めない
    $signed = Get-ChildItem $staging -Recurse -File -Include 'CadPeta.*.dll', 'cadpeta*.exe', '*.ps1' -ErrorAction SilentlyContinue |
        Where-Object { (Get-AuthenticodeSignature -FilePath $_.FullName).SignerCertificate } |
        Select-Object -First 1
    if (-not $signed) { return }

    Write-Host "[dist] dist/staging に署名済みファイルがあります（例: $($signed.FullName)）。" -ForegroundColor Yellow
    Write-Host "ビルドは staging を作り直すため、この署名は失われ、全件の再署名が要ります。" -ForegroundColor Yellow
    Write-Host "README や LICENSE の直しだけなら sign_dist.ps1 が staging を保ったまま差し替える（RELEASE.md §2-1）。" -ForegroundColor DarkGray

    if ($DiscardSignedStaging) {
        Write-Host "[dist] -DiscardSignedStaging 指定のため署名済み staging を作り直す。" -ForegroundColor Yellow
        return
    }
    if ([Console]::IsInputRedirected) {
        throw "署名済み staging を消すことになり、非対話実行のため確認できない。意図的に作り直すなら -DiscardSignedStaging を付けて再実行すること。"
    }
    $ans = Read-Host "署名済み staging を作り直しますか？ (y/N)"
    if ($ans -notmatch '^(y|yes)$') {
        throw "利用者が続行を選ばなかったため中止した（staging は残っている）。"
    }
    Write-Host "[dist] 署名済み staging を作り直す（利用者確認済み）。" -ForegroundColor Yellow
}

# ---------------- ディスパッチ ----------------
# dirty worktree なら続行可否を確認する（成果物とコミットの対応が保証できなくなるため）
Confirm-Worktree
$headCommit = (& git -C $root rev-parse HEAD).Trim()

# 各ターゲットは自分の副ディレクトリしか掃除しないため、-Only 部分実行や前回の中断で
# 他ターゲットの旧成果物が dist/staging に残置しうる（人が誤って NETLOAD する事故のもと）。
# ここで staging ルートを丸ごと作り直し、実行対象外の旧成果物を必ず消す。
Confirm-StagingDiscard
New-CleanDir $staging

switch ($Only) {
    "bundle" { Build-Bundle }
    "cli"    { Build-Cli }
    "gui"    { Build-Gui }
    "compat" { Build-Compat }
    "all"    { Build-Bundle; Build-Cli; Build-Gui; Build-Compat }
}

# sign_dist.ps1 が署名の前に照合するため、署名する自作のファイルごとの SHA256 を staging の記録に書く
$builtTargets = if ($Only -eq "all") { @("bundle", "cli", "gui", "compat") } else { @($Only) }
Write-SignRecord $staging $builtTargets

# 圧縮前 staging の検査だけでは、Compress-Archive の欠落・破損を検出できない。
# 作成した ZIP を開き直し、全収録バイトが staging と一致することを共通関門で確認する。
$packageVerifier = Join-Path $root "tests\tools\distribution\package_verify.ps1"
& $packageVerifier -Only $Only -Version $Version -StagingRoot $staging -ArtifactsDir $artifacts

# ターゲットごとの zip ファイル名は固定（$Version 付き）なので、今回 -Only で実行した
# ターゲットに対応する zip だけを manifest の対象に絞り込む
$zipMap = @{
    bundle  = Join-Path $artifacts "CADPeta-AutoCAD-$Version.zip"
    cli     = Join-Path $artifacts "CADPeta-CLI-$Version.zip"
    gui     = Join-Path $artifacts "CADPeta-GUI-$Version.zip"
    compat  = Join-Path $artifacts "CADPeta-CompatCAD-$Version.zip"
}
$builtZips = $builtTargets | ForEach-Object { $zipMap[$_] }
Write-Manifest $headCommit $builtZips
