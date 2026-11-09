# Aster Aura 本地安装 / 更新 / 回滚骨架（install-update）

入口是 `tools/install-update.ps1`，复用既有发布边界闸 `tools/_publication-guard.ps1`，
消费 `tools/release-bundle.ps1` 产出的发布束（见 [BUNDLING.md](BUNDLING.md)）。

## 状态声明

**LOCAL-ONLY / UNSIGNED DEVELOPMENT** —— 本骨架与其安装产物仅用于本机开发与测试：
无代码签名、无网络更新、无诊断上传、无云/移动/广告/积分行为；不声称商业可用
（与 `release-metadata.json` 的 `releaseStatus` 一致）。

## 用法

```powershell
# 安装 / 更新（<InstallRoot> 必须是仓库、用户主目录、系统目录、盘根之外的目录）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-update.ps1 `
  -Action Install -InstallRoot D:\aura-install -BundleDir D:\aura-bundle-1

# 回滚到上一版本（指针的 previousVersionId）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-update.ps1 `
  -Action Rollback -InstallRoot D:\aura-install

# 查看状态（只读，不写任何文件）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-update.ps1 `
  -Action Status -InstallRoot D:\aura-install
```

## 安装根布局

| 路径 | 说明 |
| --- | --- |
| `active.json` | 活动版本指针（原子切换：同卷 tmp 写入 + `File.Replace`/`File.Move`） |
| `install.json` | 安装器状态与动作历史（追加记录，含回滚记录） |
| `versions\<versionId>\` | 版本化束副本；**保留全部历史版本，从不删除既有版本** |
| `logs\` | 每次动作的日志 |

`versionId = <release-metadata.version>-<bundle.json.diffHash 前 8 位>`：同一束内容得到
同一 versionId，重复安装按歧义拒绝（exit 4），绝不静默覆盖。

## 安装流程（Install）

1. 用法/输入校验 → 2. 发布边界闸校验 `-InstallRoot`（**任何写入之前**）→
3. 安装根歧义检查 → 4. 束目录路径链 + 树内 junction 清查（只读输入）→
5. 束结构 / `verification.json` verdict=ok / `publish-manifest.sha256` 全量重算哈希 →
6. 复制到 `versions\.staging-*` 暂存 → 7. 暂存副本复核同一清单 →
8. 改名为 `versions\<versionId>` → 9. 原子切换 `active.json` 指针。

**回滚语义**：步骤 6–9 任一步失败，都会移除本次暂存/新版本目录并保持 `active.json`
原值（`File.Replace` 的原子性保证指针要么是旧值要么是新值，不存在中间态）；
此前安装的版本目录一律保留。

## 拒绝行为与退出码

| 退出码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `2` | 操作失败：清单哈希不符 / 束未通过验证（verdict≠ok）/ 复制或复核失败 / 指针切换失败（均已回滚，active 不变） |
| `3` | 用法或输入无效：动作未知 / 缺 `-BundleDir` / 束结构缺失 / 安装根或指针不存在 |
| `4` | 歧义拒绝：安装根已存在且不像安装根 / 束目录在安装根内 / 版本目录已存在（任何写入之前拒绝） |
| `7` | `PUBLICATION_DENIED`：`-InstallRoot` 落在仓库内部、用户主目录、系统目录、盘根，
        或 UNC / 含 `..` 段 / junction-symlink 路径链 / 父链缺失——固定输出
        `PUBLICATION_DENIED`，绝不 fallback |

受保护根与 [BUNDLING.md](BUNDLING.md) 一致（守卫库 `Get-PubProtectedRoots` + 源仓库）。
注意 `%TEMP%` 位于 `%USERPROFILE%` 内，**不能**作 `-InstallRoot`；请使用例如 `D:\aura-install`。

## 数据边界

- 脚本只写入显式传入且通过守卫校验的 `-InstallRoot`；束目录是**只读输入**。
- 验证一律使用外部合成目录与合成束，绝不读取/修改真实用户数据（`%APPDATA%\AuraChat`、
  聊天、记忆、媒体、凭据）。
- 无任何网络行为：不下载、不上传、不探测远端。
