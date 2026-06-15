# Alife Function - OutputTagAutoPatcher

自动检测输入来源并补全 `qchat` / `speak` 输出标签的 Alife 插件。

## 解决的问题

Alife 数字生命在不同输入渠道（QQ文字、QQ语音、桌面语音）之间切换时，AI 经常：
- **忘记加输出标签** → 消息发不出去
- **加错标签** → 语音输入却用文字回复
- **多轮对话中输出标签混乱**

本 Function 在 LLM 处理消息前自动检测输入来源，注入标签使用提示，引导 AI 使用正确的输出标签。

## 工作原理

```
QQ文字消息 ──→ OnChatSend检测(qq_text) ──→ 注入提示：默认使用 <qchat>
QQ语音消息 ──→ OnChatSend检测(qq_voice) ──→ 注入提示：用 <qchat voice=true>
桌面语音输入 ──→ OnChatSend检测(desktop_speech) ──→ 注入提示：用 <speak>
```

### 检测逻辑

| 输入特征 | 识别为 | 注入提示 |
|---------|--------|---------|
| 消息含 `[QQ]` 或 `QQ消息` | `qq_text` | 默认使用 `<qchat>` |
| 消息含 `[QQ]` + 语音关键词 | `qq_voice` | 使用 `<qchat voice=true>` |
| 消息含 `[Desktop]` 或 `桌面` + 语音关键词 | `desktop_speech` | 使用 `<speak>` |
| 仅含语音关键词 | `desktop_speech` | 使用 `<speak>` |

语音关键词：`[语音]`, `[voice]`, `record`, `语音消息`, `speak:`

## 安装

1. 将 `Alife.Function.OutputTagAutoPatcher` 文件夹放入 Alife 的 `sources/Alife.Function/` 目录
2. 在 Alife 配置中启用「输出标签自动补全」模块
3. 重启 Alife

## 依赖

- Alife.Framework
- Alife.Function.FunctionCaller

## 配置

| 配置项 | 类型 | 默认 | 说明 |
|--------|------|------|------|
| `EnableDetection` | bool | `true` | 启用输入源检测 |
| `ForcePatch` | bool | `true` | 强制补全模式 |
| `DebugLog` | bool | `false` | 调试日志 |
| `InputSourceMap` | dict | 见代码 | 自定义输入源→提示映射 |

## License

AGPL-3.0
