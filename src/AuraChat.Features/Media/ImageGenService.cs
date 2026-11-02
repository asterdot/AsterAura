using System.Diagnostics;
using System.Text.Json.Nodes;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Media;

/// <summary>
/// 生图（逐条照原 features/imagegen.js）：openai / sdwebui / comfyui / local / code 五种 provider，
/// 超时 240 秒；结果既能给调用方字节，也能直接落进 MediaStore。
/// </summary>
public sealed class ImageGenService
{
    /// <summary>表情自画用的统一画风（原 STICKER_STYLE，一个字都别改）。</summary>
    public const string StickerStyle =
        "Q 版大头贴表情，蓝色头发、发尾浅海蓝、头顶一撮呆毛的女孩，粗黑描边，平涂，纯白背景，表情夸张好认，画面干净";

    private const int TimeoutMs = 240_000;

    private readonly FeatureHost _host;
    private readonly MediaStore _media;
    private readonly AreaLog _log;

    public ImageGenService(FeatureHost host, MediaStore media)
    {
        _host = host;
        _media = media;
        _log = host.Log.Child("image");
    }

    private string Provider => _host.Text("openai", "imageGen", "provider");
    private string BaseUrl => _host.Text("", "imageGen", "baseUrl").TrimEnd('/');
    private string Model => _host.Text("", "imageGen", "model");
    private string SizeText => _host.Text("1024x1024", "imageGen", "size");
    private int Steps => _host.Int(25, "imageGen", "steps");
    private double CfgScale => _host.Num(7, "imageGen", "cfgScale");
    private string Sampler => _host.Text("DPM++ 2M Karras", "imageGen", "sampler");
    private string NegativePrompt => _host.Text("", "imageGen", "negativePrompt");
    private int _drawCount;

    /// <summary>生图开着没有（原 imagegen.enabled）。</summary>
    public bool Enabled()
    {
        if (_host.Flag(false, "local", "image")) return true;
        if (!_host.Flag(false, "imageGen", "enabled")) return false;
        var provider = Provider;
        return provider == "code" || BaseUrl.Length > 0;
    }

    /// <summary>画一张，返回图片字节；失败抛 MediaException（中文原因）。</summary>
    public async Task<byte[]> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        if (_host.Flag(false, "local", "image"))
            throw new MediaException("本地生图还没接上，先在设置里选一个生图服务");

        if (!_host.Flag(false, "imageGen", "enabled")) throw new MediaException("生图还没开启");

        var provider = Provider;
        if (provider == "code") throw new MediaException("代码画图由模型自己调用工具完成，这里不生成");

        var started = Stopwatch.StartNew();
        var full = StyleOf(prompt);
        var bytes = provider switch
        {
            "sdwebui" => await ViaSdWebUiAsync(full, ct),
            "comfyui" => await ViaComfyUiAsync(full, ct),
            _ => await ViaOpenAiAsync(full, ct),
        };

        _log.Info($"画好了一张图（{bytes.Length / 1024}KB，{started.Elapsed.TotalSeconds:0.0}s）：{MediaUtil.Slice(prompt, 40)}");
        return bytes;
    }

    /// <summary>画一张并存进媒体库（表情自画 / 生图工具用）。</summary>
    public async Task<(string MediaId, int Bytes, byte[] Data)> GenerateToMediaAsync(string chatId, string prompt, CancellationToken ct = default)
    {
        var bytes = await GenerateAsync(prompt, ct);
        var (mediaId, size) = _media.Save(chatId, bytes, MediaUtil.ImageMime(bytes));
        return (mediaId, size, bytes);
    }

    /// <summary>画风前缀 + prompt（原 styleOf）。</summary>
    private string StyleOf(string prompt)
    {
        var style = _host.Text("", "imageGen", "stylePrompt").Trim();
        return string.IsNullOrEmpty(style) ? prompt : $"{style}，{prompt}";
    }

    /// <summary>自画表情的计数（原 selfDrive 的 drawCount 在这一层也留一份，方便面板看）。</summary>
    public int DrawCount => _drawCount;

    public JsonNode? Snapshot() => new JsonObject
    {
        ["enabled"] = Enabled(),
        ["provider"] = Provider,
        ["ownerOnly"] = _host.Flag(true, "imageGen", "ownerOnly"),
        ["size"] = SizeText,
        ["steps"] = Steps,
        ["cfgScale"] = CfgScale,
    };

    private (int Width, int Height) Size()
    {
        var text = SizeText;
        var parts = text.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return (512, 512);
    }

    // ---------------- openai（也兜住一切 OpenAI 兼容服务） ----------------

    private async Task<byte[]> ViaOpenAiAsync(string prompt, CancellationToken ct)
    {
        if (BaseUrl.Length == 0 || Model.Length == 0) throw new MediaException("还没填生图服务地址或模型");

        var headers = new Dictionary<string, string>();
        var apiKey = _host.Text("", "imageGen", "apiKey");
        if (apiKey.Length > 0) headers["authorization"] = $"Bearer {apiKey}";

        var body = new JsonObject
        {
            ["model"] = Model,
            ["prompt"] = prompt,
            ["size"] = SizeText,
            ["n"] = 1,
            ["response_format"] = "b64_json",
            ["image_size"] = SizeText,
        };

        var (status, text, _) = await MediaUtil.PostJsonAsync($"{BaseUrl}/images/generations", body, headers, TimeoutMs, ct);
        JsonObject? json = null;
        try { json = JsonNode.Parse(text) as JsonObject; } catch { }

        if (status is < 200 or >= 300)
        {
            var message = json?["error"]?["message"]?.ToString() ?? json?["message"]?.ToString() ?? $"HTTP {status}";
            throw new MediaException(message);
        }
        if (json is null) throw new MediaException("生图服务没有返回图片");

        var first = (json["data"] as JsonArray ?? json["images"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (first is null) throw new MediaException("生图服务没有返回图片");

        var b64 = first["b64_json"]?.ToString();
        if (!string.IsNullOrEmpty(b64)) return MediaUtil.DecodeBase64(b64);

        var url = first["url"]?.ToString();
        if (!string.IsNullOrEmpty(url))
            return await MediaUtil.FetchBytesAsync(url, 20L * 1024 * 1024, TimeoutMs, null, allowLocal: false, ct);

        throw new MediaException("生图服务没有返回图片");
    }

    // ---------------- SD WebUI ----------------

    private async Task<byte[]> ViaSdWebUiAsync(string prompt, CancellationToken ct)
    {
        var baseUrl = BaseUrl.Length > 0 ? BaseUrl : "http://127.0.0.1:7860";
        var (width, height) = Size();

        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["negative_prompt"] = NegativePrompt,
            ["steps"] = Steps,
            ["cfg_scale"] = CfgScale,
            ["sampler_name"] = Sampler,
            ["width"] = width,
            ["height"] = height,
            ["batch_size"] = 1,
            ["n_iter"] = 1,
        };

        var (status, text, _) = await MediaUtil.PostJsonAsync($"{baseUrl}/sdapi/v1/txt2img", body, null, TimeoutMs, ct);
        if (status is < 200 or >= 300) throw new MediaException($"SD WebUI 报错 HTTP {status}");

        JsonObject? json = null;
        try { json = JsonNode.Parse(text) as JsonObject; } catch { }
        var b64 = (json?["images"] as JsonArray)?.FirstOrDefault()?.ToString();
        if (string.IsNullOrEmpty(b64)) throw new MediaException("SD WebUI 没有返回图片");

        var bytes = MediaUtil.DecodeBase64(b64);
        if (bytes.Length == 0) throw new MediaException("SD WebUI 没有返回图片");
        return bytes;
    }

    // ---------------- ComfyUI ----------------

    private async Task<byte[]> ViaComfyUiAsync(string prompt, CancellationToken ct)
    {
        var baseUrl = BaseUrl.Length > 0 ? BaseUrl : "http://127.0.0.1:8188";
        var checkpoint = Model.Length > 0 ? Model : "v1-5-pruned-emaonly.safetensors";
        var (width, height) = Size();
        var seed = Random.Shared.NextInt64(0, 1_000_000_000_000);

        var workflow = new JsonObject
        {
            ["3"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["seed"] = seed,
                    ["steps"] = Steps,
                    ["cfg"] = CfgScale,
                    ["sampler_name"] = Sampler,
                    ["scheduler"] = "normal",
                    ["denoise"] = 1,
                    ["model"] = new JsonArray("4", 0),
                    ["positive"] = new JsonArray("6", 0),
                    ["negative"] = new JsonArray("7", 0),
                    ["latent_image"] = new JsonArray("5", 0),
                },
            },
            ["4"] = new JsonObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JsonObject { ["ckpt_name"] = checkpoint },
            },
            ["5"] = new JsonObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = 1 },
            },
            ["6"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject { ["text"] = prompt, ["clip"] = new JsonArray("4", 1) },
            },
            ["7"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject { ["text"] = NegativePrompt, ["clip"] = new JsonArray("4", 1) },
            },
            ["8"] = new JsonObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JsonObject { ["samples"] = new JsonArray("3", 0), ["vae"] = new JsonArray("4", 2) },
            },
            ["9"] = new JsonObject
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new JsonObject { ["filename_prefix"] = "aurachat", ["images"] = new JsonArray("8", 0) },
            },
        };

        var (status, text, _) = await MediaUtil.PostJsonAsync($"{baseUrl}/prompt", new JsonObject { ["prompt"] = workflow }, null, 30_000, ct);
        JsonObject? submitted = null;
        try { submitted = JsonNode.Parse(text) as JsonObject; } catch { }
        var promptId = submitted?["prompt_id"]?.ToString();
        if (status is < 200 or >= 300 || string.IsNullOrEmpty(promptId))
            throw new MediaException($"提交工作流失败 HTTP {status}");

        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1500, ct);
            var historyUrl = $"{baseUrl}/history/{Uri.EscapeDataString(promptId)}";
            string historyText;
            try
            {
                var bytes = await MediaUtil.GetBytesAsync(historyUrl, null, 15_000, 4 * 1024 * 1024, ct);
                historyText = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                continue;
            }

            JsonObject? history;
            try { history = JsonNode.Parse(historyText) as JsonObject; } catch { continue; }
            var outputs = history?[promptId]?["outputs"] as JsonObject;
            var image = outputs?.Select(pair => (pair.Value?["images"] as JsonArray)?.FirstOrDefault() as JsonObject)
                .FirstOrDefault(item => item is not null);
            if (image is null) continue;

            var filename = image["filename"]?.ToString() ?? "";
            var subfolder = image["subfolder"]?.ToString() ?? "";
            var type = image["type"]?.ToString() ?? "output";
            var viewUrl = $"{baseUrl}/view?filename={Uri.EscapeDataString(filename)}" +
                          $"&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
            return await MediaUtil.GetBytesAsync(viewUrl, null, 60_000, 20L * 1024 * 1024, ct);
        }

        throw new MediaException("ComfyUI 画太久没出图");
    }
}
