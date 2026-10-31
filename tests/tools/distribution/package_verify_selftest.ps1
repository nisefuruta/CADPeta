# package_verify.ps1 の自己試験（-SelfTest）を、単体試験（ScriptSelfTests）から引数なしで実行するための入口。
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "package_verify.ps1") -SelfTest
