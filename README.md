# wechat-daemon

`wechat-daemon` 是 Windows 端 WeChat 后台服务。它通过 UI Automation 读取当前可见的 WeChat 聊天窗口，记录可见消息，修复重叠 viewport，并通过本地 API 提供结构化 WeChat 上下文。

它不读取 WeChat 数据库。事实来源是用户当前能看到的 WeChat UI。

## 为什么需要

直接读取 WeChat 数据库有账号风险，也依赖私有实现。可见 UI 采集更慢，但它和用户看到的内容一致，也不依赖 WeChat 内部协议。

核心原则：

```text
漏掉上下文，比写入错误记忆更可接受。
```

## 功能

- 查找当前 active WeChat window。
- 通过 Windows UI Automation 读取聊天标题和可见消息。
- 在文本信息不足时，用 screen capture 判断消息方向。
- 按 contact 保存本地 records。
- 将重叠 viewport 合并成稳定 message islands。
- 将 timestamp、call、media、system、gap 保存为 metadata records。
- 提供 WebSocket tools，用于实时 WeChat automation。
- 提供 HTTP endpoints，用于读取 contact records、处理 review jobs、写回语义结果。
- 在 destructive write 或 semantic write 前保存 snapshot。

## 系统模型

```text
visible WeChat UI
  -> Windows UI Automation
  -> screen capture
  -> viewport merge and repair
  -> local profiles
  -> HTTP / WebSocket API
```

## 仓库结构

```text
src/
  Backend/
    BackendProgram.cs
    WeChatAutomationService.cs
    DaemonApiService.cs
    ScreenCapture.cs
    Win32Helper.cs

  Shared/
    BackendProtocol.cs
    Constants.cs
    JsonHelper.cs
    MessageProcessor.cs
    WindowAttachmentHelper.cs

.agents/
  skills/
    process-queue/
    consolidate-insights/

docs/
  semantic_routing.md

scripts/
  *.py
```

## 构建

要求：

- Windows
- .NET Framework compiler：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- 本机已运行桌面版 WeChat

构建：

```bat
build.bat
```

输出：

```text
wechat-backend.exe
```

运行：

```bat
wechat-backend.exe
```

默认服务地址：

```text
http://127.0.0.1:8081/wechat/
```

## WebSocket API

连接：

```text
ws://127.0.0.1:8081/wechat/
```

请求：

```json
{
  "id": "1",
  "method": "get_current_state",
  "params": {}
}
```

methods：

- `get_current_state`
- `fetch_messages_by_chat`
- `reply_to_messages_by_chat`

示例：

```json
{
  "id": "2",
  "method": "fetch_messages_by_chat",
  "params": {
    "chat_name": "Laura Moses",
    "last_n": 50
  }
}
```

## HTTP API

base：

```text
http://127.0.0.1:8081/wechat/daemon
```

endpoints：

- `GET /health`
- `GET /contacts`
- `GET /contacts/{contact}`
- `GET /contacts/{contact}/chat-history`
- `DELETE /contacts/{contact}/chat-history`
- `PUT /contacts/{contact}/profile`
- `PATCH /contacts/{contact}/insight`
- `GET /jobs`
- `GET /jobs/{id}`
- `POST /jobs/{id}/result`

## 数据存储

contact folder：

```text
profiles/<contact>/
```

files：

```text
chat_history.json
info.json
snapshots/
```

chat record：

```json
{
  "Id": "rec_...",
  "IslandId": "island_...",
  "Type": "message",
  "Text": "[me]: message text",
  "Status": "pending",
  "CapturedAt": "2026-07-09T12:00:00"
}
```

record types：

- `message`
- `timestamp`
- `media`
- `call`
- `system`
- `gap`

metadata records 用于排序和修复。普通客户端应隐藏这些记录，除非展示 debug state。

## merge 模型

daemon 只读取可见 viewport。chat history 通过重叠内容增长：

```text
saved:  a b c d e
seen:       c d e f g
merged: a b c d e f g
```

当两个已保存 islands 被新的 viewport 连接起来，daemon 会合并 islands，并将受影响的 message records 标记为 pending，等待 semantic review。

## semantic jobs

`GET /jobs` 返回待处理 review work。

job types：

- `review:<contact>`：将 pending records 转成 profile updates 和 dated insights。
- `consolidate:<contact>`：将相关 insights 合并成长期 threads。

job result 必须通过下面的 endpoint 写入：

```text
POST /jobs/{id}/result
```

agents 不应直接编辑 `profiles/`、`chat_history.json` 或 `info.json`。

## 正确性原则

- 保留可见 WeChat 顺序。
- 宁可缺 records，不生成不存在的 records。
- hidden metadata 不进入普通 UI。
- 每条持久化 insight 都保留 source record IDs。
- 用户可见 summary 不保留 raw actor tokens。
- destructive write 或 semantic write 前保存 snapshot。

