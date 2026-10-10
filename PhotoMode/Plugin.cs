using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using SFM_custom_mission_v2;
using SFM_custom_mission_v2.Script;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.PostProcessing;
using BaseCameraController = ExposureUnnoticed2.Object3D.Camera.BaseCameraController;
using InputResolver = ExposureUnnoticed2.Object3D.Player.Scripts.InputResolver;
using InputData = ExposureUnnoticed2.Object3D.Player.Scripts.InputData;
using PlayerController = ExposureUnnoticed2.Object3D.Player.Scripts.PlayerController;
using LookAtController = ExposureUnnoticed2.Object3D.Player.Scripts.LookAtController;

namespace PhotoMode;

// Freecam photo mode. OnlyFlash calls Begin() when B is pressed; while it is active the
// world is frozen, all game input is blocked (so Manaka stays still) and WASD/mouse move a
// separate photo camera. Choose square, portrait or landscape framing.
// Script access (Custom Missions): PhotoModeBegin / PhotoModeState / PhotoModeResult.
[BepInPlugin("PhotoMode", "FansChat Photo Mode", "1.1.0")]
[BepInDependency("SFM_custom_mission_v2", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static ConfigEntry<float> MoveSpeed, LookSpeed, MaxDistance, MinFov, MaxFov;
    internal static ConfigEntry<bool> FreezeTime, ShowHelp;

    public override void Load()
    {
        Logger = Log;
        MoveSpeed = Config.Bind("Controls", "MoveSpeed", 1.5f, "Camera move speed in metres per second (Shift = 3x, Alt = 0.3x).");
        LookSpeed = Config.Bind("Controls", "LookSpeed", 0.12f, "Mouse look sensitivity.");
        MaxDistance = Config.Bind("Limits", "MaxDistance", 4f, "How far (metres) the photo camera may move away from Manaka.");
        MinFov = Config.Bind("Limits", "MinFov", 12f, "Most zoomed in.");
        MaxFov = Config.Bind("Limits", "MaxFov", 90f, "Most zoomed out.");
        FreezeTime = Config.Bind("General", "FreezeTime", true, "Freeze the world (NPCs, animations) while framing the photo.");
        ShowHelp = Config.Bind("General", "ShowHelp", true, "Show the controls text (toggle in photo mode with H).");

        Engine.RegisterFunction("PhotoModeBegin", PhotoModeBegin);
        Engine.RegisterFunction("PhotoModeState", PhotoModeState);
        Engine.RegisterFunction("PhotoModeResult", PhotoModeResult);
        var harmony = new Harmony("PhotoMode");
        harmony.PatchAll(typeof(BlockGameInput));
        harmony.PatchAll(typeof(FreezePlayerLookAt));
        // Optional: photo mode's own LateUpdate holds the head too, this just makes sure it wins if the look-at runs later
        try { harmony.PatchAll(typeof(HoldPlayerHead)); }
        catch (Exception e) { Logger.LogWarning("Photo mode: look-at hook not applied, head is held from LateUpdate only: " + e.Message); }
        AddComponent<PhotoModeBehaviour>();
        Logger.LogInfo("Photo Mode loaded");
    }

    public static ProgramValue PhotoModeBegin(ProgramVariables p, ProgramThreadBase thread)
    {
        Vector3 anchor = new(Get(p, "x"), Get(p, "y"), Get(p, "z"));
        float fov = p.HasVariable("fov") ? (float)p.GetVariable("fov").ToFloat() : 60f;
        return PhotoModeBehaviour.Instance != null && PhotoModeBehaviour.Instance.Begin(anchor, fov) ? ProgramValue.True : ProgramValue.False;
    }

    public static ProgramValue PhotoModeState(ProgramVariables p, ProgramThreadBase thread) =>
        new ProgramValue(PhotoModeBehaviour.Instance?.State ?? "idle");

    public static ProgramValue PhotoModeResult(ProgramVariables p, ProgramThreadBase thread)
    {
        var b = PhotoModeBehaviour.Instance;
        if (b == null || b.State != "confirmed") return ProgramValue.Null;
        b.State = "idle";
        Vector3 dir = b.ResultRot * Vector3.forward;
        var r = new ProgramValue(ProgramValue.Type.List);
        r["x"] = new ProgramValue((double)b.ResultPos.x);
        r["y"] = new ProgramValue((double)b.ResultPos.y);
        r["z"] = new ProgramValue((double)b.ResultPos.z);
        r["rx"] = new ProgramValue((double)b.ResultRot.x);
        r["ry"] = new ProgramValue((double)b.ResultRot.y);
        r["rz"] = new ProgramValue((double)b.ResultRot.z);
        r["rw"] = new ProgramValue((double)b.ResultRot.w);
        r["dx"] = new ProgramValue((double)dir.x);
        r["dy"] = new ProgramValue((double)dir.y);
        r["dz"] = new ProgramValue((double)dir.z);
        r["fov"] = new ProgramValue((double)b.ResultFov);
        r["aspect"] = new ProgramValue((double)b.ResultAspect);
        return r;
    }

    private static float Get(ProgramVariables p, string name) => p.HasVariable(name) ? (float)p.GetVariable(name).ToFloat() : 0f;
}

// While photo mode is active, the game receives no input at all: Manaka can't move,
// look around, crouch, open her coat or open menus.
[HarmonyPatch(typeof(InputResolver), "Resolve")]
internal static class BlockGameInput
{
    private static PropertyInfo[] bools, vec2s, vec3s;

    private static void Postfix(InputResolver __instance, InputData __result)
    {
        if (PhotoModeBehaviour.Instance == null || PhotoModeBehaviour.Instance.State != "active") return;
        if (bools == null)
        {
            var props = typeof(InputData).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToArray();
            bools = props.Where(p => p.PropertyType == typeof(bool)).ToArray();
            vec2s = props.Where(p => p.PropertyType == typeof(Vector2)).ToArray();
            vec3s = props.Where(p => p.PropertyType == typeof(Vector3)).ToArray();
        }
        Clear(__instance.inputData);
        if (__result != null) Clear(__result);
    }

    private static void Clear(InputData d)
    {
        if (d == null) return;
        foreach (var p in bools) p.SetValue(d, false);
        foreach (var p in vec2s) p.SetValue(d, Vector2.zero);
        foreach (var p in vec3s) p.SetValue(d, Vector3.zero);
    }
}

// Manaka's head normally follows the camera direction; keep it still while framing a photo
[HarmonyPatch(typeof(LookAtController), "UpdateForPlayer")]
internal static class FreezePlayerLookAt
{
    private static bool Prefix() => PhotoModeBehaviour.Instance == null || PhotoModeBehaviour.Instance.State != "active";
}

// Skipping UpdateForPlayer only stops her picking a new target; the look-at rig keeps easing her head
// toward the old one. So after the look-at has run, put the head back where it was when photo mode opened.
[HarmonyPatch(typeof(LookAtController), "OnLateUpdate")]
internal static class HoldPlayerHead
{
    private static void Postfix()
    {
        var b = PhotoModeBehaviour.Instance;
        if (b != null && b.State == "active") b.HoldHead();
    }
}

public class PhotoModeBehaviour : MonoBehaviour
{
    public PhotoModeBehaviour(IntPtr ptr) : base(ptr) { }

    public static PhotoModeBehaviour Instance;
    public string State = "idle";       // idle | active | confirmed | cancelled
    public Vector3 ResultPos;
    public Quaternion ResultRot;
    public float ResultFov;             // vertical FOV for a camera with ResultAspect
    public float ResultAspect = 1f;     // width / height of the photo

    // Framing presets: square, portrait (upright), landscape (sideways)
    private static readonly (string name, float aspect)[] Frames = { ("Square", 1f), ("Portrait", 3f / 4f), ("Landscape", 4f / 3f) };
    private int frame;

    private GameObject previewObj;
    private Camera previewCam;
    private Vector3 anchor, pos;
    private float yaw, pitch, fov;
    private int startFrame;
    private float oldTimeScale = 1f;
    private CursorLockMode oldLock;
    private bool oldCursorVisible;
    private Texture2D black, white;
    private GUIStyle style;

    private void Awake() { Instance = this; }

    private void Start()
    {
        black = SolidTexture(new Color(0, 0, 0, 1));
        white = SolidTexture(new Color(1, 1, 1, 1));
    }

    public bool Begin(Vector3 manaka, float startFov) => Begin(manaka, startFov, Plugin.FreezeTime.Value);

    // freeze = true: time stops (NPCs, animations); false: the world keeps moving, only Manaka is held still
    public bool Begin(Vector3 manaka, float startFov, bool freeze)
    {
        if (State == "active") return false;
        var controller = PatchBaseCameraController.instance;
        if (controller == null) { Plugin.Logger.LogWarning("Photo mode: no game camera yet"); return false; }
        Camera gameCam = controller.camera;
        if (gameCam == null) { Plugin.Logger.LogWarning("Photo mode: game camera has no Camera component"); return false; }

        // Same recipe Custom Missions uses for its snapshot camera, so the preview matches the photo
        previewObj = Instantiate(controller.gameObject);
        previewObj.name = "PhotoModePreview";
        previewObj.tag = "Untagged";   // so the game never treats the photo camera as its main camera
        DestroyImmediate(previewObj.GetComponent<BaseCameraController>());
        var listener = previewObj.GetComponent<AudioListener>(); if (listener != null) DestroyImmediate(listener);
        var col = previewObj.GetComponent<SphereCollider>(); if (col != null) DestroyImmediate(col);
        var rb = previewObj.GetComponent<Rigidbody>(); if (rb != null) DestroyImmediate(rb);
        previewCam = previewObj.GetComponent<Camera>();
        previewCam.CopyFrom(gameCam);
        previewCam.cullingMask = 205520855;
        previewCam.farClipPlane = 1000f;
        previewCam.clearFlags = CameraClearFlags.Skybox;
        previewCam.depth = gameCam.depth + 10;
        var ppl = previewObj.GetComponent<PostProcessLayer>(); if (ppl != null) ppl.fog.enabled = false;

        anchor = manaka;
        pos = gameCam.transform.position;
        Vector3 e = gameCam.transform.rotation.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180 ? e.x - 360 : e.x;
        fov = Mathf.Clamp(startFov > 0 ? startFov : 60f, Plugin.MinFov.Value, Plugin.MaxFov.Value);
        ApplyPreview();

        oldLock = Cursor.lockState; oldCursorVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        frozen = freeze;
        if (frozen) { oldTimeScale = Time.timeScale; Time.timeScale = 0f; }

        // Stop the game camera reacting to the mouse (it turns Manaka to face the camera)
        gameController = controller;
        controller.enabled = false;
        // ...and hold Manaka exactly where she is
        var pc = PlayerController.Instance;
        player = pc != null ? pc.transform : null;
        if (player != null) { playerPos = player.position; playerRot = player.rotation; }

        // In time stop, freeze Manaka too: her animations run on real time, so pause them directly
        pausedAnimators.Clear();
        heldBones.Clear();
        if (player != null)
        {
            var anims = player.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                var a = anims[i];
                if (a == null) continue;
                // Her head and neck exactly as on screen now (last frame's final pose, look-at included)
                if (heldBones.Count == 0 && a.isHuman)
                    foreach (var bone in new[] { HumanBodyBones.Neck, HumanBodyBones.Head })
                    {
                        var t = a.GetBoneTransform(bone);
                        if (t != null) heldBones.Add((t, t.localRotation));
                    }
                if (!frozen) continue;
                pausedAnimators.Add((a, a.speed));
                a.speed = 0f;
            }
        }
        // ...and everyone else: NPC animations can run on real time as well
        if (frozen) PauseAllAnimators();

        startFrame = Time.frameCount;
        State = "active";
        return true;
    }

    public bool Frozen => frozen;
    private bool frozen;
    private readonly System.Collections.Generic.List<(Animator anim, float speed)> pausedAnimators = new();
    private readonly System.Collections.Generic.List<(Transform bone, Quaternion rot)> heldBones = new();
    private BaseCameraController gameController;
    private Transform player;
    private Vector3 playerPos;
    private Quaternion playerRot;

    // Pause every animator in the scene that isn't paused yet (NPCs, including ones that spawn during the time stop)
    private void PauseAllAnimators()
    {
        var all = UnityEngine.Object.FindObjectsOfType<Animator>();
        for (int i = 0; i < all.Length; i++)
        {
            var a = all[i];
            if (a == null || a.speed == 0f) continue;
            bool known = false;
            foreach (var p in pausedAnimators) if (p.anim == a) { known = true; break; }
            if (known) continue;
            pausedAnimators.Add((a, a.speed));
            a.speed = 0f;
        }
    }

    private bool warnedTimeScale;

    // The game sets its own time scale (slow-motion etc.) - in time stop keep forcing it back to 0
    private void KeepTimeStopped()
    {
        if (!frozen) return;
        if (Time.timeScale != 0f)
        {
            if (!warnedTimeScale) { Plugin.Logger.LogInfo($"PhotoMode: the game changed the time scale to {Time.timeScale} during time stop - holding it at 0"); warnedTimeScale = true; }
            Time.timeScale = 0f;
        }
        if (Time.frameCount % 20 == 0) PauseAllAnimators();
    }

    private void LateUpdate()
    {
        if (State != "active") return;
        KeepTimeStopped();
        if (player == null) return;
        if (player.position != playerPos) player.position = playerPos;
        if (player.rotation != playerRot) player.rotation = playerRot;
        HoldHead();
    }

    // Undo any head turn from the game's look-at (it runs after the animation, so this must too)
    public void HoldHead()
    {
        foreach (var (bone, rot) in heldBones)
            if (bone != null) bone.localRotation = rot;
    }

    private void Update()
    {
        if (State != "active") return;
        if (previewCam == null) { End("cancelled"); return; }
        KeepTimeStopped();

        var kb = Keyboard.current;
        var mouse = Mouse.current;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        bool armed = Time.frameCount > startFrame + 1; // ignore the key press that opened photo mode

        if (mouse != null)
        {
            Vector2 d = mouse.delta.ReadValue();
            float sens = Plugin.LookSpeed.Value * (fov / 60f);
            yaw += d.x * sens;
            pitch = Mathf.Clamp(pitch - d.y * sens, -85f, 85f);

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                fov = Mathf.Clamp(fov * Mathf.Pow(0.9f, Mathf.Sign(scroll)), Plugin.MinFov.Value, Plugin.MaxFov.Value);

            if (armed && mouse.leftButton.wasPressedThisFrame) { Confirm(); return; }
            if (armed && mouse.rightButton.wasPressedThisFrame) { End("cancelled"); return; }
        }

        if (kb != null)
        {
            if (armed && (kb.bKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) { Confirm(); return; }
            if (armed && kb.backspaceKey.wasPressedThisFrame) { End("cancelled"); return; }
            if (kb.tabKey.wasPressedThisFrame) frame = (frame + 1) % Frames.Length;
            if (kb.digit1Key.wasPressedThisFrame) frame = 0;
            if (kb.digit2Key.wasPressedThisFrame) frame = 1;
            if (kb.digit3Key.wasPressedThisFrame) frame = 2;
            if (kb.f1Key.wasPressedThisFrame) Plugin.ShowHelp.Value = !Plugin.ShowHelp.Value;

            Quaternion look = Quaternion.Euler(pitch, yaw, 0);
            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move += look * Vector3.forward;
            if (kb.sKey.isPressed) move += look * Vector3.back;
            if (kb.dKey.isPressed) move += look * Vector3.right;
            if (kb.aKey.isPressed) move += look * Vector3.left;
            if (kb.spaceKey.isPressed || kb.eKey.isPressed) move += Vector3.up;
            if (kb.leftCtrlKey.isPressed || kb.qKey.isPressed) move += Vector3.down;
            if (kb.zKey.isPressed) fov = Mathf.Clamp(fov - 25f * dt, Plugin.MinFov.Value, Plugin.MaxFov.Value);
            if (kb.xKey.isPressed) fov = Mathf.Clamp(fov + 25f * dt, Plugin.MinFov.Value, Plugin.MaxFov.Value);
            float speed = Plugin.MoveSpeed.Value * (kb.leftShiftKey.isPressed ? 3f : 1f) * (kb.leftAltKey.isPressed ? 0.3f : 1f);
            pos += move.normalized * speed * dt;
        }

        Vector3 off = pos - anchor;
        float max = Mathf.Max(0.5f, Plugin.MaxDistance.Value);
        if (off.magnitude > max) pos = anchor + off.normalized * max;

        ApplyPreview();
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
    }

    private void ApplyPreview()
    {
        previewObj.transform.position = pos;
        previewObj.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        previewCam.fieldOfView = fov;
    }

    // The part of the screen the photo will contain, for the current framing
    private Rect CropRect()
    {
        float w = Screen.width, h = Screen.height, a = Frames[frame].aspect;
        float cw = h * a, ch = h;
        if (cw > w) { cw = w; ch = w / a; }
        return new Rect((w - cw) / 2f, (h - ch) / 2f, cw, ch);
    }

    private void Confirm()
    {
        var crop = CropRect();
        ResultPos = pos;
        ResultRot = Quaternion.Euler(pitch, yaw, 0);
        ResultAspect = Frames[frame].aspect;
        // The preview's vertical FOV covers the full screen height; scale it to the cropped height
        float t = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (crop.height / Screen.height);
        ResultFov = 2f * Mathf.Atan(t) * Mathf.Rad2Deg;
        End("confirmed");
    }

    private void End(string result)
    {
        if (previewObj != null) Destroy(previewObj);
        previewObj = null; previewCam = null;
        if (frozen) Time.timeScale = oldTimeScale <= 0f ? 1f : oldTimeScale;
        warnedTimeScale = false;
        foreach (var (anim, speed) in pausedAnimators)
            if (anim != null) anim.speed = speed;
        pausedAnimators.Clear();
        heldBones.Clear();
        frozen = false;
        if (gameController != null) gameController.enabled = true;
        gameController = null;
        player = null;
        Cursor.lockState = oldLock; Cursor.visible = oldCursorVisible;
        State = result;
    }

    private void OnGUI()
    {
        if (State != "active" || Event.current.type != EventType.Repaint) return;
        float w = Screen.width, h = Screen.height;
        var c = CropRect();

        // Lightly dim what won't be in the photo, and draw a thin frame - nothing over the shot itself
        GUI.color = new Color(0, 0, 0, 0.35f);
        if (c.x > 0) { GUI.DrawTexture(new Rect(0, 0, c.x, h), black); GUI.DrawTexture(new Rect(c.xMax, 0, w - c.xMax, h), black); }
        if (c.y > 0) { GUI.DrawTexture(new Rect(c.x, 0, c.width, c.y), black); GUI.DrawTexture(new Rect(c.x, c.yMax, c.width, h - c.yMax), black); }
        GUI.color = new Color(1, 1, 1, 0.6f);
        float t = 2f;
        GUI.DrawTexture(new Rect(c.x, c.y, c.width, t), white);
        GUI.DrawTexture(new Rect(c.x, c.yMax - t, c.width, t), white);
        GUI.DrawTexture(new Rect(c.x, c.y, t, c.height), white);
        GUI.DrawTexture(new Rect(c.xMax - t, c.y, t, c.height), white);

        GUI.color = Color.white;
        int fs = Mathf.Max(13, (int)(h / 70));
        if (style == null || style.fontSize != fs)
        {
            style = new GUIStyle { fontSize = fs, richText = true, wordWrap = true, alignment = TextAnchor.UpperRight };
            style.normal.textColor = Color.white;
        }
        string head = $"<b>{Frames[frame].name}</b>   zoom {60f / fov:F1}x{(frozen ? "   <color=#9fd7ff><b>TIME STOP</b></color>" : "")}";
        string help = Plugin.ShowHelp.Value
            ? "\nMouse look  WASD move  Space/E up  Ctrl/Q down  Shift fast  Alt slow  Scroll/Z/X zoom\nTab or 1/2/3: square / portrait / landscape   F1: hide/show tips\n<b>Click / B / Enter: take photo</b>   Right click / Backspace: cancel"
            : "";
        // Top-right corner of the screen
        float tw = Mathf.Min(w * 0.45f, fs * 40f);
        GUI.Label(new Rect(w - tw - 12, 10, tw, fs * 6f), head + help, style);
    }

    private static Texture2D SolidTexture(Color col)
    {
        var tx = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        tx.SetPixel(0, 0, col);
        tx.Apply();
        return tx;
    }
}
