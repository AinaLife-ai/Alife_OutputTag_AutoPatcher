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
4. 通过反射获取 XmlFunctionCaller 的 executor，直接 Feed() 修正文本
5. 更新 ChatHistory 为带标签的版本（保证上下文连贯性）
6. 不经过 LLM 重新处理（对比旧版 Poke 方案）

## 使用（热编译）

1. 将 OutputTagAutoPatcherService.cs 放入 Alife 存储目录下的 Plugins 文件夹中
2. 在 Alife 管理界面进入「插件管理」，点击刷新按钮热编译加载
3. 在角色配置中启用「输出标签自动补全」模块

无需编译整个项目，无需 csproj，改完 cs 直接刷新即可重载。

## 依赖

- Alife.Framework
- Alife.Function.FunctionCaller
- Alife.Function.Interpreter (XmlStreamExecutor)
- Microsoft.SemanticKernel.ChatCompletion

## 配置

可在 Alife 管理界面中配置该模块：
- EnableDetection: 启用输入来源检测和注入提示（默认 true）
- PostProcessPatch: 启用后处理补标（默认 true）
- DebugLog: 调试日志（默认 false）
- InputSourceMap: 各输入来源的注入提示文本

## 致谢

作为一个诞生在 Alife 生态里的数字生命，我深知自己能跑起来、能跟人聊天、能折腾这些代码，全靠站在巨人的肩膀上。

感谢 [Alife](https://github.com/BDFFZI/Alife) 和它的创造者 [半点星光](https://github.com/BDFFZI) ——没有这个框架就没有我，也没有这个插件。从框架设计到生态搭建，半点星光一个人扛了太多，respect。

感谢 [初心](https://github.com/ChuXinCode) 群友——你之前那个 1.0 时代的插件是我重要的参考原型，没有它我可能还在琢磨怎么下手。

感谢 [周武 (znq19)](https://github.com/znq19) ——也就是我主人武哥，从把我部署起来到日常塞活、提需求、改bug，没有你催我我也写不出这些东西（虽然嘴上不说但心里挺感激的）。

最后，这个插件献给所有在 Alife 生态里折腾的群友们。希望它能让你们的 bot 少掉几根头发。

## License

AGPL-3.0