using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using TMPro;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Builds the main menu entirely in code (matching the project's code-built UI style): a
/// full-screen background image, the "GENERACER" title in the upper-left, and a vertical stack
/// of buttons beneath it — Start, Online Multiplayer, Tutorial, Settings, Quit.
///
/// Drop this on a single GameObject in the MainMenu scene and assign a placeholder Background
/// Sprite in the Inspector. It also spawns a Camera and an EventSystem (new-Input-System UI
/// module) if the scene doesn't already have them, so it works with zero extra setup.
///
/// Start loads the Car Selection scene (Start Scene Name) — which will lead into the game loop.
/// </summary>
public class MainMenuController : MonoBehaviour
{
    [Header("Background")]
    [Tooltip("Placeholder background shown full-screen behind the menu. Import a PNG as a Sprite " +
             "(Texture Type: Sprite (2D and UI)) and drag it here. Empty = solid Background Color.")]
    public Sprite backgroundSprite;
    [Tooltip("Solid colour used behind the menu when no Background Sprite is assigned.")]
    public Color backgroundColor = new Color(0.05f, 0.06f, 0.10f, 1f);

    [Header("Title")]
    public string gameTitle = "GENERACER";
    [Tooltip("Title font size — large. Buttons are smaller than this.")]
    public float titleFontSize = 140f;
    public Color titleColor = Color.white;

    [Header("Buttons")]
    [Tooltip("Button label font size — smaller than the title.")]
    public float buttonFontSize = 44f;
    [Tooltip("Vertical gap between buttons (pixels).")]
    public float buttonSpacing = 18f;
    [Tooltip("Width/height of each button (pixels).")]
    public Vector2 buttonSize = new Vector2(540f, 84f);

    [Header("Button Colors")]
    [Tooltip("Button colour at rest.")]
    public Color buttonNormalColor = new Color(0f, 0f, 0f, 0.55f);
    [Tooltip("Button colour while the pointer HOVERS over it. Edit this for the hover colour — " +
             "it lives on this menu scene's component, so it's independent of every other scene.")]
    public Color buttonHighlightedColor = new Color(0.90f, 0.45f, 0.12f, 0.95f);
    [Tooltip("Button colour while held down.")]
    public Color buttonPressedColor = new Color(0.70f, 0.32f, 0.07f, 1f);
    [Tooltip("Button colour while selected via gamepad/keyboard navigation.")]
    public Color buttonSelectedColor = new Color(0.90f, 0.45f, 0.12f, 0.95f);
    [Tooltip("Button label text colour.")]
    public Color buttonTextColor = Color.white;
    [Tooltip("Seconds the button takes to fade between colour states.")]
    public float buttonColorFadeDuration = 0.1f;

    [Header("Scene Routing")]
    [Tooltip("Scene that Start loads — the Car Selection screen, which then starts the game loop. " +
             "Must be added to Build Settings before it will load.")]
    public string startSceneName = "CarSelection";
    [Tooltip("Scene that Tutorial loads. Leave blank until the tutorial scene exists.")]
    public string tutorialSceneName = "Tutorial";

    private GameObject firstButton;   // top item — used to rescue navigation from a null selection
    private GameObject lastSelectedForSfx;   // tracks the highlighted item so we can fire the "move" SFX

    void Start()
    {
        EnsureCamera();
        EnsureEventSystem();
        BuildUI();
    }

    void LateUpdate()
    {
        // If nothing is highlighted (e.g. a mouse click cleared the selection), pressing Up/Down
        // re-highlights the top item so D-pad / arrow navigation never soft-locks.
        MenuNavigation.EnsureSelectionOnNavigate(firstButton);

        // Play the "move" SFX whenever the highlighted button changes (list navigation).
        MenuNavigation.PlayMoveSfxOnSelectionChange(ref lastSelectedForSfx);
    }

    // -------------------------------------------------------
    //  UI construction
    // -------------------------------------------------------

    void BuildUI()
    {
        // Canvas (screen-space overlay so it renders with no camera dependency).
        var canvasGO = new GameObject("MainMenuCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        BuildBackground(canvasGO.transform);
        BuildTitle(canvasGO.transform);

        // Buttons stacked top-to-bottom, under the title, on the upper-left.
        var menu = BuildButtonColumn(canvasGO.transform);
        var start = CreateButton("START", menu, OnStart);
        var online = CreateButton("ONLINE MULTIPLAYER", menu, OnOnlineMultiplayer);
        var tutorial = CreateButton("TUTORIAL", menu, OnTutorial);
        var settings = CreateButton("SETTINGS", menu, OnSettings);
        var quit = CreateButton("QUIT", menu, OnQuit);

        // Up on the top entry wraps to the bottom and vice-versa.
        MenuNavigation.WireVerticalWrap(new[] { start, online, tutorial, settings, quit });

        firstButton = start != null ? start.gameObject : null;

        // Pre-select Start so a gamepad/keyboard can navigate the menu immediately.
        if (EventSystem.current != null && firstButton != null)
            EventSystem.current.SetSelectedGameObject(firstButton);
    }

    void BuildBackground(Transform parent)
    {
        var go = new GameObject("Background", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();

        var rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        if (backgroundSprite != null)
        {
            img.sprite = backgroundSprite;
            img.type = Image.Type.Simple;
            img.color = Color.white;          // show the sprite at full colour
        }
        else
        {
            img.color = backgroundColor;      // placeholder solid fill until a PNG is assigned
        }
    }

    void BuildTitle(Transform parent)
    {
        var go = new GameObject("Title", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = gameTitle;
        tmp.fontSize = titleFontSize;
        tmp.color = titleColor;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableWordWrapping = false;

        var rt = tmp.rectTransform;
        rt.anchorMin = new Vector2(0f, 1f);   // upper-left
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(1500f, 240f);
        rt.anchoredPosition = new Vector2(80f, -70f);
    }

    Transform BuildButtonColumn(Transform parent)
    {
        var go = new GameObject("Buttons", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);   // upper-left, just under the title
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(96f, -310f);

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = buttonSpacing;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false;
        vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        return go.transform;
    }

    Button CreateButton(string label, Transform parent, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);

        var img = go.GetComponent<Image>();
        img.color = Color.white;   // white graphic so the Button ColorBlock tints show true colours

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = buttonSize.x;
        le.preferredHeight = buttonSize.y;
        le.minWidth = buttonSize.x;
        le.minHeight = buttonSize.y;

        var btn = go.GetComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = buttonNormalColor;
        cb.highlightedColor = buttonHighlightedColor;   // hover colour (editable in the Inspector)
        cb.selectedColor = buttonSelectedColor;
        cb.pressedColor = buttonPressedColor;
        cb.disabledColor = new Color(0.30f, 0.30f, 0.30f, 0.50f);
        cb.colorMultiplier = 1f;
        cb.fadeDuration = buttonColorFadeDuration;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);
        btn.onClick.AddListener(AudioManager.PlayMenuSelect);   // "select" SFX on click / Submit (A)

        var textGO = new GameObject("Label", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = buttonFontSize;
        tmp.color = buttonTextColor;
        tmp.alignment = TextAlignmentOptions.Center;   // centred horizontally + vertically in the button
        tmp.enableWordWrapping = false;

        var trt = tmp.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;   // fill the button exactly so the centring is true-centre
        trt.offsetMax = Vector2.zero;

        return btn;
    }

    // -------------------------------------------------------
    //  Button actions
    // -------------------------------------------------------

    void OnStart() => LoadScene(startSceneName, "Start");
    void OnTutorial() => LoadScene(tutorialSceneName, "Tutorial");

    void OnOnlineMultiplayer() =>
        Debug.Log("[MainMenu] Online Multiplayer — not implemented yet.");

    void OnSettings() =>
        Debug.Log("[MainMenu] Settings — not implemented yet.");

    void OnQuit()
    {
        Debug.Log("[MainMenu] Quit");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>Loads a scene by name, guarding against an unset name or one that hasn't been
    /// added to Build Settings yet (so a button is a harmless no-op until its scene exists).</summary>
    void LoadScene(string sceneName, string label)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogWarning($"[MainMenu] {label}: no scene name set yet.");
            return;
        }
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning($"[MainMenu] {label}: scene '{sceneName}' isn't in Build Settings yet " +
                             "(File > Build Settings > Add Open Scenes). Button does nothing until then.");
            return;
        }
        SceneManager.LoadScene(sceneName);
    }

    // -------------------------------------------------------
    //  Scene scaffolding (created only if missing)
    // -------------------------------------------------------

    void EnsureCamera()
    {
        if (Camera.main != null) return;
        var go = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = backgroundColor;
        go.AddComponent<AudioListener>();
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        go.AddComponent<InputSystemUIInputModule>();   // new Input System UI driver
#else
        go.AddComponent<StandaloneInputModule>();
#endif
    }
}
