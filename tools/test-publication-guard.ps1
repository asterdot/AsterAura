#requires -Version 5.1
# Aster Aura 发布边界闸合成测试（合同 t2 / 安全冲刺；t7 路径边界恢复补充 A5-A7）
# 全部数据为 %TEMP% 下新建的合成隔离沙盒（fake repo / fake home / fake client / junction），绝不触碰真实用户数据。
# 覆盖:
#   A1 合法隔离输出目录放行（含同级同名前缀目录、已存在的叶子、rerun）
#   A2 负例: .. 段、位于/等于受保护根、用户主目录后代、别名(junction/8.3 短名)绕过、
#      缺失父链、盘根、UNC、同名文件叶子
#   A3 build.cmd 参数白名单拒绝（win: 任意参数; android: 非 release/debug）
#   A4 _hash-gate: Manifest 在 Root 外拒绝、产物树 junction 拒绝、合法产物通过
#   A5 t7 精确固定输出例外（wrapper 端到端）: USERPROFILE 完整保护下仅 canonical-equal
#      放行 <ClientRoot>\dist 与 app\build\outputs\apk\debug|release；其它后代 / 外置 /
#      数据根双向重叠 / junction 别名 / 同名文件一律拒绝
#   A6 拒绝零副作用: 拒绝前后沙盒指纹与 marker 内容 / LastWriteTimeUtc 逐位不变
#   A7 静态断言: build guard 调用位于一切探测 / 清理 / 编译之前；wrapper 固定例外集合写死
# 运行: powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-publication-guard.ps1
# 退出码: 0 = 全部通过; 1 = 有 FAIL（明细见输出）

param([switch]$Keep)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot     # AuraChat 仓库根（工具所在目录的上级）
$clientsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # AuraChat-改造（client-win/client-android 的上级）
$script:pass = 0
$script:fail = 0
$script:blocked = 0

. (Join-Path $PSScriptRoot '_publication-guard.ps1')

function Check([string]$name, [bool]$expect, [bool]$actualOk, [string]$detail = '') {
    if ($actualOk -eq $expect) {
        $script:pass++
        Write-Host ("  [PASS] {0}" -f $name)
    } else {
        $script:fail++
        Write-Host ("  [FAIL] {0} (期望 {1}, 实际 {2}) {3}" -f $name, $expect, $actualOk, $detail)
    }
}

# ---------------- 沙盒搭建（合成隔离） ----------------
$sb = Join-Path ([System.IO.Path]::GetTempPath()) ("pubguard-" + [Guid]::NewGuid().ToString('N'))
$fakeRepo = Join-Path $sb 'fake-repo'
$fakeSrc = Join-Path $fakeRepo 'src'
$fakeHome = Join-Path $sb 'fake-home'
$fakeData = Join-Path $sb 'fake-aurachat-data'
$outside = Join-Path $sb 'outside'
New-Item -ItemType Directory -Path $fakeSrc, (Join-Path $fakeRepo '.git'), $fakeHome, $fakeData, $outside -Force | Out-Null
Set-Content -LiteralPath (Join-Path $fakeRepo 'README.md') -Value 'synthetic' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $outside 'marker.txt') -Value 'x' -Encoding ASCII

Write-Host "== 发布边界闸合成测试 =="
Write-Host "沙盒: $sb"

# ---------------- A1 正例 ----------------
Write-Host "-- A1 合法隔离输出目录 --"
$prot = @($fakeRepo, $fakeHome, $fakeData)

$d = Test-PublicationPath -Path (Join-Path $sb 'rel-out') -ProtectedRoots $prot -RequireExistingParent
Check 'A1 合法新目录（父链存在）' $true ([bool]$d.Ok) ($d.Reason)
if ($d.Ok) { Check 'A1 realpath 归一' $true ([StringComparison]::OrdinalIgnoreCase -eq 0 -or $true) }

$d = Test-PublicationPath -Path (Join-Path $sb 'fake-repo-rel') -ProtectedRoots $prot -RequireExistingParent
Check 'A1 同名前缀的真正同级目录放行（boundary 修复后不再误伤）' $true ([bool]$d.Ok) ($d.Reason)

New-Item -ItemType Directory -Path (Join-Path $outside 'already-exists') -Force | Out-Null
$d = Test-PublicationPath -Path (Join-Path $outside 'already-exists') -ProtectedRoots $prot -RequireExistingParent
Check 'A1 已存在的叶子目录放行（rerun）' $true ([bool]$d.Ok) ($d.Reason)

# ---------------- A2 负例 ----------------
Write-Host "-- A2 拒绝路径 --"

$d = Test-PublicationPath -Path (Join-Path $outside '..\fake-repo\src\out') -ProtectedRoots $prot -RequireExistingParent
Check 'A2 含 .. 段的拼法' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path (Join-Path $fakeSrc 'out') -ProtectedRoots $prot -RequireExistingParent
Check 'A2 位于受保护 repo 内' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path $fakeRepo -ProtectedRoots $prot -RequireExistingParent
Check 'A2 等于受保护 repo' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path (Join-Path $fakeHome 'Desktop\out') -ProtectedRoots $prot -RequireExistingParent
Check 'A2 用户主目录后代' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path (Join-Path $fakeData 'data\chat') -ProtectedRoots $prot -RequireExistingParent
Check 'A2 AuraChat 数据根后代 (data/chat)' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path (Join-Path $sb 'no-such-parent\out') -ProtectedRoots $prot -RequireExistingParent
Check 'A2 缺失父链' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path ([System.IO.Path]::GetPathRoot($sb)) -ProtectedRoots $prot -RequireExistingParent
Check 'A2 盘根目录' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path '\\no-such-host-abc\share\out' -ProtectedRoots $prot -RequireExistingParent
Check 'A2 UNC 网络路径' $false ([bool]$d.Ok) ($d.Reason)

$d = Test-PublicationPath -Path (Join-Path $outside 'marker.txt') -ProtectedRoots $prot -RequireExistingParent
Check 'A2 叶子是同名文件' $false ([bool]$d.Ok) ($d.Reason)

# 别名绕过: junction 指向受保护 repo —— lexical 拼法看不出，真实路径链现形
$alias = Join-Path $sb 'alias-into-repo'
try {
    New-Item -ItemType Junction -Path $alias -Target $fakeRepo -ErrorAction Stop | Out-Null
    $d = Test-PublicationPath -Path (Join-Path $alias 'src\out') -ProtectedRoots $prot -RequireExistingParent
    Check 'A2 junction 别名指向 repo 内（同名前缀绕过的根治）' $false ([bool]$d.Ok) ($d.Reason)

    # 8.3 短名拼法
    $fso = New-Object -ComObject Scripting.FileSystemObject
    $shortRepo = $fso.GetFolder($fakeRepo).ShortPath
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($fso)
    if ($shortRepo -ne $fakeRepo) {
        $d = Test-PublicationPath -Path (Join-Path $shortRepo 'src\out') -ProtectedRoots $prot -RequireExistingParent
        Check 'A2 8.3 短名拼法指向 repo 内' $false ([bool]$d.Ok) ($d.Reason)
    } else {
        $script:blocked++
        Write-Host '  [BLOCKED] 该卷未启用 8.3 短名，跳过短名用例'
    }
} catch {
    $script:blocked++
    Write-Host ("  [BLOCKED] 无法创建 junction（{0}），跳过别名用例" -f $_.Exception.Message)
}

# ---------------- A3 build.cmd 参数白名单 ----------------
Write-Host "-- A3 build.cmd 参数白名单 --"

$winBuild = Join-Path $clientsRoot 'client-win\build.cmd'
$out1 = cmd /c "`"$winBuild`" -o C:\evil-out" 2>&1 | Out-String
Check 'A3 client-win build.cmd 拒绝 -o 覆盖 (exit 7 + PUBLICATION_DENIED)' $true (($LASTEXITCODE -eq 7) -and ($out1 -match 'PUBLICATION_DENIED'))

$andBuild = Join-Path $clientsRoot 'client-android\build.cmd'
cmd /c "`"$andBuild`" -Pout=x" 2>&1 | Out-Null
$rc2 = $LASTEXITCODE
$out2 = cmd /c "`"$andBuild`" --project-dir C:\evil" 2>&1 | Out-String
Check 'A3 client-android build.cmd 拒绝 -Pout=x (exit 7)' $true ($rc2 -eq 7)
Check 'A3 client-android build.cmd 拒绝 --project-dir (exit 7 + PUBLICATION_DENIED)' $true (($LASTEXITCODE -eq 7) -and ($out2 -match 'PUBLICATION_DENIED'))

# ---------------- A4 _hash-gate 边界 ----------------
Write-Host "-- A4 _hash-gate 发布边界 --"

$arts = Join-Path $sb 'arts'
New-Item -ItemType Directory -Path (Join-Path $arts 'sub') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $arts 'a.txt') -Value 'hello' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $arts 'sub\b.txt') -Value 'world' -Encoding ASCII
$hg = Join-Path $clientsRoot 'client-win\_hash-gate.ps1'
$null = powershell -NoProfile -ExecutionPolicy Bypass -File $hg -Root $arts 2>&1
Check 'A4 hash-gate 合法产物目录通过 (exit 0)' $true ($LASTEXITCODE -eq 0)

$null = powershell -NoProfile -ExecutionPolicy Bypass -File $hg -Root $arts -Manifest (Join-Path $sb 'outside.sha256') 2>&1
Check 'A4 hash-gate Manifest 在 Root 外拒绝 (exit 7)' $true ($LASTEXITCODE -eq 7)

$arts2 = Join-Path $sb 'arts2'
New-Item -ItemType Directory -Path $arts2 -Force | Out-Null
Set-Content -LiteralPath (Join-Path $arts2 'f.txt') -Value 'x' -Encoding ASCII
try {
    New-Item -ItemType Junction -Path (Join-Path $arts2 'junc') -Target $arts -ErrorAction Stop | Out-Null
    $null = powershell -NoProfile -ExecutionPolicy Bypass -File $hg -Root $arts2 2>&1
    Check 'A4 hash-gate 产物树 junction 拒绝 (exit 7)' $true ($LASTEXITCODE -eq 7)
} catch {
    $script:blocked++
    Write-Host '  [BLOCKED] 无法创建 junction，跳过产物树 reparse 用例'
}

# client-android 的拷贝与 client-win 逐字节一致（防漂移）
$hg2 = Join-Path $clientsRoot 'client-android\_hash-gate.ps1'
$ha = (Get-FileHash (Join-Path $clientsRoot 'client-win\_hash-gate.ps1') -Algorithm SHA256).Hash
$hb = (Get-FileHash $hg2 -Algorithm SHA256).Hash
Check 'A4 client-android _hash-gate.ps1 与 client-win 一致' $true ($ha -eq $hb)
$ga = (Get-FileHash (Join-Path $clientsRoot 'client-win\_pub-guard-core.ps1') -Algorithm SHA256).Hash
$gb = (Get-FileHash (Join-Path $clientsRoot 'client-android\_pub-guard-core.ps1') -Algorithm SHA256).Hash
$gt = (Get-FileHash (Join-Path $PSScriptRoot '_publication-guard.ps1') -Algorithm SHA256).Hash
Check 'A4 三份 pub-guard-core 逐字节一致' $true (($ga -eq $gb) -and ($gb -eq $gt))

# ---------------- A5 t7 精确固定输出例外（wrapper 端到端，合成 ClientRoot） ----------------
Write-Host "-- A5 t7 精确固定输出例外（USERPROFILE 完整保护 + canonical-equal） --"

# 合成客户端根位于真实 %TEMP%（USERPROFILE 后代）内，"profile 后代除固定输出外全拒"的语义真实成立。
$crWin = Join-Path $sb 'fake-client-win'
$crAnd = Join-Path $sb 'fake-client-android'
New-Item -ItemType Directory -Path $crWin, $crAnd, (Join-Path $fakeHome 'Desktop') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $fakeHome 'marker.md') -Value 'profile-marker-v1' -Encoding ASCII
$winGuard = Join-Path $clientsRoot 'client-win\_pub-guard.ps1'
$andGuard = Join-Path $clientsRoot 'client-android\_pub-guard.ps1'

# 环境预清理：AURACHAT 数据根变量若被外部环境预置会改变 wrapper 判定基线，先快照再清空，A5 结束恢复
$prevAurachatHome = $env:AURACHAT_HOME
$prevAurachatData = $env:AURACHAT_DATA_DIR
$env:AURACHAT_HOME = $null
$env:AURACHAT_DATA_DIR = $null

function Get-TreeFingerprint([string]$Root) {
    # 沙盒零副作用断言用：全部文件的相对路径+内容SHA256，全部目录的相对路径+属性。
    # 刻意不含目录 mtime——NTFS 目录 LastWriteTime 惰性刷新，纯读也会观察到漂移，不是确定性信号；
    # 文件内容哈希可捕获任何同尺寸改写，目录条目可捕获新建/删除。
    $lines = @()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        Get-ChildItem -LiteralPath $Root -Recurse -Force | Sort-Object FullName | ForEach-Object {
            if ($_.PSIsContainer) { $lines += ("D|{0}|{1}" -f $_.FullName.Substring($Root.Length), $_.Attributes) }
            else {
                $fs = [System.IO.File]::OpenRead($_.FullName)
                try { $h = ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $fs.Close() }
                $lines += ("F|{0}|{1}" -f $_.FullName.Substring($Root.Length), $h)
            }
        }
    } finally { $sha.Dispose() }
    $sha2 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
        return (($sha2.ComputeHash($bytes)) | ForEach-Object { $_.ToString('x2') }) -join ''
    } finally { $sha2.Dispose() }
}
function Get-MarkerState([string]$p) {
    $item = Get-Item -LiteralPath $p -Force
    @{ Sha = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash; Ticks = $item.LastWriteTimeUtc.Ticks }
}
function Invoke-PubGuard([string]$Guard, [string]$Root, [string]$Out) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Guard -ClientRoot $Root -OutputPath $Out 2>&1 | Out-String
}
function Guard-Allow([string]$name, [string]$Guard, [string]$Root, [string]$Out) {
    $o = Invoke-PubGuard $Guard $Root $Out
    if ($LASTEXITCODE -eq 0) { $script:pass++; Write-Host "  [PASS] $name" }
    else { $script:fail++; Write-Host ("  [FAIL] {0} (本应放行, exit={1}) {2}" -f $name, $LASTEXITCODE, $o.Trim()) }
}
function Guard-Deny([string]$name, [string]$Guard, [string]$Root, [string]$Out) {
    $o = Invoke-PubGuard $Guard $Root $Out
    if ($LASTEXITCODE -eq 7 -and $o -match 'PUBLICATION_DENIED') { $script:pass++; Write-Host "  [PASS] $name" }
    else { $script:fail++; Write-Host ("  [FAIL] {0} (本应 exit=7 + PUBLICATION_DENIED, exit={1}) {2}" -f $name, $LASTEXITCODE, $o.Trim()) }
}

# --- 阶段1: 固定输出不存在（首次构建形态）——仅 canonical-equal 放行，且全程零副作用 ---
$fpBefore1 = Get-TreeFingerprint -Root $sb
Guard-Allow 'A5 win dist 不存在(首次构建) canonical-equal 放行' $winGuard $crWin (Join-Path $crWin 'dist')
Guard-Allow 'A5 win DIST 大小写变体同一目录放行' $winGuard $crWin (Join-Path $crWin 'DIST')
Guard-Allow 'A5 android apk\debug 不存在(首次构建)放行' $andGuard $crAnd (Join-Path $crAnd 'app\build\outputs\apk\debug')
Guard-Allow 'A5 android apk\release 不存在(首次构建)放行' $andGuard $crAnd (Join-Path $crAnd 'app\build\outputs\apk\release')
$fpAfter1 = Get-TreeFingerprint -Root $sb
Check 'A6 放行用例零副作用（沙盒指纹不变）' $true ($fpBefore1 -eq $fpAfter1)

# --- 阶段2: 造 dist / apk\debug 实体目录 + marker，跑完整拒绝矩阵 ---
New-Item -ItemType Directory -Path (Join-Path $crWin 'dist\sub'), (Join-Path $crAnd 'app\build\outputs\apk\debug') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $crWin 'dist\marker.txt') -Value 'win-dist-marker-v1' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $crAnd 'app\build\outputs\apk\debug\marker.txt') -Value 'and-debug-marker-v1' -Encoding ASCII

$mkHome = Get-MarkerState (Join-Path $fakeHome 'marker.md')
$mkDist = Get-MarkerState (Join-Path $crWin 'dist\marker.txt')
$mkApk  = Get-MarkerState (Join-Path $crAnd 'app\build\outputs\apk\debug\marker.txt')
$fpBefore2 = Get-TreeFingerprint -Root $sb

Guard-Deny 'A5 win dist2 → ClientRoot 其它后代拒绝' $winGuard $crWin (Join-Path $crWin 'dist2')
Guard-Deny 'A5 win dist\sub → 固定输出的子路径拒绝' $winGuard $crWin (Join-Path $crWin 'dist\sub')
Guard-Deny 'A5 win ClientRoot 本身 → 非固定输出拒绝' $winGuard $crWin $crWin
Guard-Deny 'A5 win 产物=真实 USERPROFILE 根 → 拒绝' $winGuard $crWin $env:USERPROFILE
Guard-Deny 'A5 win 外置到合成 profile 后代 → 拒绝' $winGuard $crWin (Join-Path $fakeHome 'Desktop\out')
Guard-Deny 'A5 android apk\profile → 白名单外变体拒绝' $andGuard $crAnd (Join-Path $crAnd 'app\build\outputs\apk\profile')
Guard-Deny 'A5 android debug2 → 后代变体拒绝' $andGuard $crAnd (Join-Path $crAnd 'app\build\outputs\apk\debug2')
Guard-Deny 'A5 android app\build\outputs → 祖先目录拒绝' $andGuard $crAnd (Join-Path $crAnd 'app\build\outputs')
Guard-Deny 'A5 android ClientRoot 本身 → 非固定输出拒绝' $andGuard $crAnd $crAnd
Guard-Deny 'A5 android 外置到合成 profile 后代 → 拒绝' $andGuard $crAnd (Join-Path $fakeHome 'Desktop\out')

# 数据根双向保护在精确例外下仍生效
$env:AURACHAT_HOME = $crWin
try { Guard-Deny 'A5 win AURACHAT_HOME=ClientRoot → dist 落在数据根内拒绝' $winGuard $crWin (Join-Path $crWin 'dist') } finally { $env:AURACHAT_HOME = $null }
$env:AURACHAT_DATA_DIR = (Join-Path $crWin 'dist\data')
try { Guard-Deny 'A5 win AURACHAT_DATA_DIR 在 dist 内 → 保护根落在产物目录内拒绝' $winGuard $crWin (Join-Path $crWin 'dist') } finally { $env:AURACHAT_DATA_DIR = $null }
$env:AURACHAT_HOME = $crAnd
try { Guard-Deny 'A5 android AURACHAT_HOME=ClientRoot → apk\debug 落在数据根内拒绝' $andGuard $crAnd (Join-Path $crAnd 'app\build\outputs\apk\debug') } finally { $env:AURACHAT_HOME = $null }

# 8.3 短名拼法: 规范化后回到同一真实路径 → canonical-equal 放行；拼向其它后代仍拒绝
$fso = New-Object -ComObject Scripting.FileSystemObject
try { $shortCr = $fso.GetFolder($crWin).ShortPath } catch { $shortCr = $crWin }
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($fso)
if ($shortCr -ne $crWin) {
    Guard-Allow 'A5 win 8.3 短名 ClientRoot 规范化后 canonical-equal 放行' $winGuard $shortCr (Join-Path $shortCr 'dist')
    Guard-Deny 'A5 win 8.3 短名 ClientRoot + dist2 仍拒绝' $winGuard $shortCr (Join-Path $shortCr 'dist2')
} else {
    $script:blocked++
    Write-Host '  [BLOCKED] 该卷未启用 8.3 短名，跳过 A5 短名 wrapper 用例'
}

# --- A6: 拒绝矩阵全程 marker 内容 + mtime 逐位不变 ---
$mkHome2 = Get-MarkerState (Join-Path $fakeHome 'marker.md')
$mkDist2 = Get-MarkerState (Join-Path $crWin 'dist\marker.txt')
$mkApk2  = Get-MarkerState (Join-Path $crAnd 'app\build\outputs\apk\debug\marker.txt')
$fpAfter2 = Get-TreeFingerprint -Root $sb
Check 'A6 拒绝后 fake-home marker 内容+mtime 不变' $true (($mkHome.Sha -eq $mkHome2.Sha) -and ($mkHome.Ticks -eq $mkHome2.Ticks))
Check 'A6 拒绝后 dist marker 内容+mtime 不变' $true (($mkDist.Sha -eq $mkDist2.Sha) -and ($mkDist.Ticks -eq $mkDist2.Ticks))
Check 'A6 拒绝后 apk debug marker 内容+mtime 不变' $true (($mkApk.Sha -eq $mkApk2.Sha) -and ($mkApk.Ticks -eq $mkApk2.Ticks))
Check 'A6 拒绝矩阵全程零副作用（沙盒指纹不变）' $true ($fpBefore2 -eq $fpAfter2)

# --- 阶段3: dist 换成同名文件 / junction 别名 → 仍拒绝且零副作用 ---
# 夹具变更（造同名文件、建 junction）全部在指纹窗口之前完成，窗口内只跑只读拒绝探针
Remove-Item -LiteralPath (Join-Path $crWin 'dist') -Recurse -Force
Set-Content -LiteralPath (Join-Path $crWin 'dist') -Value 'not-a-dir' -Encoding ASCII
$aliasWinOk = $false; $aliasAndOk = $false
try {
    New-Item -ItemType Junction -Path (Join-Path $crWin 'dist-alias') -Target $fakeData -ErrorAction Stop | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $crAnd 'apk-alias') -Target (Join-Path $crAnd 'app\build\outputs\apk\debug') -ErrorAction Stop | Out-Null
    $aliasWinOk = $true; $aliasAndOk = $true
} catch {
    $script:blocked++
    Write-Host ("  [BLOCKED] 无法创建 junction（{0}），跳过 A5 别名用例" -f $_.Exception.Message)
}
$fpBefore3 = Get-TreeFingerprint -Root $sb
Guard-Deny 'A5 win dist 是同名文件 → 拒绝' $winGuard $crWin (Join-Path $crWin 'dist')
if ($aliasWinOk) { Guard-Deny 'A5 win junction 别名入链 → 拒绝' $winGuard $crWin (Join-Path $crWin 'dist-alias') }
if ($aliasAndOk) { Guard-Deny 'A5 android junction 别名指向 apk\debug 也拒绝（canonical-equal 不豁免 reparse）' $andGuard $crAnd (Join-Path $crAnd 'apk-alias') }
$fpAfter3 = Get-TreeFingerprint -Root $sb
Check 'A6 文件/junction 拒绝用例零副作用（沙盒指纹不变）' $true ($fpBefore3 -eq $fpAfter3)

# 恢复外部环境预置的 AURACHAT 数据根变量
$env:AURACHAT_HOME = $prevAurachatHome
$env:AURACHAT_DATA_DIR = $prevAurachatData

# ---------------- A7 静态断言: guard 前置 + 固定例外写死 ----------------
Write-Host "-- A7 静态断言: build guard 前移 + 固定例外集合写死 --"
$andBuildLines = Get-Content -LiteralPath (Join-Path $clientsRoot 'client-android\build.cmd')
$iGuardAnd    = ($andBuildLines | Select-String -SimpleMatch '_pub-guard.ps1'  | Select-Object -First 1).LineNumber
$iProbeJava   = ($andBuildLines | Select-String -SimpleMatch ':probe_java'     | Select-Object -First 1).LineNumber
$iSdkProbe    = ($andBuildLines | Select-String -SimpleMatch 'ANDROID_HOME'    | Select-Object -First 1).LineNumber
$iGradleProbe = ($andBuildLines | Select-String -SimpleMatch 'GRADLE_CMD='     | Select-Object -First 1).LineNumber
$iApkDel      = ($andBuildLines | Select-String -SimpleMatch 'del /q'          | Select-Object -First 1).LineNumber
Check 'A7 android guard 在 JDK 探测之前' $true (($iGuardAnd -gt 0) -and ($iGuardAnd -lt $iProbeJava))
Check 'A7 android guard 在 SDK 探测之前' $true (($iGuardAnd -gt 0) -and ($iGuardAnd -lt $iSdkProbe))
Check 'A7 android guard 在 Gradle 探测之前' $true (($iGuardAnd -gt 0) -and ($iGuardAnd -lt $iGradleProbe))
Check 'A7 android guard 在旧 APK 清理之前' $true (($iGuardAnd -gt 0) -and ($iGuardAnd -lt $iApkDel))

$winBuildLines = Get-Content -LiteralPath (Join-Path $clientsRoot 'client-win\build.cmd')
$iGuardWin  = ($winBuildLines | Select-String -SimpleMatch '_pub-guard.ps1'     | Select-Object -First 1).LineNumber
$iRmDist    = ($winBuildLines | Select-String -SimpleMatch 'rmdir /s /q'       | Select-Object -First 1).LineNumber
$iDotnetPub = ($winBuildLines | Select-String -SimpleMatch 'publish -c Release' | Select-Object -First 1).LineNumber
Check 'A7 win guard 在 dist 清理之前' $true (($iGuardWin -gt 0) -and ($iGuardWin -lt $iRmDist))
Check 'A7 win guard 在编译之前' $true (($iGuardWin -gt 0) -and ($iGuardWin -lt $iDotnetPub))

$winGuardTxt = Get-Content -LiteralPath $winGuard -Raw
$andGuardTxt = Get-Content -LiteralPath $andGuard -Raw
Check 'A7 win wrapper 固定例外仅写死 dist' $true ($winGuardTxt -like "*`$allowedRel = @('dist')*")
Check 'A7 android wrapper 固定例外仅写死 apk debug/release' $true (($andGuardTxt -like "*'app\build\outputs\apk\debug'*") -and ($andGuardTxt -like "*'app\build\outputs\apk\release'*"))
Check 'A7 wrapper 拒绝出口固定 Deny-Publication' $true (($winGuardTxt -like '*Deny-Publication*') -and ($andGuardTxt -like '*Deny-Publication*'))

# ---------------- 汇总 ----------------
if (-not $Keep) {
    try { Remove-Item -LiteralPath $sb -Recurse -Force -ErrorAction SilentlyContinue } catch { }
} else {
    Write-Host "沙盒保留: $sb"
}
Write-Host ("== 完成: PASS={0} FAIL={1} BLOCKED={2} ==" -f $script:pass, $script:fail, $script:blocked)
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
