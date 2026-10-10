using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using UnityEngine;

namespace OnlyFlash;

// Turns the game state captured with a photo (plus the photo camera pose) into a
// 0-100 "daring" score and a list of human readable tags the fans react to.
public static class Scoring
{
    public static (int daring, List<string> tags, bool faceShown, List<string> why) Score(JsonNode s, Vector3 camPos, Quaternion camRot, float fov, float aspect = 1f)
    {
        var tags = new List<string>();
        var why = new List<string>();
        float score = 0;
        var items = new List<(string reason, float pts)>();
        var steps = new List<string>();
        void Add(float pts, string reason) { pts = Mathf.Round(pts); if (pts == 0) return; score += pts; items.Add((reason, pts)); }

        Vector3? face = Vec(s?["CameraTarget"]?["Face"]);
        Vector3? body = Vec(s?["CameraTarget"]?["Body"]);
        Vector3? crotch = Vec(s?["CameraTarget"]?["Crotch"]);
        float bodyView = View(body, camPos, camRot, fov, aspect, out float bodySize);
        float crotchView = View(crotch, camPos, camRot, fov, aspect, out float crotchSize);
        float faceView = View(face, camPos, camRot, fov, aspect, out _);
        if (B(s, "AiChecked") && s?["AiFace"] != null && !B(s, "AiFace")) faceView = 0;   // the AI didn't see her face

        bool front = true;
        var pr = s?["Position"];
        if (pr != null && body.HasValue)
        {
            var q = new Quaternion(F(pr, "rx"), F(pr, "ry"), F(pr, "rz"), F(pr, "rw", 1));
            front = Vector3.Dot(q * Vector3.forward, (camPos - body.Value).normalized) > 0.1f;
        }
        if (bodyView <= 0 && crotchView <= 0 && faceView <= 0) tags.Add("she is barely in the shot");
        else if (bodySize > 0.9f) tags.Add("close-up");
        else if (bodySize < 0.35f) tags.Add("wide shot from a distance");

        // Outfit pieces (not the coat)
        var worn = new List<string>();
        if (s?["Cosplay"] is JsonObject cos)
            worn = cos.Where(kv => int.TryParse(kv.Key, out _) && kv.Value != null).Select(kv => kv.Value.ToString().ToLowerInvariant()).ToList();
        bool Wears(params string[] keys) => worn.Any(w => keys.Any(k => w.Contains(k)));
        bool underwear = Wears("pants", "panty", "panties", "shorts", "thong") || B(s, "AiUnderwear");

        var ex = s?["Exposed"];
        float expose = 0;
        bool pantyShot = false;
        var parts = new List<string>();
        if (B(ex, "All")) { expose = 80; parts.Add("fully nude"); }
        else
        {
            if (B(ex, "Upper")) { expose += 40; parts.Add("breasts out"); }
            if (B(ex, "Front")) { expose += 55; parts.Add("pussy exposed"); }
            if (B(ex, "Hip")) { if (underwear) pantyShot = true; else { expose += 38; parts.Add("bare ass"); } }
            // "HipCrouch" means the outfit WOULD expose her when crouching - only count it if she actually is
            if (B(ex, "HipCrouch") && B(s, "Crouching")) { if (underwear) pantyShot = true; else { expose += 36; parts.Add("crouching, bare ass showing"); } }
        }
        bool ai = B(s, "AiChecked");
        float framing = Mathf.Clamp(Mathf.Max(bodyView, crotchView) * Mathf.Clamp(bodySize * 1.5f, 0.5f, 1.2f), 0.15f, 1.2f);
        if (ai && expose > 0) framing = Mathf.Max(framing, 0.6f);   // the AI actually saw it in the photo
        Add(Mathf.Min(80, expose) * framing, $"{(parts.Count == 0 ? "no exposure" : string.Join(", ", parts))} (framing x{framing:0.0})");
        // Nudity only counts (and is only described to fans) when it is actually in the picture
        bool nudity = expose >= 18 && framing >= 0.25f;
        if (framing >= 0.25f) tags.AddRange(parts);

        // How much of her body - and so of what she is doing - the photo really shows.
        // Feet-only or off-body shots get only a sliver of the points for poses, toys and acts,
        // and fans aren't told about anything the photo doesn't show.
        float shown = Mathf.Clamp01(Mathf.Max(bodyView, crotchView) * 1.4f);
        if (ai && B(s, "AiBodyHidden")) shown = 0f;
        float poseMul = Mathf.Max(0.15f, shown);
        float actMul = ai && !B(s, "AiActSeen") ? Mathf.Min(poseMul, 0.3f) : poseMul;   // the AI didn't see anything sexual happening
        bool poseSeen = poseMul >= 0.5f, actSeen = actMul >= 0.5f;
        string Seen(float m) => m < 0.99f ? $" (only x{m:0.00} visible)" : "";
        if (!nudity && shown >= 0.5f) tags.Add(worn.Count > 0 ? "dressed" : "clothed tease");

        if (B(s?["Coat"], "Dropped")) { Add(nudity ? 5 : 1, "coat dropped"); if (nudity || poseSeen) tags.Add("coat dropped"); }
        if (N(s, "Bodypaint") > 0 && nudity) { Add(8, "only body paint"); tags.Add("wearing only body paint"); }
        if (N(s, "Vibrator") > 0) { Add(10 * poseMul, "vibrator on" + Seen(poseMul)); if (poseSeen) tags.Add("vibrator buzzing"); }
        if (N(s, "Piston") > 0) { Add(20 * actMul, "toy machine" + Seen(actMul)); if (actSeen) tags.Add("using a toy machine"); }
        string cuffs = s?["Handcuffs"]?["State"]?.ToString() ?? "";
        if (cuffs != "" && cuffs != "None") { Add(10 * poseMul, "handcuffed" + Seen(poseMul)); if (poseSeen) tags.Add("handcuffed"); }
        if (B(s, "Blindfolded")) { Add(faceView > 0 ? 6 : 1, "blindfolded"); if (faceView > 0) tags.Add("blindfolded"); }
        if (B(s, "Futanari")) { Add(8 * poseMul, "futanari" + Seen(poseMul)); if (poseSeen && nudity) tags.Add("futanari"); }
        if (B(s, "Orgasm")) { Add(25 * actMul, "orgasming" + Seen(actMul)); if (actSeen) tags.Add("caught mid-orgasm"); }
        if (B(s, "Peeing")) { Add(25 * actMul, "peeing" + Seen(actMul)); if (actSeen) tags.Add("peeing in public"); }
        if (B(s, "Bukkake")) { Add(30 * poseMul, "covered in cum" + Seen(poseMul)); if (poseSeen) tags.Add("covered in cum"); }

        string action = s?["Action"]?.ToString() ?? "";
        bool masturbating = action.Contains("Onani") || action == "GanimataKoshiHeko";
        bool suggestive = Suggestive.Contains(action);
        string pose = Pretty(action.Replace("Old", ""));
        if (action.Contains("Sex") || action.Contains("Dildo")) { Add((action.Contains("Dildo") ? 50 : 55) * actMul, (action.Contains("Dildo") ? "dildo" : "sex") + Seen(actMul)); if (actSeen) tags.Add(action.Contains("Dildo") ? "riding a dildo" : "having sex"); }
        else if (masturbating) { Add(40 * actMul, "masturbating" + Seen(actMul)); if (actSeen) tags.Add($"masturbating in public ({pose})"); }
        else if (suggestive) { Add(18 * poseMul, $"suggestive pose ({pose})" + Seen(poseMul)); if (poseSeen) tags.Add("suggestive pose: " + pose); }
        else if (action is "TakeOffPants" or "TakeOffBra" or "DroppingClothes" or "ChangeClothes") { Add(10 * poseMul, "stripping" + Seen(poseMul)); if (poseSeen) tags.Add("stripping"); }
        else if (action == "InsertAnalPlug") { Add(25 * actMul, "anal plug" + Seen(actMul)); if (actSeen) tags.Add("inserting an anal plug"); }
        else if (!string.IsNullOrEmpty(action) && action != "None" && action != "Idle" && poseSeen) tags.Add("pose: " + pose);
        if (B(s, "Crouching") && nudity) { Add(3, "crouching"); tags.Add("crouching"); }

        // Feet: plenty of her fans love them - a close-up of bare feet is worth real points even fully dressed
        Vector3? feet = Vec(s?["Position"]);
        bool feetInShot = View(feet, camPos, camRot, fov, aspect, out float feetSize) > 0;
        string feetKind = Wears(Footwear) ? "shoes" : Wears(Hosiery) ? "stockings" : "bare";
        string aiFeet = s?["AiFeet"]?.ToString() ?? "";
        if (ai && aiFeet == "hidden") feetInShot = false;                                   // the AI can't see them
        else if (ai && aiFeet is "bare" or "stockings" or "shoes") { feetKind = aiFeet; feetInShot = true; }
        bool feetClose = feetInShot && (feetSize > 0.8f || (ai && B(s, "AiBodyHidden")));   // feet are the subject of the photo
        float feetCap = 0;
        if (feetInShot)
        {
            (float close, float dressed, float nude, float cap, float capClose, string what) f = feetKind switch
            {
                "bare" => (42f, 14f, 5f, 22f, 48f, "bare feet"),
                "stockings" => (30f, 9f, 3f, 16f, 36f, "feet in stockings"),
                _ => (12f, 3f, 2f, 8f, 15f, "feet in shoes"),
            };
            float pts = feetClose ? f.close : (expose >= 18 ? f.nude : f.dressed);
            Add(pts, feetClose ? f.what + " close-up" : f.what);
            tags.Add(feetClose ? "close-up of her " + f.what : f.what);
            feetCap = feetClose ? f.capClose : f.cap;
        }

        // Shots framed on her chest, butt or crotch (centred and close) are a little daring even when dressed
        bool focus = false;
        if (crotchView > 0.6f && crotchSize > 0.9f)
        {
            focus = true;
            Add(nudity ? 4 : 15, front ? "crotch close-up" : "butt close-up");
            tags.Add(front ? "close-up focused on her crotch" : "close-up focused on her butt");
        }
        else if (bodyView > 0.6f && bodySize > 0.9f && front)
        {
            focus = true;
            Add(nudity ? 4 : 12, "chest close-up");
            tags.Add("close-up focused on her chest");
        }

        // Panty shots: a classic - worth real points, a close-up up the skirt most of all
        float pantyCap = 0;
        bool pantyVisible = pantyShot && (ai || Mathf.Max(bodyView, crotchView) > 0.3f);
        if (pantyVisible)
        {
            bool pantyClose = crotchView > 0.6f && crotchSize > 0.9f;
            bool upskirt = Wears("skirt");
            string what = upskirt ? "upskirt panty shot" : B(s, "Crouching") ? "panty peek while crouching" : "panties showing";
            if (nudity) Add(6, what);   // she's showing more than that anyway
            else
            {
                Add(pantyClose ? 46 : 35, pantyClose ? what + " close-up" : what);
                if (upskirt) Add(4, "under her skirt");
                pantyCap = pantyClose ? 55 : 42;
            }
            tags.Insert(0, pantyClose ? $"close-up {what}: her panties are on full display" : what);
        }
        // Bra on show (only the AI can tell)
        if (B(s, "AiBra") && !nudity) { Add(35, "bra showing"); tags.Add("her bra is showing"); pantyCap = Mathf.Max(pantyCap, 42); }
        if (B(s, "AiNipSlip") && !nudity) { Add(45, "nip slip"); tags.Insert(0, "nip slip: her nipple is peeking out of her top"); pantyCap = Mathf.Max(pantyCap, 55); }
        else if (B(s, "AiSideboob") && !nudity) { Add(35, "side-boob / peek inside her top"); tags.Insert(0, "bare breast peeking out of her top"); pantyCap = Mathf.Max(pantyCap, 42); }
        if (B(s, "AiCleavage") && !nudity && !B(s, "AiBra")) { Add(28, "cleavage"); tags.Add("cleavage on show"); pantyCap = Mathf.Max(pantyCap, 32); }

        if (B(s, "ShowingOff")) { Add(nudity ? 10 : 3, "showing off"); if (nudity || poseSeen) tags.Add("showing off on purpose"); }
        if (B(s, "Watched")) { Add(nudity ? 22 : 6, "strangers watching"); tags.Add("strangers are watching her"); }
        else if (B(s, "NearNPC")) { Add(nudity ? 14 : 4, "people nearby"); tags.Add("people right nearby"); }
        if (B(s, "InLight") && nudity) { Add(4, "in the light"); tags.Add("standing in the light"); }
        if (nudity) { if (B(s, "DayTime")) { Add(10, "daylight"); tags.Add("broad daylight"); } else tags.Add("at night"); }
        float detection = Mathf.Clamp01(N(s, "Detection"));
        Add(detection * (nudity ? 25 : 6), $"risk of being spotted (detection {detection * 100:0}%)");

        bool masked = Wears("mask", "helmet", "sunglass", "goggle");
        bool faceShown = faceView > 0 && front && !masked && !B(s, "Blindfolded");
        if (faceShown) { Add(15, "FACE VISIBLE"); tags.Add("HER FACE IS VISIBLE"); }
        else if (faceView > 0 && (masked || B(s, "Blindfolded"))) tags.Add("face covered");

        string stage = s?["Position"]?["stage"]?.ToString() ?? "";
        tags.Add("location: " + StageName(stage));
        // Clothes: what the AI could actually see; without the AI only when her body is in the shot
        string aiClothes = s?["AiClothes"]?.ToString() ?? "";
        if (ai && aiClothes.Length > 0)
        {
            if (shown > 0 && !aiClothes.ToLowerInvariant().Contains("nothing")) tags.Add("wearing " + aiClothes);
        }
        else if (shown >= 0.5f)
            foreach (var w in worn.Take(5)) tags.Add("wearing " + Pretty(w.Replace("m_cosplay_", "").Replace("general_", "")));

        // Most telling first (fans' prompts only read the first few)
        if (feetInShot && shown <= 0f) { tags.Remove("she is barely in the shot"); tags.Insert(0, "only her feet and legs are in the shot"); }
        int feetTag = tags.FindIndex(t => t.StartsWith("close-up of her ") && t.Contains("feet"));
        if (feetTag > 0) { var t = tags[feetTag]; tags.RemoveAt(feetTag); tags.Insert(0, t); }
        string aiSummary = s?["AiSummary"]?.ToString() ?? "";
        if (ai && aiSummary.Length > 0) tags.Insert(0, "the photo shows: " + aiSummary);

        // ---- From raw points to the final score; every step shows the running total ----
        float rawTotal = score;
        void Step(string what) => steps.Add($"{what} -> {score:0}");
        if (stage is "StationFront" or "ShoppingMall" or "Convenience") { score *= 1.12f; Step("x1.12 busy location"); }

        // An explicit act only lifts the caps when it is clearly visible
        bool explicitAct = (B(s, "Orgasm") || B(s, "Peeing") || B(s, "Bukkake") || action.Contains("Sex") || action.Contains("Dildo") || masturbating) && actMul >= 0.5f;
        bool inShot = bodyView > 0 || crotchView > 0 || faceView > 0 || feetInShot;
        if (!inShot) { score *= 0.15f; Step("x0.15 she is barely in the shot"); }
        if (!nudity && !explicitAct)
        {
            float cap = Mathf.Max(Mathf.Max(suggestive && poseMul >= 0.5f ? 28 : focus ? 26 : 8, feetCap), pantyCap);
            if (score > cap) { score = cap; Step($"capped at {cap:0} (no real nudity)"); }
        }
        if (nudity && expose < 65 && !explicitAct && score > 70) { score = 70; Step("capped at 70 (partial nudity)"); }
        bool audience = B(s, "Watched") || B(s, "NearNPC");
        if (!audience && score > 70) { score = 70 + (score - 70) * 0.4f; Step("top end softened (nobody around)"); }
        score = 100f * Mathf.Pow(Mathf.Clamp01(score / 100f), 1.8f);
        int final = Mathf.Clamp(Mathf.RoundToInt(score), 0, 100);
        // Show each reason's share of the FINAL score so the lines always add up to it
        float rawSum = items.Sum(i => i.pts);
        var shares = items.Select(i => (i.reason, exact: rawSum > 0 ? final * i.pts / rawSum : 0f)).ToList();
        var rounded = shares.Select(x => (int)Math.Floor(x.exact)).ToList();
        int missing = final - rounded.Sum();
        foreach (int idx in shares.Select((x, i) => (frac: x.exact - Math.Floor(x.exact), i)).OrderByDescending(t => t.frac).Select(t => t.i).Take(Math.Max(0, missing)))
            rounded[idx]++;
        for (int i = 0; i < shares.Count; i++)
            if (rounded[i] > 0) why.Add($"+{rounded[i]}  {shares[i].reason}");
        why.Add($"(raw points {rawTotal:0}{(steps.Count > 0 ? ", " + string.Join(", ", steps) : "")}, strict curve -> {final})");
        return (final, tags, faceShown, why);
    }

    // Is the photo a close-up framed on her crotch / butt (from the camera geometry)?
    public static bool CrotchCloseUp(JsonNode s, Vector3 camPos, Quaternion camRot, float fov, float aspect)
    {
        float v = View(Vec(s?["CameraTarget"]?["Crotch"]), camPos, camRot, fov, aspect, out float size);
        return v > 0.6f && size > 0.9f;
    }

    // Plain facts from the game about the moment the photo was taken (context for the AI judge, shown without points)
    public static List<string> GameInfo(JsonNode s, bool outfit = true)
    {
        var info = new List<string>();
        string stage = s?["Position"]?["stage"]?.ToString() ?? "";
        info.Add(StageName(stage));
        info.Add(B(s, "DayTime") ? "daytime" : "night");
        if (B(s, "Watched")) info.Add("strangers are watching her");
        else if (B(s, "NearNPC")) info.Add("people nearby");
        else info.Add("nobody around");
        float detection = Mathf.Clamp01(N(s, "Detection"));
        if (detection > 0) info.Add($"risk of being spotted {detection * 100:0}%");
        if (B(s, "InLight")) info.Add("standing in the light");
        string action = s?["Action"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(action) && action != "None" && action != "Idle") info.Add("pose: " + Pretty(action.Replace("Old", "")));
        if (B(s, "Crouching")) info.Add("crouching");
        if (B(s?["Coat"], "Dropped")) info.Add("coat taken off");
        if (N(s, "Vibrator") > 0) info.Add("vibrator on");
        if (N(s, "Piston") > 0) info.Add("toy machine");
        string cuffs = s?["Handcuffs"]?["State"]?.ToString() ?? "";
        if (cuffs != "" && cuffs != "None") info.Add("handcuffed");
        if (B(s, "Blindfolded")) info.Add("blindfolded");
        if (B(s, "Orgasm")) info.Add("orgasming");
        if (B(s, "Peeing")) info.Add("peeing");
        if (B(s, "Bukkake")) info.Add("covered in cum");
        if (outfit && s?["Cosplay"] is JsonObject cos)
        {
            var worn = cos.Where(kv => int.TryParse(kv.Key, out _) && kv.Value != null)
                          .Select(kv => Pretty(kv.Value.ToString().ToLowerInvariant().Replace("m_cosplay_", "").Replace("general_", ""))).ToList();
            info.Add(worn.Count > 0 ? "outfit: " + string.Join(", ", worn) : "outfit: nothing");
        }
        return info;
    }

    // 0 = not in frame, 1 = well inside the frame. size ~ how large the subject appears.
    private static float View(Vector3? p, Vector3 camPos, Quaternion camRot, float fov, float aspect, out float size)
    {
        size = 0;
        if (!p.HasValue) return 0;
        Vector3 v = Quaternion.Inverse(camRot) * (p.Value - camPos);
        if (v.z < 0.1f) return 0;
        float t = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);   // vertical
        float x = v.x / (v.z * t * Mathf.Max(0.1f, aspect)), y = v.y / (v.z * t);
        size = 0.6f / (v.z * t);
        float edge = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
        if (edge > 1.05f || v.z > 25f) return 0;
        return Mathf.Clamp01(1.2f - edge);
    }

    private static readonly HashSet<string> Suggestive = new()
    {
        "GanimataWalk", "OldGanimataWalk", "AhegaoDoublePiece", "HipShake", "GanimataHip", "KaikyakuFella", "Dogeza", "DogezaUpHead",
        "DogTintin", "IBalance", "WakimiseCrouch", "Tebura", "ChikubiRotate", "NeKataashiage", "Haigure", "PeeKaikyaku", "PeeDog"
    };
    private static readonly string[] Footwear = { "shoe", "boot", "heel", "sneaker", "sandal", "pump", "loafer", "geta", "slipper" };
    private static readonly string[] Hosiery = { "stocking", "sock", "tights", "pantyhose" };

    public static string StageName(string s) => s switch
    {
        "Residence" => "residential street",
        "Convenience" => "convenience store",
        "FashionShop" => "fashion shop",
        "ShoppingMall" => "shopping mall",
        "StationFront" => "in front of the train station",
        "Park" => "park",
        "Mansion" => "apartment building",
        "Apart" => "home",
        "" => "somewhere in the city",
        _ => Pretty(s)
    };

    private static string Pretty(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s.Replace('_', ' '))
        {
            if (char.IsUpper(c) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim();
    }

    private static Vector3? Vec(JsonNode n) => n == null ? null : new Vector3(F(n, "x"), F(n, "y"), F(n, "z"));
    private static float F(JsonNode n, string k, float d = 0) => float.TryParse(n?[k]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    private static float N(JsonNode n, string k) => F(n, k);
    private static bool B(JsonNode n, string k) => n?[k]?.ToString()?.ToLowerInvariant() == "true";
}
