# Changelog

All notable changes to Aster Aura will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### 计划中
- 云端同步服务
- Android 移动端完整验证
- B站生活能力（浏览、评论、发布）
- 视频通话功能
- 代码签名和正式安装器
- 崩溃诊断上报

---

## [0.4.0] - 2026-11 (Internal Build - 前端isOwner修复 + 生命状态页面)

### Added
- 生命状态前端展示页面（展示精力/情绪/生理状态面板）
- 生命状态实时 SSE 推送（状态变化通知前端）

### Fixed
- 修复前端 `isOwner` 判断逻辑，确保账号和角色权限正确隔离
- 生命状态面板数据刷新问题

### Technical
- 测试：198/198 通过

---

## [0.3.0] - 2026-11 (Internal Build - 广告/额度接口)

### Added
- `IAdProvider` 广告适配器接口（不侵入聊天内容）
  - `AdPlacement` 枚举：启动屏/设置页/兑换页
  - `RequestAdAsync` / `ReportImpressionAsync` / `ReportClickAsync`
- `IQuotaService` 额度服务接口
  - 余额查询 `GetBalanceAsync`
  - 流式扣费 `DeductAsync`
  - 消费记录导出

### Changed
- 商业模式从"完全闭源订阅"调整为"广告 + 可选额度 + 最终开源"

### Technical
- 接口设计遵循开源核心/商业服务分层原则

---

## [0.2.0] - 2026-11 (Internal Build - IModelProvider三模式)

### Added
- `IModelProvider` 统一模型接入接口
- `UserKeyModelProvider`：用户自带 API Key 模式
- `AuraQuotaModelProvider`：购买 Aster Aura 官方额度模式
- `LocalModelProvider`：连接 Ollama 等本地模型模式
- 设置页模型模式选择 UI

### Changed
- 模型配置从单一 apiKey 扩展为可选 Provider 模式

### Technical
- 保持对旧版 config.json 格式的向下兼容

---

## [0.1.1] - 2026-11 (Internal Build - 生命状态集成)

### Added
- `LifeStateService`：完整生理状态驱动系统
  - 精力（energy）、情绪正负（valence）、压力（stress）
  - 亲近感（affection）、饥饿（hunger）、口渴（thirst）、困倦（sleepiness）
- `life-state.json`：生命状态单一事实源
- `MoodService` 与 `LifeStateService` 集成（心情由生命状态派生）
- `ChatHub` 注入生命状态（每次对话携带当前状态）
- 时间驱动的状态漂移（精力随时间消耗，饥渴随时间增加）
- 事件反应机制（对话、喝水、休息等事件影响状态）
- 冷却机制（防止单一事件重复触发）

### Technical
- 测试：198/198 通过（新增生命状态相关测试）
- state.json 原子写入 + .bak 备份

---

## [0.1.0] - 2026-11-04/05 (Internal Build)

### Added - 核心功能

**账号与角色系统**
- 账号注册和登录（PBKDF2-SHA256密码）
- 陪伴角色创建（每账号一个）
- 旧版 scrypt 密码兼容
- 账本损坏检测
- V2 认证接口（`/api/v2/auth/register`、`/api/v2/auth/login`）
- 陪伴角色接口（`/api/v2/companions/current`）

**聊天与上下文**
- 流式对话（SSE）
- 消息自动合并（8秒窗口）
- 工具调用循环（19个工具，最多4轮）
- 上下文压缩（每6小时，保留最近30条）
- 历史回读工具（`check_history`）
- 时钟工具（`check_clock`）

**记忆与人格**
- 记忆召回和注入 system prompt
- 记忆观察和巩固
- 人格加载（Persona `.md` 文件）
- 记忆压缩边界处理

**心情系统**
- 三轴心情（多巴胺/皮质醇/血清素）
- 时间衰减机制
- 词表兜底
- 心情注入提示词

**多模态能力**
- 图片 base64 编码
- `image_url` 多模态消息（OpenAI 格式）
- 视觉模型支持（需配置支持视觉的模型）

**数据可靠性**
- `keepDays=0` 永久保留聊天文本
- 媒体文件独立保留策略
- 已读回执（`read.json`）
- 原子写入和损坏检测

**安全边界**
- Bearer token 认证
- loopback 绑定验证
- 匿名 API 返回 401
- 账号/角色隔离

**IM 基础能力**
- SSE 推送
- 已读回执（绿点 + 实时广播）
- 引用机制（整句和选区，双向）
- typing 状态
- 气泡时间戳（今天 HH:mm，跨天带日期）

**调试工具**
- `GET /api/debug/context`：查看当前上下文详情
- `POST /api/debug/compress`：手动触发上下文压缩

### Fixed - 已修复的Bug

- 修复心情时间戳 int 溢出导致三轴永不变化（`MoodService.cs:135`，改为 `long`）
- 修复语音环境探测路径错误（`VoiceService.cs`，加入 `Scripts\python.exe` 分支）
- 修复图片多模态链路缺失（整条链路重建）
- 修复工具调用循环未执行（17个工具从未被调用，新增 `ToolLoop.cs`）
- 修复自驱内容无法到达聊天层（`IChatHub.SelfDrivePushAsync` 接线）
- 修复上下文压缩失败时破坏原文（失败跳过、原文不动）

### Changed - 变更

- 聊天文本从"按天删除"改为"永久保留"
- `keepDays` 语义从"聊天保留天数"改为"媒体保留天数"
- 密码加密从 scrypt 升级到 PBKDF2-SHA256
- 旧 owner/guest 模型改为新账号/角色模型

### Technical

**测试覆盖**
- 186个测试全部通过
- 覆盖场景：账号、认证、数据可靠性、心情、工具、压缩、安全边界、IM

**架构**
- 7个项目模块（Core/Ai/Features/Server/App/Tests/TestRunner）
- 约 45,464 行 C# 代码
- .NET 9 + WPF + WebView2

**发布**
- framework-dependent win-x64 发布
- 34个发布文件（无 app-local 宿主文件）
- 安装/更新/回滚脚本（`tools/install-update.ps1`）

### Known Issues - 已知问题

- 中文路径下测试需要复制到 `C:\temp-ast\`
- 无代码签名，SmartScreen 警告
- 移动端真机可用性未完整验证
- 部分工具（`web_chat_history`）仅有 schema，无执行器

---

## [Legacy] - 2026-11-03 之前

### Node.js 插件版本

基于 `hsushjk/dsh-aurachat` 的 DSH 插件版本：
- 6,744 行 Node.js 代码
- 运行在 DeepSeek Harness 中
- 端口 2332
- 已迁移数据：557条聊天、8条记忆、3篇日记、10个表情、2个人设

---

## 版本号说明

- **0.x.x**：内测版本，未签名，本地运行
- **1.x.x**：正式版本（计划中）
- **2.x.x**：商业化版本（计划中）

当前状态：**内测版**（unsigned internal development build）
