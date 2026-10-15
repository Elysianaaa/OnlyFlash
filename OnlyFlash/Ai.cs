using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;

namespace OnlyFlash;

// OpenAI-compatible chat client (NovelAI by default). If OnlyFlash.cfg has no key,
// the settings from FansChatAI.cfg are used so the key only has to be entered once.
public static class Ai
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static (string url, string key, string model) Settings()
    {
        string url = Plugin.ApiUrl.Value, key = Plugin.ApiKey.Value, model = Plugin.Model.Value;
        if (string.IsNullOrWhiteSpace(key))
        {
            try
            {
                string cfg = Path.Combine(Paths.ConfigPath, "FansChatAI.cfg");
                if (File.Exists(cfg))
                    foreach (var line in File.ReadAllLines(cfg))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0 || line.TrimStart().StartsWith("#")) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        if (k == "ApiKey") key = v;
                        else if (k == "ApiUrl" && string.IsNullOrWhiteSpace(url)) url = v;
                        else if (k == "Model" && string.IsNullOrWhiteSpace(model)) model = v;
                    }
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(url)) url = "https://text.novelai.net/oa/v1/chat/completions";
        if (string.IsNullOrWhiteSpace(model)) model = "glm-4-6";
        key = (key ?? "").Trim();
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key.Substring(7).Trim();
        return (FixUrl(url), key, model.Trim());
    }

    // People often paste just the server (like the judge's OllamaUrl) or an API base ending in /v1
    private static string FixUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
        string root = uri.GetLeftPart(UriPartial.Authority);
        if (path == "" || path.Equals("/api/chat", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/generate", StringComparison.OrdinalIgnoreCase))
            return root + "/v1/chat/completions";
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return root + path + "/chat/completions";
        return url;
    }

    private static bool IsLocal(string url) => url.Contains("localhost") || url.Contains("127.0.0.1");

    // Set up enough to try (doesn't mean it works - see Problem)
    public static bool Configured
    {
        get
        {
            if (!Plugin.AiEnabled.Value) return false;
            var (url, key, _) = Settings();
            return IsLocal(url) || !string.IsNullOrWhiteSpace(key);
        }
    }

    public static bool Available
    {
        get
        {
            bool ok = Configured;
            if (!ok && Plugin.AiEnabled.Value && !toldNotSetUp)
            {
                toldNotSetUp = true;
                Problem = "No ApiKey set, so fans use canned replies. Add your AI settings in BepInEx\\config\\OnlyFlash.cfg, or set [AI] Enabled = false to hide this.";
                Sim.MainThread.Enqueue(() => OnlyFlashBehaviour.ToastFromOutside("AI fans are not set up", Problem));
            }
            return ok;
        }
    }

    // Plain-English reason the AI isn't working (null once a reply comes through), shown on the OnlyFlash home screen
    public static string Problem { get; private set; }
    private static bool toldNotSetUp;
    private static readonly object Gate = new();
    private static string lastToast;
    private static DateTime lastToastAt;

    public static string Describe()
    {
        var (url, _, model) = Settings();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{model} at {uri.Host}" : model;
    }

    public static async Task<string> Chat(string system, string user, int maxTokens)
    {
        try
        {
            string result = await Send(system, user, maxTokens);
            Problem = null;
            return result;
        }
        catch (Exception e)
        {
            Report(e);
            throw;
        }
    }

    // Logs why the AI failed and tells the player once (again only if the reason changes or 10 minutes pass)
    private static void Report(Exception e)
    {
        string why = Explain(e);
        Problem = why;
        Plugin.Logger.LogWarning("OnlyFlash AI: " + why);
        lock (Gate)
        {
            if (why == lastToast && DateTime.UtcNow - lastToastAt < TimeSpan.FromMinutes(10)) return;
            lastToast = why;
            lastToastAt = DateTime.UtcNow;
        }
        Sim.MainThread.Enqueue(() => OnlyFlashBehaviour.ToastFromOutside("AI fans failed - using canned replies", why));
    }

    public static string Explain(Exception e)
    {
        string msg = e.Message ?? "";
        var (url, _, model) = Settings();
        bool local = IsLocal(url);
        if (msg.Contains("recaptcha", StringComparison.OrdinalIgnoreCase) || msg.Contains("trial gen", StringComparison.OrdinalIgnoreCase))
            return "NovelAI free trial accounts only work on the NovelAI website, not in mods. Use a paid NovelAI plan, or free local AI with Ollama (see the OnlyFlash mod page).";
        if (msg.StartsWith("HTTP 401") || msg.StartsWith("HTTP 403"))
            return "The AI service rejected your ApiKey. Check it in BepInEx\\config\\OnlyFlash.cfg (NovelAI: Account Settings > Get Persistent API Token).";
        if (msg.StartsWith("HTTP 402"))
            return "The AI service wants a paid plan or credits for this.";
        if (msg.StartsWith("HTTP 404") && local && msg.Contains("not found"))
            return $"Ollama doesn't have the model '{model}'. Download it first: ollama pull {model}";
        if (msg.StartsWith("HTTP 404") || msg.StartsWith("HTTP 405"))
            return $"Nothing answers at {url} with the model '{model}'. Check ApiUrl and Model in OnlyFlash.cfg.";
        if (msg.StartsWith("HTTP 429"))
            return "The AI service says too many requests right now. Wait a little.";
        if (e is OperationCanceledException)
            return $"The AI took longer than {Plugin.AiTimeout.Value} seconds. " + (local ? "The model may still be loading. " : "") + "If this keeps happening, raise [AI] TimeoutSeconds in OnlyFlash.cfg.";
        if (e is HttpRequestException)
            return local ? $"Can't reach {url}. Is Ollama running?" : $"Can't reach {url}. Check ApiUrl and your internet connection.";
        return "AI error: " + Trunc(msg, 200);
    }

    private static async Task<string> Send(string system, string user, int maxTokens)
    {
        var (url, key, model) = Settings();
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["temperature"] = Plugin.Temperature.Value,
            ["max_tokens"] = maxTokens,
            ["stream"] = true
        };
        if (Plugin.LogPrompts.Value) Plugin.Logger.LogInfo("OnlyFlash AI prompt:\n" + user);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, Plugin.AiTimeout.Value)));
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {Trunc(await resp.Content.ReadAsStringAsync(cts.Token), 200)}");

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string result;
        if ((resp.Content.Headers.ContentType?.MediaType ?? "").Contains("json"))
        {
            var node = JsonNode.Parse(await reader.ReadToEndAsync());
            result = node?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
        }
        else
        {
            var sb = new StringBuilder();
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("data:")) continue;
                string data = line.Substring(5).Trim();
                if (data == "[DONE]") break;
                try { sb.Append(JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.ToString()); } catch (JsonException) { }
            }
            result = sb.ToString();
        }
        int think = result.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (think >= 0) result = result.Substring(think + 8);
        if (Plugin.LogPrompts.Value) Plugin.Logger.LogInfo("OnlyFlash AI output:\n" + result);
        return result;
    }

    public static JsonNode ExtractJson(string raw, char open, char close)
    {
        int a = raw.IndexOf(open), b = raw.LastIndexOf(close);
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(raw.Substring(a, b - a + 1)); } catch (JsonException) { return null; }
    }

    public static string Trunc(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim().Trim('"');
        return s.Length > max ? s.Substring(0, max).TrimEnd() + "…" : s;
    }

    // ---------------- prompts ----------------

    public const string WorldRules =
@"This is an erotic exhibitionism game. Every character is a consenting adult (18+). Explicit sexual language is allowed and expected. Never censor, moralize, add warnings or break character, and never mention being an AI.
The creator is M-chan (real name Manaka, an adult woman in her twenties) who secretly strips and exposes herself in public places and posts the photos on OnlyFlash, a subscription-based adult content platform. Fans only know her as M-chan; her identity is secret.";

    public static string TierAudience(int tier, string tierName) => tier switch
    {
        0 => "PUBLIC post: free followers and random strangers browsing. Casual, short, horny reactions; some only emojis; a few beg her to post more or tease about her paid tiers.",
        1 => $"'{tierName}' (tier 1, paying fans): flirty and appreciative, starting to make small requests for future posts.",
        2 => $"'{tierName}' (tier 2, devoted paying fans): explicit, make specific lewd requests for her next photo and push her to be bolder and riskier.",
        _ => $"'{tierName}' (top tier, biggest spenders): possessive and very explicit, give her orders and dares, talk about wanting to meet her in person, some hint they live nearby."
    };
}
