# Aster Aura Architecture

> 架构文档 - 描述系统的整体设计和关键组件

## 概述

Aster Aura 是一个基于 .NET 9 的桌面 AI 伴侣应用，采用分层架构设计，核心业务逻辑与表现层分离。

## 系统架构图

```
┌─────────────────────────────────────────────────────────┐
│                    AuraChat.App (WPF)                   │
│  ┌─────────────┐  ┌──────────────────────────────────┐ │
│  │ MainWindow  │  │       WebView2                   │ │
│  │   托盘      │  │   (assets/public/app.js)         │ │
│  └─────────────┘  └──────────────────────────────────┘ │
└────────────────────────────┬────────────────────────────┘
                             │ HTTP/SSE (127.0.0.1:2333)
┌────────────────────────────┴────────────────────────────┐
│              AuraChat.Server (Kestrel)                  │
│  ┌─────────────────────────────────────────────────┐   │
│  │  Routes: Auth / Chat / Memory / Config / Debug  │   │
│  └─────────────────────────────────────────────────┘   │
└────────────────────────────┬────────────────────────────┘
                             │
┌────────────────────────────┴────────────────────────────┐
│            AuraChat.Features (业务逻辑)                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐             │
│  │  Chat    │  │  Memory  │  │  Life    │             │
│  │  Hub     │  │  Service │  │  (Mood)  │             │
│  └──────────┘  └──────────┘  └──────────┘             │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐             │
│  │  Media   │  │  Self    │  │  Guard   │             │
│  │  (Voice/ │  │  Drive   │  │          │             │
│  │  Sticker)│  │          │  │          │             │
│  └──────────┘  └──────────┘  └──────────┘             │
└────────────────────────────┬────────────────────────────┘
                             │
┌────────────────────────────┴────────────────────────────┐
│              AuraChat.Ai (LLM 抽象层)                    │
│  ┌─────────────────────────────────────────────────┐   │
│  │  LlmClient (HTTP + SSE + Tools)                 │   │
│  └─────────────────────────────────────────────────┘   │
└────────────────────────────┬────────────────────────────┘
                             │
┌────────────────────────────┴────────────────────────────┐
│         AuraChat.Core (基础设施和接口契约)                │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐             │
│  │  Config  │  │  Data    │  │  Auth    │             │
│  │  Store   │  │  Store   │  │  Session │             │
│  └──────────┘  └──────────┘  └──────────┘             │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐             │
│  │  Logging │  │ Contracts│  │  Utils   │             │
│  │          │  │(Interface│  │          │             │
│  └──────────┘  └──────────┘  └──────────┘             │
└─────────────────────────────────────────────────────────┘
```

## 项目模块

### 1. AuraChat.Core

**职责**：提供基础设施和核心抽象

**关键组件**：
- `ConfigStore`：配置管理（config.json）
- `DataStore`：数据目录和文件路径管理
- `AccountService`：账号注册、登录、会话管理
- `JsonUtil`：JSON 序列化/反序列化工具
- `Contracts`：接口定义（IChatHub, IMemoryService, IHistoryStore 等）

**依赖**：无内部依赖（仅依赖 .NET 标准库）

### 2. AuraChat.Ai

**职责**：LLM 客户端和模型抽象

**关键组件**：
- `LlmClient`：HTTP 客户端，支持流式和工具调用
- `ILlmClient`：模型接口抽象
- `LlmOptions`：模型配置（temperature, maxTokens, tools 等）

**依赖**：AuraChat.Core

**支持特性**：
- OpenAI 兼容 API
- 流式响应（SSE）
- 工具调用（function calling）
- 多模态消息（text + image_url）

### 3. AuraChat.Features

**职责**：核心业务逻辑

**子模块**：

#### Chat
- `ChatHub`：聊天中枢，协调对话流程
- `HistoryStore`：聊天历史存储（永久保留）
- `ContextCompressor`：上下文压缩（每6小时）
- `ToolLoop`：工具调用循环（最多4轮）
- `ToolSchemas`：19个工具定义

#### Memory
- `MemoryService`：记忆管理（观察、巩固、召回）
- `RecallService`：记忆召回和注入

#### Life
- `MoodService`：三轴心情（多巴胺/皮质醇/血清素）
- `SelfDriveService`：主动行为（冲浪、推送）
- `DiaryService`：日记功能

#### Media
- `VoiceService`：语音合成（edge-tts）
- `StickerService`：表情包管理
- `ImageGenService`：图片生成

#### Guard
- `GuardService`：内容安全过滤

**依赖**：AuraChat.Core, AuraChat.Ai

### 4. AuraChat.Server

**职责**：HTTP/SSE 服务

**关键组件**：
- `AppHost`：应用启动和服务装配
- `RouteModule`：路由注册（反射自动发现）

**路由模块**（63个端点）：
- `AuthRoutes`：账号注册、登录（/api/v2/auth/*）
- `CompanionRoutes`：陪伴角色（/api/v2/companions/*）
- `ChatRoutes`：聊天、工具、已读（/api/chat, /api/stream, /api/read）
- `MemoryRoutes`：记忆查询和管理（/api/memories/*）
- `ConfigRoutes`：配置读写（/api/config）
- `DebugRoutes`：上下文调试（/api/debug/*）
- 其他：models, personas, stickers, voice 等

**依赖**：AuraChat.Core, AuraChat.Features

### 5. AuraChat.App

**职责**：WPF 桌面壳

**关键组件**：
- `MainWindow`：主窗口（WebView2 容器）
- `TrayManager`：系统托盘（WinForms）
- `Program`：入口和启动

**特性**：
- 监听 127.0.0.1:2333
- 加载静态资源（assets/public/）
- 支持 headless 模式（--headless）
- 常驻和自启动

**依赖**：AuraChat.Server

### 6. AuraChat.Tests

**职责**：自动化测试

**测试文件**（15个）：
- AccountServiceTests
- ApiProtocolE2eTests
- DataReliabilityTests
- ImageContextTests
- KeepDaysTests
- LlmContentParsingTests
- LoginFirstUiContractTests
- MemoryCompressBoundaryTests
- MoodTests
- SecurityBoundaryTests
- ToolLoopAndCompressTests
- V2AuthFlowE2eTests
- V3ReadTypingDebugTests
- VoicePyEnvTests
- StickerMd5AndTimeTests

**覆盖率**：186个测试方法，全部通过

**依赖**：所有被测项目 + xUnit

## 数据流

### 用户发送消息

```
用户输入
  ↓
WebView2 (app.js)
  ↓
POST /api/chat
  ↓
ChatRoutes.PostMessage
  ↓
ChatHub.RespondAsync
  ↓
1. 加载人设、心情、记忆
2. 构建 system prompt
3. 调用 ToolLoop
   ├─ LlmClient.AskStreamAsync
   ├─ 工具调用 (最多4轮)
   └─ 回落纯文本
4. 保存聊天记录
5. SSE 推送给前端
```

### 上下文压缩

```
ContextCompressor (每6小时)
  ↓
1. 读取聊天历史
2. 选取保留窗口外的旧文本 (≤40条)
3. 调用模型生成摘要
4. 保存到 compress.json
5. 原始聊天记录不变
  ↓
ChatHub.BuildSystemPrompt
  ↓
注入压缩摘要到 system prompt
```

### 记忆召回

```
ChatHub.RespondAsync
  ↓
RecallService.RecallAsync
  ↓
1. 从 memories.json 读取记忆
2. 按权重和相关性排序
3. 选取前 3-5 条
  ↓
注入到 system prompt
```

## 数据存储

### 目录结构

```
%APPDATA%\AuraChat\data\
├── accounts.json          # 账号信息 (PBKDF2-SHA256)
├── companions.json        # 陪伴角色
├── sessions.json          # 会话 token
├── config.json            # 应用配置
├── chats/
│   ├── <chatId>/
│   │   ├── segments/      # 聊天分片 (永久保留)
│   │   └── index.json     # 索引
├── media/                 # 图片和语音 (按 keepDays 清理)
├── memories.json          # 记忆系统
├── mood.json              # 心情状态
├── compress.json          # 上下文压缩摘要
├── diary.json             # 日记
├── stickers.json          # 表情包
└── read.json              # 已读回执
```

### 关键数据约定

**聊天历史**：
- 文本永久保留（segments + index）
- keepDays=0：媒体也永久保留
- 上下文压缩不删除原文

**记忆**：
- 只增量写入，不覆盖
- 权重衰减机制
- 召回硬上限（3-5条）

**心情**：
- 三轴：dopamine, cortisol, serotonin
- 时间衰减（long 类型时间戳，避免 int 溢出）
- 词表兜底（判断失败时使用关键词）

## 安全设计

### 认证

- Bearer token（JWT 或自定义格式）
- 账号/角色隔离
- 会话过期机制

### 边界

- loopback 绑定（127.0.0.1）
- 匿名 API 返回 401
- 旧 `/api/setup` 默认关闭，需显式环境变量

### 数据

- 密码 PBKDF2-SHA256 加密
- 旧 scrypt 自动升级
- 文件原子写入（.tmp + rename）
- 损坏检测（JSON 解析失败）

## 扩展点

### 未来架构演进

**Phase 2：模块分层**（开源准备）

```
┌────────────────────────────────────┐
│   AsterAura.Core (开源核心)         │
│   • 本地聊天引擎                    │
│   • 记忆系统                        │
│   • 人格引擎                        │
└────────────────────────────────────┘
              ↓
┌────────────────────────────────────┐
│ AsterAura.Services (可选商业服务)   │
│   • 广告适配器 (IAdProvider)        │
│   • 额度账户 (CreditService)        │
│   • 模型中转 (ModelProxy)           │
└────────────────────────────────────┘
```

**Provider 统一接口**：
- `SelfHostedProvider`（用户自带 Key）
- `CreditProvider`（购买额度）
- `LocalModelProvider`（Ollama 等本地模型）

## 性能考虑

### 当前优化

- 消息合并（8秒窗口）
- 上下文压缩（减少 token 消耗）
- 媒体文件分离存储
- SSE 流式响应（降低首字延迟）

### 已知限制

- 大量聊天记录时的读取放大（未优化分页）
- 上下文压缩需要额外模型调用
- 中文路径下测试失败（需要 C:\temp-ast 绕过）

## 部署

### 发布形态

- framework-dependent (需要 .NET 9 Runtime)
- win-x64 目标
- 34个文件（约 10-20 MB）

### 安装流程

1. 检查 .NET 9 Runtime
2. 复制文件到目标目录
3. 配置数据目录（环境变量或默认）
4. 首次启动（注册账号）

### 更新流程

- 安装脚本支持（install-update.ps1）
- 保留上一版本（rollback）
- staging + 原子切换
- 配置和数据保留

---

## 参考

- [代码规模统计](../foundation/BASELINE-2026-11-07.md)
- [数据可靠性审计](../foundation/DATA-RELIABILITY-AUDIT.md)
- [安全调用点审计](../foundation/SECURITY-CALLSITE-AUDIT.md)

---

Last Updated: 2026-11-08
