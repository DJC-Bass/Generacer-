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
/// SETTINGS, QUIT top-to-bottom; A selects, B backs out, Start closes. RESUME closes, QUIT returns
/// to the main menu. AUDIO/CONTROLS/SETTINGS open placeholder sub-screens that B backs out of.
///
/// Persistent + bootstrapped on the PlayerSystems object, and reuses <see cref="MenuState"/> so
/// while it's open the gamepad's A/B presses don't also drive the car. Look/layout come from a
/// <see cref="StartMenuConfig"/> asset at <c>Resources/StartMenuConfig</c> (editable), falling back
/// to defaults if absent. Late execution order keeps a toggling press from leaking into driving.
/// </summary>
[DefaultExecutionOrder(1000)]
public class StartMenuController : MonoBehaviour
{
    public static StartMenuController Instance { get; private set; }

    [Tooltip("Scene QUIT returns to.")]
    public string mainMenuSceneName = "MainMenu";

    [Tooltip("Scenes beyond the gameplay set (HubWorld / TrackScene / endings) where Start can open " +
             "this menu — e.g. the Tutorial, which deliberately has no HUDs or game loop but still " +
             "wants the menu so the player can quit out.")]
    public string[] extraScenes = { "Tutorial" };

    private StartMenuConfig cfg;

    private GameObject root;
    private GameObject mainPanel;
    private TextMeshProUGUI titleText;
    private TextMeshProUGUI hintText;
    private GameObject firstButton;            // RESUME — focused on open

    private Button audioBtn, controlsBtn, settingsBtn;
    private GameObject audioPanel, controlsPanel, settingsPanel;
    private Button tutorialToggleBtn;                // SETTINGS: flips the Tutorial guide on/off
    private TextMeshProUGUI tutorialToggleLabel;
    private GameObject currentSub;             // null = on the main list
    private GameObject subReturnButton;        // main button to re-focus when backing out of a sub

    private bool isOpen;
    private bool built;
    private GameObject lastSelectedForSfx;   // tracks the highlighted item so we can fire the "move" SFX

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

        // Start toggles the menu. Only open while in a gameplay scene (or a listed extra scene,
        // like the Tutorial) and no other menu is up.
        if (gp.startButton.wasPressedThisFrame)
        {
            if (isOpen) Close();
            else if (!MenuState.AnyOpen && MenuAvailableInScene(SceneManager.GetActiveScene().name)) Open();
            return;
        }

        // B: step out of a sub-screen back to the list; at the top level it closes the menu.
        if (isOpen && gp.buttonEast.wasPressedThisFrame)
        {
            AudioManager.PlayMenuBack();   // "back" SFX on B (backing out of a sub or closing the menu)
            if (currentSub != null) ShowMain();
            else Close();
        }
    }

    void LateUpdate()
    {
        // Only the main list is navigable; reset the SFX tracker elsewhere so opening the menu or
        // returning from a sub-screen doesn't fire a stray "move" sound.
        if (!isOpen || currentSub != null)
        {
            lastSelectedForSfx = null;
            return;
        }

        // Rescue navigation from a null selection (e.g. a mouse click cleared it) so pressing Up/Down
        // re-highlights the top item instead of soft-locking.
        MenuNavigation.EnsureSelectionOnNavigate(firstButton);

        // Play the "move" SFX whenever the highlighted item changes (list navigation).
        MenuNavigation.PlayMoveSfxOnSelectionChange(ref lastSelectedForSfx);
    }

    /// <summary>The menu opens in every gameplay scene (the shared HUD rule) plus any scene listed in
    /// <see cref="extraScenes"/> — kept separate so adding the Tutorial here doesn't also switch on
    /// the HUDs and car swapper, which reuse the HUD rule.</summary>
    bool MenuAvailableInScene(string sceneName)
    {
        if (GameplayHud.VisibleInScene(sceneName)) return true;
        if (extraScenes != null)
            foreach (var s in extraScenes)
                if (s == sceneName) return true;
        return false;
    }

    void Open()
    {
        EnsureUI();
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;   // stop A/B from also jumping/turbo-ing the car (game still runs)
        subReturnButton = null;
        ShowMain();
        AudioManager.PlayMenuOpen();
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        isOpen = false;
        currentSub = null;
        MenuState.AnyOpen = false;
        AudioManager.PlayMenuClose();
    }

    // -------------------------------------------------------
    //  Panel navigation
    // -------------------------------------------------------

    void ShowMain()
    {
        currentSub = null;
        if (audioPanel != null) audioPanel.SetActive(false);
        if (controlsPanel != null) controlsPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);

        if (titleText != null) titleText.text = "MENU";
        if (hintText != null) hintText.text = "A: Select     B: Back     Start: Close";

        var focus = subReturnButton != null ? subReturnButton : firstButton;
        subReturnButton = null;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus);
    }

    /// <summary>Opens a sub-screen (AUDIO/CONTROLS/SETTINGS). B returns to the list and re-focuses
    /// the button that opened it. Sub-screens are placeholders for now — fill them in later.</summary>
    void OpenSub(GameObject panel, string title, GameObject returnButton,
                 GameObject focus = null, string hint = "B: Back")
    {
        currentSub = panel;
        subReturnButton = returnButton;

        if (mainPanel != null) mainPanel.SetActive(false);
        if (audioPanel != null) audioPanel.SetActive(panel == audioPanel);
        if (controlsPanel != null) controlsPanel.SetActive(panel == controlsPanel);
        if (settingsPanel != null) settingsPanel.SetActive(panel == settingsPanel);

        if (titleText != null) titleText.text = title;
        if (hintText != null) hintText.text = hint;

        // Focus the panel's first control — null for placeholder panels, which have nothing to
        // navigate; either way B (handled in Update) returns to the list.
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus);
    }

    // -------------------------------------------------------
    //  Button actions
    // -------------------------------------------------------

    void OnResume() => Close();
    void OnAudio() => OpenSub(audioPanel, "AUDIO", audioBtn != null ? audioBtn.gameObject : null);
    void OnControls() => OpenSub(controlsPanel, "CONTROLS", controlsBtn != null ? controlsBtn.gameObject : null);

    void OnSettings()
    {
        RefreshTutorialToggleLabel();   // reflect the current preference before showing the toggle
        OpenSub(settingsPanel, "SETTINGS", settingsBtn != null ? settingsBtn.gameObject : null,
                tutorialToggleBtn != null ? tutorialToggleBtn.gameObject : null,
                "A: Toggle     B: Back");
    }

    void OnToggleTutorialGuide()
    {
        TutorialSettings.GuideEnabled = !TutorialSettings.GuideEnabled;
        RefreshTutorialToggleLabel();
    }

    void RefreshTutorialToggleLabel()
    {
        if (tutorialToggleLabel != null)
            tutorialToggleLabel.text = "Tutorial Tips: " + (TutorialSettings.GuideEnabled ? "ON" : "OFF");
    }

    void OnQuit()
    {
        Close();

        // Tear down the current run so a brand-new game loop begins on the next play. Without this,
        // the DontDestroyOnLoad GameLoopManager keeps its old phase/round/timer, and the next game
        // resumes mid-run — leaving the player stranded in the hub with no portal.
        GameLoopManager.EndRun();
        if (PlayerInventory.Instance != null) PlayerInventory.Instance.ResetToStarting();

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

        cfg = Resources.Load<StartMenuConfig>("StartMenuConfig");
        if (cfg == null) cfg = ScriptableObject.CreateInstance<StartMenuConfig>();   // defaults

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
        dimImg.color = cfg.dimColor;
        var drt = dim.GetComponent<RectTransform>();
        drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one;
        drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;

        titleText = NewText(root.transform, "Title", cfg.titleFontSize, TextAlignmentOptions.Center);
        titleText.fontStyle = FontStyles.Bold;
        titleText.color = cfg.titleColor;
        SetCentered(titleText.rectTransform, new Vector2(700f, 100f), new Vector2(0f, cfg.titleY));

        BuildMainPanel(root.transform);

        audioPanel = BuildSubPanel(root.transform, "Audio settings coming soon.");
        controlsPanel = BuildSubPanel(root.transform, "Controls settings coming soon.");
        settingsPanel = BuildSettingsPanel(root.transform);

        hintText = NewText(root.transform, "Hint", cfg.hintFontSize, TextAlignmentOptions.Center);
        hintText.color = cfg.hintColor;
        SetCentered(hintText.rectTransform, new Vector2(900f, 50f), new Vector2(0f, cfg.hintY));

        root.SetActive(false);
    }

    void BuildMainPanel(Transform parent)
    {
        mainPanel = NewUI("MainPanel", parent);
        SetCentered(mainPanel.GetComponent<RectTransform>(), new Vector2(cfg.buttonSize.x, 100f),
                    new Vector2(0f, cfg.buttonColumnY));

        var vlg = mainPanel.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = cfg.buttonSpacing;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;

        var fitter = mainPanel.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var resume = CreateButton("RESUME", mainPanel.transform, OnResume);
        audioBtn = CreateButton("AUDIO", mainPanel.transform, OnAudio);
        controlsBtn = CreateButton("CONTROLS", mainPanel.transform, OnControls);
        settingsBtn = CreateButton("SETTINGS", mainPanel.transform, OnSettings);
        var quit = CreateButton("QUIT", mainPanel.transform, OnQuit);

        firstButton = resume.gameObject;

        // Vertical wrap navigation: Up at the top goes to the bottom, Down at the bottom to the top.
        MenuNavigation.WireVerticalWrap(new[] { resume, audioBtn, controlsBtn, settingsBtn, quit });
    }

    /// <summary>The SETTINGS sub-screen: a single toggle for the Tutorial scene's on-screen tips
    /// (A flips it). Built like the main list so gamepad focus + A/B work; more toggles can be added
    /// to the same column later.</summary>
    GameObject BuildSettingsPanel(Transform parent)
    {
        var go = NewUI("SettingsPanel", parent);
        SetCentered(go.GetComponent<RectTransform>(), new Vector2(cfg.buttonSize.x, 100f),
                    new Vector2(0f, cfg.buttonColumnY));

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = cfg.buttonSpacing;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        tutorialToggleBtn = CreateButton("TutorialToggle", go.transform, OnToggleTutorialGuide);
        tutorialToggleLabel = tutorialToggleBtn.GetComponentInChildren<TextMeshProUGUI>();
        RefreshTutorialToggleLabel();   // sets the real "Tutorial Tips: ON/OFF" text (panel is hidden here)

        go.SetActive(false);
        return go;
    }

    GameObject BuildSubPanel(Transform parent, string placeholder)
    {
        var go = NewUI("SubPanel", parent);
        SetCentered(go.GetComponent<RectTransform>(), new Vector2(900f, 300f), new Vector2(0f, cfg.buttonColumnY));

        var tmp = NewText(go.transform, "Placeholder", cfg.buttonFontSize, TextAlignmentOptions.Center);
        tmp.text = placeholder;
        tmp.color = cfg.buttonTextColor;
        var trt = tmp.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

        go.SetActive(false);
        return go;
    }

    Button CreateButton(string label, Transform parent, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Color.white;   // white graphic so the ColorBlock tints show

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = cfg.buttonSize.x; le.preferredHeight = cfg.buttonSize.y;
        le.minWidth = cfg.buttonSize.x; le.minHeight = cfg.buttonSize.y;

        var btn = go.GetComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = cfg.buttonNormalColor;
        cb.highlightedColor = cfg.buttonHighlightedColor;
        cb.selectedColor = cfg.buttonSelectedColor;
        cb.pressedColor = cfg.buttonPressedColor;
        cb.colorMultiplier = 1f; cb.fadeDuration = 0.1f;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);
        btn.onClick.AddListener(AudioManager.PlayMenuSelect);   // "select" SFX on click / Submit (A)

        var tmp = NewText(go.transform, "Label", cfg.buttonFontSize, TextAlignmentOptions.Center);
        tmp.text = label; tmp.color = cfg.buttonTextColor;
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

    static TextMeshProUGUI NewText(Transform parent, string name, float fontSize, TextAlignmentOptions align)
    {
        var go = NewUI(name, parent);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        t.enableWordWrapping = false;
        return t;
    }

    static void SetCentered(RectTransform rt, Vector2 size, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
    }
}
