using System.Text.RegularExpressions;

namespace AuraChat.Core.Logging;

/// <summary>把日志里的密钥、token 打码（对齐原 Node 版 core/redact.js）。</summary>
public static partial class Redact
{
    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;

        s = SkKey().Replace(s, "sk-***");
        s = Bearer().Replace(s, "Bearer ***");
        s = QueryToken().Replace(s, "$1token=***");
        s = ApiKeyField().Replace(s, "$1\"***\"");
        return s;
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{12,}")]
    private static partial Regex SkKey();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]{12,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"([?&])token=[^&\s""']+")]
    private static partial Regex QueryToken();

    [GeneratedRegex(@"(""(?:apiKey|ttsApiKey|password|token)""\s*:\s*)""[^""]*""")]
    private static partial Regex ApiKeyField();
}
