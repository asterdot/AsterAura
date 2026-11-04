using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Chat;
using Xunit;

namespace AuraChat.Tests;

/// <summary>B1 红绿测试：用户发来的照片要变成模型能看的多模态输入（image_url），而不是一句"（还发了 N 张图片）"。</summary>
public class ImageContextTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly ConfigStore _config;
    private readonly FeatureHost _host;
    private readonly MediaStore _media;
    private readonly HistoryStore _history;
    private readonly string _chatId = "owner";

    public ImageContextTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-img-tests-" + Guid.NewGuid().ToString("N"));
        _store = new DataStore(_root);
        var log = new AppLog();
        _config = new ConfigStore(_store, log).Load();
        _host = new FeatureHost
        {
            Store = _store,
            Config = _config,
            Auth = new AuthService(_store, log),
            Log = log,
            Persona = new PersonaService(_store, Path.Combine(_root, "personas-bundled")),
        };
        _media = new MediaStore(_store, log);
        _history = new HistoryStore(_host, _media);
        _host.Set<MediaStore>(_media);
        _host.Set<IHistoryStore>(_history);
    }

    [Fact]
    public void BuildImageParts_MediaFileExists_ProducesImageUrlContent()
    {
        // 存一张真图
        var (mediaId, _) = _media.Save(_chatId, new byte[] { 1, 2, 3, 4 }, "image/png");

        var parts = ChatHubTestHook.BuildImageParts(_host, _chatId,
        [
            new JsonObject { ["mediaId"] = mediaId, ["mediaMime"] = "image/png" },
        ]);

        var part = Assert.Single(parts);
        Assert.Equal("image_url", (string?)part["type"]);
        var url = part["image_url"]?["url"]?.ToString() ?? "";
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.True(url.Length > "data:image/png;base64,".Length, "base64 不该是空的");
    }

    [Fact]
    public void BuildImageParts_WirePayload_DataAndMediaType_ProducesImageUrlContent()
    {
        // 端到端契约：网页端 POST /api/messages 发的图片是 {data, mediaType, name}
        // （assets/public/app.js:637 发送、ChatRoutes.cs:151-153 按同名字段解析落盘）。
        // 这批对象原样交给 ChatHub，所以 BuildImageParts 必须认得这个形状 —— 认不得模型就永远看不到图。
        var bytes = new byte[] { 9, 8, 7, 6, 5 };
        var b64 = Convert.ToBase64String(bytes);

        var parts = ChatHubTestHook.BuildImageParts(_host, _chatId,
        [
            new JsonObject { ["data"] = "data:image/png;base64," + b64, ["mediaType"] = "image/png", ["name"] = "photo.png" },
        ]);

        var part = Assert.Single(parts);
        Assert.Equal("image_url", (string?)part["type"]);
        Assert.Equal("data:image/png;base64," + b64, part["image_url"]?["url"]?.ToString());
    }

    [Fact]
    public void BuildImageParts_WirePayload_BareBase64_ProducesImageUrlContent()
    {
        // 同一形状但不带 data: 前缀的裸 base64（各端上传方式不同，两种都要认）
        var bytes = new byte[] { 4, 3, 2, 1 };
        var b64 = Convert.ToBase64String(bytes);

        var parts = ChatHubTestHook.BuildImageParts(_host, _chatId,
        [
            new JsonObject { ["data"] = b64, ["mediaType"] = "image/jpeg" },
        ]);

        var part = Assert.Single(parts);
        Assert.Equal("data:image/jpeg;base64," + b64, part["image_url"]?["url"]?.ToString());
    }

    [Fact]
    public void BuildImageParts_MissingMedia_ReturnsNothing()
    {
        var parts = ChatHubTestHook.BuildImageParts(_host, _chatId,
        [
            new JsonObject { ["mediaId"] = "no-such-file.png", ["mediaMime"] = "image/png" },
        ]);
        Assert.Empty(parts);
    }

    [Fact]
    public void BuildImageParts_DownscaleFlag_LargeImageWontCrash()
    {
        // 大图（随便塞 100KB 字节）不炸、仍产出 image_url
        var (mediaId, _) = _media.Save(_chatId, new byte[100_000], "image/jpeg");
        var parts = ChatHubTestHook.BuildImageParts(_host, _chatId,
        [
            new JsonObject { ["mediaId"] = mediaId, ["mediaMime"] = "image/jpeg" },
        ]);
        Assert.Single(parts);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

/// <summary>直接测 internal 方法。</summary>
public static class ChatHubTestHook
{
    public static List<JsonObject> BuildImageParts(FeatureHost host, string chatId, List<JsonObject> images)
        => ChatHub.BuildImagePartsForTest(host, chatId, images);
}
