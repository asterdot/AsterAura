#requires -Version 5.1
# 把旧 Node 版 AuraChat 的数据整体搬到新的 .NET 单体（安全收口版，合同 t2 / 迁移发布冲刺）。
# 新旧数据目录结构一致（config.json / users.json / memories.json / diary.json /
# azone-social.json / mood.json / chats/ / stickers/ / media/ / personas/ ...），
# 所以本质就是一次递归复制；另外可选地把 web.port 改掉，避开旧插件占用的 2332。
#
# 安全合同（相对旧版的行为收口）:
#   - -Source / -Dest 必须显式传入，脚本没有任何默认真实路径；
#   - 默认 DryRun: 不带 -Apply 时只打印迁移计划——不创建目录、不复制、不读取任何文件内容、
#     不输出真实数据的条数/文件数等统计；
#   - 只有显式 -Apply 才会复制。Apply 按设计会读取 Source 内容计算 SHA-256 预检清单并做
#     复制后逐文件对账（这是迁移工具的本职）；自动化验证只允许对合成数据使用 Apply；
#   - Source / Dest 路径链一律经 tools\_publication-guard.ps1 校验（复用发布边界闸:
#     逐级 realpath / junction-symlink 拒绝 / UNC / .. 段 / 同名文件叶 / 盘根），
#     源树与已存在的目标树做全树 reparse 清查，源目标相同或互相嵌套一律拒绝；
#     Dest 的受保护根 = 源仓库 + 系统目录。注意: 用户主目录有意不列为受保护根——
#     数据迁移的源和目标本来就合法位于用户主目录内，安全性靠显式路径 + 默认 DryRun +
#     显式 -Apply 保证，而不是靠把主目录一刀切禁掉；
#   - 冲突策略: Dest 已存在且非空时默认拒绝（exit 4），显式 -MergeExisting 才允许并入；
#     robocopy /XO 保证绝不覆盖目标中更新的文件；对账发现任何期望文件缺失或不符 → exit 2，
#     绝不静默吞掉冲突；
#   - 清单策略: 复制前对 Source 全量建 SHA-256 预检清单（任一文件不可读即在任何写入前失败），
#     复制后逐文件对账（存在 + 大小 + 哈希）；-ManifestPath 可选地把结果清单写成 JSON
#     （仅 Apply 落盘; 路径经守卫校验, 已存在则拒绝覆盖; 只含相对路径/大小/哈希, 不含文件内容）。
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1 -Source D:\old-data -Dest D:\new-data
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1 -Source D:\old-data -Dest D:\new-data -Port 2333
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1 -Source D:\old-data -Dest D:\new-data -Apply
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1 -Source D:\old-data -Dest D:\new-data -Apply -MergeExisting -Port 2333 -ManifestPath D:\mig-manifest.json
#
# 退出码:
#   0 = 成功（DryRun 计划成立 / Apply 复制并对账通过）
#   2 = 操作失败（源清单构建失败 / robocopy 失败 / 对账不符 / 端口改写失败 / 结果清单写入失败）
#   3 = 用法或输入无效（缺 -Source/-Dest / 源不存在或不是目录或盘根 / 仓库或守卫库缺失 /
#       清单路径拼法非法）
#   4 = 歧义拒绝（源目标相同或互相嵌套 / 目标已存在且非空且未显式 -MergeExisting /
#       清单路径已存在——均发生在任何写入之前）
#   7 = PUBLICATION_DENIED（发布边界闸: reparse 链 / UNC / .. 段 / 同名文件叶 /
#       Dest 落在源仓库或系统目录内——拒绝时固定输出 PUBLICATION_DENIED，绝不 fallback）

param(
    [string]$Source = '',
    [string]$Dest = '',
    [int]$Port = 0,
    [switch]$Apply,
    [switch]$MergeExisting,
    [string]$ManifestPath = ''
)

$ErrorActionPreference = 'Stop'
$PackageStatus = 'LOCAL-ONLY / UNSIGNED DEVELOPMENT: local data-migration tool; no code signing, no network behavior, no diagnostics upload, no cloud/mobile/ads/credits; not commercially ready.'
$ManifestSchema = 'aster-aura.migration-manifest/v1'

# --- 1. 用法与输入校验（exit 3；任何路径解析与写入之前） ---
if ([string]::IsNullOrWhiteSpace($Source) -or [string]::IsNullOrWhiteSpace($Dest)) {
    Write-Host 'REFUSED(exit 3): 用法无效: -Source 与 -Dest 都必须显式传入（本脚本没有任何默认真实路径）'; exit 3
}
if (-not $PSScriptRoot) { Write-Host 'REFUSED(exit 3): 用法无效: 请以 -File 方式运行'; exit 3 }
$repo = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path -LiteralPath (Join-Path $repo 'AuraChat.sln') -PathType Leaf)) {
    Write-Host "REFUSED(exit 3): 仓库无效（未找到 AuraChat.sln）: $repo"; exit 3
}
$guardLib = Join-Path $PSScriptRoot '_publication-guard.ps1'
if (-not (Test-Path -LiteralPath $guardLib -PathType Leaf)) {
    Write-Host "REFUSED(exit 3): 发布边界闸库缺失: $guardLib"; exit 3
}

# --- 2. 发布边界闸（复用既有 _publication-guard；拒绝时固定 PUBLICATION_DENIED + exit 7） ---
. $guardLib
$pubProtected = @($repo, $env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

# 2a. Source: 只读输入的结构校验（realpath 链; 拒绝 reparse/UNC/../同名文件叶）
$srcResolved = Resolve-PubRealPath -Path $Source
if (-not $srcResolved.Ok) { Deny-Publication "Source $($srcResolved.Reason)" }
$srcReal = $srcResolved.RealPath
if (-not (Test-Path -LiteralPath $srcReal -PathType Container)) {
    Write-Host "REFUSED(exit 3): 源目录不存在或不是目录: $srcReal"; exit 3
}
if ($srcReal.TrimEnd('\') -eq [System.IO.Path]::GetPathRoot($srcReal).TrimEnd('\')) {
    Write-Host "REFUSED(exit 3): 源目录不能是盘根: $srcReal"; exit 3
}

# 2b. Dest: 写入目标走发布闸全链（受保护根见头注; 盘根由守卫内置拒绝）
$destReal = Assert-PublicationPath -Path $Dest -ProtectedRoots $pubProtected -What 'Dest'

# 2c. 源目标重叠拒绝（相同 / 互相嵌套都不允许; exit 4，发生在任何写入之前）
if (Test-PubEqualOrInside -Child $destReal -Ancestor $srcReal) {
    Write-Host "REFUSED(exit 4): Dest 等于或位于 Source 内: $destReal"; exit 4
}
if (Test-PubEqualOrInside -Child $srcReal -Ancestor $destReal) {
    Write-Host "REFUSED(exit 4): Source 等于或位于 Dest 内: $srcReal"; exit 4
}

# 2d. 全树 reparse 清查（robocopy 会跟随 junction; 源树 / 已存在的目标树里有 reparse 一律拒绝）
$srcBad = Test-PubTreeReparse -Root $srcReal
if ($srcBad.Count -gt 0) { Deny-Publication "Source 树存在 junction/symlink: $($srcBad -join '; ')" }
if (Test-Path -LiteralPath $destReal) {
    $destBad = Test-PubTreeReparse -Root $destReal
    if ($destBad.Count -gt 0) { Deny-Publication "Dest 树存在 junction/symlink: $($destBad -join '; ')" }
}

# 2e. -ManifestPath（可选）: 先校验后使用; 文件叶路径走"已验证父目录 + 纯叶名"边界比较（与 install-update 同法）
$manifestFull = ''
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $leaf = [System.IO.Path]::GetFileName($ManifestPath)
    $parent = [System.IO.Path]::GetDirectoryName($ManifestPath)
    if ([string]::IsNullOrWhiteSpace($leaf) -or [string]::IsNullOrWhiteSpace($parent) -or $leaf -match '[\\/]') {
        Write-Host "REFUSED(exit 3): 用法无效: -ManifestPath 必须是带目录的纯文件名: $ManifestPath"; exit 3
    }
    $parentReal = Assert-PublicationPath -Path $parent -ProtectedRoots $pubProtected -What 'ManifestPath 目录'
    $manifestFull = Join-Path $parentReal $leaf
    if (-not (Test-PubBoundaryInside -Child ([System.IO.Path]::GetFullPath($manifestFull)) -Ancestor $parentReal)) {
        Deny-Publication "清单路径越界: $manifestFull"
    }
    if (Test-Path -LiteralPath $manifestFull) {
        Write-Host "REFUSED(exit 4): 清单路径已存在（绝不静默覆盖）: $manifestFull"; exit 4
    }
}

# 2f. 目标冲突策略（exit 4，发生在任何写入之前）: Dest 已存在且非空 → 必须显式 -MergeExisting
$destExists = Test-Path -LiteralPath $destReal -PathType Container
$destEntries = @()
if ($destExists) { $destEntries = @(Get-ChildItem -LiteralPath $destReal -Force -ErrorAction SilentlyContinue) }
if ($destExists -and $destEntries.Count -gt 0 -and -not $MergeExisting) {
    Write-Host "REFUSED(exit 4): Dest 已存在且非空（拒绝混入既有内容; 确要并入请显式 -MergeExisting）: $destReal"; exit 4
}

$mode = 'dryrun'
if ($Apply) { $mode = 'apply' }
Write-Host "== migrate-legacy ($mode) =="
Write-Host "package : $PackageStatus"
Write-Host "源      : $srcReal"
Write-Host "目标    : $destReal"
if ($MergeExisting) { Write-Host '合并    : 显式 -MergeExisting（并入非空目标; robocopy /XO 保证不覆盖目标中更新的文件）' }

# =============== DryRun: 只给计划，不读内容、不建目录、不复制、不出数据统计 ===============
if (-not $Apply) {
    $state = '不存在（Apply 时将创建）'
    if ($destExists) { $state = "已存在（顶层 $($destEntries.Count) 个条目）" }
    Write-Host "目标状态 : $state"
    if ($Port -gt 0) { Write-Host "端口计划 : Apply 且目标存在 config.json 时把 web.port 改为 $Port" }
    Write-Host '（DryRun: 未读取任何文件内容，未创建目录，未复制任何数据; 加 -Apply 才会执行迁移）'
    exit 0
}

# =============== Apply: 显式触发的真实迁移 ===============
function Get-FileSha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($path)
    try { return ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' }
    finally { $fs.Close(); $sha.Dispose() }
}

# --- 3a. 预检清单: 全量枚举 + SHA-256（此步失败 → exit 2, 尚未发生任何写入） ---
$startedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
Write-Host '预检清单: 枚举并计算 Source 全部文件的 SHA-256 ...'
$expected = New-Object 'System.Collections.Generic.List[object]'
$expectedBytes = [long]0
try {
    $files = @(Get-ChildItem -LiteralPath $srcReal -Recurse -File -Force -ErrorAction Stop)
} catch {
    Write-Host "REFUSED(exit 2): Source 枚举失败（未发生任何写入）: $($_.Exception.Message)"; exit 2
}
$srcBase = $srcReal.TrimEnd('\')
foreach ($f in $files) {
    # TOCTOU 兜底: 预检阶段任何条目是 reparse point 都直接拒绝（2d 已清查过整树）
    if ($f.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        Deny-Publication "Source 树出现 junction/symlink（预检时）: $($f.FullName)"
    }
    $rel = $f.FullName.Substring($srcBase.Length + 1)
    try {
        $h = Get-FileSha256 $f.FullName
    } catch {
        Write-Host "REFUSED(exit 2): 源文件不可读（未发生任何写入）: $($f.FullName): $($_.Exception.Message)"; exit 2
    }
    $expected.Add([pscustomobject]@{
        path     = $rel
        size     = $f.Length
        mtimeUtc = $f.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        mtime    = $f.LastWriteTimeUtc
        sha256   = $h
    })
    $expectedBytes += $f.Length
}
Write-Host ("预检清单: {0} 个文件, {1} 字节" -f $expected.Count, $expectedBytes)

# --- 3b. robocopy 增量复制（不删除目标已有内容; /XO 不覆盖目标中更新的文件; 限制重试避免卡死） ---
# 直接 & 调用而不是 Start-Process —— 这台机器的 no_proxy/NO_PROXY 环境变量重名，Start-Process 会直接抛异常
& robocopy "$srcReal" "$destReal" /E /XO /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
# robocopy 0-7 都算成功（1=有复制，2=有多余文件，等等）
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-Host "REFUSED(exit 2): robocopy 失败，退出码 $rc（目标可能只有部分数据; 修复后重跑前先人工检查目标）"; exit 2
}

# --- 3c. 端口改写（可选; 仅 Apply; 失败按操作失败处理, 不静默跳过） ---
$portApplied = $null
$portError = ''
if ($Port -gt 0) {
    $cfgPath = Join-Path $destReal 'config.json'
    try {
        if (-not (Test-Path -LiteralPath $cfgPath -PathType Leaf)) { throw "目标没有 config.json: $cfgPath" }
        $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $cfg.web) { $cfg | Add-Member -NotePropertyName web -NotePropertyValue ([pscustomobject]@{}) }
        $cfg.web | Add-Member -NotePropertyName port -NotePropertyValue $Port -Force
        $json = $cfg | ConvertTo-Json -Depth 32
        # 不带 BOM 写回，省得 C# 那边 JsonNode.Parse 被 BOM 噎住
        [System.IO.File]::WriteAllText($cfgPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        $portApplied = $Port
        Write-Host "已把 web.port 改成 $Port"
        # 端口改写是本工具的有意写入: 把 config.json 的期望条目更新为改写后的实际内容，
        # 让对账反映"迁移 + 端口"的最终意图状态（否则重跑 MergeExisting 必然误报 dest-newer 冲突）。
        $di2 = Get-Item -LiteralPath $cfgPath -Force
        $h2 = Get-FileSha256 $cfgPath
        $rel2 = 'config.json'
        $found = $false
        foreach ($e in $expected) {
            if ($e.path -ieq $rel2) {
                $e.size = $di2.Length
                $e.mtime = $di2.LastWriteTimeUtc
                $e.mtimeUtc = $di2.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
                $e.sha256 = $h2
                $found = $true
                break
            }
        }
        if (-not $found) {
            $expected.Add([pscustomobject]@{
                path     = $rel2
                size     = $di2.Length
                mtimeUtc = $di2.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
                mtime    = $di2.LastWriteTimeUtc
                sha256   = $h2
            })
        }
    } catch {
        $portError = $_.Exception.Message
        Write-Host "端口改写失败（数据已复制，端口未动）: $portError"
    }
}

# --- 3d. 严格对账: 期望文件逐个核对 存在 + 大小 + SHA-256 ---
$verified = 0
$verifiedBytes = [long]0
$conflicts = New-Object 'System.Collections.Generic.List[object]'
$destBase = $destReal.TrimEnd('\')
$destFileSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
try {
    $destFiles = @(Get-ChildItem -LiteralPath $destReal -Recurse -File -Force -ErrorAction Stop)
} catch {
    Write-Host "REFUSED(exit 2): Dest 枚举失败（对账中止）: $($_.Exception.Message)"; exit 2
}
foreach ($df in $destFiles) { [void]$destFileSet.Add($df.FullName.Substring($destBase.Length + 1)) }
foreach ($e in $expected) {
    $dp = Join-Path $destReal $e.path
    if (-not (Test-Path -LiteralPath $dp -PathType Leaf)) {
        $conflicts.Add([pscustomobject]@{ path = $e.path; status = 'missing'; detail = 'robocopy 后目标缺失' }); continue
    }
    $di = Get-Item -LiteralPath $dp -Force
    $newer = $di.LastWriteTimeUtc -gt $e.mtime
    if ($di.Length -ne $e.size) {
        $status = 'conflict-mismatch'
        if ($newer) { $status = 'conflict-dest-newer' }
        $conflicts.Add([pscustomobject]@{ path = $e.path; status = $status; detail = "大小不符 src=$($e.size) dst=$($di.Length)" }); continue
    }
    try { $dh = Get-FileSha256 $dp } catch {
        $conflicts.Add([pscustomobject]@{ path = $e.path; status = 'conflict-mismatch'; detail = "目标文件不可读: $($_.Exception.Message)" }); continue
    }
    if ($dh -ine $e.sha256) {
        $status = 'conflict-mismatch'
        if ($newer) { $status = 'conflict-dest-newer' }
        $conflicts.Add([pscustomobject]@{ path = $e.path; status = $status; detail = 'SHA-256 不符' }); continue
    }
    $verified++; $verifiedBytes += $e.size
}
$expectedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($e in $expected) { [void]$expectedSet.Add($e.path) }
$extra = @($destFileSet | Where-Object { -not $expectedSet.Contains($_) })

# --- 3e. 结果清单落盘（仅 Apply; 可选 -ManifestPath; 失败 = exit 2） ---
$finishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$reconciliation = 'fail'
if ($conflicts.Count -eq 0) { $reconciliation = 'pass' }
if (-not [string]::IsNullOrWhiteSpace($manifestFull)) {
    $manifest = [ordered]@{
        schema           = $ManifestSchema
        packageStatus    = $PackageStatus
        mode             = 'apply'
        source           = $srcReal
        dest             = $destReal
        mergeExisting    = [bool]$MergeExisting
        robocopyExitCode = $rc
        portRequested    = $Port
        portApplied      = $portApplied
        portError        = $portError
        startedAtUtc     = $startedAt
        finishedAtUtc    = $finishedAt
        expected         = @{ files = $expected.Count; bytes = $expectedBytes }
        verified         = @{ files = $verified; bytes = $verifiedBytes }
        extraDestFiles   = $extra.Count
        conflicts        = $conflicts
        reconciliation   = $reconciliation
        files            = $expected
    }
    try {
        $json = $manifest | ConvertTo-Json -Depth 6
        [System.IO.File]::WriteAllText($manifestFull, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "结果清单: $manifestFull"
    } catch {
        Write-Host "REFUSED(exit 2): 结果清单写入失败: $($_.Exception.Message)"; exit 2
    }
}

# --- 3f. 终报 ---
if ($conflicts.Count -gt 0) {
    Write-Host ("对账失败: {0} 处冲突:" -f $conflicts.Count)
    $conflicts | Select-Object -First 10 | ForEach-Object {
        Write-Host ("  [{0}] {1}: {2}" -f $_.status, $_.path, $_.detail)
    }
    if ($conflicts.Count -gt 10) { Write-Host "  ... 其余 $($conflicts.Count - 10) 条见结果清单" }
    Write-Host '（绝不静默覆盖; 冲突未解决前不得对同一目标重复 Apply）'
    exit 2
}
if ($portError) {
    Write-Host "REFUSED(exit 2): 迁移已复制但对账后的端口改写未完成: $portError"; exit 2
}
Write-Host ("对账通过: {0}/{1} 个文件逐一核对（存在 + 大小 + SHA-256）; 目标多出 {2} 个非预期文件（保留不删）" -f $verified, $expected.Count, $extra.Count)
Write-Host '迁移完成。跑 AuraChat.exe 前记得确认端口没和旧插件（DSH 宿主里的 2332）冲突。'
exit 0
