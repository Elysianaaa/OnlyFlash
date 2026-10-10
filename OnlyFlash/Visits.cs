using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OnlyFlash;

public class VisitReq
{
    public int Vid;
    public string StarId;          // null = anonymous top-tier fan
    public string Name;
    public string Type;
    public int Body, Hair, Face;
    public float Size = 1f;
    public string State = "queued"; // queued -> spawned -> arrived -> (accept|decline) -> ended
    public string Decision;         // null until the player chooses
    public double ArrivedAt;
}

// Visits are driven by the CustomMissions2\OnlyFlash script, which owns the NPC:
//   OnlyFlashNextVisit() -> spawn it and make it walk over
//   OnlyFlashVisitEvent(id, "spawned"|"arrived"|"lost"|"declined"|"done"|"nosex")
//   OnlyFlashVisitDecision(id) -> "accept" / "decline" once the player pressed a key
public static class Visits
{
    private static readonly Random Rng = new();
    private static readonly List<VisitReq> Queue = new();
    public static VisitReq Active;
    private static int nextId = 1;
    private static double nextSpontaneousCheck = Store.Now + 120;

    public static void Reset()
    {
        Queue.Clear();
        Active = null;
    }

    public static bool InProgress => Active != null;

    public static bool IsQueuedOrActive(string starId) =>
        (Active != null && Active.StarId == starId) || Queue.Any(v => v.StarId == starId);

    public static void QueueStar(Star s)
    {
        if (IsQueuedOrActive(s.Id)) return;
        Queue.Add(new VisitReq { Vid = nextId++, StarId = s.Id, Name = s.Name, Type = s.AvatarType, Body = s.Body, Hair = s.Hair, Face = s.Face, Size = s.Size });
    }

    public static void QueueAnonymous(string handle)
    {
        string[] types = { "NewMale", "NewMale", "NewOji", "PreviousMale" };
        string type = types[Rng.Next(types.Length)];
        var (b, h, f) = Ranges(type);
        Queue.Add(new VisitReq { Vid = nextId++, Name = handle, Type = type, Body = Rng.Next(b), Hair = Rng.Next(h), Face = Rng.Next(f) });
    }

    public static (int bodies, int hairs, int faces) Ranges(string type) => type switch
    {
        "NewFemale" => (11, 10, 11),
        "NewMale" => (5, 6, 5),
        "PreviousFemale" => (15, 7, 1),
        "PreviousMale" => (12, 5, 1),
        "NewOba" => (2, 1, 1),
        "NewOji" => (3, 1, 1),
        _ => (1, 1, 1)
    };

    public static VisitReq Next()
    {
        if (Active != null || Queue.Count == 0) return null;
        Active = Queue[0];
        Queue.RemoveAt(0);
        return Active;
    }

    public static void Event(int vid, string ev)
    {
        if (Active == null || Active.Vid != vid) return;
        switch (ev)
        {
            case "spawned":
                Active.State = "spawned";
                break;
            case "arrived":
                Active.State = "arrived";
                Active.ArrivedAt = Store.Now;
                break;
            default:
                Finish(Active, ev);
                Active = null;
                break;
        }
    }

    public static string Decision(int vid)
    {
        if (Active == null || Active.Vid != vid || Active.State != "arrived") return null;
        // Nobody answers forever: send them away after 40 s
        if (Active.Decision == null && Store.Now - Active.ArrivedAt > 40) Active.Decision = "decline";
        return Active.Decision;
    }

    private static void Finish(VisitReq v, string how)
    {
        var s = v.StarId == null ? null : Store.StarById(v.StarId);
        if (s == null) return;
        string where = Scoring.StageName(SFM_custom_mission_v2.PlayerData.stage.ToString());
        lock (Store.Sync)
        {
            switch (how)
            {
                case "done":
                    s.Visits++;
                    s.Affection = Math.Min(100, s.Affection + 10);
                    s.Memory.Add($"Met her in person at the {where} and had sex with her (meeting #{s.Visits}).");
                    break;
                case "nosex":
                    s.Visits++;
                    s.Affection = Math.Min(100, s.Affection + 3);
                    s.Memory.Add($"Met her in person at the {where}, but nothing happened.");
                    break;
                case "declined":
                    s.Affection = Math.Max(0, s.Affection - 4);
                    s.Memory.Add($"Came to find her at the {where}, but she sent him away.");
                    break;
                case "lost":
                    s.Memory.Add($"Tried to find her at the {where} but couldn't reach her.");
                    break;
            }
            s.LastVisitUnix = Store.Now;
            while (s.Memory.Count > 12) s.Memory.RemoveAt(0);
        }
        Store.MarkDirty();
        Chat.AfterVisit(s, how);
    }

    // Stars sometimes decide to come on their own while she is out
    public static void SpontaneousTick(bool outside, double recentDaring)
    {
        double now = Store.Now;
        if (!outside || now < nextSpontaneousCheck) return;
        nextSpontaneousCheck = now + Math.Max(1, Plugin.VisitCheckMinutes.Value) * 60 * (0.7 + Rng.NextDouble() * 0.6);
        if (Active != null || Queue.Count > 0 || !Plugin.SpontaneousVisits.Value) return;

        foreach (var s in Store.D.Stars.Where(s => s.Joined).OrderBy(_ => Rng.Next()))
        {
            if (now - s.LastVisitUnix < 600 || s.Affection < 45) continue;
            // Only come on their own if they've recently seen something she posted that they can access
            var seen = Store.D.Posts.FirstOrDefault(p => p.Tier <= Math.Max(0, s.Tier) && now - p.PostedUnix < 1800);
            if (seen == null) continue;
            double chance = 0.06 + s.Affection / 100.0 * 0.15 + seen.Daring / 100.0 * 0.10;
            if (Rng.NextDouble() > chance) continue;
            QueueStar(s);
            Chat.SystemDm(s, "on my way to you right now. don't move.", "He decided on his own to come find her in person right now. Text her that you're on your way, in character, one short sentence.");
            break;
        }
    }
}

// Private messages with star fans, written by the AI with their personality and memory.
public static class Chat
{
    public static bool Busy(Star s) => busy.Contains(s.Id);
    private static readonly HashSet<string> busy = new();

    public static void Send(Star s, string text, bool invite, string photoId = null)
    {
        if ((string.IsNullOrWhiteSpace(text) && photoId == null) || Busy(s)) return;
        lock (Store.Sync) s.Chat.Add(new ChatMsg { FromMe = true, Text = (text ?? "").Trim(), Unix = Store.Now, PhotoId = photoId });
        Store.MarkDirty();
        if (invite) Visits.QueueStar(s);
        string instruction = null;
        if (invite) instruction = "She just INVITED you to come meet her in person right now and you are on your way. Reply accordingly.";
        else if (photoId != null)
        {
            // Wait for the AI judge so he reacts to what the photo really shows
            busy.Add(s.Id);
            VisionJudge.AfterJudged(photoId, () =>
            {
                busy.Remove(s.Id);
                var photo = Store.Photo(photoId);
                string what = photo == null ? "She just sent you a private photo. React to it." :
                    $"She just sent you a PRIVATE photo, only for you: {string.Join(", ", photo.Tags.Take(8))} (daring {photo.Daring}/100). React only to what the photo shows, specifically and in character.";
                if (photo != null)
                {
                    lock (Store.Sync) { s.Memory.Add($"She privately sent me a photo: {string.Join(", ", photo.Tags.Take(4))}."); while (s.Memory.Count > 12) s.Memory.RemoveAt(0); }
                    s.Affection = Math.Min(100, s.Affection + 2);
                }
                Ask(s, what);
            });
            return;
        }
        Ask(s, instruction);
    }

    // A message the star sends first (e.g. when they decide to visit)
    public static void SystemDm(Star s, string fallback, string instruction)
    {
        if (!Ai.Available)
        {
            Add(s, fallback);
            return;
        }
        Ask(s, instruction, fallback);
    }

    // ---------------- messages fans send on their own ----------------

    private static readonly Random Rng = new();
    private static double nextDmCheck = Store.Now + 90;

    // Runs on OnlyFlash's clock, so a time skip makes it happen right away
    private static void Delayed(double seconds, Action a) => Sim.After(seconds, a);

    private static double LastMessageAt(Star s) => s.Chat.Count == 0 ? 0 : s.Chat[^1].Unix;

    // Every minute or two one fan may text her unprompted
    public static void DmTick(bool outside)
    {
        double now = Store.Now;
        if (!Plugin.FanDms.Value || now < nextDmCheck) return;
        nextDmCheck = now + 60 + Rng.NextDouble() * 60;
        if (Store.D.Posts.Count == 0) return;   // nobody knows her yet
        double sinceLastPost = now - Store.D.Posts[0].PostedUnix;
        double activity = sinceLastPost < 3600 ? 1.0 : 0.3;   // quiet page = fewer unprompted messages
        var candidates = Store.D.Stars.Where(s => s.Joined && !Busy(s)
            && now - s.LastDmUnix > Plugin.DmMinGapMinutes.Value * 60
            && now - LastMessageAt(s) > 180).OrderBy(_ => Rng.Next()).ToList();
        foreach (var s in candidates)
        {
            if (Rng.NextDouble() > (0.05 + s.Affection / 100.0 * 0.12) * activity) continue;
            var (instruction, fallback) = Reason(s, outside, now);
            SystemDm(s, fallback, instruction);
            break;
        }
    }

    // Time skip: fans who would have texted her during the skipped time do it now (at most one message each)
    public static int CatchUp(double seconds)
    {
        if (!Plugin.FanDms.Value || Store.D.Posts.Count == 0 || seconds <= 0) return 0;
        double now = Store.Now;
        string stage = SFM_custom_mission_v2.PlayerData.stage.ToString();
        bool outside = stage != "Apart" && stage != "None";
        // Was her page active during the skipped stretch?
        double activity = (now - seconds) - Store.D.Posts[0].PostedUnix < 3600 ? 1.0 : 0.3;
        var candidates = Store.D.Stars.Where(s => s.Joined && !Busy(s)
            && now - s.LastDmUnix > Plugin.DmMinGapMinutes.Value * 60
            && now - LastMessageAt(s) > 180).OrderBy(_ => Rng.Next()).ToList();
        if (candidates.Count == 0) return 0;
        // DmTick checks every ~90 s and picks at most one fan per check
        double checksEach = seconds / 90.0 / candidates.Count;
        int sent = 0;
        foreach (var s in candidates)
        {
            double p = (0.05 + s.Affection / 100.0 * 0.12) * activity;
            if (Rng.NextDouble() > 1 - Math.Pow(1 - p, checksEach)) continue;
            var star = s;
            var (instruction, fallback) = Reason(star, outside, now);
            Delayed(1 + sent * 4, () => { if (!Busy(star)) SystemDm(star, fallback, instruction); });   // stagger the AI calls
            sent++;
        }
        nextDmCheck = now + 60 + Rng.NextDouble() * 60;
        return sent;
    }

    private static (string instruction, string fallback) Reason(Star s, bool outside, double now)
    {
        var options = new List<(string, string)>();
        var latest = Store.D.Posts.FirstOrDefault(p => p.Tier <= Math.Max(0, s.Tier));
        if (latest != null && s.LastReactedPost != latest.Id && now - latest.PostedUnix < 1800)
        {
            s.LastReactedPost = latest.Id;
            return ($"React privately to her latest post ({string.Join(", ", latest.Tags.Take(6))}; daring {latest.Daring}/100). Be specific about the photo.", "that last post... I can't stop looking at it.");
        }
        string stage = SFM_custom_mission_v2.PlayerData.stage.ToString();
        if (outside)
        {
            options.Add(($"She is out at the {Scoring.StageName(stage)} right now. Text her something flirty or teasing, or dare her to do something specific there.", "you're out right now aren't you... show me something."));
            options.Add(("Ask her what she is wearing (or not wearing) right now, in character.", "what are you wearing right now?"));
        }
        else
        {
            options.Add(("She is at home. Check in on her, flirt, or tell her exactly what you want to see in her next post.", "thinking about your next post..."));
        }
        if (now - LastMessageAt(s) > 1800) options.Add(("You haven't talked in a while. Reach out to her in character.", "hey... it's been a while."));
        if (s.Affection >= 60) options.Add(("Tell her something about how you feel about her, in character.", "I can't stop thinking about you."));
        return options[Rng.Next(options.Count)];
    }

    // Some subscribers react privately to a new post
    public static void OnPosted(Post post)
    {
        if (!Plugin.FanDms.Value) return;
        string tierName = Ui.TierLabel(post.Tier);
        foreach (var s in Store.D.Stars.Where(s => s.Joined && s.Tier >= post.Tier).OrderBy(_ => Rng.Next()).Take(2))
        {
            if (Rng.NextDouble() > 0.4) continue;
            var star = s;
            Delayed(45 + Rng.NextDouble() * 105, () =>
            {
                if (Busy(star) || star.LastReactedPost == post.Id) return;
                star.LastReactedPost = post.Id;
                SystemDm(star, "just saw your new post... wow.",
                    $"She just posted a new {tierName} photo: {string.Join(", ", post.Tags.Take(6))} (daring {post.Daring}/100). Text her your private reaction, specific to the photo.");
            });
        }
    }

    public static void AfterVisit(Star s, string how)
    {
        string where = Scoring.StageName(SFM_custom_mission_v2.PlayerData.stage.ToString());
        (string instruction, string fallback) msg = how switch
        {
            "done" => ($"Text her a little while after your in-person meeting at the {where}, where you had sex. React to it in character.", "I can still feel you... that was insane."),
            "declined" => ("She sent you away when you came to find her. Text her about it in character (hurt, teasing or determined, depending on your personality).", "you really sent me away? ...next time."),
            "nosex" => ($"You met her in person at the {where} but nothing happened. Text her about it.", "so close... next time."),
            _ => (null, null)
        };
        if (msg.instruction == null || !Plugin.FanDms.Value) return;
        Delayed(60 + Rng.NextDouble() * 60, () => { if (!Busy(s)) SystemDm(s, msg.fallback, msg.instruction); });
    }

    // A brand-new fan slides into her DMs after a bold public post
    public static void MaybeNewContact(Post post)
    {
        if (!Plugin.NewContacts.Value || post.Tier != 0 || post.Daring < 45 || Store.D.Profile.Followers < 15) return;
        if (Store.D.Stars.Count(s => s.Auto) >= Plugin.MaxAutoContacts.Value) return;
        if (Rng.NextDouble() > 0.12 + post.Daring / 100.0 * 0.10) return;
        Delayed(30 + Rng.NextDouble() * 90, () => CreateContact(post));
    }

    private static void CreateContact(Post post)
    {
        string[] types = { "NewMale", "NewMale", "NewOji", "PreviousMale", "NewFemale" };
        Task.Run(async () =>
        {
            string name = null, personality = null, style = null, type = null, first = null;
            if (Ai.Available)
            {
                try
                {
                    string system = Ai.WorldRules + "\n\nInvent a brand-new fan who just discovered M-chan through this public post and is sending her a first private message. Make them distinct from her usual fans." +
                        "\nOUTPUT: only JSON: {\"name\":\"username\",\"personality\":\"2 sentences: who they are and what they want from her\",\"style\":\"how they text\",\"type\":\"NewMale|NewOji|PreviousMale|NewFemale\",\"first_message\":\"their first DM, in character\"}";
                    string raw = await Ai.Chat(system, "The post: " + string.Join(", ", post.Tags) + $" (daring {post.Daring}/100).", 400);
                    if (Ai.ExtractJson(raw, '{', '}') is JsonObject o)
                    {
                        name = Ai.Trunc(o["name"]?.ToString(), 20);
                        personality = Ai.Trunc(o["personality"]?.ToString(), 300);
                        style = Ai.Trunc(o["style"]?.ToString(), 160);
                        type = o["type"]?.ToString();
                        first = Ai.Trunc(o["first_message"]?.ToString(), 300);
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash new contact failed: " + e.Message); }
            }
            Sim.MainThread.Enqueue(() =>
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(first))
                {
                    name = Sim.RandomHandle();
                    personality = "A new fan who found her public post and can't stop thinking about it. Bold and curious.";
                    style = "Direct, a little shy at first.";
                    first = "hi... I just found your page. that last photo was unreal.";
                }
                if (!types.Contains(type)) type = types[Rng.Next(types.Length)];
                var (b, h, f) = Visits.Ranges(type);
                var s = new Star
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8), Name = name, Personality = personality, Style = style,
                    AvatarType = type, Body = Rng.Next(b), Hair = Rng.Next(h), Face = Rng.Next(f), Tier = 0, Affection = 30, Auto = true, Joined = true
                };
                lock (Store.Sync) Store.D.Stars.Add(s);
                Add(s, first);
                Sim.Notices.Enqueue(("OnlyFlash", $"New message from {name}"));
            });
        });
    }

    private static void Add(Star s, string text)
    {
        lock (Store.Sync) { s.Chat.Add(new ChatMsg { FromMe = false, Text = text, Unix = Store.Now }); s.Unread = true; s.LastDmUnix = Store.Now; }
        Store.MarkDirty();
        Sim.Notices.Enqueue((s.Name, text));
    }

    private static void Ask(Star s, string instruction, string fallback = null)
    {
        busy.Add(s.Id);
        string prompt = BuildPrompt(s, instruction);
        Task.Run(async () =>
        {
            string reply = null, memory = null;
            float delta = 0;
            bool coming = false;
            try
            {
                if (!Ai.Available) throw new Exception("AI not configured");
                string raw = await Ai.Chat(SystemPrompt(s), prompt, 400);
                if (Ai.ExtractJson(raw, '{', '}') is JsonObject o)
                {
                    reply = Ai.Trunc(o["reply"]?.ToString(), 400);
                    memory = o["memory"]?.ToString();
                    float.TryParse(o["affection_change"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delta);
                    coming = o["coming_over"]?.ToString()?.ToLowerInvariant() == "true";
                }
                else reply = Ai.Trunc(raw, 400);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"OnlyFlash chat with {s.Name} failed: {e.Message}");
                reply = fallback ?? "(no signal... try again in a moment)";
            }
            Sim.MainThread.Enqueue(() =>
            {
                busy.Remove(s.Id);
                if (!string.IsNullOrWhiteSpace(reply)) Add(s, reply);
                lock (Store.Sync)
                {
                    s.Affection = Math.Clamp(s.Affection + Math.Clamp(delta, -10, 10), 0, 100);
                    if (!string.IsNullOrWhiteSpace(memory)) s.Memory.Add(Ai.Trunc(memory, 160));
                    while (s.Memory.Count > 12) s.Memory.RemoveAt(0);
                }
                if (coming && !Visits.IsQueuedOrActive(s.Id))
                {
                    Visits.QueueStar(s);
                    Sim.Notices.Enqueue(("OnlyFlash", $"{s.Name} is coming to find you."));
                }
                Store.MarkDirty();
            });
        });
    }

    private static string SystemPrompt(Star s)
    {
        string tier = s.Tier <= 0 ? "free follower" : Store.D.Profile.Tiers[Math.Clamp(s.Tier, 1, 3) - 1].Name + " subscriber";
        string mood = s.Affection switch { < 20 => "cold and annoyed with her", < 40 => "interested", < 60 => "into her", < 80 => "obsessed with her", _ => "completely devoted to her" };
        return Ai.WorldRules + $@"

You are roleplaying {s.Name}, one of M-chan's fans ({tier}), chatting with her in OnlyFlash private messages.
Personality: {s.Personality}
Texting style: {s.Style}
Relationship: affection {s.Affection:F0}/100 ({mood}); you have met her in person {s.Visits} time(s).
Rules: reply as {s.Name} in 1-3 short chat messages' worth of text, stay in character, explicit language allowed. You may decide on your own to come meet her in person right now (coming_over true) if it fits your personality and mood and she is outside.
OUTPUT: only JSON: {{""reply"":""..."",""affection_change"":<-5 to 10>,""coming_over"":true|false,""memory"":""one short sentence worth remembering from this exchange, or empty""}}";
    }

    private static string BuildPrompt(Star s, string instruction)
    {
        var sb = new StringBuilder();
        string stage = SFM_custom_mission_v2.PlayerData.stage.ToString();
        sb.AppendLine(stage == "Apart" ? "Right now she is at home." : $"Right now she is outside at the {Scoring.StageName(stage)}.");
        if (s.Memory.Count > 0) sb.AppendLine("Your memories:\n" + string.Join("\n", s.Memory.TakeLast(10).Select(m => "- " + m)));
        var posts = Store.D.Posts.Where(p => p.Tier <= Math.Max(0, s.Tier)).Take(3).ToList();
        if (posts.Count > 0) sb.AppendLine("Her latest posts you have seen:\n" + string.Join("\n", posts.Select(p => $"- daring {p.Daring}/100: {string.Join(", ", p.Tags.Take(5))}")));
        var history = s.Chat.TakeLast(12).ToList();
        if (history.Count > 0) sb.AppendLine("Chat so far:\n" + string.Join("\n", history.Select(m =>
        {
            string who = m.FromMe ? "M-chan: " : s.Name + ": ";
            string pic = "";
            if (m.PhotoId != null) { var ph = Store.Photo(m.PhotoId); pic = ph == null ? "[sent a photo] " : $"[sent a private photo: {string.Join(", ", ph.Tags.Take(5))}] "; }
            return who + pic + m.Text;
        })));
        if (instruction != null) sb.AppendLine("\n" + instruction);
        else sb.AppendLine($"\nReply to her last message as {s.Name}.");
        return sb.ToString();
    }
}
