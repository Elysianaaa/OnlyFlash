using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using PhotoMode;
using SFM_custom_mission_v2;
using SFM_custom_mission_v2.Script;
using SFM_custom_mission_v2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.PostProcessing;
using BaseCameraController = ExposureUnnoticed2.Object3D.Camera.BaseCameraController;
using InGameMenuView = ExposureUnnoticed2.ObjectUI.InGameMenu.InGameMenuView;

namespace OnlyFlash;

[BepInPlugin("OnlyFlash", "OnlyFlash", "1.1.0")]
[BepInDependency("SFM_custom_mission_v2", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("PhotoMode", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static ConfigEntry<string> CameraKey, AcceptKey, DeclineKey, AppKey, ApiUrl, ApiKey, Model;
    internal static ConfigEntry<float> ScrollStep, SkipOnDayNight;
    internal static OnlyFlashApp App;
    internal static ConfigEntry<int> PhotoSize;
    internal static ConfigEntry<bool> ShowInGallery, SpontaneousVisits, AiEnabled, LogPrompts;
    internal static ConfigEntry<float> EngagementMinutes, VisitCheckMinutes, Temperature, PhoneScale, DmMinGapMinutes;
    internal static ConfigEntry<bool> FanDms, NewContacts;
    internal static ConfigEntry<int> MaxAutoContacts, VisionTimeout;
    internal static ConfigEntry<bool> VisionEnabled;
    internal static ConfigEntry<string> VisionUrl, VisionModel;

    public override void Load()
    {
        Logger = Log;
        CameraKey = Config.Bind("Controls", "CameraKey", "B", "Key that opens the OnlyFlash camera (photo mode) while outside.");
        AcceptKey = Config.Bind("Controls", "AcceptKey", "Y", "Key to accept a visiting fan.");
        DeclineKey = Config.Bind("Controls", "DeclineKey", "U", "Key to send a visiting fan away.");
        AppKey = Config.Bind("Controls", "AppKey", "H", "Opens OnlyFlash straight away (opens the phone menu if needed); press again to close.");
        PhotoSize = Config.Bind("Camera", "PhotoSize", 768, "Photo resolution (square).");
        ShowInGallery = Config.Bind("Camera", "ShowInGallery", false, "Also show OnlyFlash photos in the Custom Missions gallery app.");
        EngagementMinutes = Config.Bind("Simulation", "EngagementMinutes", 8f, "Real minutes until a post has received ~95% of its likes.");
        VisitCheckMinutes = Config.Bind("Visits", "VisitCheckMinutes", 4f, "How often (real minutes, while outside) star fans consider coming to you on their own.");
        SpontaneousVisits = Config.Bind("Visits", "SpontaneousVisits", true, "Allow star fans to come to you without being invited.");
        AiEnabled = Config.Bind("AI", "Enabled", true, "Use AI for comments and chats. Off = simple canned comments.");
        ApiUrl = Config.Bind("AI", "ApiUrl", "", "Leave empty to use the settings from FansChatAI.cfg.");
        ApiKey = Config.Bind("AI", "ApiKey", "", "Leave empty to use the key from FansChatAI.cfg.");
        Model = Config.Bind("AI", "Model", "", "Leave empty to use the model from FansChatAI.cfg.");
        Temperature = Config.Bind("AI", "Temperature", 1.0f, "Higher = wilder replies.");
        LogPrompts = Config.Bind("AI", "LogPrompts", false, "Write prompts and AI output to the BepInEx log.");
        PhoneScale = Config.Bind("Phone", "PhoneScale", 1.6f, "How much bigger the phone gets while OnlyFlash is open (1 = normal). Automatically limited so it fits the screen.");
        ScrollStep = Config.Bind("Phone", "ScrollStep", 0.1f, "How far one mouse-wheel notch scrolls OnlyFlash lists, as a share of the visible list (0.1 = a tenth of the screen).");
        SkipOnDayNight = Config.Bind("Simulation", "SkipOnDayNightChange", 180f, "When day turns to night or back in the game (killing time, sleeping), OnlyFlash skips this many minutes ahead so likes, comments and DMs catch up. 0 = off.");        FanDms = Config.Bind("Messages", "FanDms", true, "Star fans text you on their own (reactions to posts, flirting, requests, check-ins).");
        DmMinGapMinutes = Config.Bind("Messages", "DmMinGapMinutes", 5f, "Minimum real minutes between unprompted messages from the same fan.");
        NewContacts = Config.Bind("Messages", "NewContacts", true, "New fans sometimes slide into your DMs after bold public posts and become contacts.");
        MaxAutoContacts = Config.Bind("Messages", "MaxAutoContacts", 6, "Maximum number of contacts created that way.");
        VisionEnabled = Config.Bind("AI Judge", "Enabled", true, "A local vision AI (Ollama) looks at every new photo and corrects the daring score.");
        VisionUrl = Config.Bind("AI Judge", "OllamaUrl", "http://localhost:11434", "Where Ollama is running.");
        VisionModel = Config.Bind("AI Judge", "Model", "qwen2.5vl:7b", "Vision model to use (must be pulled in Ollama).");
        VisionTimeout = Config.Bind("AI Judge", "TimeoutSeconds", 90, "Give up after this long (the first photo of a session is slower while the model loads).");

        Store.Load(Path.Combine(Paths.ConfigPath, "OnlyFlash"));

        Engine.RegisterFunction("OnlyFlashPollNotice", PollNotice);
        Engine.RegisterFunction("OnlyFlashPollPayout", PollPayout);
        Engine.RegisterFunction("OnlyFlashNextVisit", NextVisit);
        Engine.RegisterFunction("OnlyFlashVisitEvent", VisitEvent);
        Engine.RegisterFunction("OnlyFlashVisitDecision", VisitDecision);

        App = new OnlyFlashApp();
        PluginGUI.RegisterApp(App);
        AddComponent<OnlyFlashBehaviour>();
        Logger.LogInfo($"OnlyFlash loaded: {Store.D.Photos.Count} photos, {Store.D.Posts.Count} posts, {Store.D.Stars.Count} star fans, AI {(Ai.Available ? "on" : "off")}");
    }

    private static ProgramValue PollNotice(ProgramVariables p, ProgramThreadBase t)
    {
        if (!Sim.Notices.TryDequeue(out var n)) return ProgramValue.Null;
        var v = new ProgramValue(ProgramValue.Type.List);
        v["user"] = new ProgramValue(Ui.Clean(n.user));
        v["text"] = new ProgramValue(Ui.Clean(n.text));
        return v;
    }

    private static ProgramValue PollPayout(ProgramVariables p, ProgramThreadBase t)
    {
        double pay;
        lock (Store.Sync) { pay = Sim.PendingPayout; Sim.PendingPayout = 0; }
        return new ProgramValue(pay);
    }

    private static ProgramValue NextVisit(ProgramVariables p, ProgramThreadBase t)
    {
        var v = Visits.Next();
        if (v == null) return ProgramValue.Null;
        var r = new ProgramValue(ProgramValue.Type.List);
        r["vid"] = new ProgramValue((double)v.Vid);
        r["name"] = new ProgramValue(v.Name);
        r["type"] = new ProgramValue(v.Type);
        r["body"] = new ProgramValue((double)v.Body);
        r["hair"] = new ProgramValue((double)v.Hair);
        r["face"] = new ProgramValue((double)v.Face);
        r["size"] = new ProgramValue((double)v.Size);
        Logger.LogInfo($"OnlyFlash: {v.Name} is on the way ({v.Type} {v.Body}/{v.Hair}/{v.Face})");
        return r;
    }

    private static ProgramValue VisitEvent(ProgramVariables p, ProgramThreadBase t)
    {
        var a = p.GetVariable("_args");
        int vid = (int)a[0].ToFloat();
        string ev = a[1].ToString();
        Logger.LogInfo($"OnlyFlash visit {vid}: {ev}");
        Visits.Event(vid, ev);
        return ProgramValue.Null;
    }

    private static ProgramValue VisitDecision(ProgramVariables p, ProgramThreadBase t)
    {
        string d = Visits.Decision((int)p.GetVariable("_args")[0].ToFloat());
        return d == null ? ProgramValue.Null : new ProgramValue(d);
    }
}

public class OnlyFlashBehaviour : MonoBehaviour
{
    public OnlyFlashBehaviour(IntPtr ptr) : base(ptr) { }

    private bool capturing, loggedStart, keyPending;
    private float keyDownAt;
    private float tickTimer, saveTimer, outsideSeconds;
    private bool wasOutside;
    private string toast;
    private float toastUntil;
    private Texture2D panel;

    private void Start()
    {
        panel = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        panel.SetPixel(0, 0, Color.white);
        panel.Apply();
    }

    private void Update()
    {
        while (Sim.MainThread.TryDequeue(out var action))
        {
            try { action(); } catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash: " + e); }
        }

        if (!loggedStart)
        {
            loggedStart = true;
            Plugin.Logger.LogInfo($"OnlyFlash update loop running; keyboard {(Keyboard.current != null ? "found" : "MISSING")}, camera key '{Plugin.CameraKey.Value}' parses: {Enum.TryParse(Plugin.CameraKey.Value?.Trim(), true, out Key _)}");
        }
        if (Keyboard.current != null && Keyboard.current.bKey.wasPressedThisFrame) Plugin.Logger.LogInfo("OnlyFlash: B pressed (raw)");

        bool inGame = PatchBaseCameraController.instance != null && Globals.gamestate != null;
        string stage = PlayerData.stage.ToString();
        bool outside = inGame && stage != "Apart" && stage != "None";
        float dt = Time.unscaledDeltaTime;

        // Camera: tap the key = freecam with time flowing, hold it = freecam with time stopped
        var pm = PhotoModeBehaviour.Instance;
        var camKey = KeyCtl(Plugin.CameraKey.Value);
        if (!capturing && !keyPending && camKey != null && camKey.wasPressedThisFrame)
        {
            string why = null;
            if (!inGame || stage == "None") why = "not in game yet (no camera)";
            else if (pm == null) why = "photo mode plugin not running";
            else if (pm.State != "idle") why = $"photo mode busy ({pm.State})";
            else if (Typing()) why = "typing in a text field";
            if (why != null) Plugin.Logger.LogInfo($"OnlyFlash camera key ignored: {why}");
            else { keyPending = true; keyDownAt = Time.unscaledTime; }
        }
        if (keyPending && camKey != null)
        {
            bool held = camKey.isPressed && Time.unscaledTime - keyDownAt >= 0.35f;
            bool tapped = !camKey.isPressed;
            if (held || tapped)
            {
                keyPending = false;
                try
                {
                    if (pm != null && pm.Begin(BodyPosition(), 60f, held)) { capturing = true; Plugin.Logger.LogInfo($"OnlyFlash: photo mode started ({(held ? "time stop" : "time flowing")})"); }
                    else Plugin.Logger.LogWarning("OnlyFlash: photo mode refused to start (no game camera found)");
                }
                catch (Exception e) { Plugin.Logger.LogError("OnlyFlash: photo mode failed to start: " + e); }
            }
        }
        if (capturing && pm != null)
        {
            if (pm.State == "confirmed")
            {
                pm.State = "idle";
                capturing = false;
                try { Capture(pm.ResultPos, pm.ResultRot, pm.ResultFov, pm.ResultAspect); }
                catch (Exception e) { Plugin.Logger.LogError("OnlyFlash capture failed: " + e); Toast("Photo failed - see log"); }
            }
            else if (pm.State != "active")
            {
                pm.State = "idle";
                capturing = false;
            }
        }

        // Payday when she gets home safely
        if (inGame)
        {
            if (outside) outsideSeconds += dt;
            if (wasOutside && stage == "Apart")
            {
                if (outsideSeconds >= 45) Sim.Payday();
                outsideSeconds = 0;
            }
            wasOutside = outside;
        }

        // Visiting fan: accept / send away
        var v = Visits.Active;
        if (v != null && v.State == "arrived" && v.Decision == null)
        {
            if (Pressed(Plugin.AcceptKey.Value)) v.Decision = "accept";
            else if (Pressed(Plugin.DeclineKey.Value)) v.Decision = "decline";
        }
        else if (inGame && !capturing && !keyPending && Pressed(Plugin.AppKey.Value) && !Typing())
        {
            try { ToggleApp(); } catch (Exception e) { Plugin.Logger.LogWarning("OnlyFlash app key: " + e.Message); }
        }
        if (openPending)
        {
            var cm = PluginGUI.instance;
            if (cm != null && cm.gui != null && cm.gui.active) { openPending = false; ShowVault(); }
            else if (Time.unscaledTime > openPendingUntil) { openPending = false; Plugin.Logger.LogInfo("OnlyFlash app key: the phone menu did not open"); }
        }

        ScalePhone(dt);
        Sim.RunDue();

        tickTimer += dt;
        if (tickTimer >= 1f)
        {
            // Day <-> night in the game (killing time, sleeping): OnlyFlash's time jumps too
            if (inGame)
            {
                bool? day = null;
                try { var gd = ExposureUnnoticed2.Scripts.InGame.GameState.GameStateData; if (gd != null) day = gd.IsDaytime; } catch { }
                if (day.HasValue && lastDaytime.HasValue && day != lastDaytime && Plugin.SkipOnDayNight.Value > 0 && !Visits.InProgress)
                    Sim.SkipTime(Plugin.SkipOnDayNight.Value * 60, day.Value ? "it's morning" : "night has fallen");
                lastDaytime = day;
            }
            else lastDaytime = null;
            Sim.Tick(Store.Now, tickTimer);
            double recent = Store.D.Posts.Take(3).Select(p => (double)p.Daring).DefaultIfEmpty(0).Average();
            Visits.SpontaneousTick(outside, recent);
            if (inGame) Chat.DmTick(outside);
            tickTimer = 0;
        }
        saveTimer += dt;
        if (saveTimer >= 10f) { Store.SaveIfDirty(); saveTimer = 0; }
    }

    private void OnApplicationQuit() => Store.SaveIfDirty();

    // App key: straight into OnlyFlash. Opens the game's phone menu first if it is closed; closes it if OnlyFlash is already up.
    private bool openPending;
    private float openPendingUntil;
    private bool? lastDaytime;

    private void ToggleApp()
    {
        var cm = PluginGUI.instance;
        var menu = InGameMenuView.Instance;
        if (cm == null) return;
        bool phoneUp = cm.gui != null && cm.gui.active;
        var stack = cm.windowStack;
        if (phoneUp && stack != null && stack.Count > 0 && stack[stack.Count - 1] is VaultWindow)
        {
            if (menu != null) menu.MenuClose();
            return;
        }
        if (phoneUp) { ShowVault(); return; }
        if (menu != null && menu.currentState == InGameMenuView.State.Show) return;   // menu up with another screen (map, inventory...) on top
        InGameMenuView.Open();
        openPending = true;
        openPendingUntil = Time.unscaledTime + 2f;
    }

    // Back to the OnlyFlash screen she was last on, or the OnlyFlash home screen
    private static void ShowVault()
    {
        var stack = PluginGUI.instance.windowStack;
        if (stack == null) return;
        if (stack.Count > 0 && stack[stack.Count - 1] is VaultWindow) return;
        int idx = stack.FindLastIndex(w => w is VaultWindow);
        if (idx >= 0) PluginGUI.ShowPreviousWindow(stack.Count - 1 - idx);
        else Plugin.App.Open();
    }

    // Zoom the whole phone while a OnlyFlash screen is on top, back to normal for other apps
    private Transform phoneCase;
    private void ScalePhone(float dt)
    {
        var gui = PluginGUI.instance == null ? null : PluginGUI.instance.gui;
        if (gui == null) return;
        if (phoneCase == null) phoneCase = gui.transform.Find("OuterCase");
        if (phoneCase == null) return;

        float target = 1f;
        var stack = PluginGUI.instance.windowStack;
        if (gui.active && stack != null && stack.Count > 0 && stack[stack.Count - 1] is VaultWindow)
        {
            var rt = phoneCase.GetComponent<RectTransform>();
            float fit = rt != null && rt.rect.height > 1 ? Screen.height * 0.97f / rt.rect.height : 1.5f;
            target = Mathf.Max(1f, Mathf.Min(Plugin.PhoneScale.Value, fit));
        }
        float cur = phoneCase.localScale.x;
        if (Mathf.Abs(cur - target) > 0.001f)
        {
            float next = Mathf.MoveTowards(cur, target, dt * 6f);
            phoneCase.localScale = new Vector3(next, next, 1f);
        }
    }

    private void Capture(Vector3 pos, Quaternion rot, float fov, float aspect)
    {
        if (aspect <= 0.1f) aspect = 1f;
        var ctrl = PatchBaseCameraController.instance;
        var go = Instantiate(ctrl.gameObject);
        var cam = go.GetComponent<Camera>();
        cam.enabled = false;
        DestroyImmediate(go.GetComponent<BaseCameraController>());
        var listener = go.GetComponent<AudioListener>(); if (listener != null) DestroyImmediate(listener);
        var col = go.GetComponent<SphereCollider>(); if (col != null) DestroyImmediate(col);
        var rb = go.GetComponent<Rigidbody>(); if (rb != null) DestroyImmediate(rb);

        // PhotoSize is the short side; portrait/landscape photos get a longer side
        int size = Mathf.Clamp(Plugin.PhotoSize.Value, 256, 2048);
        int width = aspect >= 1f ? Mathf.RoundToInt(size * aspect) : size;
        int height = aspect >= 1f ? size : Mathf.RoundToInt(size / aspect);
        var rt = new RenderTexture(width, height, 16);
        cam.CopyFrom(ctrl.camera);
        cam.enabled = false;
        cam.aspect = aspect;
        cam.fieldOfView = fov;
        cam.targetTexture = rt;
        cam.cullingMask = 205520855;
        cam.farClipPlane = 1000f;
        cam.clearFlags = CameraClearFlags.Skybox;
        var ppl = go.GetComponent<PostProcessLayer>(); if (ppl != null) ppl.fog.enabled = false;
        go.transform.position = pos;
        go.transform.rotation = rot;
        cam.Render();

        var snap = SnapshotManager.SaveSnapshot(rt, PlayerData.stage, pos, rot, !Plugin.ShowInGallery.Value);
        cam.targetTexture = null;
        Destroy(go);
        rt.Release();
        Destroy(rt);

        JsonNode state = JsonNode.Parse(snap.data.ToList().ToJson().ToJsonString());
        var (daring, tags, faceShown, why) = Scoring.Score(state, pos, rot, fov, aspect);
        var rec = new PhotoRec
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10),
            File = snap.filepath,
            TakenUnix = Store.Now,
            Stage = PlayerData.stage.ToString(),
            Day = PlayerData.isdaytime,
            Daring = daring,
            Tags = tags,
            FaceShown = faceShown,
            Aspect = aspect,
            Why = why
        };
        lock (Store.Sync) Store.D.Photos.Insert(0, rec);
        Store.MarkDirty();
        VisionJudge.Check(rec, state.ToJsonString(), pos, rot, fov, aspect);
        // Show the two biggest point sources right away; the full breakdown is in the Camera Roll
        var top = why.Where(w => w.StartsWith("+")).OrderByDescending(w => int.TryParse(w.Substring(1).Split(' ')[0], out int n) ? n : 0).Take(2).Select(w => w.Trim());
        var capNote = why.FirstOrDefault(w => w.StartsWith("capped"));
        Toast($"Saved to Camera Roll  -  <b>daring {daring}/100</b>{(faceShown ? "  (face visible!)" : "")}", $"{string.Join("   ", top)}{(capNote != null ? "   (" + capNote + ")" : "")}");
        Plugin.Logger.LogInfo($"OnlyFlash photo {rec.Id}: daring {daring}, tags: {string.Join(", ", tags)}");
    }

    private string toastDetail;
    private static OnlyFlashBehaviour instance;
    private void Awake() { instance = this; }
    public static void ToastFromOutside(string text, string detail = null) { if (instance != null) instance.Toast(text, detail); }

    private void Toast(string text, string detail = null)
    {
        toast = text;
        toastDetail = detail;
        toastUntil = Time.unscaledTime + (detail != null ? 8f : 5f);
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        float w = Screen.width, h = Screen.height, fs = Mathf.Max(14, h / 55f);
        var style = new GUIStyle { fontSize = (int)fs, alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true };
        style.normal.textColor = Color.white;

        if (toast != null && Time.unscaledTime < toastUntil)
        {
            // Smaller than the other prompts: a compact two-line note near the top of the screen
            int tfs = Mathf.Max(14, (int)(h / 55f));
            int dfs = Mathf.Max(12, (int)(tfs * 0.9f));
            var ts = new GUIStyle { fontSize = tfs, alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true };
            ts.normal.textColor = Color.white;
            string text = toastDetail == null ? toast : $"{toast}\n<size={dfs}><color=#e6d6ff>{toastDetail}</color></size>";
            // Size the box to the text so nothing gets cut off
            float bw = Mathf.Min(w * 0.8f, tfs * 40);
            float bh = ts.CalcHeight(new GUIContent(text), bw - tfs) + tfs * 0.8f;
            var r = new Rect(w / 2 - bw / 2, h * 0.10f, bw, bh);
            GUI.color = new Color(0.25f, 0.1f, 0.4f, 0.8f);
            GUI.DrawTexture(r, panel);
            GUI.color = Color.white;
            GUI.Label(r, text, ts);
        }

        var v = Visits.Active;
        if (v != null && v.State == "arrived" && v.Decision == null)
        {
            var r = new Rect(w / 2 - fs * 16, h * 0.70f, fs * 32, fs * 4.2f);
            GUI.color = new Color(0.25f, 0.1f, 0.4f, 0.85f);
            GUI.DrawTexture(r, panel);
            GUI.color = Color.white;
            GUI.Label(r, $"<b>{v.Name}</b> is here for you.\n[{Plugin.AcceptKey.Value.ToUpper()}] Let them   [{Plugin.DeclineKey.Value.ToUpper()}] Send them away", style);
        }
        else if (v != null && (v.State == "spawned" || v.State == "queued"))
        {
            var r = new Rect(w - fs * 17, h * 0.05f, fs * 16, fs * 1.6f);
            GUI.color = new Color(0.25f, 0.1f, 0.4f, 0.6f);
            GUI.DrawTexture(r, panel);
            GUI.color = Color.white;
            GUI.Label(r, $"{v.Name} is on the way...", style);
        }
    }

    private static Vector3 BodyPosition()
    {
        try
        {
            var b = Globals.gamestate["CameraTarget"]["Body"];
            return new Vector3((float)b["x"].ToFloat(), (float)b["y"].ToFloat(), (float)b["z"].ToFloat());
        }
        catch { return PatchBaseCameraController.instance.transform.position; }
    }

    private static UnityEngine.InputSystem.Controls.KeyControl KeyCtl(string keyName)
    {
        var kb = Keyboard.current;
        if (kb == null || string.IsNullOrWhiteSpace(keyName)) return null;
        return Enum.TryParse(keyName.Trim(), true, out Key key) ? kb[key] : null;
    }

    private static bool Pressed(string keyName)
    {
        var kb = Keyboard.current;
        if (kb == null || string.IsNullOrWhiteSpace(keyName)) return false;
        if (!Enum.TryParse(keyName.Trim(), true, out Key key)) return false;
        return kb[key].wasPressedThisFrame;
    }

    // Don't react to hotkeys while typing in a phone text field
    private static bool Typing()
    {
        var es = EventSystem.current;
        var sel = es == null ? null : es.currentSelectedGameObject;
        if (sel == null) return false;
        var field = sel.GetComponent<TMP_InputField>();
        return field != null && field.isFocused;
    }
}
