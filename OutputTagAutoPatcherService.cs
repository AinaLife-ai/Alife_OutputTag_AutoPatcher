using System.ComponentModel;
using System.Text.RegularExpressions;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Alife.Function.OutputTagAutoPatcher;

public class OutputTagAutoPatcherConfig
{
    public bool EnableDetection { get; set; } = true;
    public bool PostProcessPatch { get; set; } = true;
    public bool DebugLog { get; set; } = false;

    public Dictionary<string, string> InputSourceMap { get; set; } = new() {
        ["qq_text"] = "",
        ["qq_voice"] = "（注意：对方发了QQ语音消息，请用 <qchat voice=true> 标签回复语音）",
        ["desktop_speech"] = "（注意：当前是桌面语音输入，请用 <speak> 标签回复）",
        ["desktop_text"] = "",
    };
}

/// <summary>
/// 自动检测输入来源并补全 qchat/speak 输出标签。
/// 
/// 两种模式：
/// 1. 注入提示（ChatSend）：在 LLM 处理前注入标签使用提示
/// 2. 后处理补标（ChatOver）：在 LLM 输出完成后检测并自动补全缺失的标签
/// </summary>
[Module("输出标签自动补全",
    "自动检测输入来源并补全 qchat/speak 输出标签：1. 注入提示词引导AI正确使用标签 2. AI忘记标签时自动后处理补全",
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

    static readonly Regex OutputTagRegex = new(
        @"<(/?)(qchat|speak)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly string[] VoiceKeywords = ["[语音]", "[voice]", "record", "语音消息", "speak:"];

    // 用于收集当前流式输出的完整文本
    string? _currentOutputSource;
    readonly List<string> _currentOutputChunks = [];

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 注册函数调用（供AI手动查看状态）
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
        ChatBot.ChatReceived += OnChatReceived;
        ChatBot.ChatOver += OnChatOver;
        logger.LogInformation("[OutputTagAutoPatcher] 已启动（注入提示 + 后处理补标）");
    }

    public override async Task DestroyAsync()
    {
        ChatBot.ChatSend -= OnChatSend;
        ChatBot.ChatReceived -= OnChatReceived;
        ChatBot.ChatOver -= OnChatOver;
        await base.DestroyAsync();
        logger.LogInformation("[OutputTagAutoPatcher] 已卸载");
    }

    #region 模式1：注入提示（ChatSend）

    string OnChatSend(string message)
    {
        if (!(Configuration?.EnableDetection ?? true))
            return message;

        string inputSource = DetectInputSource(message);
        _currentOutputSource = inputSource;

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

    #endregion

    #region 模式2：后处理补标（ChatReceived + ChatOver）

    void OnChatReceived(string chunk)
    {
        if (!(Configuration?.PostProcessPatch ?? true))
            return;

        lock (_currentOutputChunks)
        {
            _currentOutputChunks.Add(chunk);
        }
    }

    void OnChatOver()
    {
        if (!(Configuration?.PostProcessPatch ?? true))
            return;

        string fullText;
        lock (_currentOutputChunks)
        {
            fullText = string.Concat(_currentOutputChunks);
            _currentOutputChunks.Clear();
        }

        if (string.IsNullOrWhiteSpace(fullText))
            return;

        // 检查是否已经包含输出标签
        if (HasOutputTag(fullText))
            return;

        // 确定正确的输出标签
        string source = _currentOutputSource ?? DetectLastInputSource();
        string tagOpen = GetExpectedTagOpen(source);
        string tagClose = GetExpectedTagClose(source);

        if (string.IsNullOrEmpty(tagOpen))
            return;

        // 在完整文本外层补上输出标签
        string patched = $"{tagOpen}{fullText}{tagClose}";

        // 修改 ChatHistory 中最后一条 assistant 消息
        var lastAssistant = ChatHistory
            .LastOrDefault(m => m.Role == AuthorRole.Assistant);
        if (lastAssistant != null)
        {
            lastAssistant.Content = patched;
        }

        if (Configuration?.DebugLog == true)
            logger.LogInformation("[后处理] 已自动补全输出标签: {Source} → {Tag}", source, tagOpen);

        // 通过 Poke 让 AI 重新处理带标签的文本
        // 这样 XmlFunctionCaller 能正确解析并执行输出
        Poke($"【系统】检测到你刚才的回复缺少输出标签，已自动补全为 {tagOpen}。请重新输出这段带标签的文本。\n{patched}");
    }

    #endregion

    #region 检测逻辑

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

    string DetectLastInputSource()
    {
        // 从 ChatHistory 中回溯检测最近的输入源
        for (int i = ChatHistory.Count - 1; i >= 0; i--)
        {
            var msg = ChatHistory[i];
            if (msg.Role == AuthorRole.User && msg.Content != null)
            {
                string source = DetectInputSource(msg.Content);
                if (!string.IsNullOrEmpty(source))
                    return source;
            }
        }
        return "qq_text"; // 默认
    }

    bool HasOutputTag(string text)
    {
        return OutputTagRegex.IsMatch(text);
    }

    string GetExpectedTagOpen(string source)
    {
        return source switch
        {
            "qq_voice" => "<qchat voice=true>",
            "qq_text" => "<qchat>",
            "desktop_speech" => "<speak>",
            "desktop_text" => "<qchat>",
            _ => "<qchat>",
        };
    }

    string GetExpectedTagClose(string source)
    {
        string tag = GetExpectedTagOpen(source).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        // 提取标签名: <qchat ...> → qchat
        string name = tag.Trim('<', '>').Split(' ')[0];
        return $"</{name}>";
    }

    #endregion

    #region AI 可调用的函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看输出标签自动补全插件的状态和当前配置")]
    public string GetStatus()
    {
        return $"""
            输出标签自动补全状态:
            - 输入源检测: {(Configuration?.EnableDetection ?? true ? "启用" : "关闭")}
            - 后处理补标: {(Configuration?.PostProcessPatch ?? true ? "启用" : "关闭")}
            - 调试日志: {(Configuration?.DebugLog == true ? "开启" : "关闭")}
            - 可用输出标签: qchat (QQ消息), qchat voice=true (QQ语音), speak (桌面语音)
            """;
    }

    #endregion
}
