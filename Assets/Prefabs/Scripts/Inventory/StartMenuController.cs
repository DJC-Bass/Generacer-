using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// In-game "start menu" (like a pause menu, but it does NOT pause the game — no Time.timeScale
/// change, so the car, AI and round timer keep running behind it). Toggled with the gamepad Start
/// button while in a gameplay scene (HubWorld / TrackScene). Lists RESUME, AUDIO, CONTROLS,
/// SETTINGS, QUIT top-to-bottom; A selects, B backs out (closes), Start closes. RESUME closes,
/// QUIT returns to the main menu.
///
/// Persistent + bootstrapped on the PlayerSystems object, and reuses <see cref="MenuState"/> so
/// while it's open the gamepad's A/B presses don't also drive the car (Jump/Turbo/Brake are
/// suppressed) — the same mechanism the store / inventory menus use. Late execution order keeps
/// the toggling press from leaking into a driving action that frame.
/// </summary>
[DefaultExecutionOrder(1000)]
public class StartMenuController : MonoBehaviour
{
    public static StartMenuController Instance { get; private set; }

    [Tooltip("Scene QUIT returns to.")]
    public string mainMenuSceneName = "MainMenu";

    private GameObject root;          // canvas root, toggled active
    private GameObject firstButton;   // RESUME — focused on open
    private bool isOpen;
    private bool built;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // Never carry the menu across a scene load (e.g. QUIT, or returning to the hub).
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) { if (isOpen) Close(); }

    void Update()
    {
        var gp = Gamepad.current;
        if (gp == null) return;

        // Start toggles the menu. Only open while in a gameplay scene and no other menu is up.
        if (gp.startButton.wasPressedThisFrame)
        {
            if (isOpen) Close();
            else if (!MenuState.AnyOpen && GameplayHud.VisibleInScene(SceneManager.GetActiveScene().name)) Open();
            return;
        }

        // B backs out — at the top level that's the same as RESUME (close the menu).
        if (isOpen && gp.buttonEast.wasPressedThisFrame)
            Close();
    }

    void Open()
    {
        EnsureUI();
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;   // stop A/B from also jumping/turbo-ing the car (game still runs)
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(firstButton);
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        isOpen = false;
        MenuState.AnyOpen = false;
    }

    // -------------------------------------------------------
    //  Button actions
    // -------------------------------------------------------

    void OnResume() => Close();

    void OnAudio() => Debug.Log("[StartMenu] Audio — not implemented yet.");
    void OnControls() => Debug.Log("[StartMenu] Controls — not implemented yet.");
    void OnSettings() => Debug.Log("[StartMenu] Settings — not implemented yet.");

    void OnQuit()
    {
        Close();
        if (!string.IsNullOrEmpty(mainMenuSceneName) && Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
            SceneManager.LoadScene(mainMenuSceneName);
        else
            Debug.LogWarning($"[StartMenu] Quit: scene '{mainMenuSceneName}' isn't in Build Settings.");
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void EnsureUI()
    {
        if (built) return;
        built = true;

        EnsureEventSystem();

        root = new GameObject("StartMenuCanvas");
        DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 300;   // above the HUDs (150) and inventory (250)
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();

        // Dim full-screen backdrop (the game stays visible — and running — behind it).
        var dim = NewUI("Dim", root.transform);
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.6f);
        var drt = dim.GetComponent<RectTransform>();
        drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one;
        drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;

        var title = NewText(root.transform, "Title", 64, TextAlignmentOptions.Center);
        title.text = "MENU";
        title.fontStyle = FontStyles.Bold;
        title.color = Color.white;
        var trt = title.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 0.5f);
        trt.sizeDelta = new Vector2(600f, 90f);
        trt.anchoredPosition = new Vector2(0f, 270f);

        // Vertical button column.
        var col = NewUI("Buttons", root.transform);
        var crt = col.GetComponent<RectTransform>();
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
        crt.anchoredPosition = new Vector2(0f, 0f);
        var vlg = col.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 16f;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;
        var fitter = col.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        firstButton = CreateButton("RESUME", col.transform, OnResume).gameObject;
        CreateButton("AUDIO", col.transform, OnAudio);
        CreateButton("CONTROLS", col.transform, OnControls);
        CreateButton("SETTINGS", col.transform, OnSettings);
        CreateButton("QUIT", col.transform, OnQuit);

        var hint = NewText(root.transform, "Hint", 26, TextAlignmentOptions.Center);
        hint.text = "A: Select     B: Back     Start: Close";
        hint.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hint.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0.5f);
        hrt.sizeDelta = new Vector2(800f, 50f);
        hrt.anchoredPosition = new Vector2(0f, -330f);

        root.SetActive(false);
    }

    Button CreateButton(string label, Transform parent, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Color.white;   // white graphic so the ColorBlock tints show

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 420f; le.preferredHeight = 76f;
        le.minWidth = 420f; le.minHeight = 76f;

        var btn = go.GetComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = new Color(0f, 0f, 0f, 0.70f);
        cb.highlightedColor = new Color(0.12f, 0.68f, 0.90f, 0.95f);
        cb.selectedColor = new Color(0.12f, 0.68f, 0.90f, 0.95f);
        cb.pressedColor = new Color(0.08f, 0.45f, 0.75f, 1f);
        cb.colorMultiplier = 1f; cb.fadeDuration = 0.1f;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);

        var tmp = NewText(go.transform, "Label", 34, TextAlignmentOptions.Center);
        tmp.text = label; tmp.color = Color.white;
        var trt = tmp.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

        return btn;
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var es = new GameObject("EventSystem");
        DontDestroyOnLoad(es);
        es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static TextMeshProUGUI NewText(Transform parent, string name, int fontSize, TextAlignmentOptions align)
    {
        var go = NewUI(name, parent);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        t.enableWordWrapping = false;
        return t;
    }
}
