using System.Text.RegularExpressions;

namespace AuraChat.Features.Chat;

/// <summary>一条气泡的种类（对齐原 reply.js 的 kind）。</summary>
public enum ReplyKind
{
    Text,
    Voice,
    Sticker,
    Image,
    Poke,
    Silence,
}

/// <summary>
/// 一条要发出去的气泡。Text 是正文（voice 的转写文本也放这里），
/// StickerTag 只在 Kind=Sticker 时有值（<sticker:标签> 的标签）。
/// </summary>
public sealed record ReplyItem(ReplyKind Kind, string Text = "", string StickerTag = "")
{
    /// <summary>引用回复编号（&lt;reply:N&gt;），只挂在第一条气泡上。</summary>
    public string? ReplyTo { get; set; }

    public bool IsSendable => Kind is not ReplyKind.Silence and not ReplyKind.Poke;

    public static ReplyItem OfText(string text) => new(ReplyKind.Text, text);
    public static ReplyItem OfVoice(string text) => new(ReplyKind.Voice, text);
    public static ReplyItem OfSticker(string tag) => new(ReplyKind.Sticker, "", tag);
    public static ReplyItem OfImage(string url) => new(ReplyKind.Image, url);
    public static ReplyItem OfPoke() => new(ReplyKind.Poke);
}

/// <summary>Parse 的完整结果：silent=true 表示她这轮选择不说话。</summary>
public sealed record ReplyParseResult(bool Silent, List<ReplyItem> Items);

/// <summary>
/// 模型回复拆条。逐字对齐原 chat/reply.js：
/// 按 ||| 与换行拆气泡、去 markdown、去「小肥鱼：」这类前缀、
/// 解析 &lt;sticker&gt; / &lt;voice&gt; / &lt;reply&gt; / &lt;poke&gt; / ![](url)，最多 5 个文字气泡。
/// </summary>
public static class ReplyParser
{
    public const string SilenceText = "[沉默]";
    private const int MaxBubbles = 5;

    private static readonly Regex FenceRe = new(@"```[a-zA-Z0-9_-]*\n?([\s\S]*?)```", RegexOptions.Compiled);
    private static readonly Regex CodeRe = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex BoldRe = new(@"\*\*([^*\n]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex UnderlineRe = new(@"__([^_\n]+)__", RegexOptions.Compiled);
    private static readonly Regex ItalicRe = new(@"(^|[^*])\*([^*\n]+)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex HeadingRe = new(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex BulletRe = new(@"^\s*[-*+]\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex QuoteRe = new(@"^\s*>\s?", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex LinkRe = new(@"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex TrailingSpaceRe = new(@"[ \t]+$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex BlankLinesRe = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex BrokenReplyTagRe = new(@"<\/?reply[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ListLineRe = new(@"^(\d+[.、)]|[-*•·])", RegexOptions.Compiled);
    private static readonly Regex NewlinesRe = new(@"\n+", RegexOptions.Compiled);
    private static readonly Regex PrefixRe = new(@"^(小肥鱼|大肥鱼|Aura|我|她|旁白)\s*[：:]\s*", RegexOptions.Compiled);

    /// <summary>气泡内的标记语法（原 reply.js:38）。分组：1=reply 编号，2=voice 正文，3=sticker 标签，4=图片地址。</summary>
    private static readonly Regex MarkerRe = new(
        @"<poke\s*/?>|<reply:\s*(\d+)\s*>|<voice>([\s\S]*?)</voice>|<sticker:([^>]+?)>(?:\s*</sticker>)?|!\[[^\]]*\]\(\s*([^)\s]+)\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex VoiceWantRe = new(@"(用|发|来|给我|录)[^，。？！\n]{0,4}语音|语音[^，。？！\n]{0,4}(说|发|讲|回|念|唱)", RegexOptions.Compiled);

    /// <summary>按 ||| 与换行拆条。整条是沉默时返回空列表。</summary>
    public static List<ReplyItem> Parse(string? reply) => ParseDetailed(reply).Items;

    /// <summary>带 silent 标记的拆条结果，聊天流水线用它判断「她选择不说话」。</summary>
    public static ReplyParseResult ParseDetailed(string? reply)
    {
        var raw = (reply ?? "").Trim();
        if (IsSilence(raw)) return new ReplyParseResult(true, []);
        var items = new List<ReplyItem>();
        string? replyTo = null;

        // 引用编号挂到「下一条」气泡上；那段文本全被过滤掉就顺延给后面的条目。
        void Attach(int from)
        {
            if (string.IsNullOrEmpty(replyTo) || items.Count <= from) return;
            items[from].ReplyTo = replyTo;
            replyTo = null;
        }

        foreach (var bubble in raw.Split("|||"))
        {
            var part = bubble.Trim();
            if (part.Length == 0) continue;

            var last = 0;
            foreach (Match m in MarkerRe.Matches(part))
            {
                var from = items.Count;
                PushText(items, part[last..m.Index]);
                Attach(from);

                from = items.Count;
                if (m.Value.StartsWith("<poke", StringComparison.Ordinal))
                {
                    if (!items.Exists(i => i.Kind == ReplyKind.Poke)) items.Add(ReplyItem.OfPoke());
                }
                else if (m.Groups[1].Success)
                {
                    replyTo = m.Groups[1].Value;
                }
                else if (m.Groups[2].Success)
                {
                    var v = StripMarkdown(m.Groups[2].Value);
                    if (v.Length > 0) items.Add(ReplyItem.OfVoice(v));
                }
                else if (m.Groups[3].Success)
                {
                    var v = m.Groups[3].Value.Trim();
                    if (v.Length > 0) items.Add(ReplyItem.OfSticker(v));
                }
                else if (m.Groups[4].Success)
                {
                    items.Add(ReplyItem.OfImage(m.Groups[4].Value));
                }

                Attach(from);
                last = m.Index + m.Length;
            }

            var tailFrom = items.Count;
            PushText(items, part[last..]);
            Attach(tailFrom);
        }

        // 引用只留第一次，后面的去掉（原 reply.js:66-71）
        var quoted = false;
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.ReplyTo)) continue;
            if (quoted) it.ReplyTo = null;
            quoted = true;
        }

        if (items.Count == 0) return new ReplyParseResult(true, []);
        return new ReplyParseResult(false, CapBubbles(items));
    }

    /// <summary>去掉 markdown 记号（原 reply.js:3-17）。</summary>
    public static string StripMarkdown(string? text)
    {
        var s = text ?? "";
        s = FenceRe.Replace(s, "$1");
        s = CodeRe.Replace(s, "$1");
        s = BoldRe.Replace(s, "$1");
        s = UnderlineRe.Replace(s, "$1");
        s = ItalicRe.Replace(s, "$1$2");
        s = HeadingRe.Replace(s, "");
        s = BulletRe.Replace(s, "");
        s = QuoteRe.Replace(s, "");
        s = LinkRe.Replace(s, "$1 $2");
        s = TrailingSpaceRe.Replace(s, "");
        s = BlankLinesRe.Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>空串 / [沉默] / ［沉默］ / (沉默) / （沉默） / 【沉默】 都算沉默。</summary>
    public static bool IsSilence(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        return t is SilenceText or "［沉默］" or "(沉默)" or "（沉默）" or "【沉默】";
    }

    /// <summary>一段文本拆成几个气泡（原 reply.js:93-102）。</summary>
    public static List<string> SplitLines(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return [];
        if (t.Contains("```", StringComparison.Ordinal) || t.Length > 240) return [t];

        var lines = NewlinesRe.Split(t)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (lines.Count <= 1) return lines;

        var listy = lines.Count(l => ListLineRe.IsMatch(l));
        if (listy >= Math.Ceiling(lines.Count / 2.0)) return [t];
        return lines;
    }

    /// <summary>
    /// 气泡间隔（原 reply.js bubbleDelay）：typing = 350 + 字数*110（约每秒 9 字），
    /// 夹在 700~4500ms，再乘 (0.8 + rand*0.5)；最后乘心情的 delay 倍数。
    /// </summary>
    public static int BubbleDelayMs(int textLength, double delayFactor = 1.0, Func<double>? rand = null)
    {
        var typing = 350.0 + Math.Max(0, textLength) * 110.0;
        var baseMs = Math.Min(4500.0, Math.Max(700.0, typing));
        var r = rand?.Invoke() ?? Random.Shared.NextDouble();
        var factor = delayFactor > 0 ? delayFactor : 1.0;
        return (int)Math.Round(baseMs * (0.8 + r * 0.5) * factor, MidpointRounding.AwayFromZero);
    }

    /// <summary>按下一条文本的长度算间隔（DelayFactor 传心情的 delay 倍数，缺省 1）。</summary>
    public static int BubbleDelayMs(string? nextText, double delayFactor = 1.0, Func<double>? rand = null)
        => BubbleDelayMs(nextText?.Length ?? 0, delayFactor, rand);

    /// <summary>这轮回复是不是在要求语音（原 reply.js:112-116）。</summary>
    public static bool WantsVoice(string? text)
    {
        var t = text ?? "";
        if (t.Contains("不要语音", StringComparison.Ordinal) || t.Contains("别发语音", StringComparison.Ordinal)) return false;
        return VoiceWantRe.IsMatch(t);
    }

    // ---------- 内部 ----------

    private static void PushText(List<ReplyItem> items, string? chunk)
    {
        // 写坏的 <reply…> 标签直接去掉，再剥 markdown
        var t = StripMarkdown(BrokenReplyTagRe.Replace(chunk ?? "", ""));
        if (t.Length == 0 || IsSilence(t)) return;
        foreach (var line in SplitLines(t))
        {
            // 去掉「小肥鱼：」「Aura：」「我：」这类说话人前缀
            var clean = PrefixRe.Replace(line, "").Trim();
            if (clean.Length == 0 || IsSilence(clean)) continue;
            items.Add(ReplyItem.OfText(clean));
        }
    }

    /// <summary>文字气泡最多 5 条：保留前 4 条，第 5 条起合并成一条（原 reply.js capBubbles）。</summary>
    private static List<ReplyItem> CapBubbles(List<ReplyItem> items)
    {
        var texts = items.Where(i => i.Kind == ReplyKind.Text).ToList();
        if (texts.Count <= MaxBubbles) return items;

        var keep = texts.Take(MaxBubbles - 1).ToList(); // 按引用比对，不能用值相等
        var merged = new ReplyItem(ReplyKind.Text, string.Join("\n", texts.Skip(MaxBubbles - 1).Select(t => t.Text)));
        var outItems = new List<ReplyItem>(items.Count);
        var placed = false;
        foreach (var it in items)
        {
            if (it.Kind != ReplyKind.Text || keep.Any(k => ReferenceEquals(k, it))) outItems.Add(it);
            else if (!placed)
            {
                outItems.Add(merged);
                placed = true;
            }
        }
        return outItems;
    }
}
