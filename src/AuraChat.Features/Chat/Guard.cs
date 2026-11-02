using System.Text.RegularExpressions;
using AuraChat.Core.Ai;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;

namespace AuraChat.Features.Chat;

/// <summary>
/// 旁白过滤（逐字对齐原 chat/guard.js）：
/// 第一层纯正则，抓「旁白：」「（笑）」这种明显的舞台说明；
/// 第二层可选（配置 guard.enabled = true）：拿本地小模型看 top_logprobs，
/// 算 logprob(旁) - logprob(说)，差得够多就当成旁白删掉。
/// </summary>
public sealed class Guard
{
    /// <summary>并发批数（原版 CONCURRENCY = 4）。</summary>
    private const int Concurrency = 4;

    /// <summary>默认判定阈值：margin ≥ 0.6 才算旁白。</summary>
    private const double DefaultThreshold = 0.6;

    /// <summary>默认小模型名（对齐 guard.js 的 g.model || 'guard'）。</summary>
    private const string DefaultModel = "guard";

    /// <summary>本地小模型超时（毫秒），原版默认 4000。</summary>
    private const int DefaultTimeoutMs = 4000;

    /// <summary>默认要几个 top_logprobs。</summary>
    private const int DefaultTopLogprobs = 25;

    // ---------- 第一层：正则（原 guard.js:7-41，一行都不能改） ----------

    private static readonly Regex LabelHeadRe = new(
        @"^(回复|回答|答|旁白|内心|备注|注|说明|计划|动作|神态|语气|思路|分析|小结|总结|待办|下一步)\s*[：:]",
        RegexOptions.Compiled);

    private static readonly Regex LabelLineRe = new(
        @"^(回复|答|接着回|然后(?:再)?加|不过要注意|这里第)",
        RegexOptions.Compiled);

    private static readonly Regex BracketedRe = new(
        @"^[\[【][^\]】]{0,20}[\]】]$",
        RegexOptions.Compiled);

    private static readonly Regex ParenRe = new(
        @"^[（(]([^）)]*)[）)]$",
        RegexOptions.Compiled);

    private static readonly Regex ParenNarrativeStartRe = new(
        @"^(心想|心里想|暗自|旁白|备注|注|动作|神态|沉默|未发送|以上)",
        RegexOptions.Compiled);

    private static readonly Regex FxLineRe = new(
        @"听不清具体内容|自然地接话即可|没转成文字，只听到声音",
        RegexOptions.Compiled);

    /// <summary>括号里只是「笑」「叹气」这种动作词（原 SHORT_ACTION 20 个）。</summary>
    private static readonly string[] ShortActions =
    [
        "笑", "哭", "叹气", "点头", "摇头", "皱眉", "愣", "愣住", "沉默", "微笑",
        "偷笑", "翻白眼", "摊手", "耸肩", "歪头", "撇嘴", "眨眼", "挠头", "抱抱", "亲亲",
    ];

    /// <summary>旁白/说话二分类的系统提示词（原 guard.js:SYS，一字不改）。</summary>
    public const string SystemPrompt =
        """
        判断一行聊天文本是「旁白」还是「说话」。

        【旁白】剧本式的描述：她做了什么动作、什么表情、心里怎么想，或者写给自己的备注。
        例：「她愣了一下」「她低下头」「她抬起头看了他一眼」「（笑）」「（叹气）」「【探头】」「（心想：他不高兴）」「（停顿了一下）」「（不知该说什么好）」「旁白：她走过去」「回复：好的」「这里应该温柔一点」「语气放软一点」「想想要怎么回他」

        【说话】她真要发给对方的话。可以是任何内容，长度不限。
        例：「（我不管）」「（真是的，这么晚还不睡）」「（哼）」「（嗯）」「（小声）」「那你听好：」「我想说的是：」「在发呆。」「我在」「喂」

        注意：
        - 带括号不代表旁白。括号里是她在表演语气/发出声音（哼、嗯、小声、算了）→ 说话。
        - 带冒号不代表旁白。「我想说的是：」「我跟你讲：」是说话。
        - 只有像剧本那样描述她的动作和神态，才是旁白。

        只回答一个字：旁 或 说
        """;

    private readonly FeatureHost _host;
    private readonly AreaLog _log;

    public Guard(FeatureHost host)
    {
        _host = host;
        _log = host.Log.Child("guard");
    }

    /// <summary>这一轮的配置快照（每次过滤重新读，改配置不用重启）。</summary>
    public sealed record GuardOptions(
        bool Enabled,
        string Model,
        int TimeoutMs,
        double Threshold,
        int TopLogprobs,
        string SystemPrompt);

    public GuardOptions Options()
    {
        var model = _host.Text(DefaultModel, "guard", "model");
        var prompt = _host.Text(SystemPrompt, "guard", "systemPrompt");
        var top = _host.Int(DefaultTopLogprobs, "guard", "topLogprobs");
        return new GuardOptions(
            Enabled: _host.Flag(false, "guard", "enabled"),
            Model: string.IsNullOrWhiteSpace(model) ? DefaultModel : model,
            TimeoutMs: Math.Max(100, _host.Int(DefaultTimeoutMs, "guard", "timeoutMs")),
            Threshold: _host.Num(DefaultThreshold, "guard", "threshold"),
            TopLogprobs: Math.Clamp(top <= 0 ? DefaultTopLogprobs : top, 1, 100),
            SystemPrompt: string.IsNullOrWhiteSpace(prompt) ? SystemPrompt : prompt);
    }

    /// <summary>
    /// 过一遍气泡：旁白/舞台说明丢掉，其余原样保留（顺序不变）。
    /// 只看文字气泡；表情、语音、图片、poke 不参与判定。
    /// </summary>
    public async Task<List<ReplyItem>> FilterAsync(List<ReplyItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return items;

        var keep = new bool[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            keep[i] = items[i].Kind != ReplyKind.Text || !IsPlannerLine(items[i].Text);
        }

        var options = Options();
        if (!options.Enabled)
        {
            return Pick(items, keep);
        }

        // 第二层：本地小模型逐行判。取不到模型就只靠正则。
        var llm = _host.Get<ILlmClient>();
        if (llm is null)
        {
            _log.Warn("guard.enabled 打开了，但没装 ILlmClient，这一轮只靠正则");
            return Pick(items, keep);
        }

        var todo = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (keep[i] && items[i].Kind == ReplyKind.Text && !string.IsNullOrWhiteSpace(items[i].Text))
            {
                todo.Add(i);
            }
        }
        if (todo.Count == 0) return Pick(items, keep);

        var warned = 0;
        var batches = new List<Task<(int Index, double? Margin)[]>>();
        for (var start = 0; start < todo.Count; start += Concurrency)
        {
            var batch = todo.Skip(start).Take(Concurrency).ToList();
            batches.Add(Task.WhenAll(batch.Select(async idx =>
            {
                try
                {
                    var margin = await MarginOfAsync(llm, options, items[idx].Text, ct).ConfigureAwait(false);
                    return (Index: idx, Margin: margin);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 原版：同一批失败只 warn 一次，这批按「判不出来」处理（保留）
                    if (Interlocked.CompareExchange(ref warned, 1, 0) == 0)
                    {
                        _log.Warn("旁白判定失败，这一轮只靠正则：{0}", ex.Message);
                    }
                    return (Index: idx, Margin: (double?)null);
                }
            })));
        }

        foreach (var results in await Task.WhenAll(batches).ConfigureAwait(false))
        {
            foreach (var (index, margin) in results)
            {
                if (margin is not null && margin.Value >= options.Threshold)
                {
                    keep[index] = false;
                }
            }
        }

        return Pick(items, keep);
    }

    /// <summary>这一行像不像旁白/舞台说明（只有正则，不问模型）。</summary>
    public static bool IsPlannerLine(string? line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0) return false;

        if (FxLineRe.IsMatch(text)) return true;
        if (BracketedRe.IsMatch(text)) return true;
        if (LabelHeadRe.IsMatch(text)) return true;

        // 「回复…」「接着回…」是说话，但整行很短时仍按旁白处理（原版判据）
        if (text.Length <= 60 && LabelLineRe.IsMatch(text)) return true;

        var m = ParenRe.Match(text);
        if (m.Success)
        {
            var inner = m.Groups[1].Value;
            if (inner.Contains('：') || inner.Contains(':')) return true;
            if (ParenNarrativeStartRe.IsMatch(inner)) return true;
            if (ShortActions.Any(a => inner.Contains(a, StringComparison.Ordinal))) return true;
        }

        return false;
    }

    /// <summary>
    /// 问小模型：logprob(旁) - logprob(说)。
    /// 结果 null = 判不出来（保留）；+∞ = 有「旁」没「说」（删）。
    /// </summary>
    public async Task<double?> MarginOfAsync(ILlmClient llm, GuardOptions options, string line, CancellationToken ct = default)
    {
        var request = new LlmOptions
        {
            Provider = _host.Text("", "guard", "provider"),
            Model = options.Model,
            System = options.SystemPrompt,
            Temperature = 0,
            MaxTokens = 1,
            TopLogprobs = options.TopLogprobs,
            TimeoutMs = options.TimeoutMs,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.TimeoutMs);

        LlmLogprobResult result;
        try
        {
            result = await llm.AskWithLogprobsAsync(request, [new LlmMessage("user", line)], cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // 超时按判不出来算
        }

        if (result?.Tokens is not { Count: > 0 }) return null;
        var first = result.Tokens[0];

        var p = double.NegativeInfinity; // 旁
        var s = double.NegativeInfinity; // 说

        void Consider(string? token, double logprob)
        {
            if (string.IsNullOrEmpty(token)) return;
            if (token.Contains('旁', StringComparison.Ordinal)) p = Math.Max(p, logprob);
            if (token.Contains('说', StringComparison.Ordinal)) s = Math.Max(s, logprob);
        }

        Consider(first.Token, first.Logprob);
        foreach (var (token, logprob) in first.Top)
        {
            Consider(token, logprob);
        }

        if (double.IsNegativeInfinity(p) && double.IsNegativeInfinity(s)) return null;
        if (double.IsNegativeInfinity(s)) return double.PositiveInfinity;
        return p - s;
    }

    private static List<ReplyItem> Pick(List<ReplyItem> items, bool[] keep)
    {
        var result = new List<ReplyItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (keep[i]) result.Add(items[i]);
        }
        return result;
    }
}
