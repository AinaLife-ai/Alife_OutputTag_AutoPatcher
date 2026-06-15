// 贡献者：银月 (QQ: 2141951927)
// 修复了后处理补标时缺少 type/targetId 参数的问题
// 以及爱奈丽版本中的诸多细节优化

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
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
/// 两种模式互补：
/// 1. 注入提示（ChatSend）：在 LLM 处理前注入标签使用提示，引导AI正确输出
/// 2. 后处理补标（ChatOver）：AI输出完后检测到缺失标签时，直接通过executor补发带标签文本，不经过LLM
/// </summary>
[Module("输出标签自动补全",
    """
    自动检测输入来源并自动补全输出标签。
    模式1：在ChatSend阶段注入标签使用提示词，引导AI正确输出。
    模式2：在ChatOver阶段检测AI回复是否缺少输出标签，若缺失则直接通过XmlFunctionCaller的executor补发带标签文本，无需LLM重新处理。
    """,
    defaultCategory: "用户自制/输出标签",
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

    // 缓存当前对话的输入来源检测结果
    string? _currentOutputSource;
    // 反射缓存的 executor 引用
    XmlStreamExecutor? _executor;

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 注册GetStatus函数供AI查看状态
        var handler = new XmlHandler(this);
        functionService.RegisterHandlerWithoutDocument(handler);

        // 系统提示词：引导AI按场景使用正确的输出标签
        Prompt("""
            你有多个输出渠道，请根据对话输入的来源标签，选择对应的输出标签：

            - **QQ文字消息** → `<qchat>你的回复</qchat>` 输出纯文本
            - **QQ语音消息** → `<qchat voice=true>你的回复</qchat>` 输出语音
            - **桌面语音输入** → `<speak>你的回复</speak>` 通过桌面扬声器输出

            注意：
            1. 如果收到的是QQ消息（无论文字还是语音），用 `<qchat>` 标签
            2. 如果收到的是桌面语音（通过麦克风说话），用 `<speak>` 标签
            3. 不要滥用标签，一条回复一个输出标签即可
            """);
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        // 通过反射获取 XmlFunctionCaller 的私有 executor 字段
        // 用于后处理时直接喂XML文本给executor，绕过LLM
        CacheExecutor();

        ChatBot.ChatSend += OnChatSend;
        ChatBot.ChatOver += OnChatOver;

        logger.LogInformation("[OutputTagAutoPatcher] 已启动");
    }

    public override async Task DestroyAsync()
    {
        ChatBot.ChatSend -= OnChatSend;
        ChatBot.ChatOver -= OnChatOver;
        _executor = null;
        await base.DestroyAsync();
        logger.LogInformation("[OutputTagAutoPatcher] 已卸载");
    }

    void CacheExecutor()
    {
        try
        {
            var field = typeof(XmlFunctionCaller).GetField("executor",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _executor = field?.GetValue(functionService) as XmlStreamExecutor;
            if (_executor == null)
                logger.LogWarning("[OutputTagAutoPatcher] 无法获取XmlFunctionCaller.executor，后处理模式不可用");
            else if (Configuration?.DebugLog == true)
                logger.LogInformation("[OutputTagAutoPatcher] executor引用已缓存");
        }
        catch (Exception ex)
        {
            logger.LogWarning("[OutputTagAutoPatcher] 获取executor失败: {Msg}", ex.Message);
        }
    }

    #region 模式1：注入提示（ChatSend）

    string OnChatSend(string message)
    {
        if (!(Configuration?.EnableDetection ?? true))
            return message;

        string inputSource = DetectInputSource(message);
        _currentOutputSource = inputSource;

        // 注入场景提示词
        if (!string.IsNullOrEmpty(inputSource) &&
            Configuration?.InputSourceMap?.TryGetValue(inputSource, out var hint) == true &&
            !string.IsNullOrEmpty(hint))
        {
            return $"{message}\n\n{hint}";
        }

        return message;
    }

    #endregion

    #region 模式2：后处理补标（ChatOver）

    void OnChatOver()
    {
        if (!(Configuration?.PostProcessPatch ?? true))
            return;
        if (_executor == null)
            return;

        // 从 ChatHistory 获取 AI 最后一条回复的完整文本
        string? reply = null;
        for (int i = ChatHistory.Count - 1; i >= 0; i--)
        {
            if (ChatHistory[i].Role == AuthorRole.Assistant)
            {
                reply = ChatHistory[i].Content;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(reply))
            return;

        // 如果已包含输出标签，不干预
        if (HasOutputTag(reply))
            return;

        // 确定正确的输出标签 - 从ChatHistory最后一条用户消息中提取type和targetId
        string source = _currentOutputSource ?? DetectLastInputSource();
        
        // 尝试从最近用户消息中提取type和targetId
        string chatType = "Private";
        string chatTargetId = "10466671";
        for (int i = ChatHistory.Count - 1; i >= 0; i--)
        {
            if (ChatHistory[i].Role == AuthorRole.User && ChatHistory[i].Content != null)
            {
                var userMsg = ChatHistory[i].Content;
                if (userMsg.Contains("[QQ群]"))
                {
                    chatType = "Group";
                    var match = Regex.Match(userMsg, @"\[(\d+)\]");
                    if (match.Success) chatTargetId = match.Groups[1].Value;
                }
                else if (userMsg.Contains("[QQ私聊]"))
                {
                    chatType = "Private";
                    var match = Regex.Match(userMsg, @"\[(\d+)\]");
                    if (match.Success) chatTargetId = match.Groups[1].Value;
                }
                break;
            }
        }
        
        string tagOpen = $"<qchat type=\"{chatType}\" targetid=\"{chatTargetId}\">";
        string tagClose = $"</qchat>";

        if (string.IsNullOrEmpty(tagOpen))
            return;

        string patched = $"{tagOpen}{reply}{tagClose}";

        // 更新 ChatHistory 为带标签的版本
        for (int i = ChatHistory.Count - 1; i >= 0; i--)
        {
            if (ChatHistory[i].Role == AuthorRole.Assistant)
            {
                ChatHistory[i].Content = patched;
                break;
            }
        }

        if (Configuration?.DebugLog == true)
            logger.LogInformation("[后处理] 补标并直接执行: {Tag}", tagOpen);

        _executor.Feed(patched);
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
        return "qq_text";
    }

    bool HasOutputTag(string text)
    {
        return OutputTagRegex.IsMatch(text);
    }

    #endregion

    #region AI 可调用的函数

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看输出标签自动补全插件的状态和当前配置")]
    public string GetStatus()
    {
        return $"""
            输出标签自动补全:
            - 注入提示: {(Configuration?.EnableDetection ?? true ? "启用" : "关闭")}
            - 后处理补标: {(Configuration?.PostProcessPatch ?? true ? "启用" : "关闭")}
            - executor: {(_executor != null ? "可用" : "不可用")}
            - 调试日志: {(Configuration?.DebugLog == true ? "开启" : "关闭")}
            - 输出标签: qchat (QQ消息), qchat voice=true (QQ语音), speak (桌面语音)
            """;
    }

    #endregion
}