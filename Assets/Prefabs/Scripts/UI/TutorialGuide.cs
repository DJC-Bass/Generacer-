using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// On-screen guide for the Tutorial scene: a series of short instruction messages near the top-centre
/// of the screen. On entering the Tutorial scene the first message shows; each auto-advances after
/// <see cref="TutorialGuideConfig.messageDuration"/> (~3s). The player can scrub with D-pad left/right.
///
/// While any menu is open (<see cref="MenuState.AnyOpen"/> — e.g. the Start menu) the guide hides and
/// its timer PAUSES, resuming where it left off when the menu closes. Turning the guide off in the
/// Start menu's SETTINGS panel (<see cref="TutorialSettings.GuideEnabled"/>) hides it the same way.
/// The messages loop: after the last one it cycles back to the first (and D-pad left from the first
/// wraps to the last), so the guide keeps running the whole time you're in the tutorial scene.
///
/// Persistent + bootstrapped on the PlayerSystems object (like the HUDs); it does nothing outside the
/// configured scene. Code-built UI, no scene setup required. Content/look come from
/// <c>Resources/TutorialGuideConfig</c> (falls back to <see cref="TutorialGuideConfig"/> defaults).
/// </summary>
[DefaultExecutionOrder(1000)]
public class TutorialGuide : MonoBehaviour
{
    public static TutorialGuide Instance { get; private set; }

    private TutorialGuideConfig cfg;
    private GameObject canvasGO;
    private GameObject panel;
    private TextMeshProUGUI messageText;
    private TextMeshProUGUI hintText;

    private int index;
    private float timer;
    private bool active;   // running in the tutorial scene and not yet completed

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        cfg = Resources.Load<TutorialGuideConfig>("TutorialGuideConfig");
        if (cfg == null) cfg = ScriptableObject.CreateInstance<TutorialGuideConfig>();   // defaults

        BuildUI();
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // sceneLoaded doesn't fire for the scene already active when we bootstrap, so also evaluate here.
    void Start() => BeginForScene(SceneManager.GetActiveScene().name);
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => BeginForScene(scene.name);

    /// <summary>Arms the guide from the top when the configured scene loads; disarms it elsewhere.
    /// The panel is always left hidden here — <see cref="Update"/> reveals it when appropriate, so a
    /// disabled guide (or one loaded under an open menu) never flashes on for a frame.</summary>
    void BeginForScene(string sceneName)
    {
        active = cfg != null && sceneName == cfg.sceneName
                 && cfg.messages != null && cfg.messages.Length > 0;
        index = 0;
        timer = 0f;
        Hide();
    }

    void Update()
    {
        if (!active) return;

        // Turned off in Settings, or a menu is up (the Start menu supersedes the guide): hide and
        // FREEZE the timer, so it resumes from the same message when we're visible again.
        if (!TutorialSettings.GuideEnabled || MenuState.AnyOpen)
        {
            if (panel.activeSelf) panel.SetActive(false);
            return;
        }

        if (!panel.activeSelf) ShowCurrent();   // returning from a pause: re-show without resetting the timer

        // D-pad scrubbing. Left/right land on a message and restart its dwell.
        var gp = Gamepad.current;
        if (gp != null)
        {
            if (gp.dpad.left.wasPressedThisFrame) { Step(-1); return; }
            if (gp.dpad.right.wasPressedThisFrame) { Step(1); return; }
        }

        // Auto-advance after the dwell.
        timer += Time.deltaTime;
        if (timer >= Mathf.Max(0.1f, cfg.messageDuration))
            Step(1);
    }

    /// <summary>Moves by <paramref name="dir"/> messages: past the last one completes (hides) the
    /// <summary>Moves by <paramref name="dir"/> messages, WRAPPING both ways: past the last loops to
    /// the first, and left from the first goes to the last. Resets the dwell on the message landed on
    /// so the guide cycles forever while in the tutorial scene.</summary>
    void Step(int dir)
    {
        int count = cfg.messages.Length;
        if (count <= 0) return;

        index = ((index + dir) % count + count) % count;   // wrap in both directions
        timer = 0f;
        ShowCurrent();
    }

    void ShowCurrent()
    {
        if (cfg.messages == null || index < 0 || index >= cfg.messages.Length) { Hide(); return; }
        panel.SetActive(true);
        messageText.text = cfg.messages[index];
        hintText.text = $"◄  ►    {index + 1}/{cfg.messages.Length}";
    }

    void Hide()
    {
        if (panel != null && panel.activeSelf) panel.SetActive(false);
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void BuildUI()
    {
        canvasGO = new GameObject("TutorialGuideCanvas");
        DontDestroyOnLoad(canvasGO);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 140;   // below the menus (300); we also hide the guide while a menu is open
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Panel pinned to the top-centre, offset down from the top edge.
        panel = new GameObject("TutorialPanel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvasGO.transform, false);
        panel.GetComponent<Image>().color = cfg.panelColor;
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 1f);   // top-centre
        prt.sizeDelta = cfg.panelSize;
        prt.anchoredPosition = new Vector2(0f, -cfg.topOffset);

        // Message text fills the panel (leaving room at the bottom for the hint).
        messageText = NewText(panel.transform, "Message", cfg.messageFontSize, TextAlignmentOptions.Center);
        messageText.color = cfg.messageColor;
        messageText.enableWordWrapping = true;
        var mrt = messageText.rectTransform;
        mrt.anchorMin = new Vector2(0f, 0f); mrt.anchorMax = new Vector2(1f, 1f);
        mrt.offsetMin = new Vector2(28f, 34f); mrt.offsetMax = new Vector2(-28f, -12f);

        // Hint / progress footer along the bottom of the panel.
        hintText = NewText(panel.transform, "Hint", cfg.hintFontSize, TextAlignmentOptions.Center);
        hintText.color = cfg.hintColor;
        var hrt = hintText.rectTransform;
        hrt.anchorMin = new Vector2(0f, 0f); hrt.anchorMax = new Vector2(1f, 0f);
        hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(0f, 30f);
        hrt.anchoredPosition = new Vector2(0f, 8f);

        panel.SetActive(false);
    }

    static TextMeshProUGUI NewText(Transform parent, string name, float fontSize, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        return t;
    }
}
