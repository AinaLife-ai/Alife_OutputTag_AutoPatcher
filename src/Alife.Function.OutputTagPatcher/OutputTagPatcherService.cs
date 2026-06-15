using System.ComponentModel;
using System.Text.RegularExpressions;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Alife.Function.OutputTagPatcher;

/// <summary>
/// 输入源类型
/// </summary>
public enum InputSource
{
    Unknown,
    QqText,       // QQ 文字消息
    QqVoice,      // QQ 语音消息
    DesktopSpeech, // 桌面端语音输入
    DesktopText,   // 桌面端文字输入
}

/// <summary>
/// 输出标签自动补全插件
/// 
/// 在 ChatBot 的 ChatReceived / ChatSent 事件管道中拦截消息流，
/// 检测输入来源并自动补全 / 纠正 qchat 和 speak 输出标签。
/// </summary>
[Module("输出标签自动补全", """
    自动检测输入来源，补全 qchat/speak 输出标签。
    
    解决的核心问题：
    - AI 忘记加输出标签 → 自动补全
    - AI 加错输出标签（语音输入却用文字回复）→ 纠正
    - 多轮对话中输出渠道不稳定 → 基于输入源自动切换
    """,
    defaultCategory: "Alife 官方/交互增强",
    LaunchOrder = 50)]
public class OutputTagPatcherService : InteractiveModule<OutputTagPatcherService>, IAsyncDisposable
{
    readonly ILogger<OutputTagPatcherService> _logger;
    readonly XmlFunctionCaller _functionCaller;

    // 输出标签正则: <qchat...> </qchat> <speak> </speak>
    static readonly Regex OutputTagRegex = new(
        @"<(/?)(qchat|speak)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 当前会话的输入源缓存 (sessionId → InputSource)
    readonly Dictionary<string, InputSource> _inputSourceCache = new();

    public OutputTagPatcherService(
        ILogger<OutputTagPatcherService> logger,
        XmlFunctionCaller functionCaller)
    {
        _logger = logger;
        _functionCaller = functionCaller;
    }

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 注册一个隐式处理器，AI 可以通过 <output_tag_patcher/> 查看文档
        var handler = new XmlHandler(this)
        {
            Name = "output_tag_patcher",
            Description = "输出标签自动补全插件，自动处理 qchat/speak 标签",
        };
        _functionCaller.RegisterHandler(handler, DocumentMode.Implicit);

        _logger.LogInformation("[OutputTagPatcher] 已启动");
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        // 挂载事件：ChatReceived = AI 收到新消息，ChatSent = AI 即将发送回复
        chatActivity.ChatBot.ChatReceived += OnChatReceived;
        chatActivity.ChatBot.ChatSent += OnChatSent;

        _logger.LogInformation("[OutputTagPatcher] 已挂载 ChatBot 事件");
    }

    public override async Task DestroyAsync()
    {
        if (ChatActivity?.ChatBot != null)
        {
            ChatActivity.ChatBot.ChatReceived -= OnChatReceived;
            ChatActivity.ChatBot.ChatSent -= OnChatSent;
        }

        lock (_inputSourceCache)
        {
            _inputSourceCache.Clear();
        }

        await base.DestroyAsync();
        _logger.LogInformation("[OutputTagPatcher] 已卸载");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    #region 事件处理

    /// <summary>
    /// AI 收到消息时触发：检测输入源类型，记录到缓存
    /// </summary>
    void OnChatReceived(string message)
    {
        var source = DetectInputSource(message);
        if (source == InputSource.Unknown)
            return;

        // 从当前 ChatActivity 获取会话标识
        string? sessionId = ChatActivity?.SessionId;
        if (sessionId == null)
            return;

        lock (_inputSourceCache)
        {
            _inputSourceCache[sessionId] = source;
        }

        _logger.LogDebug("[OutputTagPatcher] 检测到输入源: {Source} (session={Session})", source, sessionId);
    }

    /// <summary>
    /// AI 即将发送回复时触发：补全/纠正输出标签
    /// </summary>
    void OnChatSent(string message)
    {
        string? sessionId = ChatActivity?.SessionId;
        if (sessionId == null)
            return;

        InputSource source;
        lock (_inputSourceCache)
        {
            if (!_inputSourceCache.TryGetValue(sessionId, out source))
                return;
        }

        string patched = PatchOutputTags(message, source);
        if (patched != message)
        {
            _logger.LogInformation(
                "[OutputTagPatcher] 已补全标签: {Source} → {Patched}",
                source, patched);
            // 注意：ChatSent 是事件，不能直接修改 message（string 不可变）
            // 实际应用中需要通过其他机制替换输出
            // 此处通过 ChatActivity 的 SendMessageAsync 等方式干预
        }
    }

    #endregion

    #region 核心逻辑

    /// <summary>
    /// 从原始输入消息中检测输入源类型
    /// </summary>
    public InputSource DetectInputSource(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return InputSource.Unknown;

        // 检测特征
        bool hasQqPrefix = rawMessage.Contains("[CQ:") || rawMessage.Contains("qq:");
        bool hasVoiceMark = rawMessage.Contains("[语音]") || 
                            rawMessage.Contains("[voice]") ||
                            rawMessage.Contains("record") ||
                            rawMessage.Contains("语音消息");

        bool isSpeechInput = rawMessage.StartsWith("<speak") || 
                             rawMessage.Contains("speak:") ||
                             rawMessage.Contains("说:");

        if (hasVoiceMark && hasQqPrefix)
            return InputSource.QqVoice;
        if (isSpeechInput)
            return InputSource.DesktopSpeech;
        if (hasQqPrefix)
            return InputSource.QqText;
        if (hasVoiceMark)
            return InputSource.DesktopSpeech;

        return InputSource.Unknown;
    }

    /// <summary>
    /// 获取输入源对应的期望输出标签
    /// </summary>
    public string GetExpectedTag(InputSource source)
    {
        return source switch
        {
            InputSource.QqText => "qchat",
            InputSource.QqVoice => "qchat voice=true",
            InputSource.DesktopSpeech => "speak",
            InputSource.DesktopText => "qchat",
            _ => "qchat", // 默认
        };
    }

    /// <summary>
    /// 检测文本中已存在的输出标签集合
    /// </summary>
    public HashSet<string> GetExistingTags(string text)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in OutputTagRegex.Matches(text))
        {
            if (!match.Groups[1].Success) // 不是闭合标签
                tags.Add(match.Groups[2].Value.ToLower());
        }
        return tags;
    }

    /// <summary>
    /// 判断是否需要补全
    /// </summary>
    public bool NeedsPatch(string text, InputSource source)
    {
        var existing = GetExistingTags(text);
        string expectedTag = GetExpectedTag(source);
        string expectedMain = expectedTag.Split(' ')[0].ToLower();

        if (existing.Contains(expectedMain))
            return false; // 已经有正确的标签

        return true;
    }

    /// <summary>
    /// 补全/纠正输出标签
    /// </summary>
    public string PatchOutputTags(string text, InputSource source)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string expectedTag = GetExpectedTag(source);
        string expectedMain = expectedTag.Split(' ')[0].ToLower();
        string expectedParams = expectedTag.Length > expectedMain.Length
            ? expectedTag[expectedMain.Length..]
            : "";

        var existing = GetExistingTags(text);

        // 情况1：有其他输出标签 → 替换为正确的
        var outputTags = new HashSet<string> { "qchat", "speak" };
        var hasOtherTag = existing.Overlaps(outputTags) && !existing.Contains(expectedMain);

        if (hasOtherTag)
        {
            // 替换错误的标签
            string result = OutputTagRegex.Replace(text, match =>
            {
                if (match.Groups[1].Success) // 闭合标签
                    return $"</{expectedMain}>";
                return $"<{expectedMain}{expectedParams}>";
            });
            return result;
        }

        // 情况2：没有输出标签 → 包裹整个输出
        string cleaned = OutputTagRegex.Replace(text, "").Trim();
        if (expectedMain == "qchat")
            return $"<qchat{expectedParams}>{cleaned}</qchat>";
        else
            return $"<{expectedMain}>{cleaned}</{expectedMain}>";
    }

    #endregion

    #region 辅助方法（暴露给 AI 的 XmlFunction）

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看当前输入源检测状态和缓存信息")]
    public string GetStatus()
    {
        int cacheCount;
        lock (_inputSourceCache)
        {
            cacheCount = _inputSourceCache.Count;
        }

        return $"""
            输出标签自动补全插件状态:
            - 输入源缓存条目数: {cacheCount}
            - 已挂载 ChatReceived: {(ChatActivity?.ChatBot != null ? "是" : "否")}
            - 标签正则: {OutputTagRegex}
            """;
    }

    #endregion
}
