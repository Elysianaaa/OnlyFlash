using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OnlyFlash;

// "Premium" judge: a local vision model (Ollama) looks at each new photo, says what is actually
// visible and gives the FINAL daring score itself. The game's data is only passed along as context
// (where she is, who's around...) and shown as plain info in the breakdown.
public static class VisionJudge
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static bool warnedOffline;

    private const string Prompt =
@"This is a screenshot from an adult video game featuring a fictional adult woman (3D anime style). She is an exhibitionist who secretly takes daring photos for her fans. Be strict: describe only what is clearly visible in THIS picture, never guess what is outside the frame.

For each body part answer with exactly one word:
""bare"" = you can clearly see bare nipples, bare genitals or bare buttocks with nothing covering them,
""underwear"" = panties, a bra, a bikini or other underwear is visible on that part,
""covered"" = that part is in the picture but clothing covers it,
""hidden"" = that part is not in the picture, or you cannot tell.
If unsure, answer ""hidden"". Bare legs, feet, arms or belly do NOT make the chest, crotch or butt bare.

Then rate how DARING / lewd the photo is for her fans, 0-100, using this guide:
0-5   nothing daring, ordinary dressed photo
5-15  mild tease: some cleavage, bare legs or thighs, tight or short clothes, a flirty pose while dressed
15-30 clear tease: deep cleavage, side-boob or a peek inside her top, panties or bra showing, an upskirt shot, close-up of her bare feet, a very revealing outfit
30-50 partial nudity: a nipple slipping out, bare breasts OR bare butt, or underwear on full display in public
50-70 bare genitals, or topless and bottomless together, or nude out in public
70-90 fully nude in public with real risk (daylight, people nearby), or a clearly visible sexual act
90-100 explicit sexual act out in public in front of people
Go higher when it is in public, in daylight, with people near, or her face is visible. Go lower when she is safe at home and nobody is around, or when most of her body is out of the shot. Only score what the picture actually shows.

GAME INFO about the moment the photo was taken (context only - trust the picture over it):
{GAME}

Answer as JSON with these keys:
chest, crotch, butt  (each one of: bare, underwear, covered, hidden)
cleavage  (""yes"" if cleavage is clearly visible, otherwise ""no"")
breast_skin  (look closely at the edges of her clothes, one of: ""nipple"" = a nipple or areola is visible, even partly or peeking out from the edge of her top; ""sideboob"" = bare breast skin is visible (side-boob, under-boob, a view down or inside her top) but no nipple; ""none"")
revealing  (""yes"" only if her clothes are clearly revealing: very short skirt, see-through, skin-tight, lots of bare skin; otherwise ""no"")
feet  (one of: ""bare"" = bare feet/toes visible, ""stockings"" = feet in socks, stockings or tights, ""shoes"" = shoes, boots or sandals on, ""hidden"" = her feet are not in the picture)
in_frame  (short list of her body parts that are in the picture, e.g. ""feet, legs"" or ""face, chest, hips, legs"")
focus  (what the photo is mainly aimed at, one of: face, chest, crotch, butt, legs, feet, whole body, background)
sexual_act  (""yes"" only if she is clearly seen touching her genitals or breasts, using a sex toy, or having sex; otherwise ""no"")
clothes  (short list of what she wears that you can see, or ""nothing"")
other_people  (number of other people visible)
summary  (one short sentence of what is visible)
daring  (whole number 0-100 from the guide)
reasons  (2 to 4 short reasons for that score)";

    private static readonly string[] Torso = { "chest", "breast", "boob", "torso", "hip", "butt", "ass", "crotch", "groin", "pelvis", "waist", "stomach", "belly", "back", "body", "nipple", "pussy", "vagina", "genital", "cleavage" };

    // Photos the AI is still looking at
    private static readonly ConcurrentDictionary<string, byte> pending = new();
    public static bool Pending(string photoId) => photoId != null && pending.ContainsKey(photoId);

    // Run `then` on the game thread once the AI judge is done with this photo (or gave up),
    // so fans react to what the photo really shows rather than the first guess
    public static void AfterJudged(string photoId, Action then, int maxSeconds = 60)
    {
        if (!Pending(photoId)) { then(); return; }
        Task.Run(async () =>
        {
            var until = DateTime.UtcNow.AddSeconds(maxSeconds);
            while (Pending(photoId) && DateTime.UtcNow < until) await Task.Delay(400);
            Sim.MainThread.Enqueue(then);
        });
    }

    private static string Txt(JsonNode n) => n is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : n?.ToString() ?? "";

    // ---------------- reality check against the game ----------------
    // The vision model likes to "dress" a naked anime girl in imaginary underwear and to miss what she's doing.
    // The game knows exactly what she wears and does, so obvious mistakes get corrected (and the AI is asked again).

    private static readonly HashSet<string> AccessoryParts = new() { "mask", "stocking", "stockings", "sock", "socks", "ribbon", "collar", "choker", "ears", "ear", "tail", "glove", "gloves", "boot", "boots", "shoe", "shoes", "heel", "heels", "sneaker", "sneakers", "sandal", "sandals", "glasses", "sunglasses", "goggles", "hat", "cap", "horn", "horns", "wing", "wings", "halo", "necklace", "bracelet", "earring", "earrings", "headband", "tiara", "garter", "band", "cuff", "cuffs", "blindfold", "eyepatch", "hairpin", "bow", "tie", "necktie", "scarf", "belt", "piercing", "leash", "helmet",
                                                                          // toys worn as outfit pieces cover nothing either
                                                                          "plug", "analplug", "vibrator", "vibe", "rotor", "dildo", "beads", "clip", "clips" };
    private static readonly HashSet<string> BottomParts = new() { "pants", "panty", "panties", "shorts", "skirt", "bloomer", "bloomers", "thong", "underwear", "tights", "pantyhose", "leggings", "jeans", "trousers" };
    private static readonly HashSet<string> TopParts = new() { "bra", "top", "shirt", "tshirt", "hoodie", "jacket", "sweater", "blouse", "vest", "camisole", "tank", "cardigan", "coat", "blazer", "sailor", "tube" };
    private static readonly HashSet<string> FullParts = new() { "dress", "suit", "leotard", "bodysuit", "swimsuit", "bikini", "bunny", "maid", "kimono", "yukata", "uniform", "apron", "robe", "gown", "onesie", "costume", "set" };

    // Pieces already reported as unrecognised, so the log says once which one switched the corrections off
    private static readonly ConcurrentDictionary<string, byte> unknownPieces = new();

    // What her outfit can cover at all. known = every piece was recognised (otherwise no corrections are made)
    private static (bool top, bool bottom, bool known) Outfit(JsonNode state)
    {
        bool top = false, bottom = false, known = true;
        if (state?["Cosplay"] is not JsonObject cos) return (true, true, false);
        foreach (var kv in cos)
        {
            if (!int.TryParse(kv.Key, out _) || kv.Value == null) continue;
            string name = kv.Value.ToString().ToLowerInvariant();
            string piece = name.Split('_').Last();
            bool Is(HashSet<string> set) => set.Contains(piece) || (piece.EndsWith("s") && set.Contains(piece[..^1])); // "tops" -> "top"
            if (Is(FullParts)) { top = bottom = true; }
            else if (Is(BottomParts)) bottom = true;
            else if (Is(TopParts)) top = true;
            else if (!Is(AccessoryParts))
            {
                known = false;
                if (unknownPieces.TryAdd(name, 0))
                    Plugin.Logger.LogWarning($"OnlyFlash AI judge: outfit piece '{name}' not recognised, game corrections are off while she wears it (add '{piece}' to a list in VisionJudge.cs)");
            }
        }
        return (top, bottom, known);
    }

    private static readonly string[] LowerBody = { "crotch", "hip", "butt", "ass", "groin", "pelvis", "pussy", "vagina", "genital", "thigh", "body", "legs spread" };
    private static readonly string[] UnderwearWords = { "bra", "pant", "underwear", "thong", "lingerie", "bikini" };

    // Fixes the AI's answer in place; returns (fact for the AI, short label for the breakdown) for each fix
    private static List<(string fact, string label)> Correct(JsonNode state, JsonObject seen)
    {
        var fixes = new List<(string, string)>();
        var forced = new List<string>();
        string P(string k) => (seen[k]?.ToString() ?? "hidden").Trim().ToLowerInvariant();
        var (top, bottom, known) = Outfit(state);
        string inFrame = Txt(seen["in_frame"]).ToLowerInvariant();
        bool lowerInFrame = LowerBody.Any(inFrame.Contains) || P("crotch") != "hidden" || P("butt") != "hidden";
        var gx = state?["Exposed"];
        bool upperOpen = gx?["Upper"]?.ToString()?.ToLowerInvariant() == "true" || gx?["All"]?.ToString()?.ToLowerInvariant() == "true";

        if (known && !bottom)
        {
            foreach (var k in new[] { "crotch", "butt" })
                if (P(k) == "underwear") { seen[k] = "bare"; forced.Add(k); }
            if (forced.Count > 0)
                fixes.Add(("She is NOT wearing any panties, underwear or anything on her lower body. What looked like panties is her bare skin: her pussy and butt are bare.",
                           "no panties - that was her bare skin"));
        }
        if (known && !top)
        {
            if (P("chest") == "underwear")
            {
                seen["chest"] = upperOpen ? "bare" : "covered";
                if (upperOpen) forced.Add("chest");
                fixes.Add((upperOpen ? "She is NOT wearing a bra or any top: her breasts are bare." : "She is NOT wearing a bra or any top - only her coat covers her chest.",
                           upperOpen ? "no bra - her breasts are bare" : "no bra - that was her coat"));
            }
            // Nothing for a nipple to slip out of
            string skin = P("breast_skin");
            if ((skin.StartsWith("nipple") || skin.StartsWith("side")) && P("chest") != "bare") seen["breast_skin"] = "none";
        }
        if (known && (!top || !bottom))
        {
            // Don't let made-up underwear stay in what fans are told she wears
            var clothes = Txt(seen["clothes"]).Split(',').Select(c => c.Trim()).Where(c => c.Length > 0 && !UnderwearWords.Any(u => c.ToLowerInvariant().Contains(u))).ToList();
            seen["clothes"] = clothes.Count > 0 ? string.Join(", ", clothes) : "nothing";
        }

        string action = state?["Action"]?.ToString() ?? "";
        bool masturbating = action.Contains("Onani") || action == "GanimataKoshiHeko";
        bool sex = action.Contains("Sex") || action.Contains("Dildo");
        if ((masturbating || sex) && !Yes(seen["sexual_act"]) && lowerInFrame)
        {
            seen["sexual_act"] = "yes";
            fixes.Add((masturbating ? "She IS masturbating in this photo - her hand is on her pussy." : action.Contains("Dildo") ? "She IS riding a dildo in this photo." : "She IS having sex in this photo.",
                       masturbating ? "she is masturbating" : action.Contains("Dildo") ? "she is riding a dildo" : "she is having sex"));
        }
        if (forced.Count > 0) seen["_forced"] = string.Join(",", forced);
        return fixes;
    }

    private static async Task<JsonObject> Look(string url, string model, string prompt, string b64)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = "json",
            ["keep_alive"] = "15m",
            ["options"] = new JsonObject { ["temperature"] = 0 },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = prompt,
                ["images"] = new JsonArray(b64)
            })
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, Plugin.VisionTimeout.Value)));
        using var resp = await Http.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
        string text = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}: {Ai.Trunc(text, 200)}");
        string content = JsonNode.Parse(text)?["message"]?["content"]?.ToString() ?? "";
        return Ai.ExtractJson(content, '{', '}') as JsonObject ?? throw new Exception("no JSON in answer: " + Ai.Trunc(content, 200));
    }

    public static void Check(PhotoRec rec, string stateJson, Vector3 pos, Quaternion rot, float fov, float aspect)
    {
        if (!Plugin.VisionEnabled.Value || rec == null || !File.Exists(rec.File)) return;
        string url = Plugin.VisionUrl.Value.TrimEnd('/') + "/api/chat";
        string model = Plugin.VisionModel.Value;
        // Outfit names are left out of the AI's context: it should say what it sees, not repeat the game's list
        string game = "- " + string.Join("\n- ", Scoring.GameInfo(JsonNode.Parse(stateJson), outfit: false));
        string prompt = Prompt.Replace("{GAME}", game);
        pending[rec.Id] = 0;
        Task.Run(async () =>
        {
            try
            {
                string b64 = Convert.ToBase64String(File.ReadAllBytes(rec.File));
                var seen = await Look(url, model, prompt, b64);
                Plugin.Logger.LogInfo($"OnlyFlash AI judge ({rec.Id}): {seen.ToJsonString()}");
                // Got something the game knows for certain wrong? Tell it the facts and let it look again
                var fixes = Correct(JsonNode.Parse(stateJson), JsonNode.Parse(seen.ToJsonString()) as JsonObject);
                if (fixes.Count > 0)
                {
                    string again = prompt + "\n\nCORRECTIONS - facts from the game engine, 100% certain (your first look got these wrong). Look at the picture again and answer with them in mind:\n- " + string.Join("\n- ", fixes.Select(f => f.fact));
                    try
                    {
                        int.TryParse(seen["other_people"]?.ToString(), out int firstPeople);
                        seen = await Look(url, model, again, b64);
                        Plugin.Logger.LogInfo($"OnlyFlash AI judge ({rec.Id}) second look: {seen.ToJsonString()}");
                        // Keep anyone it spotted the first time
                        int.TryParse(seen["other_people"]?.ToString(), out int secondPeople);
                        if (firstPeople > secondPeople) seen["other_people"] = firstPeople;
                    }
                    catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash AI judge second look failed, correcting the first answer: " + e.Message); }
                }
                var labels = fixes.Select(f => f.label).ToList();
                Sim.MainThread.Enqueue(() =>
                {
                    try { Apply(rec, stateJson, pos, rot, fov, aspect, seen, labels); }
                    catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash AI judge result not applied: " + e.Message); }
                    finally { pending.TryRemove(rec.Id, out _); }
                });
            }
            catch (Exception e)
            {
                pending.TryRemove(rec.Id, out _);
                if (!warnedOffline || e is not HttpRequestException)
                    Plugin.Logger.LogWarning("OnlyFlash AI judge unavailable, keeping the normal score: " + e.Message);
                warnedOffline = true;
            }
        });
    }

    private static bool Yes(JsonNode n) => (n?.ToString() ?? "").Trim().ToLowerInvariant().StartsWith("y");

    // Runs on the game thread: the AI's view of the photo becomes the truth, and its score the final score
    private static void Apply(PhotoRec rec, string stateJson, Vector3 pos, Quaternion rot, float fov, float aspect, JsonObject seen, List<string> corrected)
    {
        var state = JsonNode.Parse(stateJson);
        var gameInfo = Scoring.GameInfo(state);
        // Even after a second look, anything that still contradicts the game is fixed here
        foreach (var f in Correct(state, seen)) if (!corrected.Contains(f.label)) corrected.Add(f.label);
        string forced = Txt(seen["_forced"]);
        string Part(string key)
        {
            string v = (seen[key]?.ToString() ?? "hidden").Trim().ToLowerInvariant();
            return v is "bare" or "underwear" or "covered" ? v : "hidden";
        }
        string chest = Part("chest"), crotchP = Part("crotch"), buttP = Part("butt");

        // Strict: a part only counts as bare when the AI sees it bare AND the game agrees her outfit leaves it open
        // (the AI can't invent nudity, and the game can't count what the photo doesn't show).
        // Exception: parts the game itself proves are bare (she isn't wearing anything there).
        var gx = state?["Exposed"];
        bool Game(params string[] keys) => gx == null || gx["All"]?.ToString()?.ToLowerInvariant() == "true" || keys.Any(k => gx[k]?.ToString()?.ToLowerInvariant() == "true");
        bool breasts = chest == "bare" && (forced.Contains("chest") || Game("Upper"));
        bool crotch = crotchP == "bare" && (forced.Contains("crotch") || Game("Front"));
        bool butt = buttP == "bare" && (forced.Contains("butt") || Game("Hip", "HipCrouch"));
        // "bare" the game disagrees with is most likely underwear / a skin-tight outfit
        bool underwear = crotchP == "underwear" || buttP == "underwear" || (crotchP == "bare" && !crotch) || (buttP == "bare" && !butt);
        bool bra = chest == "underwear" || (chest == "bare" && !breasts);
        int.TryParse(seen["other_people"]?.ToString(), out int people);
        bool cleavage = Yes(seen["cleavage"]);
        bool revealing = Yes(seen["revealing"]);
        bool actSeen = Yes(seen["sexual_act"]);
        string inFrame = Txt(seen["in_frame"]).ToLowerInvariant();
        bool allHidden = chest == "hidden" && crotchP == "hidden" && buttP == "hidden";
        bool noTorso = inFrame.Length > 0 && !Torso.Any(t => inFrame.Contains(t));
        bool bodyHidden = allHidden || (noTorso && !breasts && !crotch && !butt);
        bool nothingInShot = inFrame.Length == 0 || inFrame == "none" || inFrame.Contains("no body");
        string feet = (seen["feet"]?.ToString() ?? "").Trim().ToLowerInvariant();
        // Nip slips / side-boob: the game can't know (her top counts as "worn"), so the AI alone decides -
        // but only when her chest is actually in the shot
        string breastSkin = Txt(seen["breast_skin"]).Trim().ToLowerInvariant();
        bool chestInShot = chest != "hidden" || inFrame.Contains("chest") || inFrame.Contains("breast");
        bool nipSlip = !breasts && chestInShot && breastSkin.StartsWith("nipple");
        bool sideboob = !breasts && !nipSlip && chestInShot && breastSkin.StartsWith("side");
        // At home with nobody around the same shot is tamer than out in public
        string stage = state?["Position"]?["stage"]?.ToString() ?? "";
        bool inPublic = (stage != "Apart" && stage != "") || state?["NearNPC"]?.ToString() == "true" || state?["Watched"]?.ToString() == "true" || people > 0;

        // The AI's one-line description, unless it still describes underwear she isn't wearing
        string summary = Ai.Trunc(Txt(seen["summary"]), 140);
        var outfit = Outfit(state);
        if (corrected.Count > 0 && outfit.known && (!outfit.top || !outfit.bottom) && UnderwearWords.Any(u => summary.ToLowerInvariant().Contains(u)))
        {
            var bits = new List<string>();
            if (breasts) bits.Add("bare breasts");
            if (crotch) bits.Add("bare pussy");
            if (butt) bits.Add("bare butt");
            string act = corrected.FirstOrDefault(l => l.StartsWith("she is "))?.Substring(7);
            summary = $"Her {(bits.Count > 0 ? string.Join(", ", bits) : "body")} on show" + (act != null ? $", {act}" : actSeen ? ", doing something sexual" : "") + (people > 0 ? ", with people nearby" : "") + ".";
        }

        var ex = new JsonObject
        {
            ["None"] = !(breasts || crotch || butt || underwear),
            ["Upper"] = breasts,
            ["Front"] = crotch,
            ["Hip"] = butt || (underwear && !crotch),
            ["HipCrouch"] = false,
            ["All"] = breasts && crotch && butt
        };
        if (state is JsonObject st0)
        {
            if (underwear) st0["AiUnderwear"] = true;
            if (bra) st0["AiBra"] = true;
            if (cleavage) st0["AiCleavage"] = true;
            if (nipSlip) st0["AiNipSlip"] = true;
            if (sideboob) st0["AiSideboob"] = true;
            st0["AiChecked"] = true;
            if (inFrame.Length > 0) st0["AiFace"] = inFrame.Contains("face");
            st0["AiSummary"] = summary;
            st0["AiClothes"] = Ai.Trunc(Txt(seen["clothes"]), 80);
            st0["AiBodyHidden"] = bodyHidden;
            st0["AiActSeen"] = actSeen;
            if (feet is "bare" or "stockings" or "shoes" or "hidden") st0["AiFeet"] = feet;
        }
        state["Exposed"] = ex;
        if (people > 0 && state is JsonObject so && so["NearNPC"]?.ToString() != "true") so["NearNPC"] = true;

        int before = rec.Daring;
        // The game-data scoring still runs: it supplies the tags fans react to, face-visible, and a fallback number
        var (estimate, tags, faceShown, _) = Scoring.Score(state, pos, rot, fov, aspect);

        // ---- Final score: the AI's own rating, kept honest by what the AI itself reported seeing ----
        var notes = new List<string>();
        int daring;
        if (!int.TryParse(Txt(seen["daring"]).Trim().Split('.', ' ', '/')[0], out int aiScore))
        {
            daring = estimate;
            notes.Add("(the AI gave no score, so the game-data estimate is used)");
        }
        else
        {
            daring = Math.Clamp(aiScore, 0, 100);
            int cap = 100;
            string why = null;
            if (nothingInShot) { cap = 3; why = "she isn't in the shot"; }
            else if (bodyHidden) { cap = 35; why = "only her legs/feet are in the shot"; }
            else if (!breasts && !crotch && !butt && !actSeen)
            {
                // Dressed: how far it can go depends on what's actually on show
                cap = nipSlip ? (inPublic ? 50 : 40) : underwear || bra ? (inPublic ? 45 : 32) : sideboob ? (inPublic ? 35 : 28)
                    : cleavage ? 30 : revealing || feet == "bare" ? 20 : feet == "stockings" ? 15 : 8;
                why = nipSlip ? "nip slip, no full nudity" : underwear || bra ? "underwear, no nudity" : sideboob ? "side-boob, no nudity" : cleavage ? "cleavage, no nudity" : revealing ? "revealing clothes, no nudity" : "nothing much on show";
                if (!inPublic && (nipSlip || underwear || bra || sideboob)) why += ", safe at home";
            }
            else if (!crotch && !actSeen && !(breasts && butt)) { cap = 70; why = "partial nudity"; }
            if (daring > cap) { notes.Add($"(capped at {cap}: {why})"); daring = cap; }
            // Floors from what the AI itself reported, so a clear panty shot or nudity is never rated as nothing
            string focus = Txt(seen["focus"]).Trim().ToLowerInvariant();
            bool lowFocus = focus.Contains("crotch") || focus.Contains("butt") || Scoring.CrotchCloseUp(state, pos, rot, fov, aspect);
            bool feetFocus = focus.Contains("feet") || focus.Contains("foot") || bodyHidden;
            var floors = new List<(int min, string what)>
            {
                (actSeen ? 45 : 0, "a sexual act"),
                (crotch ? 40 : 0, "her pussy"),
                (breasts ? 25 : 0, "her bare breasts"),
                (butt ? 25 : 0, "her bare butt"),
                (underwear ? (lowFocus ? 25 : 15) : 0, lowFocus ? "a close panty shot" : "her panties"),
                (bra ? (focus.Contains("chest") ? 20 : 12) : 0, "her bra"),
                (nipSlip ? (focus.Contains("chest") ? 32 : 26) : 0, "her nipple slipping out"),
                (sideboob ? (focus.Contains("chest") ? 20 : 14) : 0, "bare breast peeking out"),
                (cleavage ? (focus.Contains("chest") ? 12 : 6) : 0, "cleavage"),
                (feet == "bare" && feetFocus ? 18 : feet == "stockings" && feetFocus ? 10 : 0, feet == "bare" ? "a bare feet close-up" : "a stockinged feet close-up"),
            };
            var top = floors.OrderByDescending(f => f.min).First();
            if (daring < top.min) { notes.Add($"(raised to {top.min}: the AI saw {top.what})"); daring = Math.Min(top.min, cap); }

            // People in the shot: being seen like this is the whole thrill
            bool watched = state?["Watched"]?.ToString()?.ToLowerInvariant() == "true";
            if (people > 0 || watched)
            {
                bool showing = breasts || crotch || butt || actSeen;
                bool teasing = underwear || bra || nipSlip || sideboob || cleavage;
                int bonus = showing ? (people >= 2 ? 15 : 10) : teasing ? (people >= 2 ? 10 : 7) : 2;
                if (watched) bonus += showing ? 8 : teasing ? 4 : 1;
                daring = Math.Min(100, daring + bonus);
                var who = new List<string>();
                if (people > 0) who.Add($"{people} {(people == 1 ? "person" : "people")} in the shot");
                if (watched) who.Add("strangers watching her");
                notes.Add($"(+{bonus}: {string.Join(", ", who)})");
            }
        }

        // ---- Breakdown: what the AI saw and why, then the game info (no points) ----
        string clothes = Ai.Trunc(Txt(seen["clothes"]), 80);
        string frame = Ai.Trunc(Txt(seen["in_frame"]), 60);
        var lines = new List<string> { $"AI judge: {summary}" + (string.IsNullOrWhiteSpace(frame) ? "" : $" [in shot: {frame}]") + (string.IsNullOrWhiteSpace(clothes) ? "" : $" (wearing: {clothes})") };
        if (corrected.Count > 0) lines.Add("Corrected by the game: " + string.Join("; ", corrected));
        lines.Add($"<b>AI score: {daring}/100</b>");
        var reasons = seen["reasons"] is JsonArray ra ? ra.Select(r => Ai.Trunc(r?.ToString(), 90)).Where(r => !string.IsNullOrWhiteSpace(r)).Take(4).ToList()
                    : Txt(seen["reasons"]).Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(r => Ai.Trunc(r.Trim(), 90)).Take(4).ToList();
        foreach (var r in reasons) lines.Add("- " + r);
        lines.AddRange(notes);
        lines.Add("Game info: " + string.Join(", ", gameInfo));
        if (before != daring) lines.Add($"(first estimate from game data was {before})");

        lock (Store.Sync)
        {
            rec.Daring = daring;
            rec.Tags = tags;
            rec.FaceShown = faceShown;
            rec.Why = lines;
            rec.AiChecked = true;
        }
        // Already posted before the AI finished? The post follows the corrected judgement too
        foreach (var post in Store.D.Posts.Where(p => p.PhotoId == rec.Id).ToList())
            Sim.Rejudge(post, daring, faceShown, tags);
        Store.MarkDirty();
        // No phone message; only a short on-screen note, and only when the AI actually changed the score
        if (before != daring)
            OnlyFlashBehaviour.ToastFromOutside($"AI judge: daring {before} -> <b>{daring}</b>/100", summary);
    }
}
