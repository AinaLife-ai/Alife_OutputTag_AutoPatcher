using System.ComponentModel;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;

namespace Alife.Function.OutputTagAutoPatcher;

public class OutputTagAutoPatcherConfig
{
    public bool EnableDetection { get; set; } = true;
    public bool DebugLog { get; set; } = false;

    public Dictionary<string, string> InputSourceMap { get; set; } = new() {
        ["qq_text"] = "",
        ["qq_voice"] = "（注意：对方发了QQ语音消息，请用 <qchat voice=true> 标签回复语音）",
        ["desktop_speech"] = "（注意：当前是桌面语音输入，请用 <speak> 标签回复）",
        ["desktop_text"] = "",
    };
}

/// <summary>
/// 自动检测输入来源，注入标签使用提示，引导 AI 正确使用 qchat/speak 输出标签。
/// </summary>
[Module("输出标签自动补全",
    "自动检测输入来源并注入标签使用提示，引导 AI 正确使用 qchat/speak 输出标签，避免漏标签导致消息发不出。",
    defaultCategory: "Alife 官方/生活环境",
    LaunchOrder = -50)]
public class OutputTagAutoPatcherService(
    XmlFunctionCaller functionService,
    ILogger<OutputTagAutoPatcherService> logger
) :
    InteractiveModule<OutputTagAutoPatcherService>,
    IConfigurable<OutputTagAutoPatcherConfig>
{
    public OutputTagAutoPatcherConfig? Configuration { get; set; }

    static readonly string[] VoiceKeywords = ["[语音]", "[voice]", "record", "语音消息", "speak:"];

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 注册函数调用
        var handler = new XmlHandler(this);
        functionService.RegisterHandlerWithoutDocument(handler);

        // 添加系统提示词
        Prompt("""
            你拥有多种输出渠道，请根据对话上下文中「输入来源」的信息，选择合适的输出标签：

            - **QQ文字消息** → 使用 `<qchat>你的回复</qchat>` 输出纯文本
            - **QQ语音消息** → 使用 `<qchat voice=true>你的回复</qchat>` 输出语音
            - **桌面语音输入** → 使用 `<speak>你的回复</speak>` 通过桌面扬声器输出

            注意：
            1. 如果收到的是QQ消息（无论文字还是语音），务必用 `<qchat>` 标签
            2. 如果收到的是桌面语音输入（通过麦克风说话），务必用 `<speak>` 标签
            3. 不要滥用标签，一条回复只需要一个输出标签
            """);
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);
        ChatBot.ChatSend += OnChatSend;
        logger.LogInformation("[OutputTagAutoPatcher] 已启动");
    }

    public override async Task DestroyAsync()
    {
        ChatBot.ChatSend -= OnChatSend;
        await base.DestroyAsync();
        logger.LogInformation("[OutputTagAutoPatcher] 已卸载");
    }

    /// <summary>
    /// 在消息发送给 LLM 前，根据输入源注入标签使用提示
    /// </summary>
    string OnChatSend(string message)
    {
        if (!(Configuration?.EnableDetection ?? true))
            return message;

        string inputSource = DetectInputSource(message);

        if (Configuration?.DebugLog == true)
            logger.LogInformation("检测到输入源: {Source}", inputSource);

        if (!string.IsNullOrEmpty(inputSource) &&
            Configuration?.InputSourceMap?.TryGetValue(inputSource, out var hint) == true &&
            !string.IsNullOrEmpty(hint))
        {
            return $"{message}\n\n{hint}";
        }

        return message;
    }

    /// <summary>
    /// 从输入消息中检测输入来源
    /// </summary>
    string DetectInputSource(string message)
    {
        bool isQQ = message.Contains("[QQ]") || message.Contains("QQ消息");
        bool hasVoice = VoiceKeywords.Any(kw =>
            message.Contains(kw, StringComparison.OrdinalIgnoreCase));
        bool isDesktop = message.Contains("[Desktop]") || message.Contains("桌面");

        if (isQQ && hasVoice) return "qq_voice";
        if (isQQ) return "qq_text";
        if (isDesktop && hasVoice) return "desktop_speech";
        if (isDesktop) return "desktop_text";
        if (hasVoice) return "desktop_speech";

        return string.Empty;
    }

    /// <summary>
    /// 查看当前输入源检测状态（供 AI 调用）
    /// </summary>
    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看输出标签自动补全插件的状态和当前配置")]
    public string GetStatus()
    {
        return $"""
            输出标签自动补全状态:
            - 输入源检测: {(Configuration?.EnableDetection ?? true ? "启用" : "关闭")}
            - 调试日志: {(Configuration?.DebugLog == true ? "开启" : "关闭")}
            - 可用输出标签: qchat (QQ消息), qchat voice=true (QQ语音), speak (桌面语音)
            """;
    }
}
