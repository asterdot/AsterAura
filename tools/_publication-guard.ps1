#requires -Version 5.1
# Aster Aura 发布边界闸（核心函数库，合同 t2 / 安全冲刺）
# 用途: 被 tools\release-verify.ps1 点源（client-win / client-android 各有同源拷贝 _pub-guard-core.ps1）。
# 职责: 在任何清理、构建、发布、manifest 写入之前，对输出路径做——
#   1) 规范化 + 已存在部分逐级 realpath（FSO 展开 8.3 短名、统一大小写）；
#   2) 任何一级 junction/symlink/reparse point 一律拒绝（同名前缀/别名绕过的根治）；
#   3) 权限不确定、枚举失败、父链缺失（可选强制）一律拒绝——不确定即拒绝；
#   4) 与受保护根（repo / 用户主目录 / AURACHAT_HOME / AURACHAT_DATA_DIR / 系统目录）做
#      带边界分隔符的包含比较：等于或位于其内都拒绝，不再有 lexical StartsWith 同名前缀误伤/漏放；
#   5) 拒绝时固定输出 PUBLICATION_DENIED 并以非零码 7 退出（Assert 入口），绝不 fallback。
# 只比较路径字符串、只读目录元数据（Attributes），不读取任何文件内容——不触碰真实用户数据。

# ---------------------------------------------------------------- 基础比较

function Test-PubBoundaryInside([string]$Child, [string]$Ancestor) {
    # Child 是否严格位于 Ancestor 之内（边界分隔符比较；相等不算 inside）。
    if ([string]::IsNullOrEmpty($Child) -or [string]::IsNullOrEmpty($Ancestor)) { return $false }
    $c = $Child.TrimEnd('\'); $a = $Ancestor.TrimEnd('\')
    if ($c.Length -le $a.Length) { return $false }
    return $c.StartsWith($a + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Test-PubEqualOrInside([string]$Child, [string]$Ancestor) {
    # Child 等于或位于 Ancestor 之内。
    if ([string]::IsNullOrEmpty($Child) -or [string]::IsNullOrEmpty($Ancestor)) { return $false }
    $c = $Child.TrimEnd('\'); $a = $Ancestor.TrimEnd('\')
    if ($c.Equals($a, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $c.StartsWith($a + '\', [StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------- realpath 解析

function Resolve-PubRealPath {
    # 解析真实路径：从盘根开始逐级下探，已存在部分取规范形态（FSO 展开 8.3 短名），
    # 不存在的尾部按字面拼接。任何一级是 reparse point / 同名文件 / 枚举失败 → 拒绝。
    # -RequireExistingParent: 除最后一级外的所有中间目录都必须已存在（发布 OutDir 用；客户端固定产物目录不启用）。
    # 返回 @{ Ok=bool; Reason=string; RealPath=string }。
    param([string]$Path, [switch]$RequireExistingParent)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @{ Ok = $false; Reason = '路径为空'; RealPath = '' }
    }
    # 先看原始拼法：.. 段一律拒绝（规范化会把 .. 收掉，但入口不接受这种拼法）
    if ($Path -match '(^|[\\/])\.\.([\\/]|$)') {
        return @{ Ok = $false; Reason = "路径含 .. 段: $Path"; RealPath = '' }
    }

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if (-not $root.StartsWith('\')) {
        if (-not (Test-Path -LiteralPath $root)) {
            return @{ Ok = $false; Reason = "盘符不存在: $root"; RealPath = '' }
        }
    }
    if ($root.StartsWith('\\')) {
        return @{ Ok = $false; Reason = "不接受 UNC 网络路径: $full"; RealPath = '' }
    }

    $rest = $full.Substring($root.Length).Trim('\')
    $parts = @()
    if ($rest.Length -gt 0) { $parts = $rest -split '\\' }

    # 盘根本身也要查（盘符不会是 reparse，但保持一致的确定性）
    try {
        $rootItem = Get-Item -LiteralPath $root -Force -ErrorAction Stop
        if ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            return @{ Ok = $false; Reason = "路径链存在 junction/symlink（盘根）: $root"; RealPath = '' }
        }
    } catch {
        return @{ Ok = $false; Reason = "盘根无法确认（权限不确定）: $root"; RealPath = '' }
    }

    $existing = $root
    $existingCount = 0
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $probe = $existing.TrimEnd('\') + '\' + $parts[$i]
        $probeExists = $false
        try { $probeExists = Test-Path -LiteralPath $probe } catch {
            return @{ Ok = $false; Reason = "路径链探测失败（权限不确定）: $probe"; RealPath = '' }
        }
        if (-not $probeExists) { break }
        try {
            $item = Get-Item -LiteralPath $probe -Force -ErrorAction Stop
        } catch {
            return @{ Ok = $false; Reason = "路径链无法读取（权限不确定）: $probe"; RealPath = '' }
        }
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            return @{ Ok = $false; Reason = "路径链存在 junction/symlink: $probe"; RealPath = '' }
        }
        if (-not $item.PSIsContainer) {
            return @{ Ok = $false; Reason = "路径中存在同名文件（必须是目录）: $probe"; RealPath = '' }
        }
        $existing = $probe
        $existingCount = $i + 1
    }

    if ($RequireExistingParent -and $parts.Count -gt 0 -and $existingCount -lt ($parts.Count - 1)) {
        return @{ Ok = $false; Reason = "父链不完整（中间目录不存在）: $existing"; RealPath = '' }
    }

    # 已存在部分经 FSO 规范化（展开 8.3 短名、还原真实大小写）——别名拼法在这里现出原形
    $fso = New-Object -ComObject Scripting.FileSystemObject
    try {
        $canonical = $fso.GetFolder($existing).Path
    } catch {
        return @{ Ok = $false; Reason = "已存在路径无法解析真实形态（权限不确定）: $existing"; RealPath = '' }
    }
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($fso)

    $real = $canonical.TrimEnd('\')
    if ($existingCount -lt $parts.Count) {
        $tail = ($parts[$existingCount..($parts.Count - 1)]) -join '\'
        if ($tail.Length -gt 0) { $real = $real + '\' + $tail }
    }
    return @{ Ok = $true; Reason = ''; RealPath = $real }
}

# ---------------------------------------------------------------- 决策入口

function Test-PublicationPath {
    # 综合判定一个"输出/产物"路径是否可接受。
    # 返回 @{ Ok=bool; Reason=string; RealPath=string }（不创建、不写任何东西）。
    param([string]$Path, [string[]]$ProtectedRoots = @(), [switch]$RequireExistingParent)

    $r = Resolve-PubRealPath -Path $Path -RequireExistingParent:$RequireExistingParent
    if (-not $r.Ok) { return $r }
    $real = $r.RealPath

    if ([string]::IsNullOrEmpty($real) -or $real.TrimEnd('\') -eq [System.IO.Path]::GetPathRoot($real).TrimEnd('\')) {
        return @{ Ok = $false; Reason = '输出不能是盘根目录'; RealPath = $real }
    }

    foreach ($p in $ProtectedRoots) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $pFull = ''
        try { $pFull = [System.IO.Path]::GetFullPath($p) } catch { continue }
        if ([string]::IsNullOrWhiteSpace($pFull)) { continue }
        if (Test-PubEqualOrInside -Child $real -Ancestor $pFull) {
            return @{ Ok = $false; Reason = "落在受保护目录内: $pFull"; RealPath = $real }
        }
    }
    return @{ Ok = $true; Reason = ''; RealPath = $real }
}

function Test-PubTreeReparse([string]$Root) {
    # 全树清查 reparse point（目录/文件都查）。返回违规路径列表（空 = 干净）。
    # 手写广度优先 + 已访问集合：PS 5.1 的 Get-ChildItem -Recurse 会跟随 junction，可能死循环。
    $bad = @()
    try {
        $ri = Get-Item -LiteralPath $Root -Force -ErrorAction Stop
        if ($ri.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return ,@($ri.FullName) }
    } catch { return ,@("$Root (无法读取: 权限不确定)") }

    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $queue = New-Object 'System.Collections.Generic.Queue[string]'
    $queue.Enqueue($Root)
    [void]$visited.Add($Root.TrimEnd('\'))
    while ($queue.Count -gt 0) {
        $dir = $queue.Dequeue()
        $entries = $null
        try { $entries = @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction Stop) } catch {
            $bad += "$dir (枚举失败: 权限不确定)"
            continue
        }
        foreach ($e in $entries) {
            if ($e.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { $bad += $e.FullName; continue }
            if ($e.PSIsContainer) {
                $key = $e.FullName.TrimEnd('\')
                if ($visited.Add($key)) { $queue.Enqueue($e.FullName) }
            }
        }
    }
    return ,$bad
}

function Deny-Publication([string]$Reason) {
    # 固定拒绝出口：PUBLICATION_DENIED + 非零码 7。不走异常（ErrorActionPreference 无关）。
    $msg = "PUBLICATION_DENIED: $Reason"
    [Console]::Out.WriteLine($msg)
    [Console]::Error.WriteLine($msg)
    exit 7
}

function Assert-PublicationPath {
    # Test-PublicationPath + 拒绝出口。通过时返回规范化后的 RealPath。
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$ProtectedRoots = @(),
        [switch]$RequireExistingParent,
        [string]$What = '输出目录'
    )
    $d = Test-PublicationPath -Path $Path -ProtectedRoots $ProtectedRoots -RequireExistingParent:$RequireExistingParent
    if (-not $d.Ok) { Deny-Publication "$What $($d.Reason)" }
    return $d.RealPath
}

function Get-PubProtectedRoots {
    # 常用受保护根集合：用户主目录 + AuraChat 数据根 + 系统目录。（repo 由调用方按需加入。）
    return @(
        $env:USERPROFILE, $env:AURACHAT_HOME, $env:AURACHAT_DATA_DIR,
        $env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}
