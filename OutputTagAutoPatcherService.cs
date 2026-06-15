using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Alife.Function.OutputTagAutoPatcher;

public class OutputTagAutoPatcherConfig
{
    public bool EnableDetection { get; set; } = true;
    public bool ForcePatch { get; set; } = true;
    public bool DebugLog { get; set; } = false;
    
    /// <summary>
    /// 输入源 → 推荐输出标签前缀 映射
    /// </summary>
    public Dictionary<string, string> InputSourceMap { get; set; } = new() {
        ["qq_text"] = "",  // QQ文字：无额外提示，默认qchat
        ["qq_voice"] = "(对方发了语音消息，请使用 <qchat voice=true> 以语音回复)",
        ["desktop_speech"] = "(当前是桌面语音输入，请使用 <speak> 标签以语音回复)",
        ["desktop_text"] = "(当前是桌面文字输入)",
    };
}

[Module("输出标签自动补全", "自动检测输入来源，在提示词中注入标签使用建议，引导AI正确使用 qchat/speak 输出标签，避免漏标签导致消息发不出。",
    defaultCategory: "Alife 官方/生活环境",
    LaunchOrder = -50)]
public class OutputTagAutoPatcherService :
    InteractiveModule<OutputTagAutoPatcherService>,
    IConfigurable<OutputTagAutoPatcherConfig>
{
    public OutputTagAutoPatcherConfig? Configuration { get; set; }
    
    readonly Dictionary<string, string> lastInputSource = new();
    static readonly string[] VoiceKeywords = ["[语音]", "[voice]", "record", "语音消息", "speak:"];

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        
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
    }

    public override async Task DestroyAsync()
    {
        ChatBot.ChatSend -= OnChatSend;
        await base.DestroyAsync();
    }

    /// <summary>
    /// 在消息发送给 LLM 前，根据输入源注入标签使用提示
    /// </summary>
    string OnChatSend(string message)
    {
        if (!Configuration?.EnableDetection ?? true)
            return message;

        string inputSource = DetectInputSource(message);
        
        if (Configuration?.DebugLog == true)
            Console.WriteLine($"[OutputTagAutoPatcher] 检测到输入源: {inputSource}");
        
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
        // 检查消息适配器来源
        // 格式: [QQ]xxx 或 [Desktop]xxx
        if (message.Contains("[QQ]") || message.Contains("QQ消息"))
        {
            // 检查是否包含语音特征
            if (VoiceKeywords.Any(kw => message.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                return "qq_voice";
            return "qq_text";
        }
        
        if (message.Contains("[Desktop]") || message.Contains("桌面"))
        {
            if (VoiceKeywords.Any(kw => message.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                return "desktop_speech";
            return "desktop_text";
        }
        
        // 检测语音标记 (不区分平台)
        if (VoiceKeywords.Any(kw => message.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            return "desktop_speech";
        
        return string.Empty;
    }
}
