using System.Globalization;
using System.Text.Json.Nodes;

namespace AuraChat.Core;

/// <summary>
/// JsonNode 取值的小助手。System.Text.Json 的 GetValue&lt;double&gt;() 遇到整数会抛异常，
/// 而我们的配置里端口、次数这类值多数是整数，所以统一走这里转换。
/// </summary>
public static class JsonUtil
{
    public static double Num(JsonNode? node, double fallback)
    {
        if (node is null) return fallback;
        if (node is JsonValue value && value.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    public static int Int(JsonNode? node, int fallback) => (int)Math.Round(Num(node, fallback));

    public static bool Bool(JsonNode? node, bool fallback)
    {
        if (node is null) return fallback;
        if (node is JsonValue value && value.TryGetValue<bool>(out var b)) return b;
        var s = node.ToString();
        return s is "true" ? true : s is "false" ? false : fallback;
    }

    public static string Str(JsonNode? node, string fallback)
    {
        var s = node?.ToString();
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
