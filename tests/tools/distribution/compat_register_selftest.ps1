# 互換CAD登録スクリプトのARES複数版選択、DLL対応、登録解除の往復を隔離キーで検査する。
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$source = Join-Path $root "dist\compat"
$work = Join-Path $env:TEMP ("CADPeta_compat_register_" + [guid]::NewGuid().ToString("N"))
$registryRoot = "HKCU:\SOFTWARE\CADPeta.Tests\$([guid]::NewGuid().ToString('N'))"
$aresBase = Join-Path $registryRoot "Graebert GmbH"
$aresMachineBase = Join-Path $registryRoot "Machine\Graebert GmbH"
$bricscadBase = Join-Path $registryRoot "Bricsys\BricsCAD"
$bricscadMachineBase = Join-Path $registryRoot "Machine\Bricsys\BricsCAD"
$ijcadBase = Join-Path $registryRoot "IntelliJapan"
$ijcadMachineBase = Join-Path $registryRoot "Machine\IntelliJapan"
# ARES は本体を信頼場所へコピーして登録する。実 %AppData% を汚さないよう隔離先へ差し替える
$aresTrusted = Join-Path $work "trusted"
$aresCadpeta = Join-Path $aresTrusted "CADPeta"
# 年 < 2024 は profile.xml の startup LISP 方式。テスト用の profile.xml 置き場（実 %AppData% を汚さない）
$aresProfileRoot = Join-Path $work "profiles"
$profile2020 = Join-Path $aresProfileRoot "2020\Profiles\default\profile.xml"
$profile2023 = Join-Path $aresProfileRoot "2023\Profiles\default\profile.xml"

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

# 実機の profile.xml と同じ最小構造（applications グループ＋既存 LISP 項目1つ）
function New-TestProfile([string]$path) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $xml = @(
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<settings>'
        '    <group name="applications">'
        '        <group name="item1">'
        '            <setting type="QString" value="" name="description"/>'
        '            <setting type="QString" value="EXISTING.LSP" name="file"/>'
        '            <setting type="bool" value="false" name="managed"/>'
        '            <setting type="QString" value="EXISTING.LSP" name="name"/>'
        '            <setting type="int" value="2" name="reasons"/>'
        '            <setting type="QString" value="LISP" name="type"/>'
        '        </group>'
        '    </group>'
        '</settings>'
    )
    [IO.File]::WriteAllLines($path, $xml, (New-Object Text.UTF8Encoding($false)))
}
function Test-ProfileHasCadPeta([string]$path) {
    return ((Test-Path -LiteralPath $path) -and ([IO.File]::ReadAllText($path) -match 'load_cadpeta\.lsp'))
}

try {
    New-Item -ItemType Directory -Path (Join-Path $work "ares\2020") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work "ares\2023") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work "ares\2024") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work "ares\2027") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work "bricscad") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work "ijcad") -Force | Out-Null
    Copy-Item (Join-Path $source "install.bat") $work
    Copy-Item (Join-Path $source "uninstall.bat") $work
    Copy-Item (Join-Path $source "register.ps1") $work
    Copy-Item (Join-Path $source "unregister.ps1") $work
    Set-Content -LiteralPath (Join-Path $work "ares\2020\CadPeta.Plugin.Ares.dll") -Value "net48-2020-test"
    Set-Content -LiteralPath (Join-Path $work "ares\2023\CadPeta.Plugin.Ares.dll") -Value "net48-2023-test"
    Set-Content -LiteralPath (Join-Path $work "ares\2024\CadPeta.Plugin.Ares.dll") -Value "net48-test"
    Set-Content -LiteralPath (Join-Path $work "ares\2027\CadPeta.Plugin.Ares.dll") -Value "net8-test"
    Set-Content -LiteralPath (Join-Path $work "bricscad\CadPeta.Plugin.Bricscad.dll") -Value "bricscad-test"
    Set-Content -LiteralPath (Join-Path $work "ijcad\CadPeta.Plugin.Ijcad.dll") -Value "ijcad-test"
    New-Item -Path (Join-Path $aresBase "ARES Standard 2020") -Force | Out-Null
    New-Item -Path (Join-Path $aresBase "ARES Standard 2024") -Force | Out-Null
    New-Item -Path (Join-Path $aresBase "ARES Standard 2024 x64") -Force | Out-Null
    New-Item -Path (Join-Path $aresMachineBase "ARES Standard 2023") -Force | Out-Null
    New-Item -Path (Join-Path $aresMachineBase "ARES Standard 2024") -Force | Out-Null
    New-Item -Path (Join-Path $aresMachineBase "ARES Standard 2027") -Force | Out-Null
    New-Item -Path (Join-Path $aresMachineBase "ARES Commander 2027") -Force | Out-Null
    New-Item -Path (Join-Path $bricscadBase "V25x64\ja_JP") -Force | Out-Null
    New-Item -Path (Join-Path $bricscadMachineBase "V25x64\ja_JP") -Force | Out-Null
    New-Item -Path (Join-Path $bricscadMachineBase "V26x64\ja_JP") -Force | Out-Null
    New-Item -Path (Join-Path $ijcadBase "IJCAD Professional\R25\ja-JP") -Force | Out-Null
    New-Item -Path (Join-Path $ijcadMachineBase "IJCAD Professional\R25\ja-jp") -Force | Out-Null
    New-Item -Path (Join-Path $ijcadMachineBase "IJCAD Professional\R26\ja-jp") -Force | Out-Null
    # 年 < 2024（2020/2023）は profile.xml の startup LISP 方式。テスト用 profile.xml を用意する
    New-TestProfile $profile2020
    New-TestProfile $profile2023

    $register = Join-Path $work "register.ps1"
    $unregister = Join-Path $work "unregister.ps1"

    # ARES 呼び出し共通のオーバーライド（隔離した信頼場所とプロファイル置き場）。
    # 直接呼び出し（& $register @aresOv）へ名前付きで束ねるためハッシュテーブルにする
    $aresOv = @{ AresTrustedRootOverride = $aresTrusted; AresProfileRootOverride = $aresProfileRoot }

    # 登録順は利用者キーの2020→2024、次に機側検出の2023→2027。2件目（2024）まで処理した直後に
    # 失敗を注入する。2020はLISPフック（profile.xml編集）、2024はレジストリ（既存値あり）なので、
    # 「profile.xml編集の巻き戻し（2020）」と「既存レジストリ値の復元（2024）」の両方を検査する
    $rollbackKey2024 = Join-Path $aresBase "ARES Standard 2024\Applications\CADPeta"
    New-Item -Path $rollbackKey2024 -Force | Out-Null
    New-ItemProperty -LiteralPath $rollbackKey2024 -Name "LOADER" -Value "existing-loader" -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $rollbackKey2024 -Name "LOADCTRLS" -Value 7 -PropertyType DWord -Force | Out-Null
    $failureOutput = & powershell.exe -NoLogo -NoProfile -File $register -Product ares -NoPause -RegistryBaseOverride $aresBase -RegistryMachineBaseOverride $aresMachineBase -AresTrustedRootOverride $aresTrusted -AresProfileRootOverride $aresProfileRoot -FailAfterRegistrationCount 2 2>&1
    Assert-True ($LASTEXITCODE -eq 1) "途中失敗の自己試験が失敗終了しませんでした"
    Assert-True ((Get-ItemProperty -LiteralPath $rollbackKey2024).LOADER -eq "existing-loader") "途中失敗で既存LOADERを復元できませんでした"
    Assert-True ((Get-ItemProperty -LiteralPath $rollbackKey2024).LOADCTRLS -eq 7) "途中失敗で既存LOADCTRLSを復元できませんでした"
    Assert-True (-not (Test-ProfileHasCadPeta $profile2020)) "途中失敗で2020のprofile.xml編集が巻き戻されていません"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $aresBase "ARES Standard 2027\Applications\CADPeta"))) "途中失敗で部分登録（2027）が残りました"
    # 途中失敗時、この実行で新規作成した配置フォルダ（信頼場所コピー）も撤去される
    Assert-True (-not (Test-Path -LiteralPath $aresCadpeta) -or -not (Get-ChildItem -LiteralPath $aresCadpeta -ErrorAction SilentlyContinue)) "途中失敗で配置フォルダのコピーが残りました"
    Assert-True (($failureOutput | Out-String) -match "rolled back to the previous state") "途中失敗時に復元結果を案内していません"
    Remove-Item -LiteralPath $rollbackKey2024 -Recurse -Force

    $output = (& $register -Product ares -NoPause -RegistryBaseOverride $aresBase -RegistryMachineBaseOverride $aresMachineBase @aresOv) 6>&1 | Out-String

    $key2020 = Join-Path $aresBase "ARES Standard 2020\Applications\CADPeta"
    $key2023 = Join-Path $aresBase "ARES Standard 2023\Applications\CADPeta"
    $key2024 = Join-Path $aresBase "ARES Standard 2024\Applications\CADPeta"
    $key2024x64 = Join-Path $aresBase "ARES Standard 2024 x64\Applications\CADPeta"
    $key2027 = Join-Path $aresBase "ARES Standard 2027\Applications\CADPeta"
    $machineKey2027 = Join-Path $aresMachineBase "ARES Standard 2027\Applications\CADPeta"
    $commanderKey2027 = Join-Path $aresBase "ARES Commander 2027\Applications\CADPeta"
    # 2024/2027 はレジストリ方式（LOADER が信頼場所のコピー先を指す）
    Assert-True (Test-Path -LiteralPath $key2024) "ARES 2024へレジストリ登録されていません"
    Assert-True (-not (Test-Path -LiteralPath $key2024x64)) "実効対象外のx64サフィックスキーへ登録されました"
    Assert-True (Test-Path -LiteralPath $key2027) "ARES 2027へレジストリ登録されていません"
    Assert-True (-not (Test-Path -LiteralPath $machineKey2027)) "HKLM相当の検出元へ書き込みました"
    Assert-True (-not (Test-Path -LiteralPath $commanderKey2027)) "対応外のARES製品へ登録されました"
    Assert-True ((Get-ItemProperty -LiteralPath $key2024).LOADER -like "*\CADPeta\2024\CadPeta.Plugin.Ares.dll") "ARES 2024のLOADERが信頼場所を指していません"
    Assert-True ((Get-ItemProperty -LiteralPath $key2027).LOADER -like "*\CADPeta\2027\CadPeta.Plugin.Ares.dll") "ARES 2027のLOADERが信頼場所を指していません"
    # 2020/2023 は LISP フック方式（レジストリキーは作らず profile.xml へ startup LISP を登録）
    Assert-True (-not (Test-Path -LiteralPath $key2020)) "ARES 2020にレジストリキーが作られました（LISP方式のはず）"
    Assert-True (-not (Test-Path -LiteralPath $key2023)) "ARES 2023にレジストリキーが作られました（LISP方式のはず）"
    Assert-True (Test-ProfileHasCadPeta $profile2020) "ARES 2020のprofile.xmlにstartup LISPが登録されていません"
    Assert-True (Test-ProfileHasCadPeta $profile2023) "ARES 2023のprofile.xmlにstartup LISPが登録されていません"
    Assert-True (Test-Path -LiteralPath (Join-Path $aresCadpeta "2020\load_cadpeta.lsp")) "ARES 2020のローダLISPが配置されていません"
    Assert-True (Test-Path -LiteralPath (Join-Path $aresCadpeta "2023\load_cadpeta.lsp")) "ARES 2023のローダLISPが配置されていません"
    # 配置コピーは全年式で信頼場所に存在する
    foreach ($y in @("2020", "2023", "2024", "2027")) {
        Assert-True (Test-Path -LiteralPath (Join-Path $aresCadpeta "$y\CadPeta.Plugin.Ares.dll")) "ARES $y の配置コピーが信頼場所にありません"
    }
    Assert-True ($output -match "4 entries" -and $output -match "Next steps") "成功件数または次操作の案内がありません"

    (& $unregister -Product ares -NoPause -RegistryBaseOverride $aresBase @aresOv) 6>&1 | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $key2024)) "ARES 2024の登録を解除できませんでした"
    Assert-True (-not (Test-Path -LiteralPath $key2027)) "ARES 2027の登録を解除できませんでした"
    Assert-True (-not (Test-ProfileHasCadPeta $profile2020)) "ARES 2020のprofile.xmlからstartup LISPを除去できませんでした"
    Assert-True (-not (Test-ProfileHasCadPeta $profile2023)) "ARES 2023のprofile.xmlからstartup LISPを除去できませんでした"
    # 配置コピーも撤去され、CADPetaフォルダが畳まれること
    Assert-True (-not (Test-Path -LiteralPath $aresCadpeta)) "解除後も配置フォルダのコピーが残りました"

    # -AresYear 2024（レジストリ方式）: 2024だけレジストリ登録・配置。他年式は触らない
    (& $register -Product ares -AresYear 2024 -NoPause -RegistryBaseOverride $aresBase -RegistryMachineBaseOverride $aresMachineBase @aresOv) 6>&1 | Out-Null
    Assert-True (Test-Path -LiteralPath $key2024) "-AresYear 2024が2024へ登録しませんでした"
    Assert-True (-not (Test-Path -LiteralPath $key2027)) "-AresYear 2024が2027へも登録しました"
    Assert-True (-not (Test-ProfileHasCadPeta $profile2020)) "-AresYear 2024が2020のprofile.xmlも編集しました"
    Assert-True (Test-Path -LiteralPath (Join-Path $aresCadpeta "2024\CadPeta.Plugin.Ares.dll")) "-AresYear 2024の配置コピーがありません"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $aresCadpeta "2020"))) "-AresYear 2024が2020の配置も作りました"
    (& $unregister -Product ares -AresYear 2024 -NoPause -RegistryBaseOverride $aresBase @aresOv) 6>&1 | Out-Null

    # -AresYear 2020（LISPフック方式）: 2020のprofile.xmlへ登録・配置。レジストリキーは作らない
    (& $register -Product ares -AresYear 2020 -NoPause -RegistryBaseOverride $aresBase -RegistryMachineBaseOverride $aresMachineBase @aresOv) 6>&1 | Out-Null
    Assert-True (Test-ProfileHasCadPeta $profile2020) "-AresYear 2020が2020のprofile.xmlへ登録しませんでした"
    Assert-True (-not (Test-Path -LiteralPath $key2020)) "-AresYear 2020がレジストリキーを作りました（LISP方式のはず）"
    Assert-True (-not (Test-ProfileHasCadPeta $profile2023)) "-AresYear 2020が2023のprofile.xmlも編集しました"
    Assert-True ((-not (Test-Path -LiteralPath $key2024)) -and (-not (Test-Path -LiteralPath $key2027))) "-AresYear 2020が他年式へも登録しました"
    Assert-True (Test-Path -LiteralPath (Join-Path $aresCadpeta "2020\CadPeta.Plugin.Ares.dll")) "-AresYear 2020の配置コピーがありません"
    (& $unregister -Product ares -AresYear 2020 -NoPause -RegistryBaseOverride $aresBase @aresOv) 6>&1 | Out-Null
    Assert-True (-not (Test-ProfileHasCadPeta $profile2020)) "-AresYear 2020の解除でprofile.xmlから除去できませんでした"
    Assert-True (-not (Test-Path -LiteralPath $aresCadpeta)) "-AresYear 2020解除後も配置フォルダが残りました"

    # ARESは起動時にprofile.xmlのsetting属性順を並べ替える（type/value/name → name/value/type）。
    # 並べ替え後の形でも startup LISP 項目を確実に除去できることを検査する（実機で判明した回帰）
    New-TestProfile $profile2020
    $reordered = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($profile2020))
    $insAt = -1
    for ($i = 0; $i -lt $reordered.Count; $i++) { if ($reordered[$i] -eq '    </group>') { $insAt = $i; break } }
    $aresOrderItem = @(
        '        <group name="item2">'
        '            <setting name="description" value="" type="QString"/>'
        '            <setting name="file" value="X\CADPeta\2020\load_cadpeta.lsp" type="QString"/>'
        '            <setting name="managed" value="false" type="bool"/>'
        '            <setting name="name" value="load_cadpeta.lsp" type="QString"/>'
        '            <setting name="reasons" value="2" type="int"/>'
        '            <setting name="type" value="LISP" type="QString"/>'
        '        </group>'
    )
    $reordered.InsertRange($insAt, [string[]]$aresOrderItem)
    [IO.File]::WriteAllLines($profile2020, $reordered, (New-Object Text.UTF8Encoding($false)))
    Assert-True (Test-ProfileHasCadPeta $profile2020) "属性並べ替え形の項目を用意できませんでした"
    (& $unregister -Product ares -AresYear 2020 -NoPause -RegistryBaseOverride $aresBase @aresOv) 6>&1 | Out-Null
    Assert-True (-not (Test-ProfileHasCadPeta $profile2020)) "ARES並べ替え形（name/value/type）のstartup LISP項目を除去できませんでした"
    Assert-True (([IO.File]::ReadAllText($profile2020)) -match 'EXISTING\.LSP') "除去時に既存項目まで壊しました"

    # 標準入口の.bat（AutoCAD版と同名のinstall.bat / uninstall.bat、結果表示後に必ずpauseで待つ窓）を往復検査する。
    # pauseへは空行1つをstdinで与えて即応させる。ps1側の報告は.batの窓へそのまま流れる。
    $registerBat = Join-Path $work "install.bat"
    $unregisterBat = Join-Path $work "uninstall.bat"
    $batOutput = ("" | & $env:ComSpec /c $registerBat -Product ares -RegistryBaseOverride $aresBase -RegistryMachineBaseOverride $aresMachineBase -AresTrustedRootOverride $aresTrusted -AresProfileRootOverride $aresProfileRoot 2>&1) | Out-String
    Assert-True ($LASTEXITCODE -eq 0) "install.batが成功終了しませんでした（exit=$LASTEXITCODE）"
    Assert-True (Test-Path -LiteralPath $key2024) "install.bat経由でARES 2024へレジストリ登録されていません"
    Assert-True (Test-Path -LiteralPath $key2027) "install.bat経由でARES 2027へレジストリ登録されていません"
    Assert-True (Test-ProfileHasCadPeta $profile2020) "install.bat経由でARES 2020のprofile.xmlへ登録されていません"
    Assert-True (Test-ProfileHasCadPeta $profile2023) "install.bat経由でARES 2023のprofile.xmlへ登録されていません"
    Assert-True (Test-Path -LiteralPath (Join-Path $aresCadpeta "2020\CadPeta.Plugin.Ares.dll")) "install.bat経由で配置コピーが信頼場所にありません"
    Assert-True ($batOutput -match "4 entries" -and $batOutput -match "Press any key") "install.batがps1の報告とpause待ちを表示していません"
    $batOutput = ("" | & $env:ComSpec /c $unregisterBat -Product ares -RegistryBaseOverride $aresBase -AresTrustedRootOverride $aresTrusted -AresProfileRootOverride $aresProfileRoot 2>&1) | Out-String
    Assert-True ($LASTEXITCODE -eq 0) "uninstall.batが成功終了しませんでした（exit=$LASTEXITCODE）"
    Assert-True (-not (Test-Path -LiteralPath $key2024) -and -not (Test-Path -LiteralPath $key2027)) "uninstall.bat経由でレジストリ解除できませんでした"
    Assert-True ((-not (Test-ProfileHasCadPeta $profile2020)) -and (-not (Test-ProfileHasCadPeta $profile2023))) "uninstall.bat経由でprofile.xmlから除去できませんでした"
    Assert-True (-not (Test-Path -LiteralPath $aresCadpeta)) "uninstall.bat経由で配置フォルダのコピーが残りました"
    Assert-True ($batOutput -match "Removed" -and $batOutput -match "Press any key") "uninstall.batがps1の報告とpause待ちを表示していません"
    $missingBase = Join-Path $registryRoot "Missing"
    $batOutput = ("" | & $env:ComSpec /c $registerBat -Product ares -RegistryBaseOverride $missingBase -RegistryMachineBaseOverride $missingBase -AresTrustedRootOverride $aresTrusted -AresProfileRootOverride $aresProfileRoot 2>&1) | Out-String
    Assert-True ($LASTEXITCODE -eq 1) "install.batが失敗をexit 1で伝えませんでした（exit=$LASTEXITCODE）"
    Assert-True ($batOutput -match "Registration did not complete" -and $batOutput -match "Install did not complete" -and $batOutput -match "Press any key") "install.batが失敗理由の表示とpause待ちを行っていません"

    $bricscadOutput = (& $register -Product bricscad -NoPause -RegistryBaseOverride $bricscadBase -RegistryMachineBaseOverride $bricscadMachineBase) 6>&1 | Out-String
    $bricscad25 = Join-Path $bricscadBase "V25x64\ja_JP\Applications\CADPeta"
    $bricscad26 = Join-Path $bricscadBase "V26x64\ja_JP\Applications\CADPeta"
    Assert-True ((Test-Path -LiteralPath $bricscad25) -and (Test-Path -LiteralPath $bricscad26)) "BricsCADの全版へ登録されていません"
    Assert-True ($bricscadOutput -match "2 entries") "BricsCADの登録件数案内が不正です"
    (& $unregister -Product bricscad -NoPause -RegistryBaseOverride $bricscadBase) 6>&1 | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $bricscad25) -and -not (Test-Path -LiteralPath $bricscad26)) "BricsCADの全版を解除できませんでした"

    $ijcadOutput = (& $register -Product ijcad -NoPause -RegistryBaseOverride $ijcadBase -RegistryMachineBaseOverride $ijcadMachineBase) 6>&1 | Out-String
    $ijcad25 = Join-Path $ijcadBase "IJCAD Professional\R25\ja-JP\Applications\CADPeta"
    $ijcad26 = Join-Path $ijcadBase "IJCAD Professional\R26\ja-JP\Applications\CADPeta"
    Assert-True ((Test-Path -LiteralPath $ijcad25) -and (Test-Path -LiteralPath $ijcad26)) "IJCADの全版へ登録されていません"
    Assert-True ($ijcadOutput -match "2 entries") "IJCADの登録件数案内が不正です"
    (& $unregister -Product ijcad -NoPause -RegistryBaseOverride $ijcadBase) 6>&1 | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $ijcad25) -and -not (Test-Path -LiteralPath $ijcad26)) "IJCADの全版を解除できませんでした"

    Write-Host "OK: ARES、BricsCAD、IJCADの複数版登録、版とDLLの対応、全解除を確認しました。" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    $testRoot = "HKCU:\SOFTWARE\CADPeta.Tests"
    if ((Test-Path -LiteralPath $testRoot) -and -not (Get-ChildItem -LiteralPath $testRoot -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $testRoot -Force
    }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
