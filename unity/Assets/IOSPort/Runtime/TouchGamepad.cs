using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace IOSPort
{
    /// <summary>
    /// On-screen gamepad.  Touches are hit-tested against the drawn controls and
    /// converted into the state of a virtual <see cref="Gamepad"/> device, so the
    /// game's existing gamepad bindings work unchanged.  The overlay never
    /// receives UI raycasts, and hides itself when a real controller connects.
    /// </summary>
    public sealed class TouchGamepad : MonoBehaviour
    {
        enum Kind { Stick, Button }

        sealed class Control
        {
            public Kind kind;
            public string label;
            public GamepadButton button;
            public bool isTrigger;          // drives an analog trigger instead of a button bit
            public bool leftTrigger;
            public Vector2 anchor;          // normalized position inside the safe area (0..1)
            public float radius;            // in reference pixels (1080p height)
            public RectTransform rect;
            public Image image;
            public RectTransform knob;
            public int touchId = -1;
            public Vector2 stickValue;
            public bool pressed;
        }

        const float RefHeight = 1080f;
        const float IdleAlpha = 0.28f;
        const float PressedAlpha = 0.6f;

        static TouchGamepad instance;
        Gamepad pad;
        Canvas canvas;
        RectTransform root;
        readonly List<Control> controls = new List<Control>();
        Rect lastSafeArea;
        Vector2Int lastScreen;
        Sprite circle;
        Font font;
        bool visible = true;

        public static void Create()
        {
            if (instance != null) return;
            var go = new GameObject("IOSTouchGamepad");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<TouchGamepad>();
        }

        void Awake()
        {
            pad = InputSystem.AddDevice<Gamepad>("IOSTouchGamepad");
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            circle = MakeCircleSprite(128);

            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var rootGo = new GameObject("SafeArea", typeof(RectTransform));
            rootGo.transform.SetParent(transform, false);
            root = (RectTransform)rootGo.transform;

            // left side
            Add(Kind.Stick, "", default, new Vector2(0.14f, 0.24f), 150);
            // face buttons (Xbox style positions: A bottom, B right, X left, Y top)
            Add(Kind.Button, "A", GamepadButton.South, new Vector2(0.86f, 0.14f), 62);
            Add(Kind.Button, "B", GamepadButton.East, new Vector2(0.94f, 0.30f), 62);
            Add(Kind.Button, "X", GamepadButton.West, new Vector2(0.78f, 0.30f), 62);
            Add(Kind.Button, "Y", GamepadButton.North, new Vector2(0.86f, 0.46f), 62);
            // shoulders / triggers
            Add(Kind.Button, "LB", GamepadButton.LeftShoulder, new Vector2(0.06f, 0.62f), 50);
            Add(Kind.Button, "LT", GamepadButton.LeftTrigger, new Vector2(0.06f, 0.80f), 50, trigger: true, left: true);
            Add(Kind.Button, "RB", GamepadButton.RightShoulder, new Vector2(0.94f, 0.62f), 50);
            Add(Kind.Button, "RT", GamepadButton.RightTrigger, new Vector2(0.94f, 0.80f), 50, trigger: true);
            // menu buttons
            Add(Kind.Button, "SEL", GamepadButton.Select, new Vector2(0.42f, 0.93f), 40);
            Add(Kind.Button, "START", GamepadButton.Start, new Vector2(0.58f, 0.93f), 40);

            Layout();
            InputSystem.onDeviceChange += OnDeviceChange;
            RefreshVisibility();
        }

        void OnDestroy()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
            if (pad != null && pad.added) InputSystem.RemoveDevice(pad);
            if (instance == this) instance = null;
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is Gamepad && device != pad) RefreshVisibility();
        }

        void RefreshVisibility()
        {
            bool physical = false;
            foreach (var d in InputSystem.devices)
                if (d is Gamepad && d != pad) physical = true;
            visible = !physical;
            root.gameObject.SetActive(visible);
        }

        void Add(Kind kind, string label, GamepadButton button, Vector2 anchor, float radius,
            bool trigger = false, bool left = false)
        {
            var c = new Control
            {
                kind = kind, label = label, button = button, anchor = anchor, radius = radius,
                isTrigger = trigger, leftTrigger = left
            };
            var go = new GameObject(kind == Kind.Stick ? "Stick" : label, typeof(RectTransform));
            go.transform.SetParent(root, false);
            c.rect = (RectTransform)go.transform;
            c.rect.anchorMin = c.rect.anchorMax = Vector2.zero;
            c.image = go.AddComponent<Image>();
            c.image.sprite = circle;
            c.image.raycastTarget = false;
            c.image.color = new Color(1, 1, 1, IdleAlpha);
            if (kind == Kind.Stick)
            {
                var knob = new GameObject("Knob", typeof(RectTransform));
                knob.transform.SetParent(go.transform, false);
                c.knob = (RectTransform)knob.transform;
                var ki = knob.AddComponent<Image>();
                ki.sprite = circle;
                ki.raycastTarget = false;
                ki.color = new Color(1, 1, 1, 0.45f);
            }
            else
            {
                var t = new GameObject("Label", typeof(RectTransform));
                t.transform.SetParent(go.transform, false);
                var tr = (RectTransform)t.transform;
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = tr.offsetMax = Vector2.zero;
                var text = t.AddComponent<Text>();
                text.font = font;
                text.text = label;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(0, 0, 0, 0.75f);
                text.resizeTextForBestFit = true;
                text.resizeTextMaxSize = 40;
                text.raycastTarget = false;
            }
            controls.Add(c);
        }

        float Scale => Mathf.Max(0.5f, Screen.height / RefHeight);

        void Layout()
        {
            Rect safe = Screen.safeArea;
            lastSafeArea = safe;
            lastScreen = new Vector2Int(Screen.width, Screen.height);
            root.anchorMin = root.anchorMax = Vector2.zero;
            root.pivot = Vector2.zero;
            root.anchoredPosition = safe.position;
            root.sizeDelta = safe.size;
            float s = Scale;
            foreach (var c in controls)
            {
                float r = c.radius * s;
                c.rect.sizeDelta = new Vector2(r * 2, r * 2);
                c.rect.anchoredPosition = new Vector2(c.anchor.x * safe.width, c.anchor.y * safe.height);
                if (c.knob != null) c.knob.sizeDelta = new Vector2(r * 0.9f, r * 0.9f);
            }
        }

        Vector2 CenterOnScreen(Control c)
        {
            Rect safe = lastSafeArea;
            return new Vector2(safe.x + c.anchor.x * safe.width, safe.y + c.anchor.y * safe.height);
        }

        void Update()
        {
            if (Screen.safeArea != lastSafeArea || Screen.width != lastScreen.x || Screen.height != lastScreen.y)
                Layout();
            if (!visible || pad == null) return;

            var ts = Touchscreen.current;
            float s = Scale;
            foreach (var c in controls) c.pressed = false;

            if (ts != null)
            {
                foreach (var touch in ts.touches)
                {
                    var phase = touch.phase.ReadValue();
                    if (phase == UnityEngine.InputSystem.TouchPhase.None) continue;
                    int id = touch.touchId.ReadValue();
                    bool ended = phase == UnityEngine.InputSystem.TouchPhase.Ended ||
                                 phase == UnityEngine.InputSystem.TouchPhase.Canceled;
                    Vector2 pos = touch.position.ReadValue();

                    Control owner = null;
                    foreach (var c in controls)
                        if (c.touchId == id) { owner = c; break; }
                    if (ended)
                    {
                        if (owner != null)
                        {
                            owner.touchId = -1;
                            owner.stickValue = Vector2.zero;
                        }
                        continue;
                    }
                    if (owner == null)
                    {
                        // a new touch grabs the closest control under the finger
                        float best = float.MaxValue;
                        foreach (var c in controls)
                        {
                            if (c.touchId != -1) continue;
                            float r = c.radius * s * (c.kind == Kind.Stick ? 1.4f : 1.15f);
                            float d = Vector2.Distance(pos, CenterOnScreen(c));
                            if (d < r && d < best)
                            {
                                best = d;
                                owner = c;
                            }
                        }
                        if (owner == null) continue;
                        owner.touchId = id;
                    }
                    if (owner.kind == Kind.Stick)
                    {
                        Vector2 delta = (pos - CenterOnScreen(owner)) / (owner.radius * s);
                        owner.stickValue = Vector2.ClampMagnitude(delta, 1f);
                    }
                    owner.pressed = true;
                }
            }

            var state = new GamepadState();
            foreach (var c in controls)
            {
                if (c.kind == Kind.Stick)
                {
                    Vector2 v = c.touchId != -1 ? c.stickValue : Vector2.zero;
                    if (v.magnitude < 0.15f) v = Vector2.zero;
                    state.leftStick = v;
                    if (v.y > 0.5f) state = state.WithButton(GamepadButton.DpadUp);
                    if (v.y < -0.5f) state = state.WithButton(GamepadButton.DpadDown);
                    if (v.x < -0.5f) state = state.WithButton(GamepadButton.DpadLeft);
                    if (v.x > 0.5f) state = state.WithButton(GamepadButton.DpadRight);
                    if (c.knob != null) c.knob.anchoredPosition = v * c.radius * s * 0.6f;
                    c.image.color = new Color(1, 1, 1, c.touchId != -1 ? PressedAlpha : IdleAlpha);
                    continue;
                }
                bool down = c.pressed && c.touchId != -1;
                if (down)
                {
                    // triggers are analog axes in GamepadState (their GamepadButton values
                    // are not bit indices), everything else is a button bit
                    if (c.isTrigger)
                    {
                        if (c.leftTrigger) state.leftTrigger = 1f; else state.rightTrigger = 1f;
                    }
                    else state = state.WithButton(c.button);
                }
                c.image.color = new Color(1, 1, 1, down ? PressedAlpha : IdleAlpha);
            }
            InputSystem.QueueStateEvent(pad, state);
        }

        static Sprite MakeCircleSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    float a = Mathf.Clamp01(r - d);
                    float ring = d > r * 0.86f ? 1f : 0.55f;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * ring * 255));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
