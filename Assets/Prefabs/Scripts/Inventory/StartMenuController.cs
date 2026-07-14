using System.Collections.Generic;
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
/// to the main menu.
///
/// The AUDIO / CONTROLS / SETTINGS sub-screens mirror the Main Menu's settings, built from the shared
/// <see cref="SettingsUI"/> widgets + <see cref="RebindController"/>: AUDIO = Music/SFX volume sliders,
/// CONTROLS = per-binding rebinding + reset, SETTINGS = the Tutorial-tips toggle alongside the
/// Video/Graphics options (resolution / display mode / quality / vsync).
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
    private GameObject currentFirst;           // the current screen's first control (nav rescue target)

    private Button audioBtn, controlsBtn, settingsBtn;
    private GameObject audioPanel, controlsPanel, settingsPanel;
    private Button tutorialToggleBtn;                // SETTINGS: flips the Tutorial guide on/off
    private TextMeshProUGUI tutorialToggleLabel;
    private GameObject currentSub;             // null = on the main list
    private GameObject subReturnButton;        // main button to re-focus when backing out of a sub

    // AUDIO
    private Slider musicSlider, sfxSlider;
    private TextMeshProUGUI musicValueText, sfxValueText;
    private GameObject audioFirst;

    // VIDEO (lives in the SETTINGS panel next to the Tutorial toggle)
    private OptionSelector resSel, dispSel, qualSel, vsyncSel;
    private List<Vector2Int> resolutionOptions;

    // CONTROLS (rebinding)
    private GeneracerControls controlsForRebind;
    private RebindController rebind;
    private GameObject controlsFirst;

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
    void OnDestroy() { if (Instance == this) controlsForRebind?.Dispose(); }

    // Never carry the menu across a scene load (e.g. QUIT, or returning to the hub).
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) { if (isOpen) Close(); }

    void Update()
    {
        var gp = Gamepad.current;
        if (gp == null) return;

        // While a rebind is listening, RebindController owns input (Start/Esc cancel) — leave the menu
        // alone so the captured press doesn't also toggle/close it.
        if (rebind != null && rebind.IsRebinding) return;

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
        // Nothing to steer when closed, or while a rebind is capturing input.
        if (!isOpen || (rebind != null && rebind.IsRebinding)) { lastSelectedForSfx = null; return; }

        // Rescue navigation from a null selection (e.g. a mouse click cleared it) so pressing Up/Down
        // re-highlights this screen's first control instead of soft-locking.
        MenuNavigation.EnsureSelectionOnNavigate(currentFirst);

        // Play the "move" SFX whenever the highlighted item changes (list or sub-screen navigation).
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
        currentFirst = firstButton;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus);
    }

    /// <summary>Opens a sub-screen (AUDIO/CONTROLS/SETTINGS). B returns to the list and re-focuses the
    /// button that opened it.</summary>
    void OpenSub(GameObject panel, string title, GameObject returnButton, GameObject focus, string hint)
    {
        currentSub = panel;
        subReturnButton = returnButton;

        if (mainPanel != null) mainPanel.SetActive(false);
        if (audioPanel != null) audioPanel.SetActive(panel == audioPanel);
        if (controlsPanel != null) controlsPanel.SetActive(panel == controlsPanel);
        if (settingsPanel != null) settingsPanel.SetActive(panel == settingsPanel);

        if (titleText != null) titleText.text = title;
        if (hintText != null) hintText.text = hint;

        currentFirst = focus;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus);
    }

    // -------------------------------------------------------
    //  Button actions
    // -------------------------------------------------------

    void OnResume() => Close();

    void OnAudio()
    {
        RefreshAudioValues();   // re-sync to the live volumes (may have changed since this was built)
        OpenSub(audioPanel, "AUDIO", audioBtn != null ? audioBtn.gameObject : null,
                audioFirst, "Left/Right: Adjust     B: Back");
    }

    void OnControls()
    {
        RefreshControlsLabels();   // re-sync to the current bindings (e.g. rebound from the main menu)
        OpenSub(controlsPanel, "CONTROLS", controlsBtn != null ? controlsBtn.gameObject : null,
                controlsFirst, "A: Rebind     B: Back     Start/Esc: cancel a rebind");
    }

    void OnSettings()
    {
        RefreshTutorialToggleLabel();   // reflect the current preference before showing the toggle
        RefreshVideoValues();           // re-sync the video options to the live screen/quality state
        OpenSub(settingsPanel, "SETTINGS", settingsBtn != null ? settingsBtn.gameObject : null,
                tutorialToggleBtn != null ? tutorialToggleBtn.gameObject : null,
                "A: Toggle     Left/Right: Change     B: Back");
    }

    // Sub-screens are built once (persistent menu); re-sync their widgets to the live state on each open
    // so a change made elsewhere (e.g. the Main Menu) isn't shown stale.
    void RefreshAudioValues()
    {
        if (musicSlider != null) { musicSlider.SetValueWithoutNotify(InitialMusic()); UpdateMusicValueText(musicSlider.value); }
        if (sfxSlider   != null) { sfxSlider.SetValueWithoutNotify(InitialSfx());     UpdateSfxValueText(sfxSlider.value); }
    }

    void RefreshVideoValues()
    {
        if (resSel != null && resolutionOptions != null)
        {
            int targetW = GameSettings.HasResolution ? GameSettings.ResolutionWidth  : Screen.width;
            int targetH = GameSettings.HasResolution ? GameSettings.ResolutionHeight : Screen.height;
            int idx = 0;
            for (int i = 0; i < resolutionOptions.Count; i++)
                if (resolutionOptions[i].x == targetW && resolutionOptions[i].y == targetH) { idx = i; break; }
            resSel.SetIndexSilent(idx);
        }
        if (dispSel  != null) dispSel.SetIndexSilent(SettingsUI.FullscreenIndexOf(Screen.fullScreenMode));
        if (qualSel  != null) qualSel.SetIndexSilent(QualitySettings.GetQualityLevel());
        if (vsyncSel != null) vsyncSel.SetIndexSilent(QualitySettings.vSyncCount > 0 ? 1 : 0);
    }

    void RefreshControlsLabels()
    {
        if (controlsForRebind != null) InputRebinding.ApplyOverridesTo(controlsForRebind.asset);
        if (rebind != null) rebind.RefreshLabels();
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

        audioPanel = BuildAudioPanel(root.transform);
        controlsPanel = BuildControlsPanel(root.transform);
        settingsPanel = BuildSettingsPanel(root.transform);

        hintText = NewText(root.transform, "Hint", cfg.hintFontSize, TextAlignmentOptions.Center);
        hintText.color = cfg.hintColor;
        SetCentered(hintText.rectTransform, new Vector2(1100f, 50f), new Vector2(0f, cfg.hintY));

        root.SetActive(false);
    }

    void BuildMainPanel(Transform parent)
    {
        mainPanel = NewUI("MainPanel", parent);
        SetupColumn(mainPanel, cfg.buttonColumnY, cfg.buttonSpacing);

        var resume = CreateButton("RESUME", mainPanel.transform, OnResume);
        audioBtn = CreateButton("AUDIO", mainPanel.transform, OnAudio);
        controlsBtn = CreateButton("CONTROLS", mainPanel.transform, OnControls);
        settingsBtn = CreateButton("SETTINGS", mainPanel.transform, OnSettings);
        var quit = CreateButton("QUIT", mainPanel.transform, OnQuit);

        firstButton = resume.gameObject;

        // Vertical wrap navigation: Up at the top goes to the bottom, Down at the bottom to the top.
        MenuNavigation.WireVerticalWrap(new[] { resume, audioBtn, controlsBtn, settingsBtn, quit });
    }

    // -------------------------------------------------------
    //  AUDIO sub-screen (Music + SFX volume)
    // -------------------------------------------------------

    GameObject BuildAudioPanel(Transform parent)
    {
        var go = NewUI("AudioPanel", parent);
        SetupColumn(go, cfg.buttonColumnY, cfg.buttonSpacing);

        musicSlider = BuildSliderRow(go.transform, "MUSIC", InitialMusic(), OnMusicChanged, out musicValueText);
        sfxSlider   = BuildSliderRow(go.transform, "SFX",   InitialSfx(),   OnSfxChanged,   out sfxValueText);

        SettingsUI.WireVerticalWrap(new Selectable[] { musicSlider, sfxSlider });

        UpdateMusicValueText(musicSlider.value);
        UpdateSfxValueText(sfxSlider.value);

        audioFirst = musicSlider.gameObject;
        go.SetActive(false);
        return go;
    }

    Slider BuildSliderRow(Transform col, string label, float initial,
                          UnityEngine.Events.UnityAction<float> onChanged, out TextMeshProUGUI valueText)
    {
        var row = NewUI(label + "Row", col);
        float w = cfg.buttonSize.x + 200f;
        var le = row.AddComponent<LayoutElement>();
        le.preferredWidth = w; le.minWidth = w; le.preferredHeight = 54f; le.minHeight = 54f;

        var lbl = SettingsUI.NewText(row.transform, "Label", cfg.buttonFontSize * 0.8f, TextAlignmentOptions.MidlineLeft);
        lbl.text = label; lbl.color = cfg.buttonTextColor;
        Stretch(lbl.rectTransform, new Vector2(0f, 0f), new Vector2(0.28f, 1f), new Vector2(10f, 0f), Vector2.zero);

        var slider = SettingsUI.VolumeSlider(row.transform, Theme(), initial, onChanged);
        Stretch(slider.GetComponent<RectTransform>(), new Vector2(0.30f, 0.25f), new Vector2(0.80f, 0.75f), Vector2.zero, Vector2.zero);

        var val = SettingsUI.NewText(row.transform, "Value", cfg.buttonFontSize * 0.8f, TextAlignmentOptions.MidlineRight);
        val.color = cfg.buttonTextColor;
        Stretch(val.rectTransform, new Vector2(0.82f, 0f), new Vector2(1f, 1f), Vector2.zero, new Vector2(-10f, 0f));

        valueText = val;
        return slider;
    }

    void OnMusicChanged(float v)
    {
        if (AudioManager.Instance != null) AudioManager.Instance.SetMusicVolume(v);
        GameSettings.MusicVolume = v;
        UpdateMusicValueText(v);
    }

    void OnSfxChanged(float v)
    {
        if (AudioManager.Instance != null) AudioManager.Instance.SetSfxVolume(v);
        GameSettings.SfxVolume = v;
        UpdateSfxValueText(v);
        AudioManager.PlayMenuMove();   // tick at the NEW level so the slider previews SFX loudness
    }

    void UpdateMusicValueText(float v) { if (musicValueText != null) musicValueText.text = Mathf.RoundToInt(v * 100f) + "%"; }
    void UpdateSfxValueText(float v)   { if (sfxValueText   != null) sfxValueText.text   = Mathf.RoundToInt(v * 100f) + "%"; }

    static float InitialMusic() => AudioManager.Instance != null ? AudioManager.Instance.MusicVolume : GameSettings.MusicVolume;
    static float InitialSfx()   => AudioManager.Instance != null ? AudioManager.Instance.SfxVolume   : GameSettings.SfxVolume;

    // -------------------------------------------------------
    //  CONTROLS sub-screen (rebinding + reset)
    // -------------------------------------------------------

    GameObject BuildControlsPanel(Transform parent)
    {
        controlsForRebind = new GeneracerControls();
        InputRebinding.ApplyOverridesTo(controlsForRebind.asset);   // start from the player's saved rebinds
        var map = controlsForRebind.Driving.Get();

        rebind = root.AddComponent<RebindController>();   // on the canvas, so it only ticks while the menu is open
        rebind.Init(controlsForRebind);
        rebind.ClearRows();

        var go = NewUI("ControlsPanel", parent);
        SetupColumn(go, cfg.buttonColumnY, 5f);

        var buttons = new List<Button>();
        foreach (var action in map.actions)
        {
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isComposite) continue;

                var capturedAction = action; int bindingIndex = i;
                string rowName = SettingsUI.FriendlyActionName(action.name)
                                 + (b.isPartOfComposite ? " (" + SettingsUI.PartLabel(b.name) + ")" : "");

                var row = BuildBindingRow(go.transform, rowName, out TextMeshProUGUI valueLabel);
                valueLabel.text = capturedAction.GetBindingDisplayString(bindingIndex);
                row.onClick.AddListener(() => rebind.Begin(capturedAction, bindingIndex, valueLabel));
                rebind.RegisterRow(capturedAction, bindingIndex, valueLabel);
                buttons.Add(row);
            }
        }

        var reset = BuildRowButton(go.transform, "RESET TO DEFAULTS", () => rebind.ResetAll());
        buttons.Add(reset);

        MenuNavigation.WireVerticalWrap(buttons);
        controlsFirst = buttons.Count > 0 ? buttons[0].gameObject : null;

        go.SetActive(false);
        return go;
    }

    Button BuildBindingRow(Transform col, string rowName, out TextMeshProUGUI valueLabel)
    {
        var go = new GameObject(rowName + " Row", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(col, false);
        go.GetComponent<Image>().color = Color.white;

        float w = cfg.buttonSize.x + 170f;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = w; le.minWidth = w; le.preferredHeight = 42f; le.minHeight = 42f;

        var btn = go.GetComponent<Button>();
        ApplyColors(btn);
        btn.onClick.AddListener(AudioManager.PlayMenuSelect);

        var nameLabel = SettingsUI.NewText(go.transform, "Name", cfg.buttonFontSize * 0.72f, TextAlignmentOptions.MidlineLeft);
        nameLabel.text = rowName; nameLabel.color = cfg.buttonTextColor;
        Stretch(nameLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(18f, 0f), new Vector2(-18f, 0f));

        valueLabel = SettingsUI.NewText(go.transform, "Value", cfg.buttonFontSize * 0.72f, TextAlignmentOptions.MidlineRight);
        valueLabel.color = Color.white; valueLabel.fontStyle = FontStyles.Bold;
        Stretch(valueLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(18f, 0f), new Vector2(-18f, 0f));

        return btn;
    }

    Button BuildRowButton(Transform col, string label, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(col, false);
        go.GetComponent<Image>().color = Color.white;

        float w = cfg.buttonSize.x + 170f;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = w; le.minWidth = w; le.preferredHeight = 42f; le.minHeight = 42f;

        var btn = go.GetComponent<Button>();
        ApplyColors(btn);
        btn.onClick.AddListener(onClick);
        btn.onClick.AddListener(AudioManager.PlayMenuSelect);

        var lbl = SettingsUI.NewText(go.transform, "Label", cfg.buttonFontSize * 0.72f, TextAlignmentOptions.Center);
        lbl.text = label; lbl.color = cfg.buttonTextColor;
        Stretch(lbl.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        return btn;
    }

    // -------------------------------------------------------
    //  SETTINGS sub-screen: Tutorial-tips toggle + Video/Graphics
    // -------------------------------------------------------

    GameObject BuildSettingsPanel(Transform parent)
    {
        var go = NewUI("SettingsPanel", parent);
        SetupColumn(go, cfg.buttonColumnY, 8f);

        tutorialToggleBtn = CreateButton("TutorialToggle", go.transform, OnToggleTutorialGuide);
        tutorialToggleLabel = tutorialToggleBtn.GetComponentInChildren<TextMeshProUGUI>();
        RefreshTutorialToggleLabel();   // sets the real "Tutorial Tips: ON/OFF" text (panel is hidden here)

        // Video / Graphics options share this panel with the Tutorial toggle.
        var resLabels = SettingsUI.ResolutionOptions(out resolutionOptions, out int resStart);
        resSel  = BuildOptionRow(go.transform, "RESOLUTION",   resLabels, resStart, OnResolutionChanged);
        dispSel = BuildOptionRow(go.transform, "DISPLAY MODE", new List<string>(SettingsUI.FullscreenLabels),
                                 SettingsUI.FullscreenIndexOf(Screen.fullScreenMode), OnFullscreenChanged);
        var qLabels = new List<string>(QualitySettings.names);
        int qStart = Mathf.Clamp(QualitySettings.GetQualityLevel(), 0, Mathf.Max(0, qLabels.Count - 1));
        qualSel = BuildOptionRow(go.transform, "QUALITY", qLabels, qStart, OnQualityChanged);
        vsyncSel = BuildOptionRow(go.transform, "V-SYNC", new List<string> { "Off", "On" },
                                  QualitySettings.vSyncCount > 0 ? 1 : 0, OnVSyncChanged);

        SettingsUI.WireVerticalWrap(new Selectable[] { tutorialToggleBtn, resSel, dispSel, qualSel, vsyncSel });

        go.SetActive(false);
        return go;
    }

    OptionSelector BuildOptionRow(Transform col, string label, IList<string> options, int start, System.Action<int> onChanged)
    {
        var row = NewUI(label + "Row", col);
        float w = cfg.buttonSize.x + 170f;
        var le = row.AddComponent<LayoutElement>();
        le.preferredWidth = w; le.minWidth = w; le.preferredHeight = 46f; le.minHeight = 46f;

        var lbl = SettingsUI.NewText(row.transform, "Label", cfg.buttonFontSize * 0.72f, TextAlignmentOptions.MidlineLeft);
        lbl.text = label; lbl.color = cfg.buttonTextColor;
        Stretch(lbl.rectTransform, new Vector2(0f, 0f), new Vector2(0.45f, 1f), new Vector2(18f, 0f), Vector2.zero);

        var sel = SettingsUI.OptionCycler(row.transform, Theme(), cfg.buttonFontSize * 0.66f, options, start, onChanged);
        Stretch(sel.GetComponent<RectTransform>(), new Vector2(0.47f, 0.12f), new Vector2(1f, 0.88f), Vector2.zero, new Vector2(-8f, 0f));

        return sel;
    }

    void OnResolutionChanged(int index)
    {
        if (resolutionOptions == null || index < 0 || index >= resolutionOptions.Count) return;
        var r = resolutionOptions[index];
        GameSettings.SetResolution(r.x, r.y);
        Screen.SetResolution(r.x, r.y, SelectedFullscreenMode());
    }

    void OnFullscreenChanged(int index)
    {
        var mode = SettingsUI.FullscreenModes[Mathf.Clamp(index, 0, SettingsUI.FullscreenModes.Length - 1)];
        GameSettings.FullScreenModeValue = (int)mode;
        Screen.fullScreenMode = mode;
    }

    void OnQualityChanged(int index)
    {
        GameSettings.QualityLevel = index;
        QualitySettings.SetQualityLevel(index, true);
        if (GameSettings.HasVSync) QualitySettings.vSyncCount = GameSettings.VSync;
        if (vsyncSel != null) vsyncSel.SetIndexSilent(QualitySettings.vSyncCount > 0 ? 1 : 0);
    }

    void OnVSyncChanged(int index)
    {
        GameSettings.VSync = index;
        QualitySettings.vSyncCount = index;
    }

    FullScreenMode SelectedFullscreenMode()
    {
        int i = dispSel != null ? dispSel.Index : SettingsUI.FullscreenIndexOf(Screen.fullScreenMode);
        return SettingsUI.FullscreenModes[Mathf.Clamp(i, 0, SettingsUI.FullscreenModes.Length - 1)];
    }

    // -------------------------------------------------------
    //  Shared builders / helpers
    // -------------------------------------------------------

    SettingsUI.Theme Theme() => new SettingsUI.Theme
    {
        normal = cfg.buttonNormalColor,
        highlighted = cfg.buttonHighlightedColor,
        selected = cfg.buttonSelectedColor,
        pressed = cfg.buttonPressedColor,
        text = cfg.buttonTextColor,
        fade = 0.1f,
    };

    void ApplyColors(Selectable s)
    {
        var cb = s.colors;
        cb.normalColor = cfg.buttonNormalColor;
        cb.highlightedColor = cfg.buttonHighlightedColor;
        cb.selectedColor = cfg.buttonSelectedColor;
        cb.pressedColor = cfg.buttonPressedColor;
        cb.colorMultiplier = 1f; cb.fadeDuration = 0.1f;
        s.colors = cb;
    }

    // Centres a code-built panel and gives it a vertical layout that grows to fit its rows.
    void SetupColumn(GameObject go, float y, float spacing)
    {
        SetCentered(go.GetComponent<RectTransform>(), new Vector2(cfg.buttonSize.x, 100f), new Vector2(0f, y));

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = spacing;
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    static void Stretch(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
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
        ApplyColors(btn);
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
