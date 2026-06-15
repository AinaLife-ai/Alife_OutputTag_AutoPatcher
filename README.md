# Alife Output Tag AutoPatcher

自动检测输入来源，补全 `qchat` / `speak` 输出标签的 Alife 插件。

## 解决的问题

Alife 数字生命在不同输入渠道（QQ文字、QQ语音、桌面语音）之间切换时，AI 经常：
- **忘记加输出标签** → 消息发不出去
- **加错标签** → 语音输入却用文字回复，反之亦然
- **多轮对话中输出渠道不稳定** → 上下文混乱

本插件自动检测输入来源，在 LLM 输出阶段补全/纠正输出标签。

## 工作原理

```
QQ文字消息 ──→ 检测输入源(qq_text)    ──→ 自动补全 <qchat> 标签
QQ语音消息 ──→ 检测输入源(qq_voice)   ──→ 自动补全 <qchat voice=true> 标签
桌面语音输入 ──→ 检测输入源(desktop_speech) ──→ 自动补全 <speak> 标签
```

### 检测逻辑

1. **QQ 消息特征**: `[CQ:` 或 `qq:` 前缀
2. **语音标记**: `[语音]`、`[voice]`、`record`、`语音消息`
3. **桌面语音**: `<speak` 前缀、`speak:`、`说:`

### 标签补全策略

| 输入源 | 期望输出标签 | 说明 |
|--------|------------|------|
| `qq_text` | `<qchat>` | QQ文字消息 → 文字回复 |
| `qq_voice` | `<qchat voice=true>` | QQ语音消息 → 语音回复 |
| `desktop_speech` | `<speak>` | 桌面端语音 → 语音回复 |
| `desktop_text` | `<qchat>` | 桌面端文字 → 文字回复 |

## 项目结构

```
src/Alife.Function.OutputTagPatcher/
├── Alife.Function.OutputTagPatcher.csproj  # 项目文件
└── OutputTagPatcherService.cs               # 核心实现
```

## 构建

```bash
cd src/Alife.Function.OutputTagPatcher
dotnet build
```

构建产物为 `bin/Debug/net8.0/Alife.Function.OutputTagPatcher.dll`。

## 安装

1. 将编译后的 DLL 放入 Alife 的插件目录
2. 或在 Alife 插件市场搜索 "Output Tag 自动补全" 安装
3. 重启 Alife 桌宠

## 项目参考

本项目的实现参考了 Alife 官方模块：
- `Alife.Function.QChat` — QQ 消息收发与语音标志
- `Alife.Function.Speech` — 桌面语音合成与播放
- `Alife.Function.FunctionCaller` — XML 函数调用框架

## License

AGPL-3.0
