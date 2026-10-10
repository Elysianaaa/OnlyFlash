using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OnlyFlash;

// Followers, likes, comments, tier subscribers and payday.
public static class Sim
{
    private static readonly Random Rng = new();
    public static readonly ConcurrentQueue<(string user, string text)> Notices = new();
    public static readonly ConcurrentQueue<Action> MainThread = new();
    public static double PendingPayout;

    private static readonly double[] TierBase = { 0, 0.10, 0.035, 0.010 };
    private static readonly int[] TierDefaultPrice = { 0, 100, 300, 1000 };

    // Engagement time constant: a post reaches ~95% of its likes after EngagementMinutes
    private static double Tau => Math.Max(20, Plugin.EngagementMinutes.Value * 60 / 3.0);
    public static double Progress(Post p, double now) => 1 - Math.Exp(-Math.Max(0, now - p.PostedUnix) / Tau);
    // New followers arrive faster than the likes finish rolling in (~95% within about 3 minutes by default)
    public static double FollowerProgress(Post p, double now) => 1 - Math.Exp(-Math.Max(0, now - p.PostedUnix) / (Tau * 0.4));
    public static int Likes(Post p, double now) => (int)Math.Round(p.TargetLikes * Progress(p, now));
    public static int PaidSubs => (int)Store.D.Profile.Tiers.Sum(t => Math.Floor(t.Subs));
    public static double TierAudience(int tier) => Store.D.Profile.Tiers.Skip(tier - 1).Sum(t => t.Subs);

    public static List<Comment> Visible(Post p, double now)
    {
        lock (Store.Sync) return p.Comments.Where(c => c.ShowAt <= now).OrderBy(c => c.ShowAt).ToList();
    }

    // ---------------- reach ----------------
    // The more she has posted, the further each new post spreads (more new followers, likes and comments)
    public static double Momentum(int postsBefore) => 1 + 0.6 * Math.Log(1 + postsBefore);
    private static double Discovery(int daring, double momentum) => (6 + daring * 1.0) * momentum;   // strangers who see a public post
    private static double PublicLikes(int daring, double followers, double momentum) => (followers + Discovery(daring, momentum)) * (0.15 + 0.6 * daring / 100.0);
    // Followers snowball: every post makes the next one pull in noticeably more people (keeps climbing, no plateau)
    public static double FollowerMomentum(int postsBefore) => 1 + 0.3 * Math.Pow(Math.Max(0, postsBefore), 0.85);
    private static double PublicGain(int daring, double followers, int postsBefore) => Discovery(daring, FollowerMomentum(postsBefore)) * (0.08 + 0.45 * daring / 100.0) + followers * 0.05 * daring / 100.0;
    private static double CommentRate(double momentum) => 0.10 * (0.6 + 0.4 * momentum);
    private const int MaxComments = 20;

    public static Post CreatePost(PhotoRec photo, int tier, string caption = "")
    {
        var prof = Store.D.Profile;
        double d = photo.Daring / 100.0;
        double now = Store.Now;
        var post = new Post
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10),
            PhotoId = photo.Id, Tier = tier, PostedUnix = now, Daring = photo.Daring,
            Tags = photo.Tags.ToList(), Stage = photo.Stage, FaceShown = photo.FaceShown,
            Caption = (caption ?? "").Trim()
        };

        double momentum = Momentum(Store.D.Posts.Count);
        if (tier == 0)
        {
            double audience = prof.Followers + Discovery(photo.Daring, momentum);
            post.TargetLikes = (int)Math.Round(PublicLikes(photo.Daring, prof.Followers, momentum) * (0.8 + Rng.NextDouble() * 0.45));
            post.FollowerGain = PublicGain(photo.Daring, prof.Followers, Store.D.Posts.Count);
            if (photo.FaceShown) post.FollowerGain *= 1.3;
            post.TargetComments = audience < 8 ? (Rng.NextDouble() < 0.35 ? 1 : 0) : (int)Math.Round(post.TargetLikes * CommentRate(momentum));
        }
        else
        {
            var t = prof.Tiers[tier - 1];
            t.Posts++;
            t.Quality = t.Posts == 1 ? photo.Daring : t.Quality * 0.75 + photo.Daring * 0.25;
            double audience = TierAudience(tier);
            post.TargetLikes = (int)Math.Round(audience * (0.45 + 0.4 * d));
            post.TargetComments = audience < 1 ? 0 : Math.Max(1, (int)Math.Round(audience * (0.20 + 0.12 * tier) * (0.6 + 0.4 * momentum)));
            // Paid posts also tease the free followers a little
            post.FollowerGain = (0.5 + prof.Followers * 0.005 * d) * FollowerMomentum(Store.D.Posts.Count);
        }
        post.TargetComments = Math.Min(post.TargetComments, MaxComments);

        lock (Store.Sync)
        {
            Store.D.Posts.Insert(0, post);
            photo.Posted = true;
        }
        Store.MarkDirty();

        // Fans react once the AI judge has finished with the photo, so they describe what it really shows
        VisionJudge.AfterJudged(photo.Id, () =>
        {
            if (!Store.D.Posts.Contains(post)) return;   // wiped by a reset meanwhile
            if (post.TargetComments > 0 && post.CommentBatches == 0 && !post.BatchPending) RequestComments(post, Math.Min(6, post.TargetComments));
            MaybeInnerCircleVisitor(post);
            Chat.OnPosted(post);
            Chat.MaybeNewContact(post);
        });
        return post;
    }

    // The AI judge corrected a photo after it was already posted: follow the new score
    public static void Rejudge(Post post, int daring, bool face, List<string> tags)
    {
        if (post.Daring == daring && post.FaceShown == face) { post.Tags = tags.ToList(); return; }
        var prof = Store.D.Profile;
        double d0 = post.Daring / 100.0, d1 = daring / 100.0;
        lock (Store.Sync)
        {
            // Momentum as it was when this post went up (posts are stored newest first)
            int postsBefore = Math.Max(0, Store.D.Posts.Count - 1 - Store.D.Posts.IndexOf(post));
            double momentum = Momentum(postsBefore);
            if (post.Tier == 0)
            {
                double f = prof.Followers;
                post.TargetLikes = (int)Math.Round(post.TargetLikes * PublicLikes(daring, f, momentum) / Math.Max(0.01, PublicLikes(post.Daring, f, momentum)));
                double g = post.FollowerGain * PublicGain(daring, f, postsBefore) / Math.Max(0.0001, PublicGain(post.Daring, f, postsBefore));
                if (post.FaceShown && !face) g /= 1.3; else if (!post.FaceShown && face) g *= 1.3;
                post.FollowerGain = g;
                if (post.TargetComments > 0 && f >= 8) post.TargetComments = Math.Min(MaxComments, (int)Math.Round(post.TargetLikes * CommentRate(momentum)));
            }
            else
            {
                var t = prof.Tiers[post.Tier - 1];
                t.Quality += (daring - post.Daring) * (t.Posts <= 1 ? 1.0 : 0.25);
                post.TargetLikes = (int)Math.Round(post.TargetLikes * (0.45 + 0.4 * d1) / (0.45 + 0.4 * d0));
                post.FollowerGain = (0.5 + prof.Followers * 0.005 * d1) * FollowerMomentum(postsBefore);
            }
            post.Daring = daring;
            post.FaceShown = face;
            post.Tags = tags.ToList();
        }
    }

    // ---------------- time ----------------

    // Things that happen "later" run on OnlyFlash's clock, so skipping time makes them happen right away
    private static readonly List<(double due, Action action)> scheduled = new();

    public static void After(double seconds, Action action)
    {
        lock (scheduled) scheduled.Add((Store.Now + seconds, action));
    }

    public static void RunDue()
    {
        List<Action> due;
        double now = Store.Now;
        lock (scheduled)
        {
            if (scheduled.Count == 0) return;
            due = scheduled.Where(x => x.due <= now).Select(x => x.action).ToList();
            if (due.Count == 0) return;
            scheduled.RemoveAll(x => x.due <= now);
        }
        foreach (var a in due)
        {
            try { a(); } catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash: " + e); }
        }
    }

    public static void ClearScheduled() { lock (scheduled) scheduled.Clear(); }

    // Fast-forward OnlyFlash: likes, followers, subscribers, comments and DMs catch up as if the time had passed
    public static void SkipTime(double seconds, string why)
    {
        if (seconds <= 0) return;
        lock (Store.Sync) Store.D.Profile.TimeOffset += seconds;
        Store.MarkDirty();
        Tick(Store.Now, seconds);
        RunDue();
        int dms = Chat.CatchUp(seconds);
        int mins = (int)Math.Round(seconds / 60);
        int comments;
        lock (Store.Sync) comments = Store.D.Posts.Take(40).Sum(p => p.Comments.Count(c => c.ShowAt <= Store.Now));
        Plugin.Logger.LogInfo($"OnlyFlash: skipped {mins} min ({why}); {dms} fan DM(s) on the way, {comments} comments visible");
        Notices.Enqueue(("OnlyFlash", $"{(mins >= 60 ? $"{mins / 60.0:0.#} h" : $"{mins} min")} later... {why}"));
    }

    public static void Tick(double now, double dt)
    {
        var prof = Store.D.Profile;
        bool changed = false;
        lock (Store.Sync)
        {
            foreach (var p in Store.D.Posts.Take(40))
            {
                double realized = p.FollowerGain * FollowerProgress(p, now);
                if (realized - p.GainRealized > 0.0001)
                {
                    prof.Followers += realized - p.GainRealized;
                    p.GainRealized = realized;
                    changed = true;
                }
                // First wave missing (lost when the game closed mid-request) - once the AI judge is done with the photo
                if (p.CommentBatches == 0 && !p.BatchPending && p.TargetComments > 0 && now - p.PostedUnix > 20 && !VisionJudge.Pending(p.PhotoId))
                    RequestComments(p, Math.Min(6, p.TargetComments));
                // Later waves (up to 3) once the post has spread, until it has its share of comments
                else if (p.CommentBatches is >= 1 and < 3 && !p.BatchPending && now - p.PostedUnix > Tau * 1.2 * p.CommentBatches)
                {
                    int have = p.Comments.Count(c => !c.FromMe && c.ReplyTo == null);
                    if (p.TargetComments - have > 0) RequestComments(p, Math.Min(8, p.TargetComments - have));
                }
            }

            // Built-in fans find her page once she has posted and gathered enough followers
            if (Store.D.Posts.Count > 0)
                foreach (var s in Store.D.Stars.Where(s => !s.Joined && s.JoinFollowers > 0 && prof.Followers >= s.JoinFollowers).ToList())
                {
                    s.Joined = true;
                    changed = true;
                    Notices.Enqueue(("OnlyFlash", $"{s.Name} started following you"));
                    var star = s;
                    MainThread.Enqueue(() => Chat.SystemDm(star, "hi... I just found your page. wow.",
                        "You just discovered M-chan's page through her posts and followed her. Send her your very first DM, in character."));
                }

            // Followers convert into paying subscribers depending on price and how bold that tier's posts are
            for (int i = 1; i <= 3; i++)
            {
                var t = prof.Tiers[i - 1];
                double priceFactor = Math.Clamp(Math.Pow(TierDefaultPrice[i] / (double)Math.Max(1, t.Price), 0.8), 0.05, 5);
                double quality = 0.4 + t.Quality / 100.0 * 1.2;
                double activity = t.Posts == 0 ? 0.25 : 1;
                double target = prof.Followers * TierBase[i] * priceFactor * quality * activity;
                double k = 1 - Math.Exp(-dt / 600.0);
                double before = Math.Floor(t.Subs);
                t.Subs += (target - t.Subs) * k;
                if (Math.Floor(t.Subs) > before) { Notices.Enqueue(("OnlyFlash", $"New {t.Name} subscriber! ({Math.Floor(t.Subs)} total)")); changed = true; }
            }
        }
        if (changed) Store.MarkDirty();
    }

    public static void Payday()
    {
        var prof = Store.D.Profile;
        double pay = 0;
        int subs = 0;
        lock (Store.Sync)
        {
            foreach (var t in prof.Tiers)
            {
                int n = (int)Math.Floor(t.Subs);
                subs += n;
                pay += n * t.Price;
            }
            if (pay <= 0) return;
            prof.TotalEarned += pay;
            prof.Paydays++;
            PendingPayout += pay;
        }
        Store.MarkDirty();
        Notices.Enqueue(("OnlyFlash", $"Payday! +{pay:N0} RP from {subs} subscriber{(subs == 1 ? "" : "s")}."));
    }

    // ---------------- comments ----------------

    private static void RequestComments(Post post, int count)
    {
        if (count <= 0) return;
        post.BatchPending = true;
        post.CommentBatches++;
        double now = Store.Now;
        // When this wave of comments naturally arrives (relative to the post, so skipped time counts)
        double waveAt = post.PostedUnix + (post.CommentBatches - 1) * Tau * 1.2;
        var tiers = Store.D.Profile.Tiers;
        string tierName = post.Tier == 0 ? "Public" : tiers[post.Tier - 1].Name;
        // Regular star fans subscribed to this tier sometimes chime in
        var stars = Store.D.Stars.Where(s => s.Joined && s.Tier >= post.Tier && Rng.NextDouble() < 0.45).Take(1).ToList();
        var existing = post.Comments.Select(c => $"{c.User}: {c.Text}").ToList();

        Task.Run(async () =>
        {
            List<(string user, string text, string memory)> made = new();
            if (Ai.Available)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"POST by M-chan ({tierName}). Daring {post.Daring}/100.");
                    sb.AppendLine("In the photo: " + string.Join(", ", post.Tags));
                    if (!string.IsNullOrWhiteSpace(post.Caption)) sb.AppendLine($"Her caption: \"{post.Caption}\" (commenters may react to it)");
                    if (existing.Count > 0) sb.AppendLine("Comments already on the post:\n" + string.Join("\n", existing.Select(e => "- " + e)));
                    sb.AppendLine($"\nWrite {count} NEW comments from different anonymous users.");
                    foreach (var s in stars)
                    {
                        sb.AppendLine($"Also add one comment from the regular '{s.Name}' (user field exactly \"{s.Name}\"). Personality: {s.Personality} Style: {s.Style}");
                        if (s.Memory.Count > 0) sb.AppendLine("  Their memories: " + string.Join(" | ", s.Memory.TakeLast(5)));
                    }
                    string system = Ai.WorldRules + "\n\nAUDIENCE: " + Ai.TierAudience(post.Tier, tierName) +
                        "\nReact to what is actually in the photo and how risky it was. Vary length (from 2 words to 2 sentences, max 200 characters), tone, typos and emoji use. Usernames look like real anonymous handles." +
                        "\nOUTPUT: only a JSON array: [{\"user\":\"...\",\"text\":\"...\",\"memory\":\"for named regulars only: one sentence they will remember\"}]";
                    string raw = await Ai.Chat(system, sb.ToString(), 900);
                    if (Ai.ExtractJson(raw, '[', ']') is JsonArray arr)
                        foreach (var n in arr)
                            if (n is JsonObject o && !string.IsNullOrWhiteSpace(o["text"]?.ToString()))
                                made.Add((Ai.Trunc(o["user"]?.ToString(), 24), Ai.Trunc(o["text"]?.ToString(), 260), o["memory"]?.ToString()));
                }
                catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash comments failed, using canned ones: " + e.Message); }
            }
            if (made.Count == 0)
                for (int i = 0; i < count; i++) made.Add((RandomHandle(), Canned(post.Tier), null));

            MainThread.Enqueue(() =>
            {
                double start = Store.Now;
                lock (Store.Sync)
                {
                    foreach (var (user, text, memory) in made)
                    {
                        var star = Store.D.Stars.FirstOrDefault(s => string.Equals(s.Name, user, StringComparison.OrdinalIgnoreCase));
                        post.Comments.Add(new Comment
                        {
                            User = string.IsNullOrWhiteSpace(user) ? RandomHandle() : user,
                            Text = text,
                            Star = star != null,
                            ShowAt = Math.Max(start + 3 + Rng.NextDouble() * 12, waveAt + 8 + Rng.NextDouble() * Tau * 2.2)
                        });
                        if (star != null)
                        {
                            star.Memory.Add($"Commented on her {tierName} post ({string.Join(", ", post.Tags.Take(3))}): " + (string.IsNullOrWhiteSpace(memory) ? Ai.Trunc(text, 80) : Ai.Trunc(memory, 150)));
                            while (star.Memory.Count > 12) star.Memory.RemoveAt(0);
                        }
                    }
                    post.BatchPending = false;
                }
                Store.MarkDirty();
                Notices.Enqueue(("OnlyFlash", $"{made.Count} new comment{(made.Count == 1 ? "" : "s")} coming in on your {tierName} post."));
            });
        });
    }

    // She comments on her own post (optionally replying to someone); the commenter answers back
    public static void ReplyToComment(Post post, string target, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string me = Store.D.Profile.Handle;
        double now = Store.Now;
        lock (Store.Sync) post.Comments.Add(new Comment { User = me, Text = text.Trim(), FromMe = true, ReplyTo = target, ShowAt = now });
        Store.MarkDirty();

        string tierName = Ui.TierLabel(post.Tier);
        var star = target == null ? null : Store.D.Stars.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
        List<string> thread;
        lock (Store.Sync)
            thread = post.Comments.Where(c => target == null || c.User == target || c.ReplyTo == target || (c.FromMe && c.ReplyTo == target))
                                  .Select(c => (c.FromMe ? "M-chan (the creator)" : c.User) + (c.ReplyTo != null && c.FromMe ? $" replying to {c.ReplyTo}" : "") + ": " + c.Text)
                                  .TakeLast(10).ToList();

        Task.Run(async () =>
        {
            List<(string user, string text)> made = new();
            if (Ai.Available)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"POST by M-chan ({tierName}). In the photo: {string.Join(", ", post.Tags.Take(8))}.");
                    if (!string.IsNullOrWhiteSpace(post.Caption)) sb.AppendLine($"Her caption: \"{post.Caption}\"");
                    sb.AppendLine("Comment thread:\n" + string.Join("\n", thread.Select(t => "- " + t)));
                    if (target != null)
                    {
                        sb.AppendLine($"\nM-chan just replied to {target}. Write {target}'s reply to her (user field exactly \"{target}\"). Optionally add ONE other user reacting to her reply.");
                        if (star != null)
                        {
                            sb.AppendLine($"{target} is a regular. Personality: {star.Personality} Style: {star.Style}");
                            if (star.Memory.Count > 0) sb.AppendLine("Their memories: " + string.Join(" | ", star.Memory.TakeLast(5)));
                        }
                        else sb.AppendLine($"Keep {target}'s voice consistent with their earlier comment.");
                    }
                    else sb.AppendLine("\nM-chan just commented on her own post. Write 1-3 replies from fans reacting to her comment.");
                    string system = Ai.WorldRules + "\n\nAUDIENCE: " + Ai.TierAudience(post.Tier, tierName) +
                        "\nFans are thrilled when the creator replies to them. Short chat-style comments, max 200 characters." +
                        "\nOUTPUT: only a JSON array: [{\"user\":\"...\",\"text\":\"...\"}]";
                    string raw = await Ai.Chat(system, sb.ToString(), 500);
                    if (Ai.ExtractJson(raw, '[', ']') is JsonArray arr)
                        foreach (var n in arr)
                            if (n is JsonObject o && !string.IsNullOrWhiteSpace(o["text"]?.ToString()))
                                made.Add((Ai.Trunc(o["user"]?.ToString(), 24), Ai.Trunc(o["text"]?.ToString(), 260)));
                }
                catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash reply failed: " + e.Message); }
            }
            if (made.Count == 0)
                made.Add((target ?? RandomHandle(), target != null ? "omg she actually replied to me" : "she's in the comments!!"));

            MainThread.Enqueue(() =>
            {
                double start = Store.Now;
                lock (Store.Sync)
                {
                    int i = 0;
                    foreach (var (user, txt) in made)
                    {
                        string u = string.IsNullOrWhiteSpace(user) ? RandomHandle() : user;
                        bool isStar = Store.D.Stars.Any(s => string.Equals(s.Name, u, StringComparison.OrdinalIgnoreCase));
                        post.Comments.Add(new Comment { User = u, Text = txt, Star = isStar, ReplyTo = me, ShowAt = start + 5 + i * 6 + Rng.NextDouble() * 10 });
                        i++;
                    }
                    if (star != null)
                    {
                        star.Memory.Add($"She replied to my comment on her {tierName} post: \"{Ai.Trunc(text, 100)}\"");
                        while (star.Memory.Count > 12) star.Memory.RemoveAt(0);
                    }
                }
                Store.MarkDirty();
                Notices.Enqueue(("OnlyFlash", target != null ? $"{target} replied to you" : "Fans are replying to your comment"));
            });
        });
    }

    private static void MaybeInnerCircleVisitor(Post post)
    {
        if (post.Tier != 3 || post.Daring < 55 || Store.D.Profile.Tiers[2].Subs < 1 || Rng.NextDouble() > 0.35) return;
        string handle = RandomHandle();
        After(60 + Rng.NextDouble() * 90, () =>
        {
            lock (Store.Sync)
                post.Comments.Add(new Comment { User = handle, Text = "I know exactly where that is. I'm close... coming to find you.", ShowAt = Store.Now });
            Store.MarkDirty();
            Visits.QueueAnonymous(handle);
            Notices.Enqueue((handle, "I'm close... coming to find you."));
        });
    }

    private static readonly string[] Handles = { "night_owl", "lurker", "silentfan", "ramen_king", "deepblue", "anon", "trainspotter", "salaryman", "kuro", "shadow", "yami", "tokyo_wolf", "sleepless", "voyeur", "m_fanboy", "late_shift" };
    public static string RandomHandle() => Handles[Rng.Next(Handles.Length)] + "_" + Rng.Next(10, 999);

    private static readonly string[][] CannedByTier =
    {
        new[] { "omg", "🔥🔥🔥", "how are you not caught lol", "more please", "first time seeing you, instant follow", "is this real??", "need the paid version" },
        new[] { "worth every RP", "you look amazing out there", "next time a little more? 😏", "my favourite creator", "can't believe you did that in public" },
        new[] { "do it in daylight next time", "I want to see you closer to people", "so fucking hot", "next post: completely nude, no coat", "you're getting braver ♡" },
        new[] { "you belong to us now", "do it right in front of someone next time", "I'm paying for more than this", "tell me where you'll be next", "I want to see you in person" },
    };
    private static string Canned(int tier) => CannedByTier[Math.Clamp(tier, 0, 3)][Rng.Next(CannedByTier[Math.Clamp(tier, 0, 3)].Length)];
}
