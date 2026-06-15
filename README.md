# Alife OutputTag AutoPatcher

自动检测输入来源并补全 `qchat` / `speak` 输出标签的 KiraAI 插件。

## 解决的问题

Alife 数字生命在不同输入渠道（QQ文字、QQ语音、桌面语音）之间切换时，AI 经常：
- **忘记加输出标签** → 消息发不出去
- **加错标签** → 语音输入却用文字回复，反之亦然
- **多轮对话输出渠道不稳定** → 上下文混乱

本插件自动检测输入来源，在 LLM 请求阶段注入上下文提示，引导 AI 使用正确的输出标签。

## 工作原理

```
QQ文字消息 ──→ 检测输入源(qq_text) ──→ 注入提示：用 <qchat> 回复
QQ语音消息 ──→ 检测输入源(qq_voice) ──→ 注入提示：用 <qchat voice=true> 回复
桌面语音输入 ──→ 检测输入源(desktop_speech) ──→ 注入提示：用 <speak> 回复
```

### 检测逻辑（优先级从高到低）

1. 事件是否携带语音标记（`[语音]`、`record` 等）
2. 适配器类型（`qq` adapter → QQ 平台）
3. 消息内容特征（语音关键词匹配）

### 标签补全策略

| 输入源 | 期望输出标签 | 说明 |
|--------|------------|------|
| `qq_text` | `<qchat>` | QQ文字消息，文字回复 |
| `qq_voice` | `<qchat voice=true>` | QQ语音消息，语音回复 |
| `desktop_speech` | `<speak>` | 桌面端语音输入，语音回复 |
| `desktop_text` | `<qchat>` | 桌面端文字输入，文字回复 |

## 安装

1. 将本仓库 clone 或下载到 KiraAI 的 plugins 目录
2. 在配置文件中启用插件
3. （可选）自定义 `schema.json` 中的映射规则

## 配置项

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `auto_detect` | boolean | `true` | 是否自动检测输入源 |
| `force_patch` | boolean | `true` | 是否强制补全标签（需要框架支持 response 钩子） |
| `input_source_map` | object | 见 schema | 自定义输入源→输出标签映射 |
| `debug_log` | boolean | `false` | 是否输出调试日志 |

## 自定义映射

如果你想让 QQ 语音消息仍然用文字回复，或者桌面语音用 `<qchat>` 输出，修改 `input_source_map` 即可：

```json
{
    "input_source_map": {
        "qq_text": "qchat",
        "qq_voice": "qchat",
        "desktop_speech": "qchat voice=true",
        "desktop_text": "qchat"
    }
}
```

## License

AGPL-3.0
