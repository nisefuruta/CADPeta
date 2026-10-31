# 互換 CAD の自動読み込みの登録を試験の間だけ外して戻す関数（compat_cad_com.ps1）の約束を、
# CAD のレジストリへ触れずに一時 HKCU キーで確かめる。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$evidenceLog = Join-Path $env:TEMP "compat_autoload_selftest_$PID.log"
$PollMilliseconds = 100
. "$PSScriptRoot\compat_cad_com.ps1"

$testRoot = "HKCU:\Software\CADPeta\Tests\CompatAutoload_$PID"
$key = Join-Path $testRoot 'Applications\CADPeta'
$backupDir = Join-Path $env:TEMP "compat_autoload_selftest_$PID"
$backup = Join-Path $backupDir 'Ares2027.reg'
$checked = New-Object System.Collections.Generic.List[string]

function New-TestRegistration {
  New-Item -Path $key -Force | Out-Null
  New-ItemProperty -Path $key -Name LOADER -Value 'C:\installed\CadPeta.Plugin.Ares.dll' -PropertyType String | Out-Null
  New-ItemProperty -Path $key -Name LOADCTRLS -Value 2 -PropertyType DWord | Out-Null
  New-ItemProperty -Path $key -Name MANAGED -Value 1 -PropertyType DWord | Out-Null
}

function Assert-TestRegistration([string]$label) {
  if (-not (Test-Path -LiteralPath $key)) { throw "${label}: 登録のキーが無い" }
  $item = Get-Item -LiteralPath $key
  $expected = @{ LOADER = @('C:\installed\CadPeta.Plugin.Ares.dll', 'String'); LOADCTRLS = @(2, 'DWord'); MANAGED = @(1, 'DWord') }
  if (@($item.GetValueNames()).Count -ne $expected.Count) { throw "${label}: 値の数が違う: $(@($item.GetValueNames()) -join ',')" }
  foreach ($name in $expected.Keys) {
    if ("$($item.GetValue($name))" -cne "$($expected[$name][0])") { throw "${label}: $name の値が違う: $($item.GetValue($name))" }
    if ("$($item.GetValueKind($name))" -cne $expected[$name][1]) { throw "${label}: $name の型が違う: $($item.GetValueKind($name))" }
  }
}

try {
  New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

  # 1. 登録があるとき: 外すとキーが無くなり、戻すと値と型が一致し、バックアップが消える
  New-TestRegistration
  $state = $null; Suspend-CompatAutoload $key $backup ([ref]$state)
  if (Test-Path -LiteralPath $key) { throw '1: 外した後も登録のキーが残っている' }
  if (-not (Test-Path -LiteralPath $backup)) { throw '1: バックアップが無い' }
  $failures = @()
  Resume-CompatAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "1: 戻す処理が失敗を返した: $($failures -join '; ')" }
  Assert-TestRegistration '1'
  if (Test-Path -LiteralPath $backup) { throw '1: 戻した後もバックアップが残っている' }
  # 同じ処理の 2 回目も同じ結果になる
  $state = $null; Suspend-CompatAutoload $key $backup ([ref]$state)
  Resume-CompatAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "1: 2 回目の戻す処理が失敗を返した: $($failures -join '; ')" }
  Assert-TestRegistration '1(2 回目)'
  $checked.Add('登録があるとき、外して戻すと値と型が一致し、バックアップが消える（2 回目も同じ）')

  # 2. 登録が無いとき: 何も変えず、バックアップも作らない
  Remove-Item -LiteralPath $key -Recurse -Force
  $state = $null; Suspend-CompatAutoload $key $backup ([ref]$state)
  if (Test-Path -LiteralPath $backup) { throw '2: 登録が無いのにバックアップを作った' }
  Resume-CompatAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "2: 戻す処理が失敗を返した: $($failures -join '; ')" }
  if (Test-Path -LiteralPath $key) { throw '2: 登録が無かったのにキーを作った' }
  $checked.Add('登録が無いとき、何も変えずバックアップも作らない')

  # 3. 前回のバックアップが残っているとき: 中止し、キーもバックアップも変えない
  New-TestRegistration
  [IO.File]::WriteAllText($backup, 'previous backup')
  $threw = $false
  $state = $null; try { Suspend-CompatAutoload $key $backup ([ref]$state) } catch { $threw = $true }
  if (-not $threw) { throw '3: 前回のバックアップがあるのに中止しなかった' }
  Assert-TestRegistration '3'
  if ([IO.File]::ReadAllText($backup) -cne 'previous backup') { throw '3: 前回のバックアップを上書きした' }
  Remove-Item -LiteralPath $backup -Force
  $checked.Add('前回のバックアップが残っているとき、中止してキーもバックアップも変えない')

  # 4. 戻すのに失敗したとき: 失敗の一覧に入れ、バックアップを残す（バックアップを壊して注入する）
  $state = $null; Suspend-CompatAutoload $key $backup ([ref]$state)
  $original = [IO.File]::ReadAllBytes($backup)
  [IO.File]::WriteAllText($backup, 'broken')
  $failures = @()
  Resume-CompatAutoload $state ([ref]$failures)
  if ($failures.Count -eq 0) { throw '4: 戻せなかったのに失敗を返さなかった' }
  if (-not (Test-Path -LiteralPath $backup)) { throw '4: 戻せなかったのにバックアップを消した' }
  $checked.Add('戻すのに失敗したとき、失敗の一覧に入れてバックアップを残す')

  # 5. 戻すだけの処理: バックアップから戻して一致を確かめ、バックアップを消す
  [IO.File]::WriteAllBytes($backup, $original)
  $errors = @(Restore-CompatAutoloadBackup $key $backup)
  if ($errors.Count -ne 0) { throw "5: 戻すだけの処理が失敗を返した: $($errors -join '; ')" }
  Assert-TestRegistration '5'
  if (Test-Path -LiteralPath $backup) { throw '5: 戻した後もバックアップが残っている' }
  $checked.Add('戻すだけの処理は、バックアップから戻して一致を確かめ、バックアップを消す')

  # ARES 2023 以前: profile.xml の applications のスタートアップに登録した load_cadpeta.lsp を外して戻す
  $profileDir = Join-Path $backupDir 'profiles\__unnamed profile__'
  New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
  $profileXml = Join-Path $profileDir 'profile.xml'
  $profileBackup = Join-Path $backupDir 'Ares2020.profile'
  $item = { param($n, $file) "        <group name=`"item$n`">`r`n            <setting name=`"file`" type=`"QString`" value=`"$file`"/>`r`n            <setting name=`"name`" type=`"QString`" value=`"$(Split-Path -Leaf $file)`"/>`r`n        </group>`r`n" }
  $originalText = "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`r`n<settings>`r`n    <group name=`"applications`">`r`n" +
    (& $item 1 '%FX_APPDATA_PATH%\Support\JDTOOLS.LSP') + (& $item 2 '%FX_HOME_PATH%\AppData\Roaming\ARES Standard Edition x64\CADPeta\2020\load_cadpeta.lsp') +
    "    </group>`r`n    <group name=`"options`">`r`n        <setting name=`"lastapploadfolder`" type=`"QString`" value=`"`"/>`r`n    </group>`r`n</settings>`r`n"
  $originalBytes = [Text.UTF8Encoding]::new($false).GetBytes($originalText)
  [IO.File]::WriteAllBytes($profileXml, $originalBytes)
  function Assert-ProfileOriginal([string]$label) {
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($profileXml)) -cne [Convert]::ToBase64String($originalBytes)) { throw "${label}: profile.xml が元のバイト列に戻っていない" }
  }

  # 6. 登録があるとき: 外すと load_cadpeta.lsp の項目だけが無くなり、ARES が実行中に profile.xml を書き換えても、戻すと元のバイト列になる
  $state = $null; Suspend-CompatProfileAutoload @($profileXml) $profileBackup ([ref]$state)
  $suspended = [IO.File]::ReadAllText($profileXml)
  if ($suspended -match 'load_cadpeta\.lsp') { throw '6: 外した後も load_cadpeta.lsp の登録が残っている' }
  if ($suspended -notmatch 'JDTOOLS\.LSP') { throw '6: ほかのスタートアップの項目まで外した' }
  if (-not (Test-Path -LiteralPath $profileBackup)) { throw '6: バックアップが無い' }
  [IO.File]::WriteAllText($profileXml, $suspended.Replace('value=""/>', 'value="C:\changed-by-ares\"/>'))
  $failures = @()
  Resume-CompatProfileAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "6: 戻す処理が失敗を返した: $($failures -join '; ')" }
  Assert-ProfileOriginal '6'
  if (Test-Path -LiteralPath $profileBackup) { throw '6: 戻した後もバックアップが残っている' }
  $checked.Add('profile.xml の load_cadpeta.lsp の登録だけを外し、CAD が実行中に書き換えても、戻すと元のバイト列になってバックアップが消える')

  # 7. 登録が無い profile.xml（ARES 2024 以降）: 中身は変えずにバックアップし、CAD が実行中に書き換えた値
  #    （最後に読み込んだ DLL のフォルダ lastapploadfolder など）も、戻すと元のバイト列になる
  $noEntry = Join-Path $backupDir 'profiles\other\profile.xml'
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $noEntry) | Out-Null
  $noEntryText = "<settings>`r`n    <group name=`"applications`">`r`n" + (& $item 1 'JDTOOLS.LSP') + "    </group>`r`n    <setting name=`"lastapploadfolder`" type=`"QString`" value=`"`"/>`r`n</settings>`r`n"
  $noEntryBytes = [Text.UTF8Encoding]::new($false).GetBytes($noEntryText)
  [IO.File]::WriteAllBytes($noEntry, $noEntryBytes)
  $state = $null; Suspend-CompatProfileAutoload @($noEntry) $profileBackup ([ref]$state)
  if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($noEntry)) -cne [Convert]::ToBase64String($noEntryBytes)) { throw '7: 登録の無い profile.xml の中身を変えた' }
  [IO.File]::WriteAllText($noEntry, $noEntryText.Replace('value=""/>', 'value="C:\CADPeta-e2e-deleted\"/>'))
  $failures = @(); Resume-CompatProfileAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "7: 戻す処理が失敗を返した: $($failures -join '; ')" }
  if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($noEntry)) -cne [Convert]::ToBase64String($noEntryBytes)) { throw '7: CAD が書き換えた profile.xml を元のバイト列へ戻さなかった' }
  if (Test-Path -LiteralPath $profileBackup) { throw '7: 戻した後もバックアップが残っている' }
  $checked.Add('load_cadpeta.lsp の登録が無い profile.xml は中身を変えず、CAD が実行中に書き換えても元のバイト列へ戻す')

  # 7b. profile.xml が 1 つも無い（ARES 2024 以降の製品）: 何もせず、バックアップも作らない
  foreach ($none in @(@(), $null)) {
    $state = $null; Suspend-CompatProfileAutoload $none $profileBackup ([ref]$state)
    if (Test-Path -LiteralPath $profileBackup) { throw '7b: profile.xml が無いのにバックアップを作った' }
    $failures = @(); Resume-CompatProfileAutoload $state ([ref]$failures)
    if ($failures.Count -ne 0) { throw "7b: 戻す処理が失敗を返した: $($failures -join '; ')" }
  }
  $checked.Add('profile.xml が 1 つも無い製品では、何もせずバックアップも作らない')

  # 8. 前回のバックアップが残っているとき: 中止し、profile.xml もバックアップも変えない
  New-Item -ItemType Directory -Force -Path $profileBackup | Out-Null
  [IO.File]::WriteAllText((Join-Path $profileBackup 'previous'), 'previous backup')
  $threw = $false
  $state = $null; try { Suspend-CompatProfileAutoload @($profileXml) $profileBackup ([ref]$state) } catch { $threw = $true }
  if (-not $threw) { throw '8: 前回のバックアップがあるのに中止しなかった' }
  Assert-ProfileOriginal '8'
  if ([IO.File]::ReadAllText((Join-Path $profileBackup 'previous')) -cne 'previous backup') { throw '8: 前回のバックアップを変えた' }
  Remove-Item -LiteralPath $profileBackup -Recurse -Force
  $checked.Add('profile.xml の前回のバックアップが残っているとき、中止して profile.xml もバックアップも変えない')

  # 9. 戻すのに失敗したとき: 失敗の一覧に入れ、バックアップを残す（バックアップを壊して注入する）
  $state = $null; Suspend-CompatProfileAutoload @($profileXml) $profileBackup ([ref]$state)
  $saved = @(Get-ChildItem -LiteralPath $profileBackup -File -Filter '*.xml')
  if ($saved.Count -ne 1) { throw "9: バックアップの profile.xml が 1 つでない: $($saved.Count)" }
  $savedBytes = [IO.File]::ReadAllBytes($saved[0].FullName)
  [IO.File]::WriteAllText($saved[0].FullName, 'broken')
  $failures = @(); Resume-CompatProfileAutoload $state ([ref]$failures)
  if ($failures.Count -eq 0) { throw '9: 戻せなかったのに失敗を返さなかった' }
  if (-not (Test-Path -LiteralPath $profileBackup)) { throw '9: 戻せなかったのにバックアップを消した' }
  $checked.Add('profile.xml を戻すのに失敗したとき、失敗の一覧に入れてバックアップを残す')

  # 10. 戻すだけの処理: バックアップから元の場所へ戻して一致を確かめ、バックアップを消す
  [IO.File]::WriteAllBytes($saved[0].FullName, $savedBytes)
  $errors = @(Restore-CompatProfileBackup $profileBackup)
  if ($errors.Count -ne 0) { throw "10: 戻すだけの処理が失敗を返した: $($errors -join '; ')" }
  Assert-ProfileOriginal '10'
  if (Test-Path -LiteralPath $profileBackup) { throw '10: 戻した後もバックアップが残っている' }
  $checked.Add('profile.xml の戻すだけの処理は、バックアップから元の場所へ戻して一致を確かめ、バックアップを消す')

  # 11. CAD が終了していないとき: 戻さずにバックアップを残し、失敗を返す（CAD の終了時にメモリ上の設定で上書きされるため）。
  #     CAD が終了した後なら戻せる。動いている CAD の代わりに、この自己試験を実行している powershell.exe を指定する
  $state = $null; Suspend-CompatProfileAutoload @($profileXml) $profileBackup ([ref]$state)
  $product = @{ Registry = $null; Profile = $state }
  $failures = @()
  Resume-CompatProductAutoload $product (Get-Process -Id $PID).Path ([ref]$failures)
  if (-not @($failures | Where-Object { $_ -match '終了していない' }).Count) { throw "11: CAD が動いているのに失敗を返さなかった: $($failures -join '; ')" }
  if ([IO.File]::ReadAllText($profileXml) -match 'load_cadpeta\.lsp') { throw '11: CAD が動いているのに profile.xml を戻した' }
  if (-not (Test-Path -LiteralPath $profileBackup)) { throw '11: CAD が動いているのにバックアップを消した' }
  $failures = @()
  Resume-CompatProductAutoload $product (Join-Path $backupDir 'not-running\ARESS.exe') ([ref]$failures)
  if ($failures.Count -ne 0) { throw "11: CAD の終了後に戻せなかった: $($failures -join '; ')" }
  Assert-ProfileOriginal '11'
  $checked.Add('CAD が終了していないときは profile.xml を戻さずにバックアップを残して失敗を返し、終了後に戻せる')

  # 12. インストール先（register.ps1 が ARES の信頼するフォルダへ置く配置フォルダなど）: 戻さず、試験の前後でファイルの名前と内容の
  #     SHA256 が同じことだけを確かめる。更新時刻だけの違いは失敗にせず、内容が変われば失敗の一覧に入れる
  $install = Join-Path $backupDir 'install\CADPeta'
  $installDll = Join-Path $install '2024\CadPeta.Plugin.Ares.dll'
  New-Item -ItemType Directory -Force (Split-Path -Parent $installDll) | Out-Null
  [IO.File]::WriteAllText($installDll, 'installed-a')
  $notRunning = Join-Path $backupDir 'not-running\ARESS.exe'
  $product = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install)
  (Get-Item -LiteralPath $installDll).LastWriteTime = (Get-Date).AddDays(-3)
  $failures = @(); Resume-CompatProductAutoload $product $notRunning ([ref]$failures)
  if ($failures.Count -ne 0) { throw "12: 更新時刻が変わっただけで失敗にした: $($failures -join '; ')" }
  $product = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install)
  [IO.File]::WriteAllText($installDll, 'installed-b')
  $failures = @(); Resume-CompatProductAutoload $product $notRunning ([ref]$failures)
  if (-not @($failures | Where-Object { $_ -like "*$installDll*" }).Count) { throw "12: インストール先の内容が変わったのに失敗を返さなかった: $($failures -join '; ')" }
  $checked.Add('インストール先はファイルの名前と内容の SHA256 で前後を比べ、更新時刻だけの違いは失敗にしない')

  # 13. 読めないファイル: 前の記録で読めなければ、登録を外す前に止まる。後の記録で読めなければ、照合の失敗にする
  New-TestRegistration
  $stream = [IO.FileStream]::new($installDll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
  try {
    $threw = $false
    $product = $null
    try { Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install) } catch { $threw = $true }
  }
  finally { $stream.Dispose() }
  if (-not $threw) { throw '13: 前の記録で読めないファイルがあるのに止まらなかった' }
  Assert-TestRegistration '13'
  $product = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install)
  $stream = [IO.FileStream]::new($installDll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
  try { $failures = @(); Resume-CompatProductAutoload $product $notRunning ([ref]$failures) }
  finally { $stream.Dispose() }
  if (-not @($failures | Where-Object { $_ -match '読めない' }).Count) { throw "13: 後の記録で読めないファイルがあるのに失敗を返さなかった: $($failures -join '; ')" }
  $checked.Add('読めないファイルは、前の記録なら何も変えずに止まり、後の記録なら照合の失敗にする')

  # 14. 登録を外さない実行(インストール済みの CADPeta を試験する。peta_option_smoke_compat.ps1 の -SkipPluginLoad)でも、
  #     インストール先を記録して比べる(登録の無い製品で確かめる。実際の製品の登録には触れない)
  [IO.File]::WriteAllText($installDll, 'installed-a')
  $product = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install) -KeepAutoload
  [IO.File]::WriteAllText($installDll, 'installed-c')
  $failures = @(); Resume-CompatProductAutoload $product $notRunning ([ref]$failures)
  if (-not @($failures | Where-Object { $_ -like "*$installDll*" }).Count) { throw "14: 登録を外さない実行で、インストール先の変化を失敗にしなかった: $($failures -join '; ')" }
  $checked.Add('登録を外さない実行でも、インストール先を記録して比べる')


  # 15. 互換 CAD の E2E は、Suspend-CompatProductAutoload(インストール先の前の記録)を条件付きで飛ばさない
  $e2e = @('..\regression\peta_option_smoke_compat.ps1', '..\regression\peta_window_rotation_compat.ps1',
    '..\regression\window_contract\window_contract_compat.ps1', '..\pdf-emf\gradient_compat_e2e.ps1') | ForEach-Object { Join-Path $PSScriptRoot $_ }
  foreach ($script in $e2e) {
    $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
    $calls = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Suspend-CompatProductAutoload' }, $true))
    if ($calls.Count -ne 1) { throw "15: Suspend-CompatProductAutoload の呼び出しが 1 つでない: $script" }
    for ($parent = $calls[0].Parent; $null -ne $parent; $parent = $parent.Parent) {
      if ($parent -is [Management.Automation.Language.IfStatementAst]) { throw "15: Suspend-CompatProductAutoload を if の中で呼んでいる(インストール先を記録しない実行ができる): $script" }
    }
  }
  $checked.Add('互換 CAD の E2E は、インストール先の前の記録を条件付きで飛ばさない')
  # 16. COM の試験で AutoCAD を使うとき(peta_window_rotation_compat.ps1 の Acad2026・Acad2027): DLL のフォルダを信頼済みのフォルダの
  #     直下へコピーし、削除するときはコピーしたものだけを消して元からあるファイルを残す。同じ名前のファイルがあれば何もコピーしない
  $savedTrusted = $CadPetaTrustedDotnetFolder
  $CadPetaTrustedDotnetFolder = Join-Path $backupDir 'trusted-dotnet'
  try {
    $build = Join-Path $backupDir 'build'
    New-Item -ItemType Directory -Force (Join-Path $build 'lang'), $CadPetaTrustedDotnetFolder | Out-Null
    [IO.File]::WriteAllText((Join-Path $build 'CadPeta.Plugin.Acad.dll'), 'plugin')
    [IO.File]::WriteAllText((Join-Path $build 'lang\1041.lang'), 'ja')
    # 深いフォルダの DLL(コピーで作る中間のフォルダ runtimes、win、lib も、削除するときに残さない)
    New-Item -ItemType Directory -Force (Join-Path $build 'runtimes\win\lib\net8.0') | Out-Null
    [IO.File]::WriteAllText((Join-Path $build 'runtimes\win\lib\net8.0\Microsoft.Win32.SystemEvents.dll'), 'events')
    [IO.File]::WriteAllText((Join-Path $CadPetaTrustedDotnetFolder 'Foreign.dll'), 'foreign')
    $stage = New-CompatPluginStage 'Acad2026' (Join-Path $build 'CadPeta.Plugin.Acad.dll')
    if ($stage.Plugin -ne (Join-Path $CadPetaTrustedDotnetFolder 'CadPeta.Plugin.Acad.dll') -or -not (Test-Path -LiteralPath (Join-Path $CadPetaTrustedDotnetFolder 'lang\1041.lang'))) {
      throw "16: 信頼済みのフォルダの直下へコピーしていない: $($stage.Plugin)"
    }
    $failures = @(); Remove-CompatPluginStage $stage ([ref]$failures)
    if ($failures.Count) { throw "16: コピーを削除できなかった: $($failures -join '; ')" }
    $left = @(Get-ChildItem -LiteralPath $CadPetaTrustedDotnetFolder -Recurse -Force | ForEach-Object { $_.Name })
    if (($left -join ',') -ne 'Foreign.dll' -or [IO.File]::ReadAllText((Join-Path $CadPetaTrustedDotnetFolder 'Foreign.dll')) -ne 'foreign') { throw "16: コピーしたものだけを削除していない: $($left -join ',')" }
    [IO.File]::WriteAllText((Join-Path $CadPetaTrustedDotnetFolder 'CadPeta.Plugin.Acad.dll'), 'someone else')
    $threw = $false
    try { [void](New-CompatPluginStage 'Acad2026' (Join-Path $build 'CadPeta.Plugin.Acad.dll')) } catch { $threw = $true }
    if (-not $threw -or (Test-Path -LiteralPath (Join-Path $CadPetaTrustedDotnetFolder 'lang'))) { throw '16: 同じ名前のファイルがあるのに止まらなかったか、ほかのファイルをコピーした' }
    if ([IO.File]::ReadAllText((Join-Path $CadPetaTrustedDotnetFolder 'CadPeta.Plugin.Acad.dll')) -ne 'someone else') { throw '16: 同じ名前の元からあるファイルを上書きした' }
  }
  finally { $CadPetaTrustedDotnetFolder = $savedTrusted }
  $checked.Add('COM の試験の AutoCAD は、信頼済みのフォルダの直下へコピーし、コピーしたものだけを削除し、同じ名前のファイルがあれば何もコピーしない')

  # 16b. ARES の信頼するフォルダ(%AppData%\ARES Standard Edition x64)の下へのコピーが途中で失敗したとき(プラグインの DLL が無い):
  #      例外で止まり、作った実行ごとのフォルダを残さない(呼び出し側は戻り値を受け取れないので、後始末できない)
  $savedAppData = $env:APPDATA
  $env:APPDATA = Join-Path $backupDir 'appdata'
  try {
    $aresRoot = Join-Path $env:APPDATA 'ARES Standard Edition x64'
    $aresBuild = Join-Path $backupDir 'ares-build'
    New-Item -ItemType Directory -Force $aresRoot, $aresBuild | Out-Null
    [IO.File]::WriteAllText((Join-Path $aresBuild 'CadPeta.Core.dll'), 'core')
    $threw = $false
    try { [void](New-CompatPluginStage 'Ares2027' (Join-Path $aresBuild 'CadPeta.Plugin.Ares.dll')) } catch { $threw = $true }
    if (-not $threw) { throw '16b: プラグインの DLL をコピーできなかったのに止まらなかった' }
    $leftStages = @(Get-ChildItem -LiteralPath $aresRoot -Directory -Filter 'CADPeta-e2e-*')
    if ($leftStages.Count) { throw "16b: コピーの途中で失敗したのに、作ったフォルダが残った: $($leftStages.Name -join ', ')" }
  }
  finally { $env:APPDATA = $savedAppData }
  $checked.Add('ARES の信頼するフォルダへのコピーが途中で失敗したら、作ったフォルダを残さずに止まる')

  # 16c. COM の試験で AutoCAD を使うとき、インストール済みの bundle があれば、プロファイルの記録も含めて何も変えずに止まる
  $installedBundle = Join-Path $backupDir 'Autodesk\ApplicationPlugins\CADPeta.bundle'
  New-Item -ItemType Directory -Force $installedBundle | Out-Null
  $threw = $false
  try { $acadState = $null; Suspend-CompatProductAutoload 'Acad2026' ([ref]$acadState) -InstallRoots @($installedBundle) } catch { $threw = $true }
  if (-not $threw) { throw '16c: インストール済みの bundle があるのに止まらなかった' }
  if ($null -ne $acadState) { throw '16c: 止まったのに、プロファイルの記録を取った' }
  Assert-RecordEnvironmentUnchanged '16c'
  $checked.Add('COM の試験の AutoCAD は、インストール済みの bundle があれば何も変えずに止まる')

  # 17. 実行前の記録は、環境を変える前の 1 か所で取る。登録を外した後(戻す前)に、もう一度実行前の記録を取ろうとしたら止め、戻した後は取れる
  $product = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install)
  $threw = $false
  try { $second = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$second) -InstallRoots @($install) } catch { $threw = $true }
  if (-not $threw) { throw '17: 環境を変えた後(戻す前)に、もう一度実行前の記録を取れてしまった' }
  $failures = @(); Resume-CompatProductAutoload $product $notRunning ([ref]$failures)
  if ($failures.Count) { throw "17: 戻せなかった: $($failures -join '; ')" }
  $product = $null; Suspend-CompatProductAutoload 'Ijcad' ([ref]$product) -InstallRoots @($install)
  $failures = @(); Resume-CompatProductAutoload $product $notRunning ([ref]$failures)
  $checked.Add('環境を変えた後(戻す前)に実行前の記録を取ろうとしたら止め、戻した後は取れる')

  # 18. profile.xml のバックアップを作る途中で失敗したとき(2 つ目の profile.xml を読めない): profile.xml を変えずに止まり、
  #     作りかけのバックアップを残さない(残すと次の実行が止まり、paths.txt の無いバックアップは戻すだけの処理でも戻せない)
  $missingXml = Join-Path $backupDir 'profiles\missing\profile.xml'
  $threw = $false
  $state = $null; try { Suspend-CompatProfileAutoload @($profileXml, $missingXml) $profileBackup ([ref]$state) } catch { $threw = $true }
  if (-not $threw) { throw '18: 読めない profile.xml があるのに止まらなかった' }
  Assert-ProfileOriginal '18'
  if (Test-Path -LiteralPath $profileBackup) { throw '18: 作りかけのバックアップを残した' }
  $state = $null; Suspend-CompatProfileAutoload @($profileXml) $profileBackup ([ref]$state)
  $failures = @(); Resume-CompatProfileAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "18: 失敗の後の実行で戻せなかった: $($failures -join '; ')" }
  Assert-ProfileOriginal '18(次の実行)'
  $checked.Add('profile.xml のバックアップを作る途中で失敗したときは、profile.xml を変えずに止まり、作りかけのバックアップを残さない')

  # 19. 登録の書き出しが途中で失敗したとき(reg export が書きかけのファイルを残して 0 以外を返す): 登録を変えずに止まり、
  #     書きかけのバックアップを残さない。reg export の失敗は、Invoke-CompatRegExe を置き換えて起こす
  $realRegExe = ${function:Invoke-CompatRegExe}
  Set-Item -Path function:Invoke-CompatRegExe -Value {
    param([string]$arguments)
    if ($arguments -match '^export "[^"]+" "([^"]+)"') { [IO.File]::WriteAllText($Matches[1], 'partial'); return 1 }
    return (& $realRegExe $arguments)
  }.GetNewClosure()
  try {
    $threw = $false
    $state = $null; try { Suspend-CompatAutoload $key $backup ([ref]$state) } catch { $threw = $true }
  }
  finally { Set-Item -Path function:Invoke-CompatRegExe -Value $realRegExe }
  if (-not $threw) { throw '19: reg export が失敗したのに止まらなかった' }
  Assert-TestRegistration '19'
  if (Test-Path -LiteralPath $backup) { throw '19: 書きかけのバックアップを残した' }
  $state = $null; Suspend-CompatAutoload $key $backup ([ref]$state)
  $failures = @(); Resume-CompatAutoload $state ([ref]$failures)
  if ($failures.Count -ne 0) { throw "19: 失敗の後の実行で戻せなかった: $($failures -join '; ')" }
  Assert-TestRegistration '19(次の実行)'
  $checked.Add('登録の書き出しが途中で失敗したときは、登録を変えずに止まり、書きかけのバックアップを残さない')

  # 20. 戻すだけの処理(compat_autoload_restore.ps1)も、CAD が終了していなければ登録も profile.xml も戻さずに、バックアップを残して失敗を返す。
  #     CAD が終了した後なら戻せる。動いている CAD の代わりに、この自己試験を実行している powershell.exe を指定する
  $registry = $null; Suspend-CompatAutoload $key $backup ([ref]$registry)
  $state = $null; Suspend-CompatProfileAutoload @($profileXml) $profileBackup ([ref]$state)
  $errors = @(Restore-CompatProductBackups $key $backup $profileBackup (Get-Process -Id $PID).Path)
  if (-not @($errors | Where-Object { $_ -match '終了していない' }).Count) { throw "20: CAD が動いているのに失敗を返さなかった: $($errors -join '; ')" }
  if (Test-Path -LiteralPath $key) { throw '20: CAD が動いているのに登録を戻した' }
  if ([IO.File]::ReadAllText($profileXml) -match 'load_cadpeta\.lsp') { throw '20: CAD が動いているのに profile.xml を戻した' }
  if (-not (Test-Path -LiteralPath $backup) -or -not (Test-Path -LiteralPath $profileBackup)) { throw '20: CAD が動いているのにバックアップを消した' }
  $errors = @(Restore-CompatProductBackups $key $backup $profileBackup $notRunning)
  if ($errors.Count -ne 0) { throw "20: CAD の終了後に戻せなかった: $($errors -join '; ')" }
  Assert-TestRegistration '20'
  Assert-ProfileOriginal '20'
  if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $profileBackup)) { throw '20: 戻した後もバックアップが残っている' }
  # compat_autoload_restore.ps1 は Restore-CompatProductBackups だけで戻す（CAD の終了を確かめずに戻す関数を直接呼ばない）
  $restoreAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'compat_autoload_restore.ps1'), [ref]$null, [ref]$null)
  $restoreCalls = @($restoreAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
  if (-not ($restoreCalls -contains 'Restore-CompatProductBackups')) { throw '20: compat_autoload_restore.ps1 が Restore-CompatProductBackups で戻していない' }
  foreach ($direct in 'Restore-CompatAutoloadBackup', 'Restore-CompatProfileBackup') {
    if ($restoreCalls -contains $direct) { throw "20: compat_autoload_restore.ps1 が CAD の終了を確かめずに $direct を呼んでいる" }
  }
  $checked.Add('戻すだけの処理も、CAD が終了していなければ登録も profile.xml も戻さずにバックアップを残して失敗を返し、終了後に戻せる')

  "compat_autoload_selftest OK（確かめた約束 $($checked.Count) 件）"
  $checked | ForEach-Object { "  - $_" }
}
finally {
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
  if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
  if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
}
