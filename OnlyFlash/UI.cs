using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SFM_custom_mission_v2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OnlyFlash;

// ---------------------------------------------------------------- helpers

public static class Ui
{
    public static readonly Color Bg = new(0.96f, 0.94f, 0.99f);
    public static readonly Color Card = new(1f, 1f, 1f);
    public static readonly Color Accent = new(0.55f, 0.25f, 0.85f);
    public static readonly Color AccentSoft = new(0.86f, 0.80f, 0.97f);
    public static readonly Color Ink = new(0.12f, 0.10f, 0.16f);
    public static readonly Color Muted = new(0.45f, 0.42f, 0.52f);
    public static readonly Color Pink = new(0.93f, 0.36f, 0.62f);

    public static UnityAction Act(Action a) => a;

    // The phone font has no emoji: drop surrogate pairs and other symbols it can't draw
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsSurrogate(c)) continue;
            if (c >= '☀' && c <= '➿') continue;
            if (c == '️' || c == '‍') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public static string Esc(string s) => Clean(s).Replace("<", "‹").Replace(">", "›");

    public static string Ago(double unix)
    {
        double s = Store.Now - unix;
        if (s < 60) return "just now";
        if (s < 3600) return $"{(int)(s / 60)}m ago";
        if (s < 86400) return $"{(int)(s / 3600)}h ago";
        return $"{(int)(s / 86400)}d ago";
    }

    public static string TierLabel(int tier) => tier == 0 ? "Public" : Store.D.Profile.Tiers[tier - 1].Name;

    // Scrollable vertical list filling the window below the menu bar
    public static GameObject Scroll(Window w, float bottomMargin = 0f)
    {
        var list = PluginGUI.CreateRect("ScrollRect", w.gameObject, new Vector2(0f, -PluginGUI.menubarheight - 9f - bottomMargin), Bg);
        PluginGUI.SetPosition(list, new Vector2(0f, -PluginGUI.menubarheight), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        var sr = list.AddComponent<ScrollRect>();
        sr.horizontal = false;
        sr.vertical = true;
        sr.scrollSensitivity = 0f;                           // the wheel is handled by Wheel() at a fixed step
        sr.movementType = ScrollRect.MovementType.Clamped;   // stop exactly at the first/last item
        list.AddComponent<Mask>();
        var content = PluginGUI.CreateRect("Content", list, Vector2.zero, new Color(1, 1, 1, 0));
        PluginGUI.SetPosition(content, null, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlHeight = false;
        vlg.spacing = PluginGUI.scale * 1.5f;
        var pad = new RectOffset();
        pad.left = pad.right = pad.top = (int)(PluginGUI.scale * 2);
        pad.bottom = (int)(PluginGUI.scale * 4);
        vlg.padding = pad;
        var csf = content.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        sr.content = content.GetComponent<RectTransform>();
        return content;
    }

    // ---- App icon: a white lightning bolt (the "flash") on the brand blue ----
    public static readonly Color Brand = new(0f, 0.69f, 0.94f);
    private static Sprite bolt;

    public static Sprite BoltSprite()
    {
        if (bolt != null) return bolt;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        // Classic "zap" bolt on a 24-unit grid (y pointing down)
        var pts = new[] { new Vector2(13, 2), new Vector2(3, 14), new Vector2(12, 14), new Vector2(11, 22), new Vector2(21, 10), new Vector2(12, 10) };
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(pts[i].x / 24f, 1f - pts[i].y / 24f);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int hits = 0;   // 3x3 samples per pixel = smooth edges
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                        if (Inside(pts, new Vector2((x + (sx + 0.5f) / 3f) / n, (y + (sy + 0.5f) / 3f) / n))) hits++;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, hits / 9f));
            }
        tex.Apply();
        bolt = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        bolt.hideFlags = HideFlags.HideAndDontSave;
        return bolt;
    }

    private static bool Inside(Vector2[] poly, Vector2 p)
    {
        bool c = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                c = !c;
        return c;
    }

    // Mouse wheel / touchpad: one notch moves a fixed share of the visible list, however long the list is
    public static void Wheel(GameObject content)
    {
        if (content == null || !content.activeInHierarchy) return;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return;
        float notches = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(notches) < 0.001f) return;
        if (Mathf.Abs(notches) > 20f) notches /= 120f;   // raw Windows wheel units
        var sr = content.transform.parent == null ? null : content.transform.parent.GetComponent<ScrollRect>();
        if (sr == null) return;
        var view = sr.GetComponent<RectTransform>();
        var canvas = view.GetComponentInParent<Canvas>();
        var root = canvas == null ? null : canvas.rootCanvas;
        Camera cam = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        if (!RectTransformUtility.RectangleContainsScreenPoint(view, mouse.position.ReadValue(), cam)) return;
        var crt = content.GetComponent<RectTransform>();
        float max = crt.rect.height - view.rect.height;
        if (max <= 0f) return;
        var pos = crt.anchoredPosition;
        pos.y = Mathf.Clamp(pos.y - notches * view.rect.height * Mathf.Clamp(Plugin.ScrollStep.Value, 0.01f, 1f), 0f, max);
        crt.anchoredPosition = pos;
        sr.velocity = Vector2.zero;
    }

    // CM2 parents new UI with "keep world scale", so anything built while the phone is zoomed shrinks.
    // Reset every piece of a OnlyFlash window to scale 1 so the zoom applies evenly.
    public static void FixScale(GameObject root)
    {
        if (root == null) return;
        var all = root.GetComponentsInChildren<RectTransform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i].localScale != Vector3.one) all[i].localScale = Vector3.one;
    }

    public static string SizedRowName(float aspect) => "SizedRow|" + aspect.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static void Rebuild(GameObject content)
    {
        if (content == null) return;
        var w = content.transform;
        while (w != null && !w.name.EndsWith("App")) w = w.parent;
        if (w != null) FixScale(w.gameObject);
        var crt = content.GetComponent<RectTransform>();
        LayoutRebuilder.ForceRebuildLayoutImmediate(crt);
        // Rows with a fixed width:height ratio (photos, thumbnail grids) get their height from the real width
        bool resized = false;
        for (int i = 0; i < content.transform.childCount; i++)
        {
            var ch = content.transform.GetChild(i).GetComponent<RectTransform>();
            if (ch == null || !ch.name.StartsWith("SizedRow|")) continue;
            if (!float.TryParse(ch.name.Substring(9), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a) || a <= 0.1f) continue;
            float h = ch.rect.width / a;
            if (h > 1f && Mathf.Abs(ch.rect.height - h) > 0.5f) { ch.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h); resized = true; }
        }
        if (resized) LayoutRebuilder.ForceRebuildLayoutImmediate(crt);
        var parent = content.transform.parent;
        if (parent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(parent.GetComponent<RectTransform>());
    }

    public static void Clear(GameObject content)
    {
        for (int i = content.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(content.transform.GetChild(i).gameObject);
    }

    public static GameObject Row(GameObject parent, float height, Color color)
    {
        var row = PluginGUI.CreateRect("Row", parent, new Vector2(0f, height), color, Textures.Get("roundedCorners"));
        PluginGUI.SetBorder(row, 3f);
        PluginGUI.SetPosition(row, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        return row;
    }

    public static TextMeshProUGUI Text(GameObject parent, string text, float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var go = PluginGUI.CreateText(text, parent, new Vector2(-4f, -2f), color, size, align);
        PluginGUI.SetPosition(go, new Vector2(2f, -1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        var t = go.GetComponent<TextMeshProUGUI>();
        t.enableWordWrapping = true;
        t.richText = true;
        t.overflowMode = TextOverflowModes.Ellipsis;
        return t;
    }

    // A row whose height grows with its (wrapped) text
    public static TextMeshProUGUI WrapRow(GameObject parent, string text, float size, Color textColor, Color bg, TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        var row = PluginGUI.CreateRect("WrapRow", parent, new Vector2(0f, 8f), bg, Textures.Get("roundedCorners"));
        PluginGUI.SetBorder(row, 3f);
        PluginGUI.SetPosition(row, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        var go = PluginGUI.CreateText(text, row, Vector2.zero, textColor, size, align);
        PluginGUI.SetPosition(go, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        var t = go.GetComponent<TextMeshProUGUI>();
        t.margin = new Vector4(PluginGUI.scale * 2.5f, PluginGUI.scale * 1.5f, PluginGUI.scale * 2.5f, PluginGUI.scale * 1.5f);
        t.enableWordWrapping = true;
        t.richText = true;
        var vlg = row.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlHeight = true;
        var csf = row.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return t;
    }

    public static GameObject Button(GameObject parent, string text, float height, Color color, Color textColor, Action onClick, float fontSize = 6.5f)
    {
        var b = PluginGUI.CreateButtonBase(parent, "Button", new Vector2(0f, height), 0.25f, color, Act(onClick));
        PluginGUI.SetPosition(b, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        var t = PluginGUI.CreateText(text, b, Vector2.zero, textColor, fontSize, TextAlignmentOptions.Center);
        PluginGUI.SetPosition(t, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        var tm = t.GetComponent<TextMeshProUGUI>();
        tm.enableAutoSizing = true;
        tm.fontSizeMax = PluginGUI.scale * fontSize;
        tm.fontSizeMin = PluginGUI.scale * 3f;
        tm.richText = true;
        return b;
    }

    // Button placed inside a row at a horizontal fraction [x0, x1]
    public static GameObject ButtonAt(GameObject row, string text, float x0, float x1, Color color, Color textColor, Action onClick, float fontSize = 6f)
    {
        var b = PluginGUI.CreateButtonBase(row, "Button", new Vector2(-2f, -2f), 0.25f, color, Act(onClick));
        PluginGUI.SetPosition(b, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(x0, 0f), new Vector2(x1, 1f));
        var t = PluginGUI.CreateText(text, b, Vector2.zero, textColor, fontSize, TextAlignmentOptions.Center);
        PluginGUI.SetPosition(t, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        var tm = t.GetComponent<TextMeshProUGUI>();
        tm.enableAutoSizing = true;
        tm.fontSizeMax = PluginGUI.scale * fontSize;
        tm.fontSizeMin = PluginGUI.scale * 3f;
        return b;
    }

    public static TMP_InputField Input(GameObject parent, string initial, float height, Action<string> onChange, float fontSize = 6f)
    {
        var bg = PluginGUI.CreateRect("TextBackground", parent, new Vector2(0f, height), new Color(0.88f, 0.86f, 0.92f), Textures.Get("roundedCorners"));
        PluginGUI.SetBorder(bg, 2f);
        PluginGUI.SetPosition(bg, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        return InputIn(bg, initial, onChange, fontSize);
    }

    public static TMP_InputField InputIn(GameObject bg, string initial, Action<string> onChange, float fontSize = 6f)
    {
        // Standard TMP layout: box (input field) > text area (viewport, clips) > text.
        // The field's caret/scroll maths assume the viewport is a child of the box; without it the text drifts off the caret.
        float boxHeight = bg.GetComponent<RectTransform>().sizeDelta.y / PluginGUI.scale;
        var area = PluginGUI.CreateRect("TextArea", bg, new Vector2(-3f, -2f), new Color(1, 1, 1, 0));
        PluginGUI.SetPosition(area, new Vector2(1.5f, -1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        area.AddComponent<RectMask2D>();   // long text stays inside the grey box and scrolls with the caret
        var field = bg.AddComponent<TMP_InputField>();
        var txt = PluginGUI.CreateText("", area, Vector2.zero, Ink, fontSize, boxHeight >= 16f ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
        PluginGUI.SetPosition(txt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        var tm = txt.GetComponent<TextMeshProUGUI>();
        tm.enableWordWrapping = true;
        field.textViewport = area.GetComponent<RectTransform>();
        field.textComponent = tm;
        field.lineType = TMP_InputField.LineType.MultiLineSubmit;
        field.text = initial ?? "";
        if (onChange != null)
        {
            UnityAction<string> cb = onChange;
            field.onValueChanged.AddListener(cb);
        }
        // If the box was built on a visible screen, OnEnable already ran without a text component: run it again
        if (field.isActiveAndEnabled) { field.enabled = false; field.enabled = true; }
        return field;
    }

    // Square photo that fills the list width, whatever the phone size is
    // Text box + a row for buttons, pinned to the bottom of a window
    public static (TMP_InputField input, GameObject buttons) InputPanel(Window w, float height)
    {
        var panel = PluginGUI.CreateRect("InputPanel", w.gameObject, new Vector2(-4f, height), Bg);
        PluginGUI.SetPosition(panel, new Vector2(0f, 9f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(1f, 0f));
        var fieldBg = PluginGUI.CreateRect("Field", panel, new Vector2(-2f, 14f), new Color(0.88f, 0.86f, 0.92f), Textures.Get("roundedCorners"));
        PluginGUI.SetBorder(fieldBg, 2f);
        PluginGUI.SetPosition(fieldBg, new Vector2(0f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        var input = InputIn(fieldBg, "", null, 5.5f);
        var buttons = PluginGUI.CreateRect("Buttons", panel, new Vector2(0f, 13f), new Color(1, 1, 1, 0));
        PluginGUI.SetPosition(buttons, new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(1f, 0f));
        return (input, buttons);
    }

    // Makes a row (e.g. from WrapRow) clickable
    public static void Clickable(TextMeshProUGUI rowText, Action onClick)
    {
        var row = rowText.transform.parent.gameObject;
        var b = row.AddComponent<UnityEngine.UI.Button>();
        b.onClick.AddListener(Act(onClick));
    }

    public static RawImage Image(GameObject parent, Texture2D tex, float aspect = 1f)
    {
        // Height is set from the real width in Rebuild() (an AspectRatioFitter updated too late and left gaps)
        var row = PluginGUI.CreateRect(SizedRowName(aspect > 0.1f ? aspect : 1f), parent, new Vector2(0f, 60f), new Color(1, 1, 1, 0));
        PluginGUI.SetPosition(row, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        var img = PluginGUI.CreateRect("Image", row, Vector2.zero, Color.white);
        PluginGUI.SetPosition(img, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(1f, 1f));
        var oldImage = img.GetComponent<UnityEngine.UI.Image>();
        if (oldImage != null) UnityEngine.Object.DestroyImmediate(oldImage);
        var raw = img.AddComponent<RawImage>();
        raw.texture = tex;
        if (tex == null) raw.color = new Color(0.8f, 0.78f, 0.85f);
        return raw;
    }
    // Square thumbnail of a non-square photo: crop the centre instead of squashing it
    public static void CropToSquare(RawImage raw, float aspect)
    {
        if (aspect > 1.01f) raw.uvRect = new Rect(0.5f - 0.5f / aspect, 0f, 1f / aspect, 1f);
        else if (aspect < 0.99f) raw.uvRect = new Rect(0f, 0.5f - 0.5f * aspect, 1f, aspect);
    }

    // ---- photo texture cache    // (most recently used first)
    private static readonly Dictionary<string, Texture2D> Cache = new();
    private static readonly LinkedList<string> Lru = new();

    public static Texture2D Load(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        if (Cache.TryGetValue(file, out var t) && t != null)
        {
            Lru.Remove(file);
            Lru.AddFirst(file);
            return t;
        }
        if (!File.Exists(file)) return null;
        try
        {
            var tex = new Texture2D(2, 2) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            tex.LoadImage(File.ReadAllBytes(file));
            Cache[file] = tex;
            Lru.AddFirst(file);
            while (Lru.Count > 48)
            {
                string old = Lru.Last.Value;
                Lru.RemoveLast();
                if (Cache.TryGetValue(old, out var ot) && ot != null) UnityEngine.Object.Destroy(ot);
                Cache.Remove(old);
            }
            return tex;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("OnlyFlash could not load photo: " + e.Message);
            return null;
        }
    }
}

// ---------------------------------------------------------------- app

public class OnlyFlashApp : App
{
    public override Window InitializeWindow() => new HomeWindow();

    public override GameObject CreateButton(GameObject parent, float size) =>
        PluginGUI.CreateButton(parent, "OnlyFlashMainMenuButton", size, 0.2f, Ui.Brand, Color.white, Ui.BoltSprite(), Ui.Act(Open), size * 0.3f);
}

public class VaultWindow : Window
{
    protected GameObject content;
    private int rebuild = 3;

    public VaultWindow(string title) : base(title, Ui.Bg) { }

    protected void RequestRebuild() => rebuild = 3;

    public override void OnShow()
    {
        gameObject.active = true;
        Ui.FixScale(gameObject);
        Shown();
        RequestRebuild();
        Ui.Rebuild(content);
    }

    public override void OnUpdate()
    {
        if (rebuild > 0) { rebuild--; Ui.FixScale(gameObject); Ui.Rebuild(content); }
        Ui.Wheel(content);
        Tick();
    }

    protected virtual void Shown() { }
    protected virtual void Tick() { }
}

public class HomeWindow : VaultWindow
{
    private readonly TextMeshProUGUI stats, aiStatus;
    private TextMeshProUGUI msgButtonText;
    private float timer;
    private static bool testing;

    public HomeWindow() : base("OnlyFlash")
    {
        content = Ui.Scroll(this);
        var c = content;
        stats = Ui.WrapRow(c, "", 6.5f, Ui.Ink, Ui.Card);
        Ui.Button(c, "Camera Roll", 13f, Ui.Accent, Color.white, () => PluginGUI.ShowWindow(new CameraRollWindow()));
        Ui.Button(c, "My Posts", 13f, Ui.Accent, Color.white, () => PluginGUI.ShowWindow(new PostsWindow()));
        Ui.Button(c, "Tiers & Prices", 13f, Ui.Accent, Color.white, () => PluginGUI.ShowWindow(new TiersWindow()));
        var mb = Ui.Button(c, "Messages", 13f, Ui.Pink, Color.white, () => PluginGUI.ShowWindow(new MessagesWindow()));
        msgButtonText = mb.GetComponentInChildren<TextMeshProUGUI>();
        Ui.WrapRow(c, "<b>Skip ahead</b> <size=85%>- likes, comments and DMs catch up</size>", 5.5f, Ui.Ink, Ui.Bg);
        var skipRow = Ui.Row(c, 11f, new Color(1, 1, 1, 0));
        var skipColor = new Color(0.55f, 0.5f, 0.85f);
        (string label, float minutes)[] skips = { ("30 min", 30f), ("1 hour", 60f), ("5 hours", 300f) };
        for (int i = 0; i < skips.Length; i++)
        {
            var (label, minutes) = skips[i];
            Ui.ButtonAt(skipRow, label, i / 3f, (i + 1) / 3f, skipColor, Color.white, () =>
            {
                if (Visits.InProgress) { OnlyFlashBehaviour.ToastFromOutside("Can't skip time", "Someone is on the way to you right now."); return; }
                Sim.SkipTime(minutes * 60, "skipped ahead");
                Refresh();
            }, 6f);
        }
        aiStatus = Ui.WrapRow(c, "", 5.5f, Ui.Ink, Ui.Card);
        Ui.Button(c, "Test AI fans", 10f, new Color(0.85f, 0.83f, 0.88f), Ui.Ink, TestAi, 5.5f);
        Ui.Button(c, "Reset OnlyFlash...", 10f, new Color(0.85f, 0.83f, 0.88f), Ui.Ink, () => PluginGUI.ShowWindow(new ResetWindow()), 5.5f);
        Ui.WrapRow(c, $"<size=80%>Press <b>{Plugin.CameraKey.Value}</b> outside to open the camera. Photos land in your Camera Roll; post them publicly or to a paid tier. Subscribers pay you when you get home safely.</size>", 5.5f, Ui.Muted, Ui.Bg);
        Refresh();
        Ui.Rebuild(c);
    }

    private void Refresh()
    {
        var p = Store.D.Profile;
        int paid = Sim.PaidSubs;
        double next = p.Tiers.Sum(t => Math.Floor(t.Subs) * t.Price);
        var sb = new StringBuilder();
        sb.AppendLine($"<b><size=125%>@{Ui.Esc(p.Handle)}</size></b>");
        sb.AppendLine($"<color=#7a40d9><b>{Math.Floor(p.Followers):N0}</b></color> followers   <color=#ec5c9e><b>{paid:N0}</b></color> paying subscribers");
        foreach (var t in p.Tiers) sb.AppendLine($"<size=85%>  {Ui.Esc(t.Name)}: {Math.Floor(t.Subs):N0} x {t.Price:N0} RP</size>");
        sb.AppendLine($"Next payday: <b>{next:N0} RP</b>   Earned so far: {p.TotalEarned:N0} RP");
        sb.Append($"<size=85%>{Store.D.Photos.Count(x => !x.Deleted)} photos, {Store.D.Posts.Count} posts</size>");
        stats.text = sb.ToString();
        int unread = Store.D.Stars.Count(s => s.Unread);
        if (msgButtonText != null) msgButtonText.text = unread > 0 ? $"Messages  <b>({unread} new)</b>" : "Messages";
        string ai = AiStatus();
        if (aiStatus != null && aiStatus.text != ai) { aiStatus.text = ai; Ui.Rebuild(content); }
    }

    private static string AiStatus()
    {
        string s;
        if (!Plugin.AiEnabled.Value) s = "off - fans use canned comments";
        else if (!Ai.Configured) s = "<color=#d0305a>not set up</color> - add your AI settings in BepInEx\\config\\OnlyFlash.cfg (see the mod page)";
        else if (Ai.Problem != null) s = "<color=#d0305a>not working</color> - " + Ui.Esc(Ai.Problem);
        else s = Ui.Esc(Ai.Describe());
        return $"<size=85%><b>AI fans:</b> {s}</size>";
    }

    // One tiny request so players can see right away whether their AI settings work, and why not
    private static void TestAi()
    {
        if (testing) return;
        if (!Ai.Configured)
        {
            OnlyFlashBehaviour.ToastFromOutside("AI fans are not set up", Plugin.AiEnabled.Value
                ? "Add your AI settings in BepInEx\\config\\OnlyFlash.cfg (see the mod page), then restart the game."
                : "AI is switched off ([AI] Enabled = false in OnlyFlash.cfg).");
            return;
        }
        testing = true;
        OnlyFlashBehaviour.ToastFromOutside("Testing AI fans...", Ui.Esc(Ai.Describe()));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Task.Run(async () =>
        {
            string title, detail;
            try
            {
                string reply = Ai.Trunc(await Ai.Chat("You are a fan of M-chan, texting her. Answer in one short sentence.", "Say hi to M-chan.", 300), 90);
                title = "AI fans work!";
                detail = $"{Ui.Esc(Ai.Describe())} answered in {sw.Elapsed.TotalSeconds:0.0} s" + (reply != "" ? $": \"{Ui.Esc(reply)}\"" : "");
            }
            catch (Exception e)
            {
                title = "AI fans failed - using canned replies";
                detail = Ui.Esc(Ai.Explain(e));
            }
            Sim.MainThread.Enqueue(() => { testing = false; OnlyFlashBehaviour.ToastFromOutside(title, detail); });
        });
    }

    protected override void Shown() => Refresh();

    protected override void Tick()
    {
        timer += Time.unscaledDeltaTime;
        if (timer > 1f) { timer = 0; Refresh(); }
    }
}

public class CameraRollWindow : VaultWindow
{
    public CameraRollWindow() : base("Camera Roll")
    {
        content = Ui.Scroll(this);
        var c = content;
        var photos = Store.D.Photos.Where(p => !p.Deleted).Take(60).ToList();
        if (photos.Count == 0)
            Ui.WrapRow(c, $"No photos yet. Go outside and press <b>{Plugin.CameraKey.Value}</b> to open the camera.", 6f, Ui.Muted, Ui.Card);
        float cell = 27f;
        for (int i = 0; i < photos.Count; i += 3)
        {
            var row = PluginGUI.CreateRect(Ui.SizedRowName(3f), c, new Vector2(0f, cell), new Color(1, 1, 1, 0));
            PluginGUI.SetPosition(row, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            for (int k = 0; k < 3 && i + k < photos.Count; k++)
            {
                var photo = photos[i + k];
                var b = PluginGUI.CreateButtonBase(row, "Thumb", new Vector2(-1.5f, -1.5f), 0.1f, Color.white, Ui.Act(() => PluginGUI.ShowWindow(new PhotoWindow(photo))));
                PluginGUI.SetPosition(b, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(k / 3f, 0f), new Vector2((k + 1) / 3f, 1f));
                var img = PluginGUI.CreateRect("Img", b, new Vector2(-1f, -1f), Color.white);
                PluginGUI.SetPosition(img, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(1f, 1f));
                var old = img.GetComponent<UnityEngine.UI.Image>(); if (old != null) UnityEngine.Object.DestroyImmediate(old);
                var raw = img.AddComponent<RawImage>();
                raw.texture = Ui.Load(photo.File);
                Ui.CropToSquare(raw, photo.Aspect);
                raw.raycastTarget = false;
                // Score label on a dark strip (a TMP outline crashes on not-yet-visible text in this game)
                var strip = PluginGUI.CreateRect("Badge", b, new Vector2(-1f, 6f), new Color(0.12f, 0.04f, 0.2f, 0.65f));
                PluginGUI.SetPosition(strip, new Vector2(0f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(1f, 0f));
                strip.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
                var badge = PluginGUI.CreateText($"<b>{photo.Daring}</b>{(photo.Posted ? " <size=70%>posted</size>" : "")}{(photo.AiChecked ? " <size=70%>AI</size>" : "")}", strip, new Vector2(-2f, 0f), Color.white, 4.5f, TextAlignmentOptions.MidlineLeft);
                PluginGUI.SetPosition(badge, new Vector2(1.5f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
                var bt = badge.GetComponent<TextMeshProUGUI>();
                bt.richText = true;
                bt.raycastTarget = false;
            }
        }
        Ui.Rebuild(c);
    }
}

public class PhotoWindow : VaultWindow
{
    private string caption = "";

    public PhotoWindow(PhotoRec photo) : base("Photo")
    {
        content = Ui.Scroll(this);
        var c = content;
        Ui.Image(c, Ui.Load(photo.File), photo.Aspect);
        Ui.WrapRow(c, $"<b>Daring {photo.Daring}/100</b>{(photo.FaceShown ? "  <color=#d0306b>face visible</color>" : "")}\n<size=85%>{Ui.Esc(string.Join(", ", photo.Tags))}</size>\n<size=80%><color=#77708a>{Ui.Ago(photo.TakenUnix)}{(photo.Posted ? "  -  already posted" : "")}</color></size>", 6f, Ui.Ink, Ui.Card);
        if (photo.Why != null && photo.Why.Count > 0)
            Ui.WrapRow(c, "<b>Why this score</b>\n<size=80%>" + string.Join("\n", photo.Why.Select(Ui.Esc)) + "</size>", 5.5f, Ui.Ink, Ui.AccentSoft);
        Ui.WrapRow(c, "<b>Caption</b> <size=80%>(optional - fans react to it)</size>", 5.5f, Ui.Ink, Ui.Bg);
        Ui.Input(c, "", 18f, s => caption = s, 5.5f);
        Ui.Button(c, $"Post publicly  <size=80%>({Math.Floor(Store.D.Profile.Followers):N0} followers)</size>", 12f, Ui.Accent, Color.white, () => DoPost(photo, 0, caption));
        for (int i = 1; i <= 3; i++)
        {
            int tier = i;
            var t = Store.D.Profile.Tiers[i - 1];
            Ui.Button(c, $"Post to {Ui.Esc(t.Name)}  <size=80%>({Math.Floor(Sim.TierAudience(tier)):N0} can see)</size>", 12f, i == 3 ? Ui.Pink : new Color(0.65f, 0.4f, 0.9f), Color.white, () => DoPost(photo, tier, caption));
        }
        Ui.Button(c, "Remove from Camera Roll", 10f, new Color(0.85f, 0.83f, 0.88f), Ui.Ink, () =>
        {
            photo.Deleted = true;
            Store.MarkDirty();
            PluginGUI.ShowPreviousWindow(2);
            PluginGUI.ShowWindow(new CameraRollWindow());
        }, 5.5f);
        Ui.Rebuild(c);
    }

    private static void DoPost(PhotoRec photo, int tier, string caption)
    {
        var post = Sim.CreatePost(photo, tier, Ui.Clean(caption));
        Store.Save();
        PluginGUI.ShowWindow(new PostWindow(post));
    }
}

public class PostsWindow : VaultWindow
{
    private readonly List<(Post post, TextMeshProUGUI label)> rows = new();
    private float timer;

    public PostsWindow() : base("My Posts")
    {
        content = Ui.Scroll(this);
        var c = content;
        var posts = Store.D.Posts.Take(40).ToList();
        if (posts.Count == 0) Ui.WrapRow(c, "Nothing posted yet. Open a photo in your Camera Roll to post it.", 6f, Ui.Muted, Ui.Card);
        foreach (var post in posts)
        {
            var p = post;
            var b = PluginGUI.CreateButtonBase(c, "PostRow", new Vector2(0f, 20f), 0.15f, Ui.Card, Ui.Act(() => PluginGUI.ShowWindow(new PostWindow(p))));
            PluginGUI.SetPosition(b, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            var img = PluginGUI.CreateRect("Img", b, new Vector2(18f, 18f), Color.white);
            PluginGUI.SetPosition(img, new Vector2(1f, 0f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            var old = img.GetComponent<UnityEngine.UI.Image>(); if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var raw = img.AddComponent<RawImage>();
            raw.texture = Ui.Load(Store.Photo(p.PhotoId)?.File);
            Ui.CropToSquare(raw, Store.Photo(p.PhotoId)?.Aspect ?? 1f);
            raw.raycastTarget = false;
            var tgo = PluginGUI.CreateText("", b, new Vector2(-22f, -2f), Ui.Ink, 5.5f, TextAlignmentOptions.MidlineLeft);
            PluginGUI.SetPosition(tgo, new Vector2(21f, -1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 1f));
            var tm = tgo.GetComponent<TextMeshProUGUI>();
            tm.richText = true;
            tm.enableWordWrapping = true;
            rows.Add((p, tm));
        }
        Refresh();
        Ui.Rebuild(c);
    }

    private void Refresh()
    {
        double now = Store.Now;
        foreach (var (post, label) in rows)
            label.text = $"<b>{Ui.Esc(Ui.TierLabel(post.Tier))}</b>  -  daring {post.Daring}\n<color=#ec5c9e>{Sim.Likes(post, now):N0} likes</color>   {Sim.Visible(post, now).Count} comments\n<size=85%><color=#77708a>{Ui.Ago(post.PostedUnix)}</color></size>";
    }

    protected override void Tick()
    {
        timer += Time.unscaledDeltaTime;
        if (timer > 1.5f) { timer = 0; Refresh(); }
    }
}

public class PostWindow : VaultWindow
{
    private readonly Post post;
    private readonly TextMeshProUGUI stats;
    private readonly TMP_InputField input;
    private readonly TextMeshProUGUI sendLabel;
    private string replyTo;
    private int shown = -1;
    private float timer;
    private int commentStart;

    public PostWindow(Post p) : base("Post")
    {
        post = p;
        content = Ui.Scroll(this, 30f);
        Ui.Image(content, Ui.Load(Store.Photo(p.PhotoId)?.File), Store.Photo(p.PhotoId)?.Aspect ?? 1f);
        stats = Ui.WrapRow(content, "", 6f, Ui.Ink, Ui.Card);
        commentStart = content.transform.childCount;

        var (field, buttons) = Ui.InputPanel(this, 30f);
        input = field;
        var send = Ui.ButtonAt(buttons, "Comment", 0f, 0.72f, Ui.Accent, Color.white, Send);
        sendLabel = send.GetComponentInChildren<TextMeshProUGUI>();
        Ui.ButtonAt(buttons, "Clear", 0.72f, 1f, new Color(0.85f, 0.83f, 0.88f), Ui.Ink, () => SetReply(null));
        Refresh(true);
    }

    private void SetReply(string user)
    {
        replyTo = user;
        if (sendLabel != null) sendLabel.text = user == null ? "Comment" : $"Reply to @{Ui.Esc(user)}";
    }

    private void Send()
    {
        string text = Ui.Clean(input.text);
        if (string.IsNullOrWhiteSpace(text)) return;
        Sim.ReplyToComment(post, replyTo, replyTo == null ? text : $"@{replyTo} {text}");
        input.text = "";
        SetReply(null);
        Refresh(true);
    }

    private void Refresh(bool force)
    {
        double now = Store.Now;
        var visible = Sim.Visible(post, now);
        string cap = string.IsNullOrWhiteSpace(post.Caption) ? "" : $"<i>\"{Ui.Esc(post.Caption)}\"</i>\n";
        stats.text = $"{cap}<b>{Ui.Esc(Ui.TierLabel(post.Tier))}</b>  -  {Ui.Ago(post.PostedUnix)}\n<color=#ec5c9e><b>{Sim.Likes(post, now):N0}</b> likes</color>   <b>{visible.Count}</b> comments\n<size=85%>{Ui.Esc(string.Join(", ", post.Tags.Take(6)))}</size>\n<size=75%><color=#77708a>Tap a comment to reply to it.</color></size>";
        if (!force && visible.Count == shown) return;
        shown = visible.Count;
        for (int i = content.transform.childCount - 1; i >= commentStart; i--)
            UnityEngine.Object.Destroy(content.transform.GetChild(i).gameObject);
        if (visible.Count == 0)
            Ui.WrapRow(content, post.TargetComments == 0 && Sim.Progress(post, now) > 0.5 ? "No comments. Bolder photos and more followers bring more comments." : "No comments yet...", 5.5f, Ui.Muted, Ui.Bg);
        foreach (var cm in visible)
        {
            if (cm.FromMe)
            {
                Ui.WrapRow(content, $"<b>{Ui.Esc(cm.User)}</b> <size=75%>(you)</size>  {Ui.Esc(cm.Text)}", 5.5f, Color.white, Ui.Accent, TextAlignmentOptions.TopRight);
                continue;
            }
            string color = cm.Star ? "#7a40d9" : "#2a2238";
            var row = Ui.WrapRow(content, $"<b><color={color}>{Ui.Esc(cm.User)}</color></b>  {Ui.Esc(cm.Text)}", 5.5f, Ui.Ink, cm.Star ? Ui.AccentSoft : Ui.Card);
            string user = cm.User;
            Ui.Clickable(row, () => SetReply(user));
        }
        Ui.Rebuild(content);
    }

    protected override void Tick()
    {
        timer += Time.unscaledDeltaTime;
        if (timer > 1f) { timer = 0; Refresh(false); }
    }
}

public class TiersWindow : VaultWindow
{
    public TiersWindow() : base("Tiers & Prices")
    {
        content = Ui.Scroll(this);
        var c = content;
        Ui.WrapRow(c, "<size=85%>Set a name and monthly price (RP per payday) for each tier. Cheaper tiers attract more subscribers; bolder posts in a tier make more fans pay for it. Higher tiers also see every lower tier's posts.</size>", 5.5f, Ui.Muted, Ui.Bg);
        for (int i = 0; i < 3; i++)
        {
            var t = Store.D.Profile.Tiers[i];
            Ui.WrapRow(c, $"<b>Tier {i + 1}</b>  -  {Math.Floor(t.Subs):N0} subscribers, {t.Posts} posts, avg daring {t.Quality:F0}", 6f, Ui.Ink, Ui.AccentSoft);
            Ui.Input(c, t.Name, 11f, s => { if (!string.IsNullOrWhiteSpace(s)) { t.Name = s.Trim(); Store.MarkDirty(); } });
            Ui.Input(c, t.Price.ToString(), 11f, s => { if (int.TryParse(s.Trim(), out int v) && v >= 0) { t.Price = v; Store.MarkDirty(); } });
        }
        Ui.WrapRow(c, "<size=80%>Top row: tier name. Bottom row: price in RP. Changes save automatically.</size>", 5f, Ui.Muted, Ui.Bg);
        Ui.Rebuild(c);
    }
}

public class MessagesWindow : VaultWindow
{
    public MessagesWindow() : base("Messages")
    {
        content = Ui.Scroll(this);
        var c = content;
        var known = Store.D.Stars.Where(s => s.Joined).ToList();
        if (known.Count == 0)
            Ui.WrapRow(c, "No messages yet. Post photos and grow your followers - fans will start finding your page.", 5.5f, Ui.Muted, Ui.Card);
        else if (Store.D.Stars.Any(s => !s.Joined))
            Ui.WrapRow(c, "<size=85%>More fans will find you as your following grows.</size>", 5f, Ui.Muted, Ui.Bg);
        foreach (var star in known)
        {
            var s = star;
            string tier = s.Tier <= 0 ? "follower" : Store.D.Profile.Tiers[Math.Clamp(s.Tier, 1, 3) - 1].Name;
            string last = s.Chat.Count == 0 ? "<i>say hi</i>" : string.IsNullOrWhiteSpace(s.Chat[^1].Text) ? "<i>[photo]</i>" : Ui.Esc(s.Chat[^1].Text);
            if (last.Length > 60) last = last.Substring(0, 60) + "...";
            // Card that grows with its text (fixed-height buttons clipped long names/previews)
            var card = Ui.WrapRow(c, $"<b>{Ui.Esc(s.Name)}</b>{(s.Unread ? " <color=#ffe066>(new)</color>" : "")}\n<size=80%>{Ui.Esc(tier)}  -  affection {s.Affection:F0}</size>\n<size=85%>{last}</size>",
                5.5f, Color.white, s.Unread ? Ui.Pink : Ui.Accent);
            Ui.Clickable(card, () => PluginGUI.ShowWindow(new ChatWindow(s)));
        }
        Ui.Button(c, "+ Create a star fan", 12f, new Color(0.85f, 0.83f, 0.88f), Ui.Ink, () => PluginGUI.ShowWindow(new CreateStarWindow()), 6f);
        Ui.Rebuild(c);
    }
}

public class ChatWindow : VaultWindow
{
    private readonly Star star;
    private readonly TMP_InputField input;
    private int shown = -1;
    private bool wasBusy;
    private float timer;

    public ChatWindow(Star s) : base(s.Name)
    {
        star = s;
        float bottom = 30f;
        content = Ui.Scroll(this, bottom);

        var (field, buttons) = Ui.InputPanel(this, bottom);
        input = field;
        Ui.ButtonAt(buttons, "Send", 0f, 0.36f, Ui.Accent, Color.white, () => SendNow(false));
        Ui.ButtonAt(buttons, "Photo", 0.36f, 0.64f, new Color(0.65f, 0.4f, 0.9f), Color.white,
            () => PluginGUI.ShowWindow(new PhotoPickerWindow(photo =>
            {
                Chat.Send(star, Ui.Clean(input.text), false, photo.Id);
                input.text = "";
            })));
        Ui.ButtonAt(buttons, "Invite over", 0.64f, 1f, Ui.Pink, Color.white, () => SendNow(true));
        Refresh(true);
    }

    private void SendNow(bool invite)
    {
        string text = input.text;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!invite) return;
            string stage = SFM_custom_mission_v2.PlayerData.stage.ToString();
            text = stage == "Apart" ? "Come see me next time I'm out..." : $"Come find me. I'm at the {Scoring.StageName(stage)}.";
        }
        Chat.Send(star, text, invite);
        input.text = "";
        Refresh(true);
    }

    protected override void Shown()
    {
        star.Unread = false;
        Store.MarkDirty();
    }

    private void Refresh(bool force)
    {
        bool busy = Chat.Busy(star);
        if (!force && star.Chat.Count == shown && busy == wasBusy) return;
        shown = star.Chat.Count;
        wasBusy = busy;
        star.Unread = false;
        Ui.Clear(content);
        Ui.WrapRow(content, $"<size=80%>{Ui.Esc(star.Personality)}\nAffection {star.Affection:F0}/100  -  met you {star.Visits} time(s)</size>", 5f, Ui.Muted, Ui.Bg);
        foreach (var m in star.Chat.TakeLast(40))
        {
            if (m.PhotoId != null)
            {
                var ph = Store.Photo(m.PhotoId);
                if (ph != null) Ui.Image(content, Ui.Load(ph.File), ph.Aspect);
            }
            if (string.IsNullOrWhiteSpace(m.Text)) continue;
            if (m.FromMe) Ui.WrapRow(content, Ui.Esc(m.Text), 5.5f, Color.white, Ui.Accent, TextAlignmentOptions.TopRight);
            else Ui.WrapRow(content, $"<b>{Ui.Esc(star.Name)}</b>  {Ui.Esc(m.Text)}", 5.5f, Ui.Ink, Ui.Card);
        }
        if (busy) Ui.WrapRow(content, $"<i>{Ui.Esc(star.Name)} is typing...</i>", 5f, Ui.Muted, Ui.Bg);
        if (Visits.IsQueuedOrActive(star.Id)) Ui.WrapRow(content, $"<i>{Ui.Esc(star.Name)} is on the way to you.</i>", 5f, Ui.Pink, Ui.Bg);
        Ui.Rebuild(content);
        var sr = content.transform.parent.GetComponent<ScrollRect>();
        if (sr != null) sr.verticalNormalizedPosition = 0f;
    }

    protected override void Tick()
    {
        timer += Time.unscaledDeltaTime;
        if (timer > 0.5f) { timer = 0; Refresh(false); }
    }
}

// Full wipe of the app, with a confirmation step
public class ResetWindow : VaultWindow
{
    private int taps;
    private TextMeshProUGUI label;

    public ResetWindow() : base("Reset OnlyFlash")
    {
        content = Ui.Scroll(this);
        var c = content;
        Ui.WrapRow(c, "<b>This erases everything in OnlyFlash:</b>\n- all photos in the Camera Roll (the image files too)\n- all posts, likes and comments\n- followers, subscribers and earnings history\n- every fan's chat history and memories\n\nFans like Sora, Kenji and Mr. Tanaka start over as strangers. NovelAI keeps nothing on its side, so nothing remembers you afterwards.\n\n<b>Your game save and RP are not touched.</b>", 5.5f, Ui.Ink, Ui.Card);
        var btn = Ui.Button(c, "Erase everything", 13f, new Color(0.85f, 0.25f, 0.3f), Color.white, Confirm);
        label = btn.GetComponentInChildren<TextMeshProUGUI>();
        Ui.Rebuild(c);
    }

    private void Confirm()
    {
        if (++taps < 2)
        {
            if (label != null) label.text = "Tap again to confirm";
            return;
        }
        int files = Store.ResetAll();
        Ui.Clear(content);
        Ui.WrapRow(content, $"<b>OnlyFlash has been reset.</b>\n{files} photo files removed. You're starting fresh with 0 followers.", 6f, Ui.Ink, Ui.Card);
        RequestRebuild();
    }
}

// Pick a Camera Roll photo (used to send pics in DMs)
public class PhotoPickerWindow : VaultWindow
{
    public PhotoPickerWindow(Action<PhotoRec> onPick) : base("Send a photo")
    {
        content = Ui.Scroll(this);
        var c = content;
        var photos = Store.D.Photos.Where(p => !p.Deleted).Take(60).ToList();
        if (photos.Count == 0)
            Ui.WrapRow(c, $"No photos yet. Press <b>{Plugin.CameraKey.Value}</b> to take some.", 6f, Ui.Muted, Ui.Card);
        for (int i = 0; i < photos.Count; i += 3)
        {
            var row = PluginGUI.CreateRect(Ui.SizedRowName(3f), c, new Vector2(0f, 27f), new Color(1, 1, 1, 0));
            PluginGUI.SetPosition(row, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            for (int k = 0; k < 3 && i + k < photos.Count; k++)
            {
                var photo = photos[i + k];
                var b = PluginGUI.CreateButtonBase(row, "Thumb", new Vector2(-1.5f, -1.5f), 0.1f, Color.white, Ui.Act(() =>
                {
                    onPick(photo);
                    PluginGUI.ShowPreviousWindow();
                }));
                PluginGUI.SetPosition(b, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(k / 3f, 0f), new Vector2((k + 1) / 3f, 1f));
                var img = PluginGUI.CreateRect("Img", b, new Vector2(-1f, -1f), Color.white);
                PluginGUI.SetPosition(img, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(1f, 1f));
                var old = img.GetComponent<UnityEngine.UI.Image>(); if (old != null) UnityEngine.Object.DestroyImmediate(old);
                var raw = img.AddComponent<RawImage>();
                raw.texture = Ui.Load(photo.File);
                Ui.CropToSquare(raw, photo.Aspect);
                raw.raycastTarget = false;
            }
        }
        Ui.Rebuild(c);
    }
}

public class CreateStarWindow : VaultWindow
{
    private static readonly string[] Types = { "NewMale", "NewOji", "PreviousMale", "NewFemale", "PreviousFemale", "NewOba" };
    private readonly Star draft = new() { Id = "", Name = "", Personality = "", Style = "", Tier = 2, Affection = 40 };
    private int typeIndex;
    private TextMeshProUGUI lookLabel;

    public CreateStarWindow() : base("New star fan")
    {
        content = Ui.Scroll(this);
        var c = content;
        Ui.WrapRow(c, "<b>Name</b>", 5.5f, Ui.Ink, Ui.Bg);
        Ui.Input(c, "", 11f, s => draft.Name = s);
        Ui.WrapRow(c, "<b>Personality</b> <size=80%>(who they are, what they want from her)</size>", 5.5f, Ui.Ink, Ui.Bg);
        Ui.Input(c, "", 24f, s => draft.Personality = s);
        Ui.WrapRow(c, "<b>Texting style</b> <size=80%>(optional)</size>", 5.5f, Ui.Ink, Ui.Bg);
        Ui.Input(c, "", 11f, s => draft.Style = s);
        lookLabel = Ui.WrapRow(c, "", 5.5f, Ui.Ink, Ui.AccentSoft);
        var r1 = Ui.Row(c, 12f, new Color(1, 1, 1, 0));
        Ui.ButtonAt(r1, "Type", 0f, 0.25f, Ui.Accent, Color.white, () => { typeIndex = (typeIndex + 1) % Types.Length; draft.Body = draft.Hair = draft.Face = 0; UpdateLook(); });
        Ui.ButtonAt(r1, "Body", 0.25f, 0.5f, Ui.Accent, Color.white, () => { draft.Body = (draft.Body + 1) % Visits.Ranges(Types[typeIndex]).bodies; UpdateLook(); });
        Ui.ButtonAt(r1, "Hair", 0.5f, 0.75f, Ui.Accent, Color.white, () => { draft.Hair = (draft.Hair + 1) % Visits.Ranges(Types[typeIndex]).hairs; UpdateLook(); });
        Ui.ButtonAt(r1, "Face", 0.75f, 1f, Ui.Accent, Color.white, () => { draft.Face = (draft.Face + 1) % Visits.Ranges(Types[typeIndex]).faces; UpdateLook(); });
        var r2 = Ui.Row(c, 12f, new Color(1, 1, 1, 0));
        Ui.ButtonAt(r2, "Subscribed tier", 0f, 0.5f, new Color(0.65f, 0.4f, 0.9f), Color.white, () => { draft.Tier = draft.Tier % 3 + 1; UpdateLook(); });
        Ui.ButtonAt(r2, "Size", 0.5f, 1f, new Color(0.65f, 0.4f, 0.9f), Color.white, () => { draft.Size = draft.Size >= 1.15f ? 0.9f : draft.Size + 0.05f; UpdateLook(); });
        Ui.Button(c, "Create", 13f, Ui.Pink, Color.white, Create);
        UpdateLook();
        Ui.Rebuild(c);
    }

    private void UpdateLook()
    {
        var (b, h, f) = Visits.Ranges(Types[typeIndex]);
        lookLabel.text = $"Look: <b>{Types[typeIndex]}</b>  body {draft.Body + 1}/{b}, hair {draft.Hair + 1}/{h}, face {draft.Face + 1}/{f}, size {draft.Size:F2}\nSubscribed to: <b>{Ui.Esc(Store.D.Profile.Tiers[draft.Tier - 1].Name)}</b>\n<size=80%>You'll see their look when they first come to you.</size>";
    }

    private void Create()
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) return;
        draft.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        draft.AvatarType = Types[typeIndex];
        if (string.IsNullOrWhiteSpace(draft.Personality)) draft.Personality = "A devoted fan of M-chan.";
        if (string.IsNullOrWhiteSpace(draft.Style)) draft.Style = "Casual texting.";
        draft.Joined = true;
        lock (Store.Sync) Store.D.Stars.Add(draft);
        Store.Save();
        PluginGUI.ShowPreviousWindow(2);
        PluginGUI.ShowWindow(new MessagesWindow());
    }
}
