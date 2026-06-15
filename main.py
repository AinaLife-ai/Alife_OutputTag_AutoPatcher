import re
from typing import Optional, Dict

from core.plugin import BasePlugin, logger, on, Priority
from core.chat.message_utils import KiraMessageBatchEvent
from core.provider.llm_model import LLMRequest


class OutputTagAutoPatcherPlugin(BasePlugin):
    """
    自动检测输入源并补全 qchat/speak 输出标签。
    
    解决的核心问题：
    - AI忘记加输出标签 → 自动补全
    - AI加错输出标签（语音输入却用文字回复）→ 纠正
    - 多轮对话中输出渠道不稳定 → 基于输入源自动切换
    """

    TAG_PATTERN = re.compile(r'<(/?)(qchat|speak|qq?)\b[^>]*>', re.IGNORECASE)

    def __init__(self, ctx, cfg: dict):
        super().__init__(ctx, cfg)
        self.auto_detect = cfg.get("auto_detect", True)
        self.force_patch = cfg.get("force_patch", True)
        self.debug_log = cfg.get("debug_log", False)
        
        # 输入源 → 推荐输出标签 映射
        default_map = {
            "qq_text": "qchat",
            "qq_voice": "qchat voice=true",
            "desktop_speech": "speak",
            "desktop_text": "qchat",
        }
        self.input_source_map: Dict[str, str] = cfg.get("input_source_map", default_map)
        
        self._last_input_source: Dict[str, str] = {}

    async def initialize(self):
        logger.info(
            f"OutputTagAutoPatcher 初始化: "
            f"auto_detect={self.auto_detect}, force_patch={self.force_patch}"
        )

    async def terminate(self):
        self._last_input_source.clear()
        logger.info("OutputTagAutoPatcher 已终止")

    def _detect_input_source(self, event) -> Optional[str]:
        """
        从事件上下文中检测输入来源类型。
        
        检测逻辑（优先级从高到低）：
        1. 事件中是否包含语音标记
        2. 事件 adapter 类型
        3. 消息内容特征
        """
        # 尝试从事件元数据中检测
        raw_event = getattr(event, 'raw_event', None) or event
        
        # 检测是否为语音消息
        message_str = str(getattr(raw_event, 'message', '') or getattr(raw_event, 'text', ''))
        
        # adapter 类型检测
        adapter = getattr(raw_event, 'adapter_name', '') or getattr(event, 'adapter', '')
        session = getattr(event, 'session', '') or getattr(event, 'sid', '')
        
        # QQ平台检测
        is_qq = 'qq' in adapter.lower() or session.startswith('qq:')
        
        # 语音特征检测
        has_voice = any(kw in message_str.lower() for kw in [
            '[语音]', '[voice]', 'record', '语音消息', 
            'speak:', '说:', '讲:'
        ])
        
        # 桌面语音检测 (MiMo TTS 或其他语音输入)
        is_speech_input = hasattr(event, 'is_voice') and event.is_voice
        
        if self.debug_log:
            logger.debug(
                f"输入源检测: adapter={adapter}, session={session}, "
                f"has_voice={has_voice}, is_speech_input={is_speech_input}"
            )
        
        if is_speech_input:
            return "desktop_speech"
        if has_voice and is_qq:
            return "qq_voice"
        if is_qq:
            return "qq_text"
        if has_voice:
            return "desktop_speech"
        
        return None

    def _detect_current_tags(self, text: str) -> set:
        """检测文本中已存在的输出标签"""
        tags = set()
        for match in self.TAG_PATTERN.finditer(text):
            is_close = match.group(1) == '/'
            tag_name = match.group(2).lower()
            if not is_close:
                tags.add(tag_name)
        return tags

    def _should_patch(self, text: str, expected_tag: str) -> bool:
        """判断是否需要补全标签"""
        existing = self._detect_current_tags(text)
        
        # 提取期望的主标签名（去掉参数）
        expected_main = expected_tag.split()[0].lower()
        
        # 如果已经包含期望标签，不处理
        if expected_main in existing:
            return False
        
        # 如果包含其他输出标签（如应该用qchat但用了speak），需要纠正
        output_tags = {'qchat', 'speak', 'qq'}
        has_other_tag = bool(existing & output_tags)
        
        if has_other_tag:
            return True  # 标签错了，需要纠正
            
        # 没有找到任何输出标签
        return True

    def _patch_tags(self, text: str, expected_tag: str) -> str:
        """
        补全/纠正输出标签。
        
        处理策略：
        1. 如果文本已有其他输出标签 → 替换为正确的
        2. 如果文本没有输出标签 → 包裹整个输出
        3. 保留文本中非标签的内容不变
        """
        output_tags = {'qchat', 'speak', 'qq'}
        expected_main = expected_tag.split()[0].lower()
        
        # 先清理错误的标签
        cleaned = self.TAG_PATTERN.sub('', text).strip()
        
        # 根据期望标签包裹
        if expected_main == 'qchat':
            # qchat 可能带 voice 参数
            params = expected_tag[len('qchat'):].strip()
            return f"<qchat{params}>{cleaned}</qchat>"
        elif expected_main == 'speak':
            return f"<speak>{cleaned}</speak>"
        
        return cleaned

    def _inject_context_hint(self, req: LLMRequest, input_source: str):
        """
        向 LLM 请求注入上下文提示，让 AI 知道应该使用什么输出标签。
        在 prompt 层面引导，减少后续强制补全的需要。
        """
        source_hints = {
            "qq_text": "【输出提示】当前是QQ文字对话，回复请用 <qchat> 标签输出纯文本。",
            "qq_voice": "【输出提示】对方发送了QQ语音消息，回复请用 <qchat voice=true> 标签输出语音。",
            "desktop_speech": "【输出提示】当前是桌面语音对话，回复请用 <speak> 标签输出语音。",
            "desktop_text": "【输出提示】当前是桌面文字输入，回复请用 <qchat> 标签输出。",
        }
        
        hint = source_hints.get(input_source)
        if not hint:
            return
        
        # 在最后一条系统消息或用户消息后追加提示
        for i in range(len(req.messages) - 1, -1, -1):
            msg = req.messages[i]
            role = msg.get("role", "") if isinstance(msg, dict) else getattr(msg, "role", "")
            content = msg.get("content", "") if isinstance(msg, dict) else getattr(msg, "content", "")
            
            if role == "system":
                new_content = content + "\n\n" + hint
                if isinstance(msg, dict):
                    msg["content"] = new_content
                else:
                    msg.content = new_content
                if self.debug_log:
                    logger.debug(f"注入上下文提示到 system prompt: {hint}")
                break

    @on.llm_request(priority=Priority.LOW)
    async def auto_patch_output_tags(self, event: KiraMessageBatchEvent, req: LLMRequest, *_):
        """
        在 LLM 请求阶段：
        1. 检测输入源
        2. 注入上下文提示（引导AI正确使用标签）
        3. 记录本次输入源供后续使用
        """
        if not self.auto_detect:
            return
        
        sid = event.sid
        
        # 检测输入源
        input_source = self._detect_input_source(event)
        
        if input_source:
            self._last_input_source[sid] = input_source
            
            if self.debug_log:
                logger.info(f"[{sid}] 检测到输入源: {input_source}")
            
            # 注入提示引导 AI
            self._inject_context_hint(req, input_source)

    # 注意：KiraAI 的插件系统如果支持 llm_response 事件，
    # 可以在响应阶段做强制补全。但目前我们主要依赖请求阶段的提示注入。
    # 
    # 如果框架支持 on.llm_response，可以取消下面的注释：
    #
    # @on.llm_response(priority=Priority.LOW)
    # async def force_patch_output(self, event, response_text: str, *_):
    #     if not self.force_patch:
    #         return response_text
    #     
    #     sid = event.sid
    #     input_source = self._last_input_source.get(sid)
    #     if not input_source:
    #         return response_text
    #     
    #     expected_tag = self.input_source_map.get(input_source)
    #     if not expected_tag:
    #         return response_text
    #     
    #     if self._should_patch(response_text, expected_tag):
    #         patched = self._patch_tags(response_text, expected_tag)
    #         logger.info(
    #             f"[{sid}] 强制补全标签: "
    #             f"输入={input_source} → 期望标签={expected_tag}"
    #         )
    #         return patched
    #     
    #     return response_text
