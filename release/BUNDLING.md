# Aster Aura 发布束（release bundle）

本目录存放发布元数据；打包入口是 `tools/release-bundle.ps1`，复用既有发布边界闸
`tools/_publication-guard.ps1`，与全流程验收脚本 `tools/release-verify.ps1` 分工互补。

## 用法

```powershell
# 在仓库根执行（<OutDir> 必须是用户主目录之外的全新目录）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-bundle.ps1 -RepoRoot . `
  -OutDir D:\aura-bundle-1 [-Configuration Release] [-RuntimeId win-x64] [-SmokePort 24334] `
  [-SkipBuild] [-SkipSmoke]
```

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `-RepoRoot` | 脚本上级目录 | 仓库根（须含 `AuraChat.sln` 与 `release/release-metadata.json`） |
| `-OutDir` | 必填 | 输出目录；必须全新或不存在，且不得指向受保护路径（见下） |
| `-Configuration` | `Release` | 构建/发布配置 |
| `-RuntimeId` | `win-x64` | 发布 RID（锁定框架依赖，`--self-contained false`） |
| `-SmokePort` | `24334` | 冒烟监听端口（写入束内隔离 config.json） |
| `-SkipBuild` | 关 | 跳过显式 `dotnet build`（publish 自身仍会构建） |
| `-SkipSmoke` | 关 | 跳过隔离冒烟运行 |

## 输出布局

`release-metadata.json`（产品/版本/运行时/包型/状态声明）、`bundle.json`（HEAD、
status/diff 哈希、SDK、布局守卫与 runtimeconfig 框架记录）、`identity.txt`、
`publish\app\`（FDD win-x64 产物）、`publish-manifest.sha256`、`manifest.json`、
`verification.json`、`summary.md`、`output-manifest.sha256`、`logs\`。

## 拒绝行为与退出码

**所有拒绝都发生在任何写入之前**（先校验后创建目录）：

| 退出码 | 含义 |
| --- | --- |
| `0` | 打包完成且无 FAIL |
| `2` | 有步骤 FAIL（build/publish/布局守卫/新鲜度/manifest/冒烟），详见 `verification.json` |
| `3` | 用法或输入无效（仓库/解决方案/发布元数据缺失、`-OutDir` 为空） |
| `4` | 歧义输出拒绝：`-OutDir` 已存在且非空，或 `-OutDir` 包含源仓库 |
| `7` | `PUBLICATION_DENIED`（发布边界闸）：仓库内部、用户主目录、系统目录、盘根、
         UNC 路径、含 `..` 段、junction/symlink 路径链、父链缺失——拒绝时固定输出
         `PUBLICATION_DENIED`，绝不 fallback |

受保护根 = 源仓库 ∪ 用户主目录 ∪ `AURACHAT_HOME`/`AURACHAT_DATA_DIR` ∪ Windows/程序目录
（与守卫库 `Get-PubProtectedRoots` 一致）。注意 `%TEMP%` 通常位于 `%USERPROFILE%` 内，
因此**不能**作为 `-OutDir`；请使用例如 `D:\aura-bundle-1`。

## 关键守卫

- **混合布局守卫**：FDD 产物中若出现 app-local `hostfxr.dll`/`hostpolicy.dll`/
  `coreclr.dll`/`System.Private.CoreLib.dll` 等（t8 混合部署启动失败根因），步骤
  `layout-guard` FAIL（退出码 2）；runtimeconfig 要求的共享框架会记录进 `bundle.json`。
- **新鲜度**：`publish\app\public|personas` 与源 `assets/` 逐字节一致，防止旧静态资源混入。
- **隔离冒烟**：使用束内 `iso-home`/`iso-data`（`AURACHAT_HOME`/`AURACHAT_DATA_DIR`），
  绝不触碰真实用户数据；运行后停止进程并清理环境变量。
- **可复现性**：构建确定性由 `Directory.Build.props`（`Deterministic` + `PathMap`）保证；
  每个束通过 `identity.txt`/`bundle.json` 绑定源码快照，束只在所记录快照下有意义。
