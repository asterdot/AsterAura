﻿﻿#requires -Version 5.1
# tools/test-migrate-legacy.ps1 — 行为验证脚本（仅用合成数据，不碰真实路径）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-migrate-legacy.ps1
# 全部测试目录在 %TEMP%\migtest-* 内创建，结束后自动清理。

$ErrorActionPreference = 'Stop'

# ---- 定位被测脚本 ----
if (-not $PSScriptRoot) { Write-Error '请以 -File 方式运行'; exit 1 }
$migScript = Join-Path $PSScriptRoot 'migrate-legacy.ps1'
if (-not (Test-Path -LiteralPath $migScript -PathType Leaf)) {
    Write-Error "被测脚本不存在: $migScript"; exit 1
}

# ---- 沙箱根（%TEMP% 下随机目录，结束后清理） ----
$sandboxRoot = Join-Path $env:TEMP ("migtest-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $sandboxRoot -Force | Out-Null

# ---- 测试计数 ----
$totalPass = 0
$totalFail = 0
$results = [System.Collections.Generic.List[object]]::new()

function Assert-Eq([string]$label, $got, $expected, [string]$detail='') {
    if ($got -eq $expected) {
        $script:totalPass++
        $script:results.Add([pscustomobject]@{ label=$label; result='PASS'; got=$got; expected=$expected; detail=$detail })
        Write-Host "[PASS] $label"
    } else {
        $script:totalFail++
        $script:results.Add([pscustomobject]@{ label=$label; result='FAIL'; got=$got; expected=$expected; detail=$detail })
        Write-Host "[FAIL] $label  (got=$got  expected=$expected  $detail)"
    }
}

function Assert-True([string]$label, [bool]$cond, [string]$detail='') {
    Assert-Eq $label $cond $true $detail
}

function Assert-False([string]$label, [bool]$cond, [string]$detail='') {
    Assert-Eq $label $cond $false $detail
}

# 运行被测脚本，返回退出码；$outVar 收标准输出
function Invoke-Mig {
    param([string[]]$ArgList, [ref]$OutText)
    # 子进程把 PUBLICATION_DENIED 等写到 stderr; 本脚本 ErrorActionPreference=Stop 会把它
    # 升级成终止性错误, 所以这里临时放宽为 Continue, 收集后再恢复。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $migScript @ArgList 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($OutText) { $OutText.Value = ($out | Out-String) }
    return $code
}

# 建一个带合成文件的源目录
function New-TestSrc([string]$path, [switch]$WithConfig) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    $bytes1 = [System.Text.Encoding]::UTF8.GetBytes("content-alpha`n")
    $bytes2 = [System.Text.Encoding]::UTF8.GetBytes("content-beta`n")
    [System.IO.File]::WriteAllBytes((Join-Path $path 'file_a.txt'), $bytes1)
    [System.IO.File]::WriteAllBytes((Join-Path $path 'file_b.txt'), $bytes2)
    New-Item -ItemType Directory -Path (Join-Path $path 'chats') -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $path 'chats\c1.txt'), $bytes1)
    if ($WithConfig) {
        $cfg = '{"web":{"port":2332}}'
        [System.IO.File]::WriteAllText((Join-Path $path 'config.json'), $cfg, (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs  = [System.IO.File]::OpenRead($path)
    try { return ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '' }
    finally { $fs.Close(); $sha.Dispose() }
}

Write-Host ""
Write-Host "===== test-migrate-legacy.ps1 ====="
Write-Host "被测脚本 : $migScript"
Write-Host "沙箱根   : $sandboxRoot"
Write-Host ""

# ============================================================
# T1 — DryRun 默认（不带 -Apply）: exit 0，且目标目录未被创建
# ============================================================
Write-Host "--- T1: DryRun ---"
$t1s = Join-Path $sandboxRoot 'T1\src'
$t1d = Join-Path $sandboxRoot 'T1\dst'
New-TestSrc -path $t1s
$code = Invoke-Mig -ArgList @('-Source', $t1s, '-Dest', $t1d)
Assert-Eq 'T1.exit-code-0' $code 0
Assert-False 'T1.dest-not-created' (Test-Path -LiteralPath $t1d)

# ============================================================
# T2 — 缺 -Source 或 -Dest → exit 3
# ============================================================
Write-Host "--- T2: 缺 -Source / -Dest ---"
$code2a = Invoke-Mig -ArgList @('-Dest', (Join-Path $sandboxRoot 'T2\dst'))
Assert-Eq 'T2a.no-source-exit-3' $code2a 3
$code2b = Invoke-Mig -ArgList @('-Source', (Join-Path $sandboxRoot 'T2\src'))
Assert-Eq 'T2b.no-dest-exit-3' $code2b 3

# ============================================================
# T3 — 源目录不存在 → exit 3
# ============================================================
Write-Host "--- T3: 源不存在 ---"
$code3 = Invoke-Mig -ArgList @('-Source', (Join-Path $sandboxRoot 'T3\nosuchdir'), '-Dest', (Join-Path $sandboxRoot 'T3\dst'))
Assert-Eq 'T3.src-missing-exit-3' $code3 3

# ============================================================
# T4 — 源 == 目标 → exit 4
# ============================================================
Write-Host "--- T4: 源等于目标 ---"
$t4s = Join-Path $sandboxRoot 'T4\data'
New-TestSrc -path $t4s
$code4 = Invoke-Mig -ArgList @('-Source', $t4s, '-Dest', $t4s)
Assert-Eq 'T4.src-eq-dest-exit-4' $code4 4

# ============================================================
# T5 — 目标位于源内 / 源位于目标内 → exit 4
# ============================================================
Write-Host "--- T5: 嵌套路径 ---"
$t5s = Join-Path $sandboxRoot 'T5\src'
$t5d = Join-Path $sandboxRoot 'T5\src\sub'
New-TestSrc -path $t5s
$code5a = Invoke-Mig -ArgList @('-Source', $t5s, '-Dest', $t5d)
Assert-Eq 'T5a.dest-inside-src-exit-4' $code5a 4
# 反向: 源位于目标内。源目录必须真实存在（脚本先校验源存在 -> exit 3, 再校验嵌套 -> exit 4）
$t5inner = Join-Path $sandboxRoot 'T5b\src\inner'
New-TestSrc -path $t5inner
$code5b = Invoke-Mig -ArgList @('-Source', $t5inner, '-Dest', (Join-Path $sandboxRoot 'T5b\src'))
Assert-Eq 'T5b.src-inside-dest-exit-4' $code5b 4

# ============================================================
# T6 — 目标已存在且非空，未传 -MergeExisting → exit 4，目标内容不变
# ============================================================
Write-Host "--- T6: 目标非空无 -MergeExisting ---"
$t6s = Join-Path $sandboxRoot 'T6\src'
$t6d = Join-Path $sandboxRoot 'T6\dst'
New-TestSrc -path $t6s
New-Item -ItemType Directory -Path $t6d -Force | Out-Null
$existingContent = 'original'
[System.IO.File]::WriteAllText((Join-Path $t6d 'existing.txt'), $existingContent, (New-Object System.Text.UTF8Encoding($false)))
$code6 = Invoke-Mig -ArgList @('-Source', $t6s, '-Dest', $t6d, '-Apply')
Assert-Eq 'T6.exit-4' $code6 4
$stillThere = [System.IO.File]::ReadAllText((Join-Path $t6d 'existing.txt'))
Assert-Eq 'T6.dest-content-unchanged' $stillThere $existingContent

# ============================================================
# T7 — Happy Apply（目标不存在）: exit 0，全部文件 SHA-256 一致，无 staging 残留
# ============================================================
Write-Host "--- T7: Happy Apply ---"
$t7s = Join-Path $sandboxRoot 'T7\src'
$t7d = Join-Path $sandboxRoot 'T7\dst'
New-TestSrc -path $t7s
$code7 = Invoke-Mig -ArgList @('-Source', $t7s, '-Dest', $t7d, '-Apply')
Assert-Eq 'T7.exit-0' $code7 0
Assert-True 'T7.dest-exists' (Test-Path -LiteralPath $t7d)
# SHA-256 每个文件比较
$allMatch = $true
foreach ($f in @('file_a.txt','file_b.txt','chats\c1.txt')) {
    $sp = Join-Path $t7s $f
    $dp = Join-Path $t7d $f
    if (-not (Test-Path -LiteralPath $dp)) { $allMatch = $false; break }
    if ((Get-Sha256 $sp) -ne (Get-Sha256 $dp)) { $allMatch = $false; break }
}
Assert-True 'T7.all-sha256-match' $allMatch
# 无 staging 残留（migstage- 后缀目录）
$stagingLeft = @(Get-ChildItem -LiteralPath (Split-Path $t7d) | Where-Object { $_.Name -like '*.migstage-*' })
Assert-Eq 'T7.no-staging-leftover' $stagingLeft.Count 0

# ============================================================
# T8 — -Port 改写：exit 0，config.json web.port=23456，无 BOM，JSON 可解析
# ============================================================
Write-Host "--- T8: -Port 改写 ---"
$t8s = Join-Path $sandboxRoot 'T8\src'
$t8d = Join-Path $sandboxRoot 'T8\dst'
New-TestSrc -path $t8s -WithConfig
$code8 = Invoke-Mig -ArgList @('-Source', $t8s, '-Dest', $t8d, '-Apply', '-Port', '23456')
Assert-Eq 'T8.exit-0' $code8 0
$cfgPath8 = Join-Path $t8d 'config.json'
Assert-True 'T8.config-exists' (Test-Path -LiteralPath $cfgPath8)
$cfgBytes8 = [System.IO.File]::ReadAllBytes($cfgPath8)
$hasBom8 = ($cfgBytes8.Length -ge 3 -and $cfgBytes8[0] -eq 0xEF -and $cfgBytes8[1] -eq 0xBB -and $cfgBytes8[2] -eq 0xBF)
Assert-False 'T8.config-no-bom' $hasBom8
try {
    $cfgText8 = [System.IO.File]::ReadAllText($cfgPath8, (New-Object System.Text.UTF8Encoding($false)))
    $cfgObj8  = $cfgText8 | ConvertFrom-Json
    Assert-Eq 'T8.port-value' $cfgObj8.web.port 23456
} catch {
    Assert-False 'T8.json-parse-failed' $true "ConvertFrom-Json threw: $_"
}

# ============================================================
# T9 — -MergeExisting：exit 0，源新文件并入，目标更新的文件不被覆盖，目标独有文件保留
# ============================================================
Write-Host "--- T9: -MergeExisting ---"
$t9s = Join-Path $sandboxRoot 'T9\src'
$t9d = Join-Path $sandboxRoot 'T9\dst'
New-TestSrc -path $t9s
# 额外源文件（dst 里没有）
[System.IO.File]::WriteAllText((Join-Path $t9s 'src_only.txt'), 'src-only', (New-Object System.Text.UTF8Encoding($false)))
# 准备目标：有和源重叠的文件（目标更新），和一个目标独有文件
New-Item -ItemType Directory -Path $t9d -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $t9d 'file_a.txt'), 'dest-newer-version', (New-Object System.Text.UTF8Encoding($false)))
Start-Sleep -Milliseconds 1100  # 确保目标时间戳确实更新（mtime 粒度 1s）
# 设置目标 file_a.txt 的写入时间为未来1秒
$fi9 = [System.IO.FileInfo]::new((Join-Path $t9d 'file_a.txt'))
$fi9.LastWriteTimeUtc = [datetime]::UtcNow.AddSeconds(5)
[System.IO.File]::WriteAllText((Join-Path $t9d 'dst_only.txt'), 'dst-only', (New-Object System.Text.UTF8Encoding($false)))
$dstOnlyExpected9 = 'dst-only'
$destNewerContent9 = 'dest-newer-version'
$code9 = Invoke-Mig -ArgList @('-Source', $t9s, '-Dest', $t9d, '-Apply', '-MergeExisting')
Assert-Eq 'T9.exit-0' $code9 0
# 目标独有文件仍在
Assert-True 'T9.dst-only-kept' (Test-Path -LiteralPath (Join-Path $t9d 'dst_only.txt'))
Assert-Eq 'T9.dst-only-content' ([System.IO.File]::ReadAllText((Join-Path $t9d 'dst_only.txt'))) $dstOnlyExpected9
# 目标更新版本未被源覆盖
Assert-Eq 'T9.newer-not-overwritten' ([System.IO.File]::ReadAllText((Join-Path $t9d 'file_a.txt'))) $destNewerContent9
# 源新文件已并入
Assert-True 'T9.src-only-merged' (Test-Path -LiteralPath (Join-Path $t9d 'src_only.txt'))

# ============================================================
# T10 — 源文件被独占锁定 → exit 2，且目标目录未被创建（预检阶段就失败）
# ============================================================
Write-Host "--- T10: 预检失败（源文件锁定）---"
$t10s = Join-Path $sandboxRoot 'T10\src'
$t10d = Join-Path $sandboxRoot 'T10\dst'
New-TestSrc -path $t10s
$lockedPath10 = Join-Path $t10s 'file_a.txt'
# 用 FileShare.None 独占锁定 file_a.txt
$lockStream10 = [System.IO.File]::Open($lockedPath10, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $code10 = Invoke-Mig -ArgList @('-Source', $t10s, '-Dest', $t10d, '-Apply')
    Assert-Eq 'T10.exit-2' $code10 2
    Assert-False 'T10.dest-not-created' (Test-Path -LiteralPath $t10d)
} finally {
    $lockStream10.Close()
}

# ============================================================
# T11 — 幂等重跑：Apply 后再次 Apply（带 -MergeExisting）→ exit 0，数据不丢，无 staging 残留
# ============================================================
Write-Host "--- T11: 幂等重跑 ---"
$t11s = Join-Path $sandboxRoot 'T11\src'
$t11d = Join-Path $sandboxRoot 'T11\dst'
New-TestSrc -path $t11s -WithConfig
# 第一次 Apply
$code11a = Invoke-Mig -ArgList @('-Source', $t11s, '-Dest', $t11d, '-Apply')
Assert-Eq 'T11.first-apply-exit-0' $code11a 0
# 第二次 Apply（带 -MergeExisting；内容不变，目标文件不比源新 → 应 superseded-newer 或 verified）
$code11b = Invoke-Mig -ArgList @('-Source', $t11s, '-Dest', $t11d, '-Apply', '-MergeExisting')
Assert-Eq 'T11.second-apply-exit-0' $code11b 0
# 数据完整
$t11ok = $true
foreach ($f in @('file_a.txt','file_b.txt','chats\c1.txt','config.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $t11d $f))) { $t11ok = $false; break }
}
Assert-True 'T11.data-intact' $t11ok
# staging 无残留（backup 可有）
$stgLeft11 = @(Get-ChildItem -LiteralPath (Split-Path $t11d) | Where-Object { $_.Name -like '*.migstage-*' })
Assert-Eq 'T11.no-staging-leftover' $stgLeft11.Count 0

# ============================================================
# T12 — PUBLICATION_DENIED: Dest 落在仓库受保护根内 → exit 7 + 输出含 PUBLICATION_DENIED
# ============================================================
Write-Host "--- T12: PUBLICATION_DENIED ---"
$t12s = Join-Path $sandboxRoot 'T12\src'
New-TestSrc -path $t12s
# 用仓库内路径（不存在的子目录即可；守卫在任何写入前就拒绝）
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$t12d = Join-Path $repoRoot '__migtest_pubdenied__'
$outText12 = ''
$code12 = Invoke-Mig -ArgList @('-Source', $t12s, '-Dest', $t12d) -OutText ([ref]$outText12)
Assert-Eq 'T12.exit-7' $code12 7
Assert-True 'T12.output-contains-PUBLICATION_DENIED' ($outText12 -like '*PUBLICATION_DENIED*') "output: $outText12"

# ============================================================
# T13 — -ManifestPath 已存在 → exit 4（发生在任何写入之前）
# ============================================================
Write-Host "--- T13: ManifestPath 已存在 ---"
$t13s = Join-Path $sandboxRoot 'T13\src'
$t13d = Join-Path $sandboxRoot 'T13\dst'
$t13m = Join-Path $sandboxRoot 'T13\mani.json'
New-TestSrc -path $t13s
New-Item -ItemType Directory -Path (Split-Path $t13m) -Force | Out-Null
[System.IO.File]::WriteAllText($t13m, '{}', (New-Object System.Text.UTF8Encoding($false)))
$code13 = Invoke-Mig -ArgList @('-Source', $t13s, '-Dest', $t13d, '-ManifestPath', $t13m)
Assert-Eq 'T13.exit-4' $code13 4
Assert-False 'T13.dest-not-created' (Test-Path -LiteralPath $t13d)

# ============================================================
# T14 — -ManifestPath 正常写入: exit 0，JSON 无 BOM，schema 正确，含文件相对路径与 sha256
# ============================================================
Write-Host "--- T14: ManifestPath 正常写入 ---"
$t14s = Join-Path $sandboxRoot 'T14\src'
$t14d = Join-Path $sandboxRoot 'T14\dst'
$t14m = Join-Path $sandboxRoot 'T14\manifest.json'
New-TestSrc -path $t14s
New-Item -ItemType Directory -Path (Split-Path $t14m) -Force | Out-Null
$code14 = Invoke-Mig -ArgList @('-Source', $t14s, '-Dest', $t14d, '-Apply', '-ManifestPath', $t14m)
Assert-Eq 'T14.exit-0' $code14 0
Assert-True 'T14.manifest-exists' (Test-Path -LiteralPath $t14m)
$mBytes14 = [System.IO.File]::ReadAllBytes($t14m)
$mBom14 = ($mBytes14.Length -ge 3 -and $mBytes14[0] -eq 0xEF -and $mBytes14[1] -eq 0xBB -and $mBytes14[2] -eq 0xBF)
Assert-False 'T14.manifest-no-bom' $mBom14
try {
    $mObj14 = [System.IO.File]::ReadAllText($t14m, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
    Assert-Eq 'T14.schema' $mObj14.schema 'aster-aura.migration-manifest/v1'
    $hasFiles14 = ($null -ne $mObj14.files -and $mObj14.files.Count -gt 0)
    Assert-True 'T14.has-files' $hasFiles14
    if ($hasFiles14) {
        $firstFile14 = $mObj14.files[0]
        Assert-True 'T14.file-has-path'   ($null -ne $firstFile14.path -and $firstFile14.path -ne '')
        Assert-True 'T14.file-has-sha256' ($null -ne $firstFile14.sha256 -and $firstFile14.sha256 -ne '')
    }
} catch {
    Assert-False 'T14.manifest-parse-failed' $true "threw: $_"
}

# ============================================================
# T15 — 目标为空目录 → Apply 能成功，数据落在目标本身（不是 dst\<staging名> 子目录）
# ============================================================
Write-Host "--- T15: 目标为空目录 ---"
$t15s = Join-Path $sandboxRoot 'T15\src'
$t15d = Join-Path $sandboxRoot 'T15\dst'
New-TestSrc -path $t15s
New-Item -ItemType Directory -Path $t15d -Force | Out-Null   # 空目录
$code15 = Invoke-Mig -ArgList @('-Source', $t15s, '-Dest', $t15d, '-Apply')
Assert-Eq 'T15.exit-0' $code15 0
# file_a.txt 必须直接在 dst，不能在 dst\<any subdir> 才是
Assert-True 'T15.file-at-dest-root' (Test-Path -LiteralPath (Join-Path $t15d 'file_a.txt'))
# 不能有 dst 下 migstage- 风格的子目录
$badSub15 = @(Get-ChildItem -LiteralPath $t15d -Directory | Where-Object { $_.Name -like '*.migstage-*' })
Assert-Eq 'T15.no-staging-subdir' $badSub15.Count 0

# ============================================================
# T16 — 脚本文件本身是合法 UTF-8，BOM 存在（满足 PS 5.1 中文解析需求）
# ============================================================
Write-Host "--- T16: 脚本编码自检 ---"
$scriptBytes16 = [System.IO.File]::ReadAllBytes($migScript)
$hasBom16 = ($scriptBytes16.Length -ge 3 -and $scriptBytes16[0] -eq 0xEF -and $scriptBytes16[1] -eq 0xBB -and $scriptBytes16[2] -eq 0xBF)
Assert-True 'T16.bom-present' $hasBom16
$encUtf8 = New-Object System.Text.UTF8Encoding($true, $true)  # throwOnInvalidBytes=true
try {
    $null = $encUtf8.GetString($scriptBytes16)
    Assert-True 'T16.valid-utf8' $true
} catch {
    Assert-False 'T16.valid-utf8' $true "UTF-8 strict decode threw: $_"
}

# ============================================================
# T17 — Bug A 回归: 目标被进程占用（当前工作目录落在 dest 内）
#   期望: exit 2（不是 exit 1）+ 输出打印 staging 路径 + dest 原始内容完好 + manifest 落盘
# ============================================================
Write-Host "--- T17: Bug A 回归（目标被占用）---"
$t17s = Join-Path $sandboxRoot 'T17\src'
$t17d = Join-Path $sandboxRoot 'T17\dst'
$t17m = Join-Path $sandboxRoot 'T17\manifest.json'
New-TestSrc -path $t17s
New-Item -ItemType Directory -Path $t17d -Force | Out-Null
$t17Original = 'occupied-dest-original'
[System.IO.File]::WriteAllText((Join-Path $t17d 'existing.txt'), $t17Original, (New-Object System.Text.UTF8Encoding($false)))
$outText17 = ''
# 把本进程的工作目录设到 dest 内: 子进程继承后 [System.IO.Directory]::Move(dest->backup)
# 会被系统以"正被另一进程使用"拒绝，从而走到 3h 的 catch 分支。
$prevCwd17 = [System.IO.Directory]::GetCurrentDirectory()
$code17 = $null
try {
    [System.IO.Directory]::SetCurrentDirectory($t17d)
    $code17 = Invoke-Mig -ArgList @('-Source', $t17s, '-Dest', $t17d, '-Apply', '-MergeExisting', '-ManifestPath', $t17m) -OutText ([ref]$outText17)
} finally {
    [System.IO.Directory]::SetCurrentDirectory($prevCwd17)
}
Assert-Eq 'T17.exit-2' $code17 2
Assert-True 'T17.output-contains-refused' ($outText17 -like '*REFUSED*') "output: $outText17"
Assert-True 'T17.output-contains-staging-path' ($outText17 -like '*staging 保留*') "output: $outText17"
# dest 原始内容完好（没有半份数据 / 没有被顶掉）
Assert-Eq 'T17.dest-content-intact' ([System.IO.File]::ReadAllText((Join-Path $t17d 'existing.txt'))) $t17Original
    Assert-False 'T17.dest-has-no-partial-source-data' (Test-Path -LiteralPath (Join-Path $t17d 'file_a.txt')) 'dest 不应出现源文件（Move 失败时不应换目标）'
# staging 保留在磁盘上，且路径被打印
$t17stg = @(Get-ChildItem -LiteralPath (Split-Path $t17d) -Directory | Where-Object { $_.Name -like '*.migstage-*' })
Assert-Eq 'T17.staging-preserved' $t17stg.Count 1
Assert-True 'T17.output-contains-staging-dirname' ($outText17 -like ("*" + $t17stg[0].Name + "*")) "output: $outText17"
# 审计链: manifest 落盘, swapState != swapped
Assert-True 'T17.manifest-written' (Test-Path -LiteralPath $t17m)
if (Test-Path -LiteralPath $t17m) {
    $m17 = [System.IO.File]::ReadAllText($t17m, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
    Assert-Eq 'T17.manifest-swapState-not-swapped' $m17.swapState 'not-swapped'
    Assert-Eq 'T17.manifest-reconciliation-fail' $m17.reconciliation 'fail'
}

# ============================================================
# T18 — Bug B 回归（方向一）: 源里 media 是目录、目标里 media 是文件
#   robocopy 合并返回 rc=4（位掩码"有文件/目录复制失败"）→ 必须 exit 2，
#   绝不能静默把目标里的文件顶成目录（数据丢失）
# ============================================================
Write-Host "--- T18: Bug B 回归（源目录 vs 目标文件）---"
$t18s = Join-Path $sandboxRoot 'T18\src'
$t18d = Join-Path $sandboxRoot 'T18\dst'
New-TestSrc -path $t18s
New-Item -ItemType Directory -Path (Join-Path $t18s 'media') -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $t18s 'media\in.txt'), 'src-media-content', (New-Object System.Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Path $t18d -Force | Out-Null
$t18Original = 'dest-media-is-a-file'
[System.IO.File]::WriteAllText((Join-Path $t18d 'media'), $t18Original, (New-Object System.Text.UTF8Encoding($false)))
$outText18 = ''
$code18 = Invoke-Mig -ArgList @('-Source', $t18s, '-Dest', $t18d, '-Apply', '-MergeExisting') -OutText ([ref]$outText18)
Assert-Eq 'T18.exit-2' $code18 2
# 目标里的原文件必须完好，且仍然"是文件"（没被目录顶掉）
Assert-True 'T18.dest-media-still-file' (Test-Path -LiteralPath (Join-Path $t18d 'media') -PathType Leaf)
Assert-Eq 'T18.dest-media-content-intact' ([System.IO.File]::ReadAllText((Join-Path $t18d 'media'))) $t18Original
# staging 保留并打印路径
$t18stg = @(Get-ChildItem -LiteralPath (Split-Path $t18d) -Directory | Where-Object { $_.Name -like '*.migstage-*' })
Assert-Eq 'T18.staging-preserved' $t18stg.Count 1
Assert-True 'T18.output-contains-staging-path' ($outText18 -like '*staging 保留*') "output: $outText18"

# ============================================================
# T19 — Bug B 回归（方向二）: 源里 media 是文件、目标里 media 是目录
# ============================================================
Write-Host "--- T19: Bug B 回归（源文件 vs 目标目录）---"
$t19s = Join-Path $sandboxRoot 'T19\src'
$t19d = Join-Path $sandboxRoot 'T19\dst'
New-TestSrc -path $t19s
[System.IO.File]::WriteAllText((Join-Path $t19s 'media'), 'src-media-is-a-file', (New-Object System.Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Path (Join-Path $t19d 'media') -Force | Out-Null
$t19Original = 'dest-media-content'
[System.IO.File]::WriteAllText((Join-Path $t19d 'media\in.txt'), $t19Original, (New-Object System.Text.UTF8Encoding($false)))
$outText19 = ''
$code19 = Invoke-Mig -ArgList @('-Source', $t19s, '-Dest', $t19d, '-Apply', '-MergeExisting') -OutText ([ref]$outText19)
Assert-Eq 'T19.exit-2' $code19 2
# 目标里的原目录内容必须完好，且仍然是"目录"
Assert-True 'T19.dest-media-still-dir' (Test-Path -LiteralPath (Join-Path $t19d 'media') -PathType Container)
Assert-Eq 'T19.dest-media-content-intact' ([System.IO.File]::ReadAllText((Join-Path $t19d 'media\in.txt'))) $t19Original
$t19stg = @(Get-ChildItem -LiteralPath (Split-Path $t19d) -Directory | Where-Object { $_.Name -like '*.migstage-*' })
Assert-Eq 'T19.staging-preserved' $t19stg.Count 1
Assert-True 'T19.output-contains-staging-path' ($outText19 -like '*staging 保留*') "output: $outText19"

# ============================================================
# T20 — Bug C 回归: superseded-newer 条目必须带 sourceSha256（源版本哈希）
# ============================================================
Write-Host "--- T20: Bug C 回归（superseded-newer 审计字段）---"
$t20s = Join-Path $sandboxRoot 'T20\src'
$t20d = Join-Path $sandboxRoot 'T20\dst'
$t20m = Join-Path $sandboxRoot 'T20\manifest.json'
New-TestSrc -path $t20s
New-Item -ItemType Directory -Path $t20d -Force | Out-Null
$t20SrcContent = 'src-version-content'
$t20DstContent = 'dest-newer-version-content'
[System.IO.File]::WriteAllText((Join-Path $t20s 'file_a.txt'), $t20SrcContent, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText((Join-Path $t20d 'file_a.txt'), $t20DstContent, (New-Object System.Text.UTF8Encoding($false)))
# 目标版本更新（mtime 在未来）→ 触发 superseded-newer 分支
$t20fi = [System.IO.FileInfo]::new((Join-Path $t20d 'file_a.txt'))
$t20fi.LastWriteTimeUtc = [datetime]::UtcNow.AddSeconds(5)
$code20 = Invoke-Mig -ArgList @('-Source', $t20s, '-Dest', $t20d, '-Apply', '-MergeExisting', '-ManifestPath', $t20m)
Assert-Eq 'T20.exit-0' $code20 0
Assert-True 'T20.manifest-exists' (Test-Path -LiteralPath $t20m)
if (Test-Path -LiteralPath $t20m) {
    $m20 = [System.IO.File]::ReadAllText($t20m, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
    $sup20 = @($m20.superseded)
    Assert-Eq 'T20.superseded-count' $sup20.Count 1
    if ($sup20.Count -ge 1) {
        $e20 = $sup20[0]
        Assert-Eq 'T20.superseded-path' $e20.path 'file_a.txt'
        Assert-Eq 'T20.superseded-status' $e20.status 'superseded-newer'
        Assert-True 'T20.superseded-has-sourceSha256' ($null -ne $e20.sourceSha256 -and $e20.sourceSha256 -ne '') "sourceSha256=$($e20.sourceSha256)"
        # sourceSha256 必须是"源版本"的哈希（而不是被保留的目标版本哈希）
        $t20SrcSha = Get-Sha256 (Join-Path $t20s 'file_a.txt')
        $t20DstSha = Get-Sha256 (Join-Path $t20d 'file_a.txt')
        Assert-Eq 'T20.sourceSha256-matches-source' $e20.sourceSha256 $t20SrcSha
        Assert-True 'T20.sourceSha256-differs-from-dest' ($e20.sourceSha256 -ne $t20DstSha)
    }
}

# ============================================================
# T21 — DryRun 回归: 输出不得包含任何条目数量统计
# ============================================================
Write-Host "--- T21: DryRun 输出收紧 ---"
$t21s = Join-Path $sandboxRoot 'T21\src'
$t21d = Join-Path $sandboxRoot 'T21\dst'
New-TestSrc -path $t21s
New-Item -ItemType Directory -Path $t21d -Force | Out-Null
# 放 3 个条目，若脚本打印计数就会泄露
foreach ($n in @('a.txt','b.txt','c.txt')) {
    [System.IO.File]::WriteAllText((Join-Path $t21d $n), $n, (New-Object System.Text.UTF8Encoding($false)))
}
$outText21 = ''
$code21 = Invoke-Mig -ArgList @('-Source', $t21s, '-Dest', $t21d, '-MergeExisting') -OutText ([ref]$outText21)
Assert-Eq 'T21.exit-0' $code21 0
Assert-True 'T21.no-entry-count-word' ($outText21 -notlike '*个条目*') "output: $outText21"
Assert-True 'T21.no-top-level-word' ($outText21 -notlike '*顶层*') "output: $outText21"
Assert-True 'T21.no-numeric-count-pattern' ($outText21 -notmatch '\d+\s*(个|条|项)') "output: $outText21"
Assert-True 'T21.still-reports-dest-exists' ($outText21 -like '*已存在*') "output: $outText21"
Assert-False 'T21.dest-not-created' (Test-Path -LiteralPath (Join-Path $t21d 'file_a.txt'))

# ============================================================
# 清理沙箱
# ============================================================
try { Remove-Item -LiteralPath $sandboxRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}

# ============================================================
# 汇总
# ============================================================
Write-Host ""
Write-Host "===== 汇总 ====="
foreach ($r in $results) {
    $mark = if ($r.result -eq 'PASS') { '[PASS]' } else { '[FAIL]' }
    if ($r.result -eq 'PASS') {
        Write-Host "$mark $($r.label)"
    } else {
        Write-Host "$mark $($r.label)  got=$($r.got)  expected=$($r.expected)  $($r.detail)"
    }
}
Write-Host ""
Write-Host "通过: $totalPass  失败: $totalFail  共: ($totalPass + $totalFail)"
Write-Host ("总计: {0}/{1}" -f $totalPass, ($totalPass + $totalFail))
if ($totalFail -eq 0) {
    Write-Host "ALL PASS"
    exit 0
} else {
    Write-Host "SOME FAIL"
    exit 1
}
