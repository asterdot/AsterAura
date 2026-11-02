using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuraChat.Core.Ai;
using AuraChat.Core.Runtime;

namespace AuraChat.Features.Media;

/// <summary>媒体模块内部异常：消息本身就是给用户看的中文文案（原版 throw new Error('太大') 那一套）。</summary>
public sealed class MediaException(string message) : Exception(message);

/// <summary>
/// 媒体模块的公共零件：安全抓取（对齐原 core/net.js 的白名单 / 私网拦截 / 超时 / 大小上限）、
/// 图片尺寸与魔数、markdown/沉默判定工具、时间工具、进程运行器。只用 BCL。
/// </summary>
public static class MediaUtil
{
    /// <summary>原 stickersrc.js 的 UA。</summary>
    public const string UserAgent124 =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>原 surf.js 的 UA。</summary>
    public const string UserAgent131 =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public const long DefaultImageMaxBytes = 10L * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttp();
    private static bool _codePages;

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", UserAgent124);
        return client;
    }

    // ---------------- SSRF 防护（原 net.js） ----------------

    /// <summary>确认地址可对外访问：只允许 http/https，拒绝本机与内网（原 assertPublicUrl）。</summary>
    public static async Task AssertPublicUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new MediaException("没有地址");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new MediaException("不认识的地址");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new MediaException("只允许 http / https");

        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new MediaException("不允许访问本机地址");

        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsPrivateIp(literal)) throw new MediaException("不允许访问内网地址");
            return;
        }

        IPAddress[] addrs;
        try
        {
            addrs = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex)
        {
            throw new MediaException($"域名解析失败：{ex.Message}");
        }

        if (addrs.Length == 0) throw new MediaException("不允许访问内网地址");
        foreach (var addr in addrs)
            if (IsPrivateIp(addr)) throw new MediaException("不允许访问内网地址");
    }

    /// <summary>私网 / 保留地址判断（逐条照原 net.js 的 isPrivateIp）。</summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
            var b6 = ip.GetAddressBytes();
            if ((b6[0] & 0xFE) == 0xFC) return true;                 // fc00::/7
            if (b6[0] == 0xFE && (b6[1] & 0xC0) == 0x80) return true; // fe80::/10
            return false;
        }

        var b = ip.GetAddressBytes();
        var a = b[0];
        var c = b[1];
        if (a == 10 || a == 127 || a == 0) return true;
        if (a == 169 && c == 254) return true;
        if (a == 172 && c >= 16 && c <= 31) return true;
        if (a == 192 && c == 168) return true;
        if (a == 100 && c >= 64 && c <= 127) return true;
        if (a >= 224) return true;
        return false;
    }

    // ---------------- 抓取 ----------------

    /// <summary>
    /// 抓一段二进制（原 fetchImage）：支持 base64://、http(s)（带 SSRF 校验）、本地文件。
    /// maxBytes/timeoutMs 与原版一致由调用方给。
    /// </summary>
    public static async Task<byte[]> FetchBytesAsync(
        string src,
        long maxBytes = DefaultImageMaxBytes,
        int timeoutMs = 20_000,
        IDictionary<string, string>? headers = null,
        bool allowLocal = true,
        CancellationToken ct = default)
    {
        src = (src ?? "").Trim();
        if (src.Length == 0) throw new MediaException("没有地址");

        if (src.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            var buf = DecodeBase64(src["base64://".Length..]);
            if (buf.Length > maxBytes) throw new MediaException("太大了");
            return buf;
        }

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await AssertPublicUrlAsync(src, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, src);
            ApplyHeaders(req, headers);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var resp = await SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new MediaException($"HTTP {(int)resp.StatusCode}");
            return await ReadLimitedAsync(resp.Content, maxBytes, cts.Token);
        }

        if (allowLocal && IsLocalPath(src))
        {
            var info = new FileInfo(src);
            if (!info.Exists) throw new MediaException("不认识的地址");
            if (info.Length > maxBytes) throw new MediaException("太大了");
            return await File.ReadAllBytesAsync(src, ct);
        }

        throw new MediaException("不认识的地址");
    }

    /// <summary>抓一段网页文本，返回正文与 content-type（原 surf.js 的 fetchText）。</summary>
    public static async Task<(string Text, string ContentType)> FetchTextAsync(
        string url,
        int timeoutMs = 15_000,
        int maxBytes = 512 * 1024,
        IDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        await AssertPublicUrlAsync(url, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, headers);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var resp = await SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) throw new MediaException($"HTTP {(int)resp.StatusCode}");

        var mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var charset = resp.Content.Headers.ContentType?.CharSet;
        var bytes = await ReadLimitedAsync(resp.Content, maxBytes, cts.Token);
        return (DecodeText(bytes, charset), mediaType);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new MediaException("请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw new MediaException(ex.Message);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage req, IDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrEmpty(value)) continue;
            req.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        var len = content.Headers.ContentLength;
        if (len is > 0 && len > maxBytes) throw new MediaException("太大了");

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new MediaException("太大了");
        }
        return ms.ToArray();
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        var enc = EncodingOf(charset);
        try
        {
            return enc.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static Encoding EncodingOf(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        var cs = charset.Trim().Trim('"').ToLowerInvariant();
        try
        {
            if (cs.Contains("gb") || cs.Contains("936"))
            {
                EnsureCodePages();
                return Encoding.GetEncoding("gb18030");
            }
            return Encoding.GetEncoding(cs);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static void EnsureCodePages()
    {
        if (_codePages) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePages = true;
        }
        catch
        {
            // 拿不到代码页就退回 UTF-8（GBK 页面会乱码，但不会崩）
        }
    }

    /// <summary>原 net.js 的本地路径判断：盘符开头或 / 开头。</summary>
    public static bool IsLocalPath(string src)
        => Regex.IsMatch(src, @"^[A-Za-z]:[\\/]") || src.StartsWith('/');

    /// <summary>base64 / dataUrl 解码（原 web 路由里 stripDataUrl 之后再解）。</summary>
    public static byte[] DecodeBase64(string data)
    {
        var s = data ?? "";
        var comma = s.IndexOf(',');
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) s = s[(comma + 1)..];
        s = Regex.Replace(s, @"\s+", "");
        if (s.Length == 0) return [];
        try
        {
            return Convert.FromBase64String(s);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>简单的 JSON POST（生图 / TTS 用）。</summary>
    public static async Task<(int Status, string Body, string ContentType)> PostJsonAsync(
        string url,
        JsonNode body,
        IDictionary<string, string>? headers = null,
        int timeoutMs = 120_000,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(req, headers);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var resp = await SendAsync(req, cts.Token);
        var bytes = await ReadLimitedAsync(resp.Content, 20L * 1024 * 1024, cts.Token);
        var text = DecodeText(bytes, resp.Content.Headers.ContentType?.CharSet);
        return ((int)resp.StatusCode, text, resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    /// <summary>简单的 GET（TTS 的各家接口用），返回字节。</summary>
    public static async Task<byte[]> GetBytesAsync(
        string url,
        IDictionary<string, string>? headers = null,
        int timeoutMs = 120_000,
        long maxBytes = 20L * 1024 * 1024,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, headers);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var resp = await SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var text = "";
            try { text = DecodeText(await ReadLimitedAsync(resp.Content, 4096, cts.Token), null); } catch { }
            throw new MediaException($"HTTP {(int)resp.StatusCode}：{Slice(text, 120)}");
        }
        return await ReadLimitedAsync(resp.Content, maxBytes, cts.Token);
    }

    // ---------------- 图片（原 core/image.js） ----------------

    /// <summary>读图片宽高；认不出返回 (0,0)。</summary>
    public static (int Width, int Height) ImageSize(byte[] b)
    {
        try
        {
            if (b is null || b.Length < 24) return (0, 0);

            if (b[0] == 0x89 && b[1] == 0x50)
                return ((int)Be32(b, 16), (int)Be32(b, 20));

            if (b[0] == 0x47 && b[1] == 0x49)
                return (Le16(b, 6), Le16(b, 8));

            if (b[0] == 0x52 && b[1] == 0x49 && b[8] == 0x57 && b.Length >= 30)
            {
                var chunk = Encoding.ASCII.GetString(b, 12, 4);
                switch (chunk)
                {
                    case "VP8X":
                        return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
                    case "VP8 ":
                        return (Le16(b, 26) & 0x3FFF, Le16(b, 28) & 0x3FFF);
                    case "VP8L":
                        var bits = (uint)(b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24);
                        return ((int)((bits & 0x3FFF) + 1), (int)(((bits >> 14) & 0x3FFF) + 1));
                }
                return (0, 0);
            }

            if (b[0] == 0xFF && b[1] == 0xD8)
            {
                for (var i = 2; i + 9 < b.Length;)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                        return (Be16(b, i + 7), Be16(b, i + 5));
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    var seg = Be16(b, i + 2);
                    if (seg < 2) { i += 2; continue; }
                    i += 2 + seg;
                }
            }
        }
        catch
        {
            // 图片半截 / 索引越界，都按认不出处理
        }
        return (0, 0);
    }

    /// <summary>按魔数认 MIME；认不出给 application/octet-stream。</summary>
    public static string ImageMime(byte[] b)
    {
        if (b is null || b.Length < 4) return "application/octet-stream";
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b.Length >= 12 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        return "application/octet-stream";
    }

    /// <summary>动图判断：GIF 一律算动图，WebP 看 VP8X 的动画位。</summary>
    public static bool IsAnimated(byte[] b)
    {
        if (b is null || b.Length < 4) return false;
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;
        if (b.Length > 20 && Encoding.ASCII.GetString(b, 12, 4) == "VP8X") return (b[20] & 0x02) != 0;
        return false;
    }

    /// <summary>表情尺寸是否合格（原 stickerSizeOk）。</summary>
    public static bool StickerSizeOk(int width, int height, int bytes, bool animated)
    {
        var maxSide = animated ? 640 : 520;
        var maxBytes = animated ? 4L * 1024 * 1024 : 1536L * 1024;
        if (bytes <= 0 || bytes > maxBytes) return false;
        if (width <= 0 || height <= 0) return true;
        if (width > maxSide || height > maxSide) return false;
        if (animated) return true;
        var ratio = (double)width / Math.Max(1, height);
        return ratio is >= 0.75 and <= 1.33;
    }

    /// <summary>扩展名（原 stickers.js 的 ext）。</summary>
    public static string ExtOfImageMime(string mime) => mime switch
    {
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "jpg",
    };

    private static uint Be32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
    private static int Be16(byte[] b, int i) => b[i] << 8 | b[i + 1];
    private static int Le16(byte[] b, int i) => b[i] | b[i + 1] << 8;

    /// <summary>内容 md5（小写十六进制），表情库文件名用。</summary>
    public static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    // ---------------- 文本（原 chat/reply.js + azone 相关） ----------------

    /// <summary>去 markdown（逐条照原 stripMarkdown 的正则链）。</summary>
    public static string StripMarkdown(string? text)
    {
        var s = text ?? "";
        s = Regex.Replace(s, "```[\\s\\S]*?```", "");
        s = Regex.Replace(s, "`([^`]*)`", "$1");
        s = Regex.Replace(s, "\\*\\*([^*]+)\\*\\*", "$1");
        s = Regex.Replace(s, "__([^_]+)__", "$1");
        s = Regex.Replace(s, "\\*([^*]+)\\*", "$1");
        s = Regex.Replace(s, "^#{1,6}\\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, "^\\s*[-*+]\\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, "^\\s*>\\s?", "", RegexOptions.Multiline);
        s = Regex.Replace(s, "\\[([^\\]]+)\\]\\(([^)]+)\\)", "$1 $2");
        s = Regex.Replace(s, "[ \\t]+$", "", RegexOptions.Multiline);
        s = Regex.Replace(s, "\\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>她这轮是不是不说话（原 isSilence）。</summary>
    public static bool IsSilence(string? text)
    {
        var s = (text ?? "").Trim();
        return s.Length == 0
            || s == "[沉默]"
            || s == "［沉默］"
            || s == "(沉默)"
            || s == "（沉默）";
    }

    /// <summary>清理一条空间留言 / 回复（原 azone-social.js 的 cleanComment）。</summary>
    public static string CleanComment(string? text, int max = 60)
    {
        var s = StripMarkdown(text);
        s = s.Replace("|||", " ");
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = s.Trim();
        if (IsSilence(s)) return "";

        var lines = s.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(2);
        s = string.Join(" ", lines);
        return Slice(s, max).Trim();
    }

    /// <summary>截断到 max 个字符（原 String(x).slice(0,n)）。</summary>
    public static string Slice(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        return text.Length <= max ? text : text[..max];
    }

    /// <summary>去掉正文首尾的引号（原 azone 清洗里的一段）。</summary>
    public static string TrimQuotes(string? text)
    {
        var s = (text ?? "").Trim();
        var quotes = "「」“”\"'‘’";
        return s.Trim(quotes.ToCharArray()).Trim();
    }

    // ---------------- 时间 ----------------

    public static string DayKey(DateTime? when = null) => (when ?? DateTime.Now).ToString("yyyy-MM-dd");

    public static string Clock(DateTime when) => when.ToString("HH:mm");

    /// <summary>"HH:MM" 转当天分钟数；格式不对返回 null。</summary>
    public static int? ToMinutes(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return null;
        var m = Regex.Match(hhmm.Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value);
        var min = int.Parse(m.Groups[2].Value);
        if (h > 23 || min > 59) return null;
        return h * 60 + min;
    }

    public static string Hhmm(int minutes)
    {
        var m = ((minutes % 1440) + 1440) % 1440;
        return $"{m / 60:00}:{m % 60:00}";
    }

    /// <summary>时段判断，支持跨零点（原 time.js 的 inRange）。</summary>
    public static bool InRange(DateTime now, string? from, string? to)
    {
        var a = ToMinutes(from);
        var b = ToMinutes(to);
        if (a is null || b is null) return true;
        var n = now.Hour * 60 + now.Minute;
        return a <= b ? n >= a && n < b : n >= a || n < b;
    }

    // ---------------- 模型 ----------------

    /// <summary>问一次模型（没有模型就返回 null，调用方自己降级成中文失败文案）。</summary>
    public static async Task<string?> AskModelAsync(
        FeatureHost host,
        string system,
        string text,
        double temperature,
        int maxTokens,
        int timeoutMs = 120_000,
        CancellationToken ct = default)
    {
        var llm = host.Get<ILlmClient>();
        if (llm is null) return null;

        var options = new LlmOptions
        {
            Provider = host.Str("model", "provider"),
            Model = host.Str("model", "model"),
            System = system,
            Temperature = temperature,
            MaxTokens = maxTokens,
            TimeoutMs = timeoutMs,
        };

        try
        {
            var answer = await llm.AskAsync(options, [new LlmMessage("user", text)], ct);
            return string.IsNullOrWhiteSpace(answer) ? null : answer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            host.Log.Child("media").Warn("问模型失败：", ex.Message);
            return null;
        }
    }

    /// <summary>从一段可能带解释的文本里抠出第一个 JSON 对象（原 surf.js 的 /\{[^]*\}/ 取法）。</summary>
    public static JsonObject? ExtractJsonObject(string? text)
    {
        var s = text ?? "";
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonNode.Parse(s[start..(end + 1)]) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从一段文本里抠出第一个 JSON 数组。</summary>
    public static JsonArray? ExtractJsonArray(string? text)
    {
        var s = text ?? "";
        var start = s.IndexOf('[');
        var end = s.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonNode.Parse(s[start..(end + 1)]) as JsonArray;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>进程运行器（TTS 调 python / powershell 用）。超时杀进程，并把「超时」写进 stderr。</summary>
public static class MediaProc
{
    public static async Task<(int Code, string Out, string Err)> RunAsync(
        string file,
        IEnumerable<string> args,
        int timeoutMs = 60_000,
        string? input = null,
        IDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, "", $"启动失败：{ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            process.StandardInput.Close();
        }
        catch
        {
            // 进程提前退出时写不进去是正常的
        }

        var exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        }

        string outText, errText;
        try { outText = await stdout; } catch { outText = ""; }
        try { errText = await stderr; } catch { errText = ""; }
        if (!exited) errText += "\n超时";
        return (exited ? process.ExitCode : -1, outText, errText);
    }
}
