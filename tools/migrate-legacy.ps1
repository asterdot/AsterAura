# 把旧 Node 版 AuraChat 的数据整体搬到新的 .NET 单体。
# 新旧数据目录结构一致（config.json / users.json / memories.json / diary.json /
# azone-social.json / mood.json / chats/ / stickers/ / media/ / personas/ ...），
# 所以本质就是一次递归复制；另外可选地把 web.port 改掉，避开旧插件占用的 2332。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1
#   powershell -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1 -Port 2333
#   powershell -ExecutionPolicy Bypass -File tools\migrate-legacy.ps1 -DryRun

param(
    [string]$Source = "$env:USERPROFILE\.dsh\plugins-data\aurachat",
    [string]$Dest = "$env:APPDATA\AuraChat\data",
    [int]$Port = 0,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "旧数据目录不存在：$Source" }
if ((Resolve-Path $Source).Path -eq (Resolve-Path $Dest -ErrorAction SilentlyContinue).Path) {
    throw "源和目标是同一个目录：$Source"
}

# 复制前先把要搬的东西数一遍，迁移后好对账
$count = {
    param($dir)
    $chat = 0
    $idx = Join-Path $dir 'chats'
    if (Test-Path $idx) {
        Get-ChildItem $idx -Directory | ForEach-Object {
            $segDir = $_
            Get-ChildItem $segDir.FullName -Filter 'seg-*.json' -File | ForEach-Object {
                $chat += (Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).messages.Count
            }
        }
    }
    $mem = 0
    $memFile = Join-Path $dir 'memories.json'
    if (Test-Path $memFile) {
        $data = Get-Content $memFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $mem = if ($data.items) { $data.items.Count } else { $data.Count }
    }
    $diary = 0
    $diaryFile = Join-Path $dir 'diary.json'
    if (Test-Path $diaryFile) {
        $d = Get-Content $diaryFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $diary = if ($d.history) { $d.history.Count } else { 0 }
    }
    $stickers = 0
    $st = Join-Path $dir 'stickers'
    if (Test-Path $st) { $stickers = (Get-ChildItem $st -File -Recurse).Count }
    [pscustomobject]@{ 聊天条数 = $chat; 记忆 = $mem; 日记 = $diary; 表情包 = $stickers }
}

Write-Host "源：$Source"
Write-Host "目标：$Dest"
$before = & $count $Source
$before | Format-Table -AutoSize | Out-String | Write-Host

if ($DryRun) { Write-Host '（DryRun，不实际复制）'; exit 0 }

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# 用 robocopy：保留时间戳、只增量复制、不删目标里已有的东西（新版本自己的 logs/ 等）
# 直接 & 调用而不是 Start-Process —— 这台机器的 no_proxy/NO_PROXY 环境变量重名，Start-Process 会直接抛异常
& robocopy "$Source" "$Dest" /E /XO /NFL /NDL /NJH /NJS /NP | Out-Null
# robocopy 0-7 都算成功（1=有复制，2=有多余文件，等等）
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败，退出码 $LASTEXITCODE" }

if ($Port -gt 0) {
    $cfgPath = Join-Path $Dest 'config.json'
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $cfg.web) { $cfg | Add-Member -NotePropertyName web -NotePropertyValue ([pscustomobject]@{}) }
        $cfg.web | Add-Member -NotePropertyName port -NotePropertyValue $Port -Force
        $json = $cfg | ConvertTo-Json -Depth 32
        # 不带 BOM 写回，省得 C# 那边 JsonNode.Parse 被 BOM 噎住
        [System.IO.File]::WriteAllText($cfgPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "已把 web.port 改成 $Port"
    }
}

$after = & $count $Dest
$after | Format-Table -AutoSize | Out-String | Write-Host
Write-Host '迁移完成。跑 AuraChat.exe 前记得确认端口没和旧插件（DSH 宿主里的 2332）冲突。'
