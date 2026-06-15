# Alife Function - OutputTagAutoPatcher

自动检测输入来源并补全 qchat/speak 输出标签的 Alife 模块。

## 解决的问题

Alife 在不同输入渠道之间切换时，AI 经常忘记或误用输出标签，导致消息发不出。

## 工作原理

两种模式互补：

### 模式1：注入提示（ChatSend）

在 LLM 处理前，根据输入来源注入标签使用提示：

| 输入来源 | 注入的提示 | 期望的 AI 输出标签 |
|----------|-----------|-------------------|
| QQ 文字消息 | 无（默认 qchat） | `<qchat>` |
| QQ 语音消息 | 提示使用 voice=true | `<qchat voice=true>` |
| 桌面语音输入 | 提示使用 speak | `<speak>` |

### 模式2：后处理补标（ChatOver）

AI 输出完成后，检测到回复中缺失输出标签时：

1. 从 ChatHistory 获取 AI 最后一条回复的完整文本
2. 检查是否包含 `<qchat>` 或 `<speak>` 标签
3. 若缺失，构造带标签的修正文本
4. **通过反射获取 XmlFunctionCaller 的 executor，直接 `Feed()` 修正文本**
5. 更新 ChatHistory 为带标签的版本（保证上下文连贯性）
6. **不经过 LLM 重新处理**（对比旧版 Poke 方案）

关键：原始文本不带标签，在 Flush 时会被 executor 丢弃；修正文本的标签会被正确解析执行。

## 使用

1. 将本仓库放入 Alife 源码目录下的 `sources/Alife.Function/` 文件夹
2. 在 Alife 管理界面启用「输出标签自动补全」模块
3. 重启生效

## 依赖

- Alife.Framework
- Alife.Function.FunctionCaller
- Alife.Function.Interpreter (XmlStreamExecutor)
- Microsoft.SemanticKernel.ChatCompletion

## 配置

可在 Alife 管理界面中配置该模块：
- `EnableDetection`: 启用输入来源检测和注入提示（默认 true）
- `PostProcessPatch`: 启用后处理补标（默认 true）
- `DebugLog`: 调试日志（默认 false）
- `InputSourceMap`: 各输入来源的注入提示文本

## License

AGPL-3.0
