#requires -Version 5.1
# Aster Aura 可复现发布打包脚本（t49 / release-bundle）
# 用途: 对当前仓库快照做 "输出路径守卫 → 身份采集 → (可选)构建 → FDD win-x64 发布 → 混合布局守卫
#       → 静态资源新鲜度 → SHA-256 清单 → (可选)隔离冒烟"，把发布元数据、源码身份、产物哈希、
#       文件清单与验证状态全部落到外部输出目录，形成可审查的发布束。
#
# 纪律（与 tools\release-verify.ps1 同源）:
#   - 本脚本对仓库只读（git 采集与构建/发布读源码），全部输出写 -OutDir；
#   - -OutDir 在任何写入之前经 tools\_publication-guard.ps1 校验（复用既有发布边界闸）；
#   - 冒烟运行使用束内隔离 AURACHAT_HOME / AURACHAT_DATA_DIR，绝不触碰 %APPDATA%\AuraChat；
#   - 兼容 Windows PowerShell 5.1（不使用 pwsh 7 专属特性）。
#
# 用法（在仓库根）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-bundle.ps1 -RepoRoot . `
#     -OutDir D:\aura-bundle-1 [-Configuration Release] [-RuntimeId win-x64] [-SmokePort 24334]
#     [-SkipBuild] [-SkipSmoke]
#   完整验收等价入口见 tools\release-verify.ps1（构建+测试+发布+冒烟全流程）。
#
# 输出布局（-OutDir 下）:
#   release-metadata.json   仓库 release\release-metadata.json 的原样拷贝（产品/版本/运行时/包型/状态声明）
#   bundle.json             束身份: HEAD、status/diff 哈希、SDK、配置、布局守卫与 runtimeconfig 框架记录
#   identity.txt            git status --porcelain 全文 + 身份摘要（人读）
#   publish\app\            框架依赖 win-x64 发布产物（AuraChat.exe ...）
#   publish-manifest.sha256 publish 产物 SHA-256 清单（hash  相对路径，含复核）
#   manifest.json           publish 产物文件清单（相对路径/大小/SHA-256）
#   verification.json       各步骤状态与结论（OK/FAIL/SKIPPED，含退出码）
#   summary.md              人读步骤汇总表
#   output-manifest.sha256  顶层元数据文件自校验清单（排除自身）
#   logs\                   build / publish / smoke 日志
#
# 退出码（拒绝行为均发生在任何写入之前）:
#   0 = 打包完成且无 FAIL
#   2 = 有步骤 FAIL（build/publish/布局守卫/新鲜度/manifest/冒烟）
#   3 = 用法或输入无效（仓库/解决方案/发布元数据缺失、-OutDir 为空）
#   4 = 歧义输出拒绝（-OutDir 已存在且非空，或 -OutDir 包含源仓库）
#   7 = PUBLICATION_DENIED（发布边界闸: 仓库内部/用户主目录/系统目录/盘根/UNC/含..段/
#       junction-symlink 链/父链缺失——拒绝时固定输出 PUBLICATION_DENIED，绝不 fallback）
#
# 注意: 用户主目录整体是受保护根，因此 %TEMP%（位于 %USERPROFILE% 内）不能作 -OutDir；
#       请使用用户主目录之外的目录（如 D:\aura-bundle-1）。

param(
  [string]$RepoRoot = '',
  [Parameter(Mandatory = $true)][string]$OutDir,
  [string]$Configuration = 'Release',
  [string]$RuntimeId = 'win-x64',
  [int]$SmokePort = 24334,
  [switch]$SkipBuild,
  [switch]$SkipSmoke
)

$ErrorActionPreference = 'Continue'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$repo = [System.IO.Path]::GetFullPath($RepoRoot)

function Get-TextSha256([string]$text) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $b = [Text.Encoding]::UTF8.GetBytes($text); return ($sha.ComputeHash($b) | ForEach-Object { $_.ToString('x2') }) -join '' }
  finally { $sha.Dispose() }
}
function Get-FileSha256([string]$path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $fs = [System.IO.File]::OpenRead($path)
  try { return ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' }
  finally { $fs.Close(); $sha.Dispose() }
}
function Write-Refusal([int]$code, [string]$reason) {
  Write-Host "REFUSED(exit $code): $reason"
  if ($code -eq 7) {
    [Console]::Out.WriteLine("PUBLICATION_DENIED: $reason")
    [Console]::Error.WriteLine("PUBLICATION_DENIED: $reason")
  }
}

# --- 0. 输入校验（exit 3；任何写入之前） ---
if ([string]::IsNullOrWhiteSpace($OutDir)) { Write-Refusal 3 '用法无效: -OutDir 为空'; exit 3 }
if (-not (Test-Path -LiteralPath (Join-Path $repo 'AuraChat.sln') -PathType Leaf)) { Write-Refusal 3 "仓库无效（未找到 AuraChat.sln）: $repo"; exit 3 }
$metaPath = Join-Path $repo 'release\release-metadata.json'
if (-not (Test-Path -LiteralPath $metaPath -PathType Leaf)) { Write-Refusal 3 "缺少发布元数据: $metaPath"; exit 3 }

# --- 1. 发布边界闸（复用既有 _publication-guard；exit 7，发生在任何写入之前） ---
. (Join-Path $PSScriptRoot '_publication-guard.ps1')
$pubProtected = @( $repo ) + @( Get-PubProtectedRoots )
$out = Assert-PublicationPath -Path $OutDir -ProtectedRoots $pubProtected -RequireExistingParent -What 'OutDir'

# --- 2. 歧义拒绝（exit 4，发生在任何写入之前） ---
if (Test-Path -LiteralPath $out) {
  $existing = @(Get-ChildItem -LiteralPath $out -Force -ErrorAction SilentlyContinue)
  if ($existing.Count -gt 0) { Write-Refusal 4 "OutDir 已存在且非空（拒绝混入既有内容，请给全新目录）: $out"; exit 4 }
}
if (Test-PubBoundaryInside -Child $repo -Ancestor $out) { Write-Refusal 4 "OutDir 包含源仓库（歧义输出）: $out"; exit 4 }

New-Item -ItemType Directory -Path $out, (Join-Path $out 'logs') -Force | Out-Null
$steps = @()
function Add-Step([string]$name, [string]$status, [string]$note) {
  $script:steps += [pscustomobject]@{ step = $name; status = $status; note = $note }
  Write-Host "[$name] $status :: $note"
}

Write-Host "== release-bundle =="
Write-Host "repo : $repo"
Write-Host "out  : $out"
Write-Host "mode : Configuration=$Configuration RuntimeId=$RuntimeId SkipBuild=$SkipBuild SkipSmoke=$SkipSmoke"

# --- 3. 源码身份采集（bundle.json + identity.txt） ---
$nowUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$head = ''; $statusTxt = ''; $diffHash = ''; $statusHash = ''; $porcelainLines = 0
$isGit = Test-Path -LiteralPath (Join-Path $repo '.git') -PathType Container
if ($isGit) {
  $head = (& git -C $repo rev-parse HEAD) 2>$null
  $statusTxt = (& git -C $repo status --porcelain) 2>$null | Out-String
  $diffHash = Get-TextSha256 ((& git -C $repo diff HEAD) 2>$null | Out-String)
  $statusHash = Get-TextSha256 $statusTxt
  $porcelainLines = @(($statusTxt -split "`r?`n") | Where-Object { $_.Trim().Length -gt 0 }).Count
}
$sdk = (& dotnet --version) 2>$null
$bundleJson = [pscustomobject]@{
  schema          = 'aster-aura.release-bundle/v1'
  bundleTimeUtc   = $nowUtc
  repoRoot        = $repo
  head            = $head
  statusHash      = $statusHash
  diffHash        = $diffHash
  porcelainLines  = $porcelainLines
  dotnetSdk       = $sdk
  configuration   = $Configuration
  runtimeId       = $RuntimeId
  selfContained   = $false
  smokePort       = $SmokePort
  skipBuild       = [bool]$SkipBuild
  skipSmoke       = [bool]$SkipSmoke
  layoutGuard     = $null
  runtimeconfigFrameworks = @()
}
$bundleJsonPath = Join-Path $out 'bundle.json'
$bundleJson | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $bundleJsonPath -Encoding UTF8
(
  "HEAD=$head",
  "statusHash(SHA256 of `git status --porcelain` output)=$statusHash",
  "diffHash(SHA256 of `git diff HEAD` output)=$diffHash",
  "porcelainLines=$porcelainLines",
  "dotnetSDK=$sdk",
  "bundleTimeUtc=$nowUtc",
  "--- git status --porcelain ---",
  $statusTxt.TrimEnd()
) | Set-Content -LiteralPath (Join-Path $out 'identity.txt') -Encoding UTF8
Add-Step 'identity' 'OK' "HEAD=$head diffHash=$diffHash statusHash=$statusHash (bundle.json / identity.txt)"

# --- 4. 发布元数据（仓库 release\release-metadata.json 原样拷贝） ---
Copy-Item -LiteralPath $metaPath -Destination (Join-Path $out 'release-metadata.json') -Force
Add-Step 'metadata' 'OK' 'release\release-metadata.json 原样拷贝到束根（产品/版本/运行时/包型/状态声明）'

# --- 5. 构建（可选; -SkipBuild 仅跳过显式构建，publish 仍会增量构建） ---
$buildOk = $true
if (-not $SkipBuild) {
  $buildLog = Join-Path $out 'logs\build.log'
  Push-Location $repo
  try {
    & dotnet build AuraChat.sln -c $Configuration --no-restore --nologo *> $buildLog
    $code = $LASTEXITCODE
    $errs = (Select-String -Path $buildLog -Pattern ': error ' | Measure-Object).Count
    $warns = (Select-String -Path $buildLog -Pattern ': warning ' | Measure-Object).Count
    if ($code -eq 0 -and $errs -eq 0) { Add-Step 'build' 'OK' "exit=$code errors=0 warnings=$warns" }
    else { $buildOk = $false; Add-Step 'build' 'FAIL' "exit=$code errors=$errs warnings=$warns (logs\build.log)" }
  } finally { Pop-Location }
} else {
  Add-Step 'build' 'SKIPPED' '-SkipBuild 指定: 跳过显式 dotnet build（publish 自身仍会构建）'
}

# --- 6. 发布（锁定参数: 框架依赖 win-x64 → publish\app） ---
$publishLog = Join-Path $out 'logs\publish.log'
$publishDir = Join-Path $out 'publish\app'
$publishOk = $false
if ($buildOk) {
  # TOCTOU 前置复查: 发布目标必须是已验证 OutDir 的严格子路径，路径链无 reparse
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
} else {
  Add-Step 'publish' 'SKIPPED' 'build FAIL, 跳过发布'
}

# --- 7. 混合布局守卫（FDD 产物禁止夹带 app-local 宿主/运行时文件, t8 教训） ---
if ($publishOk) {
  $banned = @('hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll','clrjit.dll','hostfxr.h','hostpolicy.h')
  $hits = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object { $banned -contains $_.Name })
  $frameworks = @()
  $rc = Join-Path $publishDir 'AuraChat.runtimeconfig.json'
  if (Test-Path -LiteralPath $rc) {
    $rcRaw = Get-Content -LiteralPath $rc -Raw
    $frameworks = @([regex]::Matches($rcRaw, '"name"\s*:\s*"([^"]+)"\s*,\s*"version"\s*:\s*"([^"]+)"') | ForEach-Object { "$($_.Groups[1].Value) $($_.Groups[2].Value)" })
  }
  $bundleJson.layoutGuard = [pscustomobject]@{ bannedNames = $banned; hits = @($hits | ForEach-Object { $_.FullName.Substring($publishDir.Length + 1) }) }
  $bundleJson.runtimeconfigFrameworks = $frameworks
  $bundleJson | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $bundleJsonPath -Encoding UTF8
  if ($hits.Count -eq 0) {
    Add-Step 'layout-guard' 'OK' "无 app-local 宿主文件; runtimeconfig: $($frameworks -join ', ')"
  } else {
    $publishOk = $false
    Add-Step 'layout-guard' 'FAIL' "夹带 app-local 宿主文件: $(($hits | ForEach-Object { $_.FullName.Substring($publishDir.Length + 1) }) -join '; ')"
  }
}

# --- 8. 静态资源新鲜度（publish 的 public/、personas/ 与源 assets/ 逐字节一致） ---
if ($publishOk) {
  $freshBad = @()
  foreach ($pair in @(@('assets\public', 'public'), @('assets\personas', 'personas'))) {
    $srcRoot = Join-Path $repo $pair[0]; $pubRoot = Join-Path $publishDir $pair[1]
    if (-not (Test-Path -LiteralPath $srcRoot)) { continue }
    foreach ($sf in (Get-ChildItem -LiteralPath $srcRoot -Recurse -File)) {
      $rel = $sf.FullName.Substring($srcRoot.Length + 1)
      $pf = Join-Path $pubRoot $rel
      if (-not (Test-Path -LiteralPath $pf)) { $freshBad += "missing-in-publish: $($pair[1])/$rel"; continue }
      if ((Get-FileSha256 $sf.FullName) -ne (Get-FileSha256 $pf)) { $freshBad += "stale-hash: $($pair[1])/$rel" }
    }
    foreach ($pf in (Get-ChildItem -LiteralPath $pubRoot -Recurse -File)) {
      $rel = $pf.FullName.Substring($pubRoot.Length + 1)
      if (-not (Test-Path -LiteralPath (Join-Path $srcRoot $rel))) { $freshBad += "extra-in-publish: $($pair[1])/$rel" }
    }
  }
  Add-Step 'freshness' $(if ($freshBad.Count -eq 0) { 'OK' } else { 'FAIL' }) $(if ($freshBad.Count -eq 0) { 'public/ 与 personas/ 与源 assets/ 逐字节一致' } else { ($freshBad | Select-Object -First 8) -join '; ' })
}

# --- 9. 产物哈希清单（publish-manifest.sha256 + manifest.json, 全树 reparse 清查后生成） ---
if ($publishOk) {
  $treeBad = Test-PubTreeReparse -Root $publishDir
  if ($treeBad.Count -gt 0) { Deny-Publication "发布树存在 junction/symlink: $($treeBad -join '; ')" }
  $manifest = Join-Path $out 'publish-manifest.sha256'
  $mfFull = [System.IO.Path]::GetFullPath($manifest)
  if (-not (Test-PubBoundaryInside -Child $mfFull -Ancestor $out)) { Deny-Publication "Manifest 不在已验证 Root 内: $manifest" }
  $files = Get-ChildItem -LiteralPath $publishDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
    [pscustomobject]@{ path = $rel; size = $_.Length; sha256 = (Get-FileSha256 $_.FullName) }
  }
  $files | ForEach-Object { "$($_.sha256)  $($_.path)" } | Set-Content -LiteralPath $manifest -Encoding ASCII
  $files | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'manifest.json') -Encoding UTF8
  # 复核: 重算一遍并对比
  $bad = 0
  foreach ($f in $files) {
    if ((Get-FileSha256 (Join-Path $publishDir ($f.path.Replace('/', '\')))) -ne $f.sha256) { $bad++ }
  }
  Add-Step 'manifest' $(if ($bad -eq 0) { 'OK' } else { 'FAIL' }) "($($files.Count) files, recheck mismatches=$bad) -> publish-manifest.sha256 + manifest.json"
}

# --- 10. 隔离冒烟运行（束内 iso-home; 不触碰真实用户数据） ---
if (-not $SkipSmoke -and $publishOk) {
  $isoHome = Join-Path $out 'iso-home'
  $isoData = Join-Path $isoHome 'data'
  $exe = Join-Path $publishDir 'AuraChat.exe'
  $isoDecision = Test-PublicationPath -Path $isoHome
  if (-not $isoDecision.Ok -or -not (Test-PubBoundaryInside -Child $isoDecision.RealPath -Ancestor $out)) {
    Deny-Publication "isoHome 越界或路径链不确定: $isoHome"
  }
  $isoHome = $isoDecision.RealPath
  New-Item -ItemType Directory -Path $isoData -Force | Out-Null
  "{`"web`":{`"host`":`"127.0.0.1`",`"port`":$SmokePort}}" | Set-Content -LiteralPath (Join-Path $isoData 'config.json') -Encoding UTF8
  $env:AURACHAT_HOME = $isoHome
  $env:AURACHAT_DATA_DIR = $isoData
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
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 1
  & taskkill /PID $proc.Id /T /F 2>$null | Out-Null
  Remove-Item Env:\AURACHAT_HOME -ErrorAction SilentlyContinue
  Remove-Item Env:\AURACHAT_DATA_DIR -ErrorAction SilentlyContinue
  if ($listening -and $http -match '^\d{3}$') {
    Add-Step 'smoke' 'OK' "LISTEN 127.0.0.1:$SmokePort=yes HTTP /api/status=$http (隔离 home: $isoHome)"
  } else {
    Add-Step 'smoke' 'FAIL' "LISTEN=$listening http=$http exited=$($proc.HasExited) (logs\smoke.out / smoke.err)"
  }
} elseif ($SkipSmoke) {
  Add-Step 'smoke' 'SKIPPED' '-SkipSmoke 指定: 跳过隔离冒烟运行'
}

# --- 11. 验证状态汇总（verification.json + summary.md + 顶层自校验清单） ---
$failCount = ($steps | Where-Object { $_.status -eq 'FAIL' } | Measure-Object).Count
$exitCode = 0
if ($failCount -gt 0) { $exitCode = 2 }
$verification = [pscustomobject]@{
  schema        = 'aster-aura.release-verification/v1'
  bundleTimeUtc = $nowUtc
  configuration = $Configuration
  runtimeId     = $RuntimeId
  selfContained = $false
  verdict       = $(if ($failCount -eq 0) { 'ok' } else { 'failed' })
  failCount     = $failCount
  exitCode      = $exitCode
  steps         = $steps
}
$verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'verification.json') -Encoding UTF8
("# release-bundle 汇总", "", "| 步骤 | 状态 | 说明 |", "| --- | --- | --- |") | Set-Content -LiteralPath (Join-Path $out 'summary.md') -Encoding UTF8
foreach ($s in $steps) { ("| $($s.step) | $($s.status) | $($s.note) |") | Add-Content -LiteralPath (Join-Path $out 'summary.md') -Encoding UTF8 }
$topFiles = @('release-metadata.json','bundle.json','identity.txt','publish-manifest.sha256','manifest.json','verification.json','summary.md')
$topLines = foreach ($t in $topFiles) {
  $p = Join-Path $out $t
  if (Test-Path -LiteralPath $p) { "$(Get-FileSha256 $p)  $t" }
}
$topLines | Set-Content -LiteralPath (Join-Path $out 'output-manifest.sha256') -Encoding ASCII

Write-Host "== 完成: FAIL=$failCount 退出码=$exitCode 汇总: summary.md =="
exit $exitCode
