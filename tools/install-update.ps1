#requires -Version 5.1
# Aster Aura 本地安装/更新/回滚骨架（t63 / install-update）
# 用途: 对 tools\release-bundle.ps1 产出的发布束做 "受保护安装根校验 → 束结构/清单校验 →
#       版本化暂存与复核 → 原子切换 active 指针 → 失败回滚" 的本地安装/更新/回滚骨架。
#
# 状态声明（本脚本与其安装产物均为）:
#   LOCAL-ONLY / UNSIGNED DEVELOPMENT —— 仅本机开发用途；无代码签名、无网络更新、
#   无诊断上传、无云/移动/广告/积分行为；不声称商业可用（见 release\release-metadata.json 的 releaseStatus）。
#
# 纪律:
#   - 全部写入仅发生在显式传入且通过发布边界闸校验的 -InstallRoot 内；
#   - -InstallRoot 在任何写入之前经 tools\_publication-guard.ps1 校验（仓库/用户主目录/
#     系统目录/盘根/UNC/.. 段/junction-symlink 链一律拒绝，固定 PUBLICATION_DENIED + exit 7）；
#   - 束目录是只读输入: 仅读取与校验，绝不写入；脚本不读取不修改任何真实用户数据；
#   - 兼容 Windows PowerShell 5.1（不使用 pwsh 7 专属特性）。
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-update.ps1 `
#     -Action Install -InstallRoot D:\aura-install -BundleDir D:\aura-bundle-1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-update.ps1 `
#     -Action Rollback -InstallRoot D:\aura-install
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-update.ps1 `
#     -Action Status -InstallRoot D:\aura-install
#
# 安装根布局（-InstallRoot 下）:
#   active.json      活动版本指针（原子切换: 同卷 tmp 写入 + File.Replace / File.Move）
#   install.json     安装器状态与动作历史（追加记录，含回滚记录）
#   versions\<id>\   版本化束副本（保留全部历史版本，从不删除既有版本）
#   logs\            动作日志
#
# 版本标识 versionId = <release-metadata.version>-<bundle.json.diffHash 前 8 位>
# （同一束内容 → 同一 versionId；重复安装已存在版本按歧义拒绝 exit 4，不做静默覆盖。）
#
# 退出码（所有拒绝都发生在任何写入之前；失败路径一律回滚，active 指针不变）:
#   0 = 成功
#   2 = 操作失败（清单哈希不符 / 束未通过验证 / 复制或复核失败 / 指针切换失败——已回滚）
#   3 = 用法或输入无效（动作未知 / 缺 -BundleDir / 束结构缺失 / 安装根或指针不存在）
#   4 = 歧义拒绝（安装根已存在且不像安装根 / 束目录在安装根内 / 版本目录已存在）
#   7 = PUBLICATION_DENIED（发布边界闸: 仓库内 / 用户主目录 / 系统目录 / 盘根 / UNC /
#       .. 段 / junction-symlink 链 / 父链缺失——拒绝时固定输出 PUBLICATION_DENIED，绝不 fallback）

param(
  [string]$Action = 'Install',
  [Parameter(Mandatory = $true)][string]$InstallRoot,
  [string]$BundleDir = '',
  [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Continue'
$PackageStatus = 'LOCAL-ONLY / UNSIGNED DEVELOPMENT: local install/update/rollback skeleton; no code signing, no network update, no diagnostics upload, no cloud/mobile/ads/credits; not commercially ready.'
$PointerSchema = 'aster-aura.install-pointer/v1'
$StateSchema = 'aster-aura.install-state/v1'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$repo = [System.IO.Path]::GetFullPath($RepoRoot)

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
$script:log = New-Object 'System.Collections.Generic.List[string]'
function Log([string]$line) {
  $script:log.Add($line)
  Write-Host $line
}
function Save-Log([string]$dir, [string]$name) {
  try {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $p = Join-Path $dir $name
    [System.IO.File]::WriteAllLines($p, $script:log)
  } catch { Write-Host "WARN: 动作日志写入失败: $($_.Exception.Message)" }
}
function Get-SanitizedVersionId([string]$v) {
  return ($v -replace '[\\/:\*\?"<>\|]', '-')
}

# --- 1. 用法与输入校验（exit 3；任何写入之前） ---
if (@('Install', 'Rollback', 'Status') -notcontains $Action) {
  Write-Refusal 3 "用法无效: -Action 必须是 Install/Rollback/Status（当前: $Action）"; exit 3
}
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { Write-Refusal 3 '用法无效: -InstallRoot 为空'; exit 3 }
if ($Action -eq 'Install' -and [string]::IsNullOrWhiteSpace($BundleDir)) {
  Write-Refusal 3 '用法无效: -Action Install 需要 -BundleDir（release-bundle.ps1 产出目录）'; exit 3
}
if (-not (Test-Path -LiteralPath (Join-Path $repo 'AuraChat.sln') -PathType Leaf)) {
  Write-Refusal 3 "仓库无效（未找到 AuraChat.sln）: $repo"; exit 3
}

# --- 2. 发布边界闸（复用既有 _publication-guard；exit 7，发生在任何写入之前） ---
. (Join-Path $PSScriptRoot '_publication-guard.ps1')
$pubProtected = @( $repo ) + @( Get-PubProtectedRoots )
$rootReal = Assert-PublicationPath -Path $InstallRoot -ProtectedRoots $pubProtected -RequireExistingParent -What 'InstallRoot'

# 安装根歧义拒绝（exit 4，发生在任何写入之前）: 已存在且非空、又不含安装器布局（active.json /
# versions\ / install.json）的目录，不当作安装根——防止把任意已占用目录当安装目标。
if (Test-Path -LiteralPath $rootReal) {
  $existing = @(Get-ChildItem -LiteralPath $rootReal -Force -ErrorAction SilentlyContinue)
  if ($existing.Count -gt 0) {
    $looksLikeInstallRoot = @('active.json', 'install.json') | Where-Object { Test-Path -LiteralPath (Join-Path $rootReal $_) -PathType Leaf }
    if (-not $looksLikeInstallRoot -and -not (Test-Path -LiteralPath (Join-Path $rootReal 'versions') -PathType Container)) {
      Write-Refusal 4 "InstallRoot 已存在且不像安装根（无 active.json / versions\ / install.json，拒绝混入既有内容）: $rootReal"; exit 4
    }
  }
}

$versionsDir = Join-Path $rootReal 'versions'
$logsDir = Join-Path $rootReal 'logs'
$pointerPath = Join-Path $rootReal 'active.json'
$statePath = Join-Path $rootReal 'install.json'
# 派生写路径全部必须在已验证安装根之内（TOCTOU 防越界，与 release-bundle 同法）。
# 目录路径走守卫全链（realpath + reparse）；文件路径（active.json/install.json）是
# "已验证根 + 常量叶名"，守卫的目录链解析会把同名文件误判为非法，故用边界比较确认。
foreach ($p in @($versionsDir, $logsDir)) {
  $d = Test-PublicationPath -Path $p
  if (-not $d.Ok -or -not (Test-PubBoundaryInside -Child $d.RealPath -Ancestor $rootReal)) {
    Deny-Publication "派生写路径越界或路径链不确定: $p"
  }
}
foreach ($p in @($pointerPath, $statePath)) {
  $full = [System.IO.Path]::GetFullPath($p)
  if (-not (Test-PubBoundaryInside -Child $full -Ancestor $rootReal)) {
    Deny-Publication "派生写路径越界: $p"
  }
}

Log "== install-update: $Action =="
Log "installRoot : $rootReal"
Log "status      : $PackageStatus"

# --- 3. 安装器状态读取（install.json；Status/Rollback 需要，Install 允许新建） ---
function Read-InstallState {
  if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
  try { return Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}
function Read-Pointer {
  if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) { return $null }
  try { return Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

# --- 4. 束校验（只读: 结构 + verification 结论 + SHA-256 清单重算; 全部在任何写入之前） ---
function Test-BundleManifest([string]$publishAppDir, [string]$manifestPath) {
  # 逐行校验 publish-manifest.sha256: "<sha256>  <relpath>"，重算哈希并核对文件总数（无夹带）。
  $lines = @(Get-Content -LiteralPath $manifestPath -Encoding ASCII | Where-Object { $_.Trim().Length -gt 0 })
  $actual = @(Get-ChildItem -LiteralPath $publishAppDir -Recurse -File -Force)
  if ($actual.Count -ne $lines.Count) {
    return @{ Ok = $false; Detail = "文件数不符: manifest=$($lines.Count) actual=$($actual.Count)"; Count = $lines.Count }
  }
  $bad = @()
  foreach ($line in $lines) {
    if ($line -notmatch '^(?<h>[0-9a-fA-F]{64})  (?<p>.+)$') { return @{ Ok = $false; Detail = "清单行格式非法: $line"; Count = $lines.Count } }
    $rel = $Matches['p'].Replace('/', '\')
    $fp = Join-Path $publishAppDir $rel
    if (-not (Test-Path -LiteralPath $fp -PathType Leaf)) { $bad += "missing: $($Matches['p'])"; continue }
    if ((Get-FileSha256 $fp) -ine $Matches['h']) { $bad += "hash-mismatch: $($Matches['p'])" }
  }
  if ($bad.Count -gt 0) { return @{ Ok = $false; Detail = ($bad | Select-Object -First 5) -join '; '; Count = $lines.Count } }
  return @{ Ok = $true; Detail = "$($lines.Count) files all-verified"; Count = $lines.Count }
}

function Get-BundleIdentity([string]$bundle) {
  # 结构校验 + 身份提取。返回 @{ Ok; Code; Reason; VersionId; Version; DiffHash; BundleTimeUtc }。
  $required = @(
    @{ p = 'publish\app\AuraChat.exe'; w = 'publish\app\AuraChat.exe' },
    @{ p = 'release-metadata.json'; w = 'release-metadata.json' },
    @{ p = 'bundle.json'; w = 'bundle.json' },
    @{ p = 'publish-manifest.sha256'; w = 'publish-manifest.sha256' },
    @{ p = 'manifest.json'; w = 'manifest.json' },
    @{ p = 'verification.json'; w = 'verification.json' }
  )
  foreach ($r in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundle $r.p) -PathType Leaf)) {
      return @{ Ok = $false; Code = 3; Reason = "束结构缺失: $($r.w)" }
    }
  }
  $verif = $null
  try { $verif = Get-Content -LiteralPath (Join-Path $bundle 'verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
  if ($null -eq $verif -or $verif.verdict -ne 'ok') {
    $v = '?'; if ($null -ne $verif) { $v = $verif.verdict }
    return @{ Ok = $false; Code = 2; Reason = "束 verification.json verdict=$v（非 ok，拒绝安装未通过验证的束）" }
  }
  $meta = $null
  try { $meta = Get-Content -LiteralPath (Join-Path $bundle 'release-metadata.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
  if ($null -eq $meta -or [string]::IsNullOrWhiteSpace("$($meta.version)")) {
    return @{ Ok = $false; Code = 3; Reason = 'release-metadata.json 缺失或无 version 字段' }
  }
  $bj = $null
  try { $bj = Get-Content -LiteralPath (Join-Path $bundle 'bundle.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
  $diffHash = ''
  if ($null -ne $bj) { $diffHash = "$($bj.diffHash)" }
  $hash8 = 'nodiff'
  if ($diffHash -match '^[0-9a-fA-F]{8}') { $hash8 = $Matches[0].ToLowerInvariant() }
  $m = Test-BundleManifest -publishAppDir (Join-Path $bundle 'publish\app') -manifestPath (Join-Path $bundle 'publish-manifest.sha256')
  if (-not $m.Ok) { return @{ Ok = $false; Code = 2; Reason = "束清单校验失败: $($m.Detail)" } }
  return @{
    Ok = $true; Version = "$($meta.version)"; VersionId = (Get-SanitizedVersionId "$($meta.version)-$hash8")
    DiffHash = $diffHash; BundleTimeUtc = "$(if ($null -ne $bj) { $bj.bundleTimeUtc })"; ManifestCount = $m.Count
  }
}

function Switch-Pointer([hashtable]$p) {
  # 原子指针切换: 同卷 tmp 写入 → 已存在则 File.Replace（NTFS 原子），否则 File.Move。
  # 失败时旧指针文件原样保留（Replace 原子性），由调用方执行回滚。
  $tmp = Join-Path $rootReal ("active.json.tmp-{0}" -f $PID)
  $json = $p | ConvertTo-Json -Depth 4
  [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($true)))
  try {
    if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
      # PS 5.1 下 File.Replace 的 $null 备份参数会绑定失败（"path not of a legal form"），
      # 传真实备份路径（同为 NTFS 原子替换），成功后清理备份。
      $bak = "$tmp.bak"
      [System.IO.File]::Replace($tmp, $pointerPath, $bak)
      if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue }
    } else {
      [System.IO.File]::Move($tmp, $pointerPath)
    }
    return $true
  } catch {
    Write-Host "WARN: 指针切换失败: $($_.Exception.Message)"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    return $false
  }
}

function Append-InstallState([string]$actionName, [string]$versionId, [string]$status, [string]$detail, [string]$activeAfter) {
  $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  $state = Read-InstallState
  if ($null -eq $state) {
    $state = [pscustomobject]@{ schema = $StateSchema; packageStatus = $PackageStatus; createdAtUtc = $now; updatedAtUtc = $now; actions = @() }
  }
  $actions = @($state.actions)
  $actions += [pscustomobject]@{ atUtc = $now; action = $actionName; versionId = $versionId; status = $status; detail = $detail; activeAfter = $activeAfter }
  $out = [pscustomobject]@{ schema = $StateSchema; packageStatus = $PackageStatus; createdAtUtc = $state.createdAtUtc; updatedAtUtc = $now; actions = $actions }
  $out | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding UTF8
}

# =============== Status（只读，不写任何文件） ===============
if ($Action -eq 'Status') {
  if (-not (Test-Path -LiteralPath $rootReal -PathType Container)) {
    Write-Refusal 3 "安装根不存在: $rootReal"; exit 3
  }
  $ptr = Read-Pointer
  if ($null -eq $ptr) {
    Log 'active     : (无指针，尚未安装任何版本)'
  } else {
    Log "active     : $($ptr.activeVersionId)  (previous: $($ptr.previousVersionId), switchedAt: $($ptr.switchedAtUtc))"
    Log "package    : $($ptr.packageStatus)"
  }
  $vs = @()
  if (Test-Path -LiteralPath $versionsDir -PathType Container) { $vs = @(Get-ChildItem -LiteralPath $versionsDir -Directory -Force | Sort-Object Name) }
  Log "versions   : $(if ($vs.Count -gt 0) { ($vs | ForEach-Object { $_.Name }) -join ', ' } else { '(空)' })"
  $st = Read-InstallState
  if ($null -ne $st -and @($st.actions).Count -gt 0) {
    Log 'recent     :'
    @($st.actions) | Select-Object -Last 5 | ForEach-Object { Log ("  {0} {1} {2} {3}" -f $_.atUtc, $_.action, $_.versionId, $_.status) }
  }
  exit 0
}

# =============== Rollback ===============
if ($Action -eq 'Rollback') {
  if (-not (Test-Path -LiteralPath $rootReal -PathType Container)) { Write-Refusal 3 "安装根不存在: $rootReal"; exit 3 }
  $ptr = Read-Pointer
  if ($null -eq $ptr) { Write-Refusal 3 "无 active 指针，无可回滚: $pointerPath"; exit 3 }
  $prev = "$($ptr.previousVersionId)"
  if ([string]::IsNullOrWhiteSpace($prev)) { Write-Refusal 3 "指针无 previousVersionId，无可回滚版本"; exit 3 }
  $prevDir = Join-Path $versionsDir $prev
  if (-not (Test-Path -LiteralPath $prevDir -PathType Container)) {
    Write-Refusal 2 "回滚目标版本目录不存在: $prevDir"; exit 2
  }
  $id = Get-BundleIdentity -bundle $prevDir
  if (-not $id.Ok) { Write-Refusal $id.Code "回滚目标版本校验失败: $($id.Reason)"; exit $id.Code }
  $newPointer = @{
    schema = $PointerSchema; packageStatus = $PackageStatus
    activeVersionId = $prev; previousVersionId = "$($ptr.activeVersionId)"
    switchedAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); lastAction = 'Rollback'
  }
  if (Switch-Pointer $newPointer) {
    Log "rollback   : active=$prev (previous=$($ptr.activeVersionId))"
    Append-InstallState 'Rollback' $prev 'ok' $id.Reason $prev
    Save-Log $logsDir ("rollback-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    exit 0
  }
  Append-InstallState 'Rollback' $prev 'failed' '指针切换失败（旧指针原样保留）' "$($ptr.activeVersionId)"
  Save-Log $logsDir ("rollback-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
  Write-Refusal 2 '回滚失败: 指针切换未成功（active 指针保持原值）'; exit 2
}

# =============== Install ===============
# 4a. 束目录路径链校验（只读输入: 仅拒绝 reparse/UNC/不确定链; 束可以是任何可读位置）
$bundleReal = Resolve-PubRealPath -Path $BundleDir
if (-not $bundleReal.Ok) { Write-Refusal 7 "BundleDir 路径链不确定: $($bundleReal.Reason)"; exit 7 }
if (Test-PubEqualOrInside -Child $bundleReal.RealPath -Ancestor $rootReal) {
  Write-Refusal 4 "BundleDir 在 InstallRoot 内（自拷贝歧义）: $($bundleReal.RealPath)"; exit 4
}
if (-not (Test-Path -LiteralPath $bundleReal.RealPath -PathType Container)) {
  Write-Refusal 3 "BundleDir 不存在: $($bundleReal.RealPath)"; exit 3
}
$treeBad = Test-PubTreeReparse -Root $bundleReal.RealPath
if ($treeBad.Count -gt 0) { Deny-Publication "束树存在 junction/symlink: $($treeBad -join '; ')" }

# 4b. 束身份 + 清单校验（任何写入之前）
$id = Get-BundleIdentity -bundle $bundleReal.RealPath
if (-not $id.Ok) { Write-Refusal $id.Code "束校验失败: $($id.Reason)"; exit $id.Code }
$versionId = $id.VersionId
$targetDir = Join-Path $versionsDir $versionId
if (Test-Path -LiteralPath $targetDir) { Write-Refusal 4 "版本目录已存在（同一束内容不重复安装，拒绝覆盖）: $targetDir"; exit 4 }
Log "bundle      : $($bundleReal.RealPath)"
Log "versionId   : $versionId (version=$($id.Version), diffHash8=$($id.DiffHash.Substring(0, [Math]::Min(8, $id.DiffHash.Length))), manifest=$($id.ManifestCount) files)"

# 4c. 暂存 → 复核 → 原子指针切换（此起才有写入; 任一步失败即回滚，active 指针不变）
New-Item -ItemType Directory -Path $versionsDir, $logsDir -Force -ErrorAction Stop | Out-Null
$staging = Join-Path $versionsDir (".staging-{0}-{1}" -f $versionId, $PID)
function Remove-Staging { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue } }
try {
  New-Item -ItemType Directory -Path $staging -Force -ErrorAction Stop | Out-Null
  foreach ($item in @(Get-ChildItem -LiteralPath $bundleReal.RealPath -Force)) {
    Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force -ErrorAction Stop
  }
} catch {
  Remove-Staging
  Append-InstallState 'Install' $versionId 'failed-rolledback' "复制失败已回滚: $($_.Exception.Message)" ''
  Write-Refusal 2 "复制束到暂存失败（已回滚，active 指针不变）: $($_.Exception.Message)"; exit 2
}
$staged = Get-BundleIdentity -bundle $staging
if (-not $staged.Ok) {
  Remove-Staging
  Append-InstallState 'Install' $versionId 'failed-rolledback' "暂存复核失败: $($staged.Reason)" ''
  Write-Refusal 2 "暂存复核失败（已回滚，active 指针不变）: $($staged.Reason)"; exit 2
}
if (Test-Path -LiteralPath $targetDir) { Remove-Staging; Write-Refusal 4 "版本目录已存在（并发安装歧义拒绝）: $targetDir"; exit 4 }
try {
  Rename-Item -LiteralPath $staging -NewName $versionId -ErrorAction Stop
} catch {
  Remove-Staging
  Append-InstallState 'Install' $versionId 'failed-rolledback' "暂存改名失败: $($_.Exception.Message)" ''
  Write-Refusal 2 "暂存改名失败（已回滚，active 指针不变）: $($_.Exception.Message)"; exit 2
}
$oldPtr = Read-Pointer
$switchedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$newPointer = @{
  schema = $PointerSchema; packageStatus = $PackageStatus
  activeVersionId = $versionId
  previousVersionId = "$(if ($null -ne $oldPtr) { $oldPtr.activeVersionId } else { '' })"
  switchedAtUtc = $switchedAt; lastAction = 'Install'
}
if (-not (Switch-Pointer $newPointer)) {
  # 回滚: 移除刚改名的版本目录，active 指针原样保留（File.Replace 原子性保证）
  if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force -ErrorAction SilentlyContinue }
  Append-InstallState 'Install' $versionId 'failed-rolledback' '指针切换失败，版本目录已回滚移除' "$(if ($null -ne $oldPtr) { $oldPtr.activeVersionId } else { '' })"
  Write-Refusal 2 '指针切换失败（已回滚: 新版本目录已移除，active 指针不变）'; exit 2
}
Append-InstallState 'Install' $versionId 'ok' "bundle=$($bundleReal.RealPath)" $versionId
Log "installed   : active=$versionId previous=$($newPointer.previousVersionId) retained-versions=$((@(Get-ChildItem -LiteralPath $versionsDir -Directory -Force | Where-Object { $_.Name -notlike '.staging-*' })).Count)"
Save-Log $logsDir ("install-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
exit 0
