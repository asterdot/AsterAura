using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;

namespace AuraChat.Features.Media;

/// <summary>
/// tts.js 的对齐实现：openai / cosyvoice / gptsovits / http 模板 / edge-tts / Windows SAPI 六路合成。
/// 产物落在 dataDir/voice-cache（24h 清一次），返回临时文件路径，调用方读走后自行删除。
/// </summary>
public sealed class VoiceService : IVoiceService
{
    /// <summary>edge 产物至少 500B 才算成功（原 tts.js :150）。</summary>
    private const int EdgeMinBytes = 500;

    /// <summary>windows 产物至少 1000B（原 :158）。</summary>
    private const int WindowsMinBytes = 1000;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly string _cacheDir;
    private long _lastSweep;

    public VoiceService(FeatureHost host)
    {
        _host = host;
        _log = host.Log.Child("voice");
        _cacheDir = Path.Combine(host.Store.DataDir, "voice-cache");
    }

    // ---------------- 入口 ----------------

    public async Task<(string? FilePath, string? Engine, string? Reason)> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return (null, null, "没有文字");
        try
        {
            Directory.CreateDirectory(_cacheDir);
            SweepCache();

            // 指定了引擎就直接走（原 tts.js :112）
            var engine = _host.Str("voice", "engine").ToLowerInvariant();
            return engine switch
            {
                "edge" => await EdgeSynthAsync(t, ct).ConfigureAwait(false),
                "windows" => await WindowsSynthAsync(t, ct).ConfigureAwait(false),
                "openai" => await OpenAiSynthAsync(t, ct).ConfigureAwait(false),
                "cosyvoice" => await CosyVoiceSynthAsync(t, ct).ConfigureAwait(false),
                "gptsovits" => await GptSoVitsSynthAsync(t, ct).ConfigureAwait(false),
                "http" => await HttpSynthAsync(t, ct).ConfigureAwait(false),
                _ => await AutoAsync(t, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("合成失败：", ex.Message);
            return (null, null, "合成失败：" + ex.Message);
        }
    }

    /// <summary>auto：provider 派发 → openai → edge → windows 兜底（原 :120-135）。</summary>
    private async Task<(string? FilePath, string? Engine, string? Reason)> AutoAsync(string text, CancellationToken ct)
    {
        var provider = _host.Str("voice", "provider").ToLowerInvariant();
        switch (provider)
        {
            case "cosyvoice":
                return await CosyVoiceSynthAsync(text, ct).ConfigureAwait(false);
            case "gptsovits":
                return await GptSoVitsSynthAsync(text, ct).ConfigureAwait(false);
            case "http":
                return await HttpSynthAsync(text, ct).ConfigureAwait(false);
        }

        if (provider == "openai" || _host.Str("voice", "ttsBaseUrl").Length > 0)
        {
            var r = await OpenAiSynthAsync(text, ct).ConfigureAwait(false);
            if (r.FilePath is not null) return r;
        }
        if (provider == "auto" && EdgeInstalled())
        {
            var r = await EdgeSynthAsync(text, ct).ConfigureAwait(false);
            if (r.FilePath is not null) return r;
        }
        return await WindowsSynthAsync(text, ct).ConfigureAwait(false);
    }

    public bool ShouldAutoSpeak(string text)
    {
        if (!_host.Flag(false, "voice", "enabled")) return false;
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > _host.Int(80, "voice", "autoMaxChars")) return false;
        return Random.Shared.NextDouble() < _host.Num(0.15, "voice", "autoChance");
    }

    // ---------------- edge-tts ----------------

    public JsonObject EdgeState() => new()
    {
        ["installed"] = EdgeInstalled(),
        ["dir"] = PyEnvDir(),
    };

    private static string PyEnvDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AURACHAT_PYENV");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        if (Directory.Exists(@"C:\AuraChat\pyenv")) return @"C:\AuraChat\pyenv";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "AuraChat", "pyenv");
    }

    private static string? PyEnvPython()
    {
        // venv 布局兼容：Windows venv/uv 的解释器在 Scripts\python.exe，裸目录（老布局/绿色版）在根 python.exe
        foreach (var py in new[]
                 {
                     Path.Combine(PyEnvDir(), "Scripts", "python.exe"),
                     Path.Combine(PyEnvDir(), "python.exe"),
                 })
        {
            if (File.Exists(py)) return py;
        }
        return null;
    }

    private static bool EdgeInstalled() =>
        PyEnvPython() is not null &&
        Directory.Exists(Path.Combine(PyEnvDir(), "Lib", "site-packages", "edge_tts"));

    public async Task<JsonObject> InstallEdgeAsync(CancellationToken ct = default)
    {
        var dir = PyEnvDir();
        Directory.CreateDirectory(dir);

        if (PyEnvPython() is null)
        {
            // 按 py -3 -m venv → python -m venv → uv venv(3.12) 顺序试（原 :180）
            var attempts = new (string File, string[] Args)[]
            {
                ("py", ["-3", "-m", "venv", dir]),
                ("python", ["-m", "venv", dir]),
                ("uv", ["venv", "--python", "3.12", dir]),
            };
            var built = false;
            foreach (var (file, args) in attempts)
            {
                var run = await MediaProc.RunAsync(file, args, 300_000, ct: ct).ConfigureAwait(false);
                if (run.Code == 0 && PyEnvPython() is not null)
                {
                    built = true;
                    break;
                }
                _log.Debug("建 venv 失败：", file, run.Err);
            }
            if (!built)
                return new JsonObject { ["ok"] = false, ["message"] = "建 python 虚拟环境失败：电脑上要有 Python 或 uv" };
        }

        var py = PyEnvPython()!;
        var pip = await MediaProc.RunAsync(py, ["-m", "pip", "install", "edge-tts"], 600_000, ct: ct).ConfigureAwait(false);
        if (pip.Code != 0)
        {
            // 失败换 aliyun 镜像（原 :205）
            pip = await MediaProc.RunAsync(py,
                ["-m", "pip", "install", "-i", "https://mirrors.aliyun.com/pypi/simple/", "edge-tts"],
                600_000, ct: ct).ConfigureAwait(false);
        }
        if (pip.Code != 0 || !EdgeInstalled())
            return new JsonObject { ["ok"] = false, ["message"] = "装 edge-tts 失败：" + MediaUtil.Slice(pip.Err, 200) };
        return new JsonObject { ["ok"] = true, ["message"] = "edge-tts 装好了" };
    }

    private async Task<(string? FilePath, string? Engine, string? Reason)> EdgeSynthAsync(string text, CancellationToken ct)
    {
        var py = PyEnvPython();
        if (py is null || !EdgeInstalled()) return (null, null, "edge-tts 没装好");

        var outPath = CacheFile("edge", ".mp3");
        var txtPath = Path.ChangeExtension(outPath, ".txt");
        await File.WriteAllTextAsync(txtPath, text, Encoding.UTF8, ct).ConfigureAwait(false);

        var args = new List<string> { "-I", "-m", "edge_tts", "--voice", _host.Text("zh-CN-XiaoyiNeural", "voice", "voice") };
        var rate = _host.Text("+0%", "voice", "rate");
        if (rate.Length > 0 && rate != "+0%") args.AddRange(["--rate", rate]);
        var pitch = _host.Text("+0Hz", "voice", "pitch");
        if (pitch.Length > 0 && pitch != "+0Hz") args.AddRange(["--pitch", pitch]);
        args.AddRange(["--file", txtPath, "--write-media", outPath]);

        try
        {
            var run = await MediaProc.RunAsync(py, args, 60_000, ct: ct).ConfigureAwait(false);
            var size = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            return size >= EdgeMinBytes
                ? ((string? FilePath, string? Engine, string? Reason))(outPath, "edge", null)
                : ((string?, string?, string?))(null, null, "edge 合成失败：" + MediaUtil.Slice(run.Err, 200));
        }
        finally
        {
            TryDelete(txtPath);
        }
    }

    // ---------------- Windows SAPI ----------------

    private async Task<(string? FilePath, string? Engine, string? Reason)> WindowsSynthAsync(string text, CancellationToken ct)
    {
        var outPath = CacheFile("win", ".wav");
        var txtPath = Path.ChangeExtension(outPath, ".txt");
        var psPath = Path.ChangeExtension(outPath, ".ps1");
        await File.WriteAllTextAsync(txtPath, text, Encoding.UTF8, ct).ConfigureAwait(false);
        var script =
            "Add-Type -AssemblyName System.Speech\n" +
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer\n" +
            "$v = $s.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -like 'zh-*' } | Select-Object -First 1\n" +
            "if ($v) { $s.SelectVoice($v.VoiceInfo.Name) }\n" +
            $"$s.SetOutputToWaveFile('{outPath.Replace("'", "''")}')\n" +
            $"$s.Speak([System.IO.File]::ReadAllText('{txtPath.Replace("'", "''")}', [System.Text.Encoding]::UTF8))\n" +
            "$s.Dispose()\n";
        await File.WriteAllTextAsync(psPath, script, Encoding.UTF8, ct).ConfigureAwait(false);

        try
        {
            var run = await MediaProc.RunAsync("powershell",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", psPath],
                60_000, ct: ct).ConfigureAwait(false);
            var size = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            return size >= WindowsMinBytes
                ? ((string?, string?, string?))(outPath, "windows", null)
                : ((string?, string?, string?))(null, null, "windows 语音失败：" + MediaUtil.Slice(run.Err, 200));
        }
        finally
        {
            TryDelete(txtPath);
            TryDelete(psPath);
        }
    }

    // ---------------- openai 兼容 ----------------

    private async Task<(string? FilePath, string? Engine, string? Reason)> OpenAiSynthAsync(string text, CancellationToken ct)
    {
        var baseUrl = _host.Str("voice", "ttsBaseUrl").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) return (null, null, "没配语音服务地址（ttsBaseUrl）");
        var body = new JsonObject
        {
            ["model"] = _host.Text("tts-1", "voice", "ttsModel"),
            ["input"] = text,
            ["voice"] = _host.Text("alloy", "voice", "ttsVoice"),
            ["response_format"] = "mp3",
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/speech")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var apiKey = _host.Str("voice", "ttsApiKey");
        if (apiKey.Length > 0) req.Headers.Authorization = new("Bearer", apiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(60_000);
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (null, null, "语音服务出错：" + MediaUtil.Slice(err, 160));
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        var outPath = SaveAudio(bytes, "openai", 24_000);
        return outPath is null ? ((string?, string?, string?))(null, null, "语音服务没返回音频") : (outPath, "openai", null);
    }

    // ---------------- cosyvoice ----------------

    private async Task<(string? FilePath, string? Engine, string? Reason)> CosyVoiceSynthAsync(string text, CancellationToken ct)
    {
        var cfg = _host.Node("voice", "cosyvoice") as JsonObject ?? new JsonObject();
        var url = JsonUtil.Str(cfg["url"], "http://127.0.0.1:50000").Trim().TrimEnd('/');
        var mode = JsonUtil.Str(cfg["mode"], "sft");
        var spk = JsonUtil.Str(cfg["spkId"], "中文女");
        var instruct = JsonUtil.Str(cfg["instruct"], "");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(text, Encoding.UTF8), "tts_text");
        form.Add(new StringContent(spk, Encoding.UTF8), "spk_id");
        if (mode == "instruct" && instruct.Length > 0)
            form.Add(new StringContent(instruct, Encoding.UTF8), "instruct_text");

        var endpoint = mode == "instruct" ? "/inference_instruct" : "/inference_sft";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(60_000);
        using var resp = await Http.PostAsync(url + endpoint, form, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (null, null, "cosyvoice 出错：" + MediaUtil.Slice(err, 160));
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        var sampleRate = JsonUtil.Int(cfg["sampleRate"], 22_050);
        var outPath = SaveAudio(bytes, "cosyvoice", sampleRate);
        return outPath is null ? ((string?, string?, string?))(null, null, "cosyvoice 没返回音频") : (outPath, "cosyvoice", null);
    }

    // ---------------- gpt-so-vits ----------------

    private async Task<(string? FilePath, string? Engine, string? Reason)> GptSoVitsSynthAsync(string text, CancellationToken ct)
    {
        var cfg = _host.Node("voice", "gptsovits") as JsonObject ?? new JsonObject();
        var url = JsonUtil.Str(cfg["url"], "http://127.0.0.1:9880").Trim().TrimEnd('/');
        var qs = new StringBuilder(url).Append("/tts?")
            .Append("text=").Append(Uri.EscapeDataString(text))
            .Append("&text_lang=").Append(Uri.EscapeDataString(JsonUtil.Str(cfg["textLang"], "zh")))
            .Append("&ref_audio_path=").Append(Uri.EscapeDataString(JsonUtil.Str(cfg["refAudio"], "")))
            .Append("&prompt_text=").Append(Uri.EscapeDataString(JsonUtil.Str(cfg["promptText"], "")))
            .Append("&prompt_lang=").Append(Uri.EscapeDataString(JsonUtil.Str(cfg["promptLang"], "zh")))
            .Append("&media_type=wav");
        var bytes = await MediaUtil.GetBytesAsync(qs.ToString(), timeoutMs: 60_000, ct: ct).ConfigureAwait(false);
        var sampleRate = 32_000;
        var outPath = SaveAudio(bytes, "gptsovits", sampleRate);
        return outPath is null ? ((string?, string?, string?))(null, null, "gptsovits 没返回音频") : (outPath, "gptsovits", null);
    }

    // ---------------- 自定义 http 模板 ----------------

    private async Task<(string? FilePath, string? Engine, string? Reason)> HttpSynthAsync(string text, CancellationToken ct)
    {
        var template = _host.Str("voice", "http", "urlTemplate").Trim();
        if (template.Length == 0 || !template.Contains("{text}"))
            return (null, null, "http 模板里得有 {text}");
        var url = template.Replace("{text}", Uri.EscapeDataString(text));
        var bytes = await MediaUtil.GetBytesAsync(url, timeoutMs: 60_000, ct: ct).ConfigureAwait(false);
        var outPath = SaveAudio(bytes, "http", 24_000);
        return outPath is null ? ((string?, string?, string?))(null, null, "语音接口没返回音频") : (outPath, "http", null);
    }

    // ---------------- 音频落盘 ----------------

    /// <summary>按魔数认格式（原 tts.js saveAudio）：认不出就当裸 PCM 包成 wav。</summary>
    private string? SaveAudio(byte[] bytes, string engine, int fallbackSampleRate)
    {
        if (bytes is null || bytes.Length < 64) return null;
        var ext = SniffExt(bytes);
        if (ext is null)
        {
            bytes = BuildWav(bytes, fallbackSampleRate);
            ext = ".wav";
        }
        var outPath = CacheFile(engine, ext);
        File.WriteAllBytes(outPath, bytes);
        return outPath;
    }

    private static string? SniffExt(byte[] b)
    {
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F') return ".wav";
        if ((b[0] == 'I' && b[1] == 'D' && b[2] == '3') || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)) return ".mp3";
        if (b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') return ".ogg";
        return null;
    }

    private static byte[] BuildWav(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // 单声道
        w.Write(sampleRate);
        w.Write(sampleRate * 2);      // 字节率 = 采样率 × 2 字节
        w.Write((short)2);            // 块对齐
        w.Write((short)16);           // 位深
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    private string CacheFile(string engine, string ext)
        => Path.Combine(_cacheDir, $"{engine}-{Guid.NewGuid():N}{ext}");

    /// <summary>清 24h 前的旧产物（原 tts.js :90），最多 10 分钟跑一次。</summary>
    private void SweepCache()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSweep) < 600_000) return;
        Interlocked.Exchange(ref _lastSweep, now);
        try
        {
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var f in Directory.EnumerateFiles(_cacheDir))
                if (File.GetLastWriteTime(f) < cutoff)
                    TryDelete(f);
        }
        catch (Exception ex)
        {
            _log.Debug("清缓存失败：", ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 占用中的文件下一次清
        }
    }
}
