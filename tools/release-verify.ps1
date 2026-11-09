# Aster Aura 可重复发布验收脚本
# 任务: t4 / release-client（2026-11-07）。用途: 在隔离目录对任意仓库副本执行
# "身份采集 → 全量构建 → 测试 → 发布 → 哈希清单 → 静态资源新鲜度 → 冒烟运行"。
#
# 纪律:
#   - 本脚本对仓库只读（git 采集与构建读源码），全部输出写 -OutDir；
#   - -OutDir 严禁指向仓库内部或用户主目录（脚本内自检）；
#   - 冒烟运行使用隔离 AURACHAT_HOME，绝不触碰 %APPDATA%\AuraChat；
#   - 兼容 Windows PowerShell 5.1（不使用 pwsh 7 专属特性）。
#
# 用法示例（在仓库根之外）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-verify.ps1 -OutDir D:\aura-rel-1 `
#     [-XunitConsoleDll <xunit.console.dll 路径>] [-SkipSmoke] [-SkipBuild] [-SkipPublish]
#
# 退出码: 0 = 全部执行且无 FAIL/BLOCKED；2 = 有步骤 FAIL；3 = 无 FAIL 但有 BLOCKED
#         （环境阻塞，例如 testhost 无法启动）。BLOCKED 绝不能当作发布成功（详见 $OutDir\summary.md）。
#         7 = PUBLICATION_DENIED：OutDir/发布树/manifest 越界或路径链不确定，发生在任何写入之前。

#requires -Version 5.1
param(
  [string]$RepoRoot = '',
  [Parameter(Mandatory = $true)][string]$OutDir,
  [string]$Configuration = 'Release',
  [string]$RuntimeId = 'win-x64',
  [int]$SmokePort = 24334,
  [string]$XunitConsoleDll = '',
  [switch]$SkipBuild,
  [switch]$SkipPublish,
  [switch]$SkipSmoke
)

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$ErrorActionPreference = 'Continue'
$repo = [System.IO.Path]::GetFullPath($RepoRoot)

# --- 发布边界闸（合同 t2）：在任何写入/清理/构建之前，对 -OutDir 做真实路径校验 ---
# 旧实现是 lexical StartsWith：同名前缀拼法（8.3 短名、junction 别名）能绕过，父目录缺失/
# reparse 链不确定时照样放行。现在统一走 _publication-guard：realpath 链 + reparse 拒绝 +
# 带边界分隔符的受保护根包含比较 + 父链必须完整；拒绝固定输出 PUBLICATION_DENIED 且非零退出。
. (Join-Path $PSScriptRoot '_publication-guard.ps1')
$pubProtected = @( $repo ) + @( Get-PubProtectedRoots )
$out = Assert-PublicationPath -Path $OutDir -ProtectedRoots $pubProtected -RequireExistingParent -What 'OutDir'

if (Test-Path -LiteralPath (Join-Path $repo '.git') -PathType Container) { $isGit = $true } else { $isGit = $false }

New-Item -ItemType Directory -Path $out, (Join-Path $out 'logs') -Force | Out-Null
$steps = @()
function Add-Step([string]$name, [string]$status, [string]$note) {
  $script:steps += [pscustomobject]@{ step = $name; status = $status; note = $note }
  Write-Host "[$name] $status :: $note"
}

Write-Host "== release-verify =="
Write-Host "repo: $repo"
Write-Host "out : $out"

# --- 1. 源码身份 ---
$identity = Join-Path $out 'identity.txt'
$head = ''; $statusTxt = ''; $diffHash = ''; $statusHash = ''
if ($isGit) {
  $head = (& git -C $repo rev-parse HEAD) 2>$null
  $statusTxt = (& git -C $repo status --porcelain) 2>$null | Out-String
  $diffHash = (& git -C $repo diff HEAD) 2>$null | Out-String | ForEach-Object {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $b = [Text.Encoding]::UTF8.GetBytes($_); ($sha.ComputeHash($b) | ForEach-Object { $_.ToString('x2') }) -join ''
  }
  $statusHash = $statusTxt | ForEach-Object {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $b = [Text.Encoding]::UTF8.GetBytes($_); ($sha.ComputeHash($b) | ForEach-Object { $_.ToString('x2') }) -join ''
  }
}
$sdk = (& dotnet --version) 2>$null
(
  "HEAD=$head",
  "statusHash(SHA256 of `git status --porcelain` output)=$statusHash",
  "diffHash(SHA256 of `git diff HEAD` output)=$diffHash",
  "dotnetSDK=$sdk",
  "--- git status --porcelain ---",
  $statusTxt.TrimEnd()
) | Set-Content -LiteralPath $identity -Encoding UTF8
Add-Step 'identity' 'OK' "HEAD=$head SDK=$sdk (detail: identity.txt)"

# --- 2. 全量构建 ---
$buildLog = Join-Path $out 'logs\build.log'
$buildOk = $false
if (-not $SkipBuild) {
  Push-Location $repo
  try {
    & dotnet build AuraChat.sln -c $Configuration --no-incremental --nologo *> $buildLog
    $code = $LASTEXITCODE
    $errs = (Select-String -Path $buildLog -Pattern ': error ' | Measure-Object).Count
    $warns = (Select-String -Path $buildLog -Pattern ': warning ' | Measure-Object).Count
    if ($code -eq 0 -and $errs -eq 0) { $buildOk = $true; Add-Step 'build' 'OK' "exit=$code errors=0 warnings=$warns" }
    else { Add-Step 'build' 'FAIL' "exit=$code errors=$errs warnings=$warns (logs\build.log)" }
  } finally { Pop-Location }
}

# --- 3. 测试（首选进程内 TestRunner；回退 dotnet test / xunit.console）---
$testLog = Join-Path $out 'logs\test-dotnet.log'
$testOk = $false; $testSummary = ''; $testVerdict = ''
if ($buildOk -or $SkipBuild) {
  $testsBin = Join-Path $repo "src\AuraChat.Tests\bin\$Configuration\net9.0"

  # 3a. 进程内 TestRunner（绕开 testhost Win32Exception(5) 沙箱限制的既定入口, 见 src/TestRunner）
  $runnerProj = Join-Path $repo 'src\TestRunner\TestRunner.csproj'
  $runnerDll = Join-Path $repo "src\TestRunner\bin\$Configuration\net9.0\TestRunner.dll"
  if ((Test-Path -LiteralPath $runnerProj) -and (Test-Path -LiteralPath (Join-Path $testsBin 'AuraChat.Tests.dll'))) {
    $trBuildLog = Join-Path $out 'logs\build-testrunner.log'
    $trBuildCode = 1
    Push-Location $repo
    try {
      & dotnet build "src\TestRunner\TestRunner.csproj" -c $Configuration --no-incremental --nologo *> $trBuildLog
      $trBuildCode = $LASTEXITCODE
    } finally { Pop-Location }
    if ($trBuildCode -eq 0 -and (Test-Path -LiteralPath $runnerDll)) {
      $trLog = Join-Path $out 'logs\test-testrunner.log'
      & dotnet exec $runnerDll (Join-Path $testsBin 'AuraChat.Tests.dll') *> $trLog
      $trCode = $LASTEXITCODE
      $raw = ''
      if (Test-Path -LiteralPath $trLog) { $raw = Get-Content $trLog -Raw }
      $m = [regex]::Match("$raw", '== 摘要：通过 (\d+)，失败 (\d+)，跳过 (\d+)')
      if ($m.Success) {
        $testSummary = "total=$([int]$m.Groups[1].Value + [int]$m.Groups[2].Value + [int]$m.Groups[3].Value) passed=$($m.Groups[1].Value) failed=$($m.Groups[2].Value) skipped=$($m.Groups[3].Value)"
        if ($trCode -eq 0 -and [int]$m.Groups[2].Value -eq 0) {
          $testOk = $true; $testVerdict = 'OK'; Add-Step 'test-dotnet' 'OK' "TestRunner: $testSummary"
        } else {
          $testVerdict = 'FAIL'; Add-Step 'test-dotnet' 'FAIL' "TestRunner exit=${trCode}: $testSummary (logs\test-testrunner.log)"
        }
      } elseif ($trCode -eq 0) {
        $testOk = $true; $testVerdict = 'OK'; Add-Step 'test-dotnet' 'OK' "TestRunner exit=0 无摘要行 (logs\test-testrunner.log)"
      } else {
        $testVerdict = 'BLOCKED'; Add-Step 'test-dotnet' 'BLOCKED' "TestRunner exit=$trCode 且无摘要 (logs\test-testrunner.log)"
      }
    } else {
      $testVerdict = 'BLOCKED'; Add-Step 'build-testrunner' 'BLOCKED' "TestRunner 构建 exit=$trBuildCode (logs\build-testrunner.log)，回退其他运行器"
    }
  }

  # 3b. 回退: dotnet test（vstest; 摘要与退出码都通过才算 OK）
  if ($testVerdict -in @('', 'BLOCKED')) {
    Push-Location $repo
    try {
      & dotnet test "src\AuraChat.Tests\AuraChat.Tests.csproj" -c $Configuration --no-build --nologo *> $testLog
      $code = $LASTEXITCODE
      $raw = Get-Content $testLog -Raw
      if ($code -eq 0 -and $raw -match '通过!|Passed!' -and $raw -notmatch '失败!\s|Failed!\s|失败:\s*[1-9]|Failed:\s*[1-9]') {
        $testSummary = ($raw -split "`n" | Select-String '通过|Failed|Passed' | Select-Object -Last 3) -join '; '
        $testOk = $true; $testVerdict = 'OK'; Add-Step 'test-dotnet' 'OK' $testSummary
      } elseif ($code -ne 0 -and $raw -match 'Win32Exception|已中止|aborted') {
        $testVerdict = 'BLOCKED'; Add-Step 'test-dotnet' 'BLOCKED' "testhost 启动即中止(Win32Exception(5) 沙箱限制) exit=$code, 见 logs\test-dotnet.log"
      } else {
        $testVerdict = 'FAIL'; Add-Step 'test-dotnet' 'FAIL' "exit=$code (logs\test-dotnet.log)"
      }
    } finally { Pop-Location }
  }

  # 3c. 替代运行器: xunit.console（vstest 被沙箱阻塞时的既定替代路径）
  if ($testVerdict -in @('', 'BLOCKED') -and $XunitConsoleDll -and (Test-Path -LiteralPath $XunitConsoleDll)) {
    $testBin = Join-Path $repo "src\AuraChat.Tests\bin\$Configuration\net9.0"
    if (Test-Path -LiteralPath (Join-Path $testBin 'AuraChat.Tests.dll')) {
      $xmlOut = Join-Path $out 'test-results-xunit.xml'
      Push-Location $testBin
      try {
        & dotnet exec $XunitConsoleDll AuraChat.Tests.dll -xml $xmlOut -nologo *> (Join-Path $out 'logs\test-xunit.log')
        $x = [xml](Get-Content $xmlOut -Raw)
        $a = $x.SelectSingleNode('/assemblies/assembly[1]')
        if ($a) {
          $t=[int]$a.GetAttribute('total'); $p=[int]$a.GetAttribute('passed'); $f=[int]$a.GetAttribute('failed'); $sk=[int]$a.GetAttribute('skipped'); $er=[int]$a.GetAttribute('errors'); $tm=$a.GetAttribute('time')
          $testSummary = "total=$t passed=$p failed=$f skipped=$sk errors=$er time=$tm"
          if ($f -eq 0 -and $er -eq 0) { $testOk = $true; Add-Step 'test-xunit' 'OK' $testSummary }
          else { Add-Step 'test-xunit' 'FAIL' $testSummary }
        } else { Add-Step 'test-xunit' 'FAIL' '无 XML 汇总 (logs\test-xunit.log)' }
      } finally { Pop-Location }
    } else { Add-Step 'test-xunit' 'FAIL' "找不到测试产物: $testBin" }
  }
}

# --- 4. 发布(锁定参数: 框架依赖 win-x64) ---
$publishLog = Join-Path $out 'logs\publish.log'
$publishDir = Join-Path $out 'publish\app'
$publishOk = $false
if (-not $SkipPublish) {
  # TOCTOU 前置复查：发布目标必须仍是已验证 OutDir 的严格子路径，且路径链无 reparse
  $pdDecision = Test-PublicationPath -Path $publishDir
  if (-not $pdDecision.Ok -or -not (Test-PubBoundaryInside -Child $pdDecision.RealPath -Ancestor $out)) {
    Deny-Publication "publishDir 越界或路径链不确定: $publishDir"
  }
  Push-Location $repo
  try {
    & dotnet publish src\AuraChat.App\AuraChat.App.csproj -c $Configuration -r $RuntimeId --self-contained false -o $publishDir --nologo *> $publishLog
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $publishDir 'AuraChat.exe'))) {
      $publishOk = $true
      $n = (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object).Count
      Add-Step 'publish' 'OK' "framework-dependent $RuntimeId, $n files -> publish\app"
    } else { Add-Step 'publish' 'FAIL' "exit=$LASTEXITCODE (logs\publish.log)" }
  } finally { Pop-Location }
}

# --- 5. 哈希清单 ---
if ($publishOk) {
  $manifest = Join-Path $out 'publish-manifest.sha256'
  # TOCTOU 复查：发布树必须全树无 reparse/symlink，Manifest 必须仍在已验证 Root 内
  $treeBad = Test-PubTreeReparse -Root $publishDir
  if ($treeBad.Count -gt 0) { Deny-Publication "发布树存在 junction/symlink: $($treeBad -join '; ')" }
  $mfFull = [System.IO.Path]::GetFullPath($manifest)
  if (-not (Test-PubBoundaryInside -Child $mfFull -Ancestor $out)) { Deny-Publication "Manifest 不在已验证 Root 内: $manifest" }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $lines = Get-ChildItem -LiteralPath $publishDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
    $fs = [System.IO.File]::OpenRead($_.FullName)
    try { $h = ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $fs.Close() }
    "$h  $rel"
  }
  $lines | Set-Content -LiteralPath $manifest -Encoding ASCII
  # 复核: 重算一遍并对比
  $bad = 0
  foreach ($l in $lines) {
    $p = $l -split '  ', 2; $f = Join-Path $publishDir ($p[1].Replace('/', '\'))
    $fs = [System.IO.File]::OpenRead($f)
    try { $h2 = ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $fs.Close() }
    if ($h2 -ne $p[0]) { $bad++ }
  }
  Add-Step 'manifest' $(if ($bad -eq 0) { 'OK' } else { 'FAIL' }) "($($lines.Count) files, recheck mismatches=$bad)"
}

# --- 6. 静态资源新鲜度(publish 的 public/ 与源 assets/public 逐字节一致) ---
if ($publishOk) {
  $sha2 = [System.Security.Cryptography.SHA256]::Create()
  function Hash-File([string]$f) {
    $fs = [System.IO.File]::OpenRead($f)
    try { return ($sha2.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $fs.Close() }
  }
  $freshBad = @()
  foreach ($pair in @(@('assets\public', 'public'), @('assets\personas', 'personas'))) {
    $srcRoot = Join-Path $repo $pair[0]; $pubRoot = Join-Path $publishDir $pair[1]
    if (-not (Test-Path -LiteralPath $srcRoot)) { continue }
    $srcFiles = Get-ChildItem -LiteralPath $srcRoot -Recurse -File
    foreach ($sf in $srcFiles) {
      $rel = $sf.FullName.Substring($srcRoot.Length + 1)
      $pf = Join-Path $pubRoot $rel
      if (-not (Test-Path -LiteralPath $pf)) { $freshBad += "missing-in-publish: $($pair[1])/$rel"; continue }
      if ((Hash-File $sf.FullName) -ne (Hash-File $pf)) { $freshBad += "stale-hash: $($pair[1])/$rel" }
    }
    $pubFiles = Get-ChildItem -LiteralPath $pubRoot -Recurse -File
    foreach ($pf in $pubFiles) {
      $rel = $pf.FullName.Substring($pubRoot.Length + 1)
      if (-not (Test-Path -LiteralPath (Join-Path $srcRoot $rel))) { $freshBad += "extra-in-publish: $($pair[1])/$rel" }
    }
  }
  Add-Step 'freshness' $(if ($freshBad.Count -eq 0) { 'OK' } else { 'FAIL' }) $(if ($freshBad.Count -eq 0) { 'public/ 与 personas/ 与源 assets/ 逐字节一致' } else { $freshBad -join '; ' })
}

# --- 7. 冒烟运行(隔离 home; A-2 闭环尝试) ---
if (-not $SkipSmoke -and $publishOk) {
  $isoHome = Join-Path $out 'iso-home'
  $isoData = Join-Path $isoHome 'data'
  $exe = Join-Path $publishDir 'AuraChat.exe'
  # 自检（合同 t2）: isoHome 必须严格位于已验证 OutDir 内（realpath + 边界分隔符比较，不用 lexical StartsWith）
  $isoDecision = Test-PublicationPath -Path $isoHome
  if (-not $isoDecision.Ok -or -not (Test-PubBoundaryInside -Child $isoDecision.RealPath -Ancestor $out)) {
    Deny-Publication "isoHome 越界或路径链不确定: $isoHome"
  }
  $isoHome = $isoDecision.RealPath
  New-Item -ItemType Directory -Path $isoData -Force | Out-Null
  $cfg = Join-Path $isoData 'config.json'
  "{`"web`":{`"host`":`"127.0.0.1`",`"port`":$SmokePort}}" | Set-Content -LiteralPath $cfg -Encoding UTF8
  $env:AURACHAT_HOME = $isoHome
  $so = Join-Path $out 'logs\smoke.out'; $se = Join-Path $out 'logs\smoke.err'
  $proc = Start-Process -FilePath $exe -ArgumentList '--headless' -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
  Write-Host "smoke: PID=$($proc.Id) isoHome=$isoHome port=$SmokePort"
  $listening = $false
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    if ($proc.HasExited) { break }
    $c = Get-NetTCPConnection -LocalPort $SmokePort -State Listen -ErrorAction SilentlyContinue
    if ($c) { $listening = $true; break }
  }
  $http = ''
  if ($listening) {
    Start-Sleep -Seconds 1
    $http = & curl.exe -s -o NUL -w '%{http_code}' "http://127.0.0.1:$SmokePort/api/status" 2>$null
  }
  $logExists = Test-Path -LiteralPath (Join-Path $isoHome 'logs\aurachat.log')
  $crashExists = Test-Path -LiteralPath (Join-Path $isoData 'startup-crash.log')
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 1
  $exitInfo = "exited=$($proc.HasExited)"
  if ($listening -and $http -ne '') {
    Add-Step 'smoke' $(if ($http -match '^\d{3}$') { 'OK' } else { 'FAIL' }) "LISTEN $SmokePort=yes HTTP /api/status=$http applog=$logExists $exitInfo"
  } else {
    Add-Step 'smoke' 'FAIL' "LISTEN=$listening $exitInfo (logs\smoke.out / smoke.err / startup-crash.log=$crashExists)"
  }
}

# --- 汇总 ---
$summaryMd = Join-Path $out 'summary.md'
$failCount = ($steps | Where-Object { $_.status -in @('FAIL') } | Measure-Object).Count
$blockCount = ($steps | Where-Object { $_.status -eq 'BLOCKED' } | Measure-Object).Count
("# release-verify 汇总", "", "| 步骤 | 状态 | 说明 |", "| --- | --- | --- |") | Set-Content -LiteralPath $summaryMd -Encoding UTF8
foreach ($s in $steps) { ("| $($s.step) | $($s.status) | $($s.note) |") | Add-Content -LiteralPath $summaryMd -Encoding UTF8 }
$summaryJson = Join-Path $out 'summary.json'
$steps | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $summaryJson -Encoding UTF8
Write-Host "== 完成: FAIL=$failCount BLOCKED=$blockCount 汇总: summary.md =="
if ($failCount -gt 0) { exit 2 } elseif ($blockCount -gt 0) { exit 3 } else { exit 0 }
