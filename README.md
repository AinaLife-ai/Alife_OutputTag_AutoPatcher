# Alife Function - OutputTagAutoPatcher

自动检测输入来源并补全 `qchat` / `speak` 输出标签的 Alife 插件。

## 解决的问题

Alife 桌宠在不同输入渠道间切换时，AI 经常忘记/用错输出标签导致消息发不出。

## 工作原理

```
QQ文字消息 ──→ OnChatSend检测(qq_text) ──→ 注入提示：默认使用 <qchat>
QQ语音消息 ──→ OnChatSend检测(qq_voice) ──→ 注入提示：用 <qchat voice=true>
桌面语音输入 ──→ OnChatSend检测(desktop_speech) ──→ 注入提示：用 <speak>
```

## 使用

1. 将本仓库放入 Alife 源码目录下的 `sources/Alife.Function/` 文件夹
2. 在 Alife 配置中启用「输出标签自动补全」模块
3. 重启生效

## 依赖

- `Alife.Framework`
- `Alife.Function.FunctionCaller`

## License

AGPL-3.0
