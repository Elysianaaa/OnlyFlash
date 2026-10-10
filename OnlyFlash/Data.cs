using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OnlyFlash;

public class PhotoRec
{
    public string Id { get; set; }
    public string File { get; set; }
    public double TakenUnix { get; set; }
    public string Stage { get; set; }
    public bool Day { get; set; }
    public int Daring { get; set; }
    public bool FaceShown { get; set; }
    public float Aspect { get; set; } = 1f;   // width / height
    public List<string> Why { get; set; } = new();   // how the daring score was reached
    public bool AiChecked { get; set; }              // re-scored by the vision AI judge
    public List<string> Tags { get; set; } = new();
    public bool Posted { get; set; }
    public bool Deleted { get; set; }
}

public class Comment
{
    public string User { get; set; }
    public string Text { get; set; }
    public double ShowAt { get; set; }
    public bool Star { get; set; }
    public bool FromMe { get; set; }
    public string ReplyTo { get; set; }
}

public class Post
{
    public string Id { get; set; }
    public string PhotoId { get; set; }
    public int Tier { get; set; }              // 0 = public, 1..3 = paid tiers
    public string Caption { get; set; } = "";
    public double PostedUnix { get; set; }
    public int Daring { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Stage { get; set; }
    public bool FaceShown { get; set; }
    public int TargetLikes { get; set; }
    public int TargetComments { get; set; }
    public double FollowerGain { get; set; }
    public double GainRealized { get; set; }
    public int CommentBatches { get; set; }
    public bool BatchPending { get; set; }
    public List<Comment> Comments { get; set; } = new();
}

public class Tier
{
    public string Name { get; set; }
    public int Price { get; set; }
    public double Subs { get; set; }
    public double Quality { get; set; } = 30;   // running average daring of posts in this tier
    public int Posts { get; set; }
}

public class ChatMsg
{
    public bool FromMe { get; set; }
    public string Text { get; set; }
    public double Unix { get; set; }
    public string PhotoId { get; set; }       // a Camera Roll photo she sent in the DM
}

public class Star
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string AvatarType { get; set; } = "NewMale";
    public int Body { get; set; }
    public int Hair { get; set; }
    public int Face { get; set; }
    public float Size { get; set; } = 1f;
    public string Personality { get; set; }
    public string Style { get; set; }
    public int Tier { get; set; } = 3;
    public float Affection { get; set; } = 40;
    public List<string> Memory { get; set; } = new();
    public List<ChatMsg> Chat { get; set; } = new();
    public double LastVisitUnix { get; set; }
    public int Visits { get; set; }
    public bool Unread { get; set; }
    public double LastDmUnix { get; set; }
    public string LastReactedPost { get; set; }
    public bool Auto { get; set; }           // created by a fan sliding into her DMs
    public bool Joined { get; set; }         // has found her page (only then DMs / comments / visits)
    public int JoinFollowers { get; set; }   // follower count at which a built-in fan discovers her
}

public class Profile
{
    public string Handle { get; set; } = "M-chan";
    public double Followers { get; set; }
    public double TotalEarned { get; set; }
    public int Paydays { get; set; }
    public double TimeOffset { get; set; }      // seconds OnlyFlash's clock is ahead of real time (time skips)
    public List<Tier> Tiers { get; set; } = new()
    {
        new Tier { Name = "Fan Club", Price = 100 },
        new Tier { Name = "VIP", Price = 300 },
        new Tier { Name = "Inner Circle", Price = 1000 },
    };
}

public class VaultData
{
    public Profile Profile { get; set; } = new();
    public List<PhotoRec> Photos { get; set; } = new();
    public List<Post> Posts { get; set; } = new();
    public List<Star> Stars { get; set; } = new();
}

// Everything lives in BepInEx\config\OnlyFlash\vault.json
public static class Store
{
    public static VaultData D = new();
    public static readonly object Sync = new();
    private static string path;
    private static bool dirty;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Load(string dir)
    {
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "vault.json");
        try
        {
            if (File.Exists(path))
                D = JsonSerializer.Deserialize<VaultData>(File.ReadAllText(path)) ?? new VaultData();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("vault.json unreadable, starting fresh (old file kept as vault.bad.json): " + e.Message);
            try { File.Copy(path, Path.Combine(dir, "vault.bad.json"), true); } catch { }
            D = new VaultData();
        }
        ApplyStarRules();
        // A comment wave still being written when the game closed never arrived: let it be requested again
        foreach (var p in D.Posts.Where(p => p.BatchPending))
        {
            p.BatchPending = false;
            p.CommentBatches = Math.Max(0, p.CommentBatches - 1);
        }
        Save();
    }

    // Built-in fans only discover her once she has enough followers; anyone already chatted with stays
    private static readonly Dictionary<string, int> JoinAt = new() { ["sora"] = 15, ["kenji"] = 60, ["tanaka"] = 150 };

    private static void ApplyStarRules()
    {
        while (D.Profile.Tiers.Count < 3) D.Profile.Tiers.Add(new Tier { Name = "Tier " + (D.Profile.Tiers.Count + 1), Price = 100 });
        if (D.Stars.Count == 0) D.Stars = DefaultStars();
        foreach (var s in D.Stars)
        {
            if (JoinAt.TryGetValue(s.Id, out int at)) s.JoinFollowers = at;
            // Unprompted messages a built-in fan sent before she had any following: drop them (keep real conversations)
            if (!s.Joined && s.JoinFollowers > 0 && !s.Chat.Any(m => m.FromMe) && D.Profile.Followers < s.JoinFollowers)
            {
                s.Chat.Clear();
                s.Unread = false;
            }
            if (!s.Joined && (s.Chat.Count > 0 || s.Auto || s.JoinFollowers == 0)) s.Joined = true;
        }
    }

    // Erase everything OnlyFlash knows: photos, posts, followers, money history, every fan's chats and memory.
    // (NovelAI keeps nothing between requests - all memory lives in this file, so this is a full wipe.)
    public static int ResetAll()
    {
        int deleted = 0;
        lock (Sync)
        {
            foreach (var p in D.Photos)
            {
                foreach (var f in new[] { p.File, Path.ChangeExtension(p.File ?? "", ".json") })
                {
                    try { if (!string.IsNullOrEmpty(f) && File.Exists(f)) { File.Delete(f); deleted++; } } catch { }
                }
            }
            D = new VaultData();
            ApplyStarRules();
        }
        // The old FansChat AI memory (from the removed FansChat mod) too
        try
        {
            string old = Path.Combine(BepInEx.Paths.ConfigPath, "FansChatAI", "memory.json");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
        Visits.Reset();
        Sim.ClearScheduled();
        while (Sim.Notices.TryDequeue(out _)) { }
        Sim.PendingPayout = 0;
        Save();
        Plugin.Logger.LogInfo($"OnlyFlash reset: all data erased ({deleted} photo files removed)");
        return deleted;
    }

    public static void MarkDirty() => dirty = true;

    public static void SaveIfDirty()
    {
        if (dirty) Save();
    }

    public static void Save()
    {
        try
        {
            string json;
            lock (Sync) json = JsonSerializer.Serialize(D, Opts);
            File.WriteAllText(path + ".tmp", json);
            File.Copy(path + ".tmp", path, true);
            File.Delete(path + ".tmp");
            dirty = false;
        }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not save vault.json: " + e.Message); }
    }

    public static PhotoRec Photo(string id) => D.Photos.FirstOrDefault(p => p.Id == id);
    public static Star StarById(string id) => D.Stars.FirstOrDefault(s => s.Id == id);
    // OnlyFlash's clock: real time plus any skipped time
    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + (D?.Profile?.TimeOffset ?? 0);

    public static List<Star> DefaultStars() => new()
    {
        new Star
        {
            Id = "kenji", Name = "Kenji", AvatarType = "NewMale", Body = 1, Hair = 2, Face = 1, Tier = 3, Affection = 40,
            Personality = "28-year-old personal trainer. Confident, cocky and dominant but warm. Loves her boldest public photos and wants to be the one she shows off for. Competitive with other men who chase her.",
            Style = "Short, cocky texts. Calls her 'babe'. Sometimes just 'damn.' or 'where are you rn'."
        },
        new Star
        {
            Id = "tanaka", Name = "Mr. Tanaka", AvatarType = "NewOji", Body = 0, Hair = 0, Face = 0, Tier = 3, Affection = 35,
            Personality = "Wealthy, polite businessman in his fifties and her most generous patron. Formal and courteous, but his requests are shamelessly perverted. Treats meeting her like a rare privilege he has paid for.",
            Style = "Formal, polite English with proper punctuation. Calls her 'M-chan-san'. Mentions tips and gifts."
        },
        new Star
        {
            Id = "sora", Name = "Sora", AvatarType = "NewMale", Body = 3, Hair = 4, Face = 2, Tier = 1, Affection = 50,
            Personality = "Shy 21-year-old university student and her very first fan. Sweet, nervous and easily flustered. Secretly dreams of meeting her but is scared to ask; grows braver as she encourages him.",
            Style = "Lots of '...', apologises a lot, uses '>///<' and 'um'."
        },
    };
}
