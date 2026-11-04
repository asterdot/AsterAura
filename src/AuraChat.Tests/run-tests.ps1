$ErrorActionPreference = 'Continue'
# 本脚本位于 <repo>\src\AuraChatTests\run-tests.ps1；repo = 上上级目录
$repo = (Get-Item $PSScriptRoot).Parent.Parent.FullName
$proj = Join-Path $repo 'src\AuraChat.Tests\AuraChat.Tests.csproj'
Write-Host "PROJ=$proj"
& 'C:\Program Files\dotnet\dotnet.exe' test $proj --logger:"console;verbosity=normal" 2>&1
exit $LASTEXITCODE
