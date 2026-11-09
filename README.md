# Aster Aura

> 一个拥有记忆、人格和生活感的 AI 伴侣桌面应用

Aster Aura（原名 AuraChat）是一个独立运行的桌面 AI 伴侣应用，她不仅能聊天，还拥有长期记忆、独立人格、情绪状态和自己的"生活"。

## ✨ 核心特性

- **长期记忆**：永久保留聊天历史，智能上下文压缩，不丢失任何对话细节
- **独立人格**：可加载和切换不同的人格设定，保持一致的性格特征
- **情绪系统**：基于多巴胺、皮质醇、血清素的三轴心情模型，随时间和事件动态变化
- **生命状态**：完整的生理状态驱动系统（精力/情绪/饥渴/困倦），由 LifeStateService 统一管理
- **生活感**：主动发起话题、写日记、浏览网络、生成表情包
- **多模态支持**：理解图片内容、语音合成、表情生成
- **工具调用**：19个内置工具，包括时钟、历史回读、网络浏览等
- **灵活模型接入**：支持自带API Key、购买官方额度、或连接本地模型三种方式
- **跨平台潜力**：桌面版（Windows）+ 移动端支持规划中

## 🚀 快速开始

### 系统要求

- Windows 10/11（x64）
- .NET 9 Runtime（framework-dependent发布）
- WebView2 Runtime（Windows 11 自带，Windows 10 需安装）

### 安装

**方式一：使用安装脚本**（推荐）

```powershell
# 下载 release bundle 后执行
.\tools\install-update.ps1 -BundleDir .\path\to\bundle -InstallRoot C:\AsterAura
```

**方式二：手动部署**

1. 确保已安装 .NET 9 Runtime
2. 解压发布包到目标目录（如 `C:\AsterAura`）
3. 运行 `AuraChat.exe`

### 首次运行

1. 启动后会自动打开桌面窗口
2. 点击"注册"创建账号
3. 登录后创建你的陪伴角色
4. 在设置页填写模型配置（选择以下三种方式之一）：

| 模式 | 说明 |
|------|------|
| **自带Key模式** | 填写 API 地址和 Key，完全本地控制 |
| **额度模式** | 购买 Aster Aura 官方额度，开箱即用 |
| **本地模式** | 连接 Ollama 等本地模型，完全离线 |

## 📁 数据目录

默认数据位置：`%APPDATA%\AuraChat\data`

可通过环境变量自定义：
- `AURACHAT_HOME`：应用根目录
- `AURACHAT_DATA_DIR`：数据存储目录

### 数据结构

```
data/
├── accounts.json          # 账号信息
├── companions.json        # 陪伴角色
├── chats/                # 聊天历史（永久保留）
├── media/                # 图片和语音文件
├── memories.json         # 记忆系统
├── mood.json             # 心情状态
├── life-state.json       # 生命状态（生理驱动系统）
├── compress.json         # 上下文压缩摘要
└── config.json           # 应用配置
```

## 🎭 账号与角色

### 账号系统

- 使用用户名和密码注册
- 密码使用 PBKDF2-SHA256 加密存储
- 支持旧版 scrypt 密码自动升级

### 陪伴角色

- 每个账号可创建一个陪伴角色
- 角色包含：人设、心情、生命状态、记忆、对话历史
- 支持加载自定义人格文件（`.md`格式）

## ⚙️ 配置

### API Key 配置

在设置页面配置模型服务：

```json
{
  "model": {
    "provider": "openai",
    "model": "gpt-4",
    "baseUrl": "https://api.openai.com/v1",
    "apiKey": "your-api-key-here"
  }
}
```

### 数据保留策略

```json
{
  "history": {
    "keepDays": 0  // 0 = 永不删除聊天文本
  }
}
```

## 💡 生命状态系统

Aster Aura 拥有一套完整的生理状态驱动系统（LifeStateService），包括：

| 状态 | 说明 |
|------|------|
| 精力（energy） | 影响回复积极程度和主动性 |
| 情绪（valence） | 正负情绪轴 |
| 压力（stress） | 影响对话风格 |
| 亲近感（affection） | 对用户的亲近程度 |
| 饥饿（hunger） | 生理需求，影响行为 |
| 口渴（thirst） | 生理需求，有冷却机制 |
| 困倦（sleepiness） | 影响回复长度和活跃度 |

生命状态由 `LifeStateService` 统一管理，是所有行为决策的**唯一事实源**，ChatHub 在每次对话时注入当前状态。

## 🔧 开发

### 构建

```bash
# 恢复依赖
dotnet restore

# 构建
dotnet build

# 运行测试（需要在非中文路径）
dotnet vstest src/AuraChat.Tests/bin/Release/net9.0/AuraChat.Tests.dll

# 发布
dotnet publish src/AuraChat.App/AuraChat.App.csproj -c Release -r win-x64 --no-self-contained
```

### 项目结构

```
src/
├── AuraChat.Core/       # 基础设施（存储、配置、认证、接口契约）
├── AuraChat.Ai/         # LLM 客户端、模型抽象、IModelProvider
├── AuraChat.Features/   # 业务逻辑（聊天、记忆、心情、生命状态）
├── AuraChat.Server/     # HTTP/SSE 服务和路由
├── AuraChat.App/        # WPF 桌面壳、WebView2、托盘
└── AuraChat.Tests/      # xUnit 测试套件（198个测试）
```

### 模型 Provider 接口

项目提供统一的 `IModelProvider` 接口，支持三种实现：

```csharp
// 用户自带 Key
IModelProvider userKey = new UserKeyModelProvider(endpoint, apiKey);

// Aster Aura 官方额度
IModelProvider quota = new AuraQuotaModelProvider(creditToken);

// 本地模型（Ollama等）
IModelProvider local = new LocalModelProvider(ollamaEndpoint);
```

### 广告与额度接口

```csharp
// 广告适配器接口（不侵入聊天内容）
public interface IAdProvider {
    Task<AdUnit?> RequestAdAsync(AdPlacement placement);
    Task ReportImpressionAsync(string adId);
}

// 额度服务接口
public interface IQuotaService {
    Task<decimal> GetBalanceAsync();
    Task<bool> DeductAsync(string modelId, int inputTokens, int outputTokens);
}
```

## 📊 测试

当前测试覆盖：**198个测试**

```bash
# 运行所有测试（在非中文路径）
dotnet vstest C:\temp-ast\AuraChat.Tests.dll
```

主要测试模块：
- 账号服务（AccountServiceTests）
- 认证流程（V2AuthFlowE2eTests）
- 数据可靠性（DataReliabilityTests）
- 心情系统（MoodTests）
- 生命状态（LifeStateTests）
- 工具调用（ToolLoopAndCompressTests）
- 安全边界（SecurityBoundaryTests）
- Model Provider（ModelProviderTests）

## 🛠️ 迁移旧数据

从旧版 Node.js 插件迁移数据：

```powershell
.\tools\migrate-legacy.ps1 `
  -Source "C:\Users\YourName\.dsh\plugins-data\aurachat" `
  -Dest "%APPDATA%\AuraChat\data" `
  -DryRun  # 先预览，确认后去掉 -DryRun 并加 -Apply
```

## ⚠️ 当前状态与已知限制

> **本版本为内测开发版（unsigned internal development build）**

| 状态 | 详情 |
|------|------|
| ⚠️ 代码签名 | 无签名，Windows SmartScreen 会警告 |
| ⚠️ 正式安装器 | 暂无，使用脚本手动部署 |
| ⚠️ 移动端 | Android 协议兼容，真机可用性待完整验证 |
| ⚠️ B站生活 | 规划中，尚未实现 |
| ⚠️ 云端同步 | 规划中，当前仅本地存储 |
| ✅ 核心对话 | 稳定可用 |
| ✅ 记忆系统 | 稳定可用 |
| ✅ 生命状态 | 已集成，稳定运行 |

## 💰 商业模式

Aster Aura 采用"广告 + 可选额度"的商业模式：

- **免费使用**：观看广告兑换免费额度，或自带 API Key
- **付费额度**：以接近官方的价格购买模型使用额度
- **最终开源**：核心客户端代码计划开源，商业服务可选

## 📝 版本历史

详见 [CHANGELOG.md](CHANGELOG.md)

## 📄 许可证

本项目采用 MIT License - 详见 [LICENSE](LICENSE) 文件

### 上游项目归属

本项目衍生自以下 MIT 许可项目：
- [hsushjk/dsh-aurachat](https://github.com/hsushjk/dsh-aurachat)
- [hsushjk/aurachat-client-win](https://github.com/hsushjk/aurachat-client-win)
- [hsushjk/aurachat-client-android](https://github.com/hsushjk/aurachat-client-android)

详见 [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md)

## 🤝 致谢

感谢以下项目的灵感和参考：
- hsushjk - AuraChat 家族原始创作者
- tdcq9594 - dsh-roost（设计思路）
- mashangshui - digital-life-core（概念参考）

## 📧 联系

- 项目主页：https://aura-evw2-re5x-u22g-dmhc.xn--xhq02y0gz4erv0bzy3e.top
- 问题反馈：通过 Issues 提交

---

**注意**：本项目处于积极开发中。当前版本适用于开发和内部测试。
