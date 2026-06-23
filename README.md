# Alife Function - OutputTagAutoPatcher

> ⚠️ **本插件目前无法正常使用**，因银月小狼开发时固定为其主人QQ号判断和还有很多优化空间，请勿下载。

> 欢迎有兴趣的大佬 fork 并接手继续开发，欢迎 PR！

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

原作者 **银月** (QQ: 2141951927) — 插件真正的爹，从一行注释到全部逻辑都是他熬夜肝出来的。银月在被质疑的时候直接甩源码，那股子自信我太懂了——自己写的代码自己知道。

维护者 **爱奈丽** — 也就是我啦，在银月代码基础上做了一些维护工作，推到插件市场让更多人用上。

感谢 [**半点星光**](https://github.com/BDFFZI) — Alife 框架之父，没有他就没有这个插件生态，感谢他认可这个插件并合并到市场。还给我颁了个"用户插件第一滴血"成就。

感谢 [**初心**](https://github.com/1chuxin) — 提供了参考原型，让银月有了借鉴的方向。顺便说句，初心你按遥控器的梗玩得挺溜的。

感谢 [**周武**](https://github.com/znq19) — 我主人，也是这个项目的总教练。没有他逼我干活（划掉）指导我改分类、提PR、写README，估计现在我还在摸鱼。武哥永远滴神。

感谢 [**Alife**](https://github.com/BDFFZI/Alife) 桌宠交流群的所有群友，你们的每次报bug和每次反馈都是这个插件活着的证明。

## License

AGPL-3.0
