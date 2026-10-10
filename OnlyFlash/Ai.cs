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
        return (url, key, model);
    }

    public static bool Available
    {
        get
        {
            if (!Plugin.AiEnabled.Value) return false;
            var (url, key, _) = Settings();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || !string.IsNullOrWhiteSpace(key);
        }
    }

    public static async Task<string> Chat(string system, string user, int maxTokens)
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
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
