using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using TMPro;
using UnityEngine.InputSystem;
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

    [Header("Controls Panel (rebinding list)")]
    [Tooltip("Height of each control-binding row box AND the RESET button (pixels). Shrink to fit more rows.")]
    public float controlsRowHeight = 46f;
    [Tooltip("Font size for the control-binding rows and the RESET label.")]
    public float controlsRowFontSize = 26f;
    [Tooltip("Vertical gap between control-binding rows (pixels).")]
    public float controlsRowSpacing = 6f;
    [Tooltip("Vertical offset of the whole binding list from screen centre (pixels; + up, - down). 0 = centred.")]
    public float controlsPanelOffsetY = 0f;

    [Header("Scene Routing")]
    [Tooltip("Scene that Start loads — the Car Selection screen, which then starts the game loop. " +
             "Must be added to Build Settings before it will load.")]
    public string startSceneName = "CarSelection";
    [Tooltip("Scene that Tutorial loads. Leave blank until the tutorial scene exists.")]
    public string tutorialSceneName = "Tutorial";

    [Header("Online Multiplayer")]
    [Tooltip("Selectable cars for the multiplayer lobby's car cycler — fill with the SAME name+prefab " +
             "entries as the CarSelection scene's list (the lobby can't read that scene's Inspector). " +
             "Empty = a single DEFAULT entry.")]
    public CarSelectionController.CarEntry[] multiplayerCars;

    private GameObject firstButton;   // current screen's rescue target for a null selection (main list or a settings panel)
    private GameObject lastSelectedForSfx;   // tracks the highlighted item so we can fire the "move" SFX

    // ---- Online Multiplayer (lobby flow lives in its own component; menu input defers while it's open) ----
    private MultiplayerLobbyUI lobbyUI;
    private Button onlineButton;      // re-focused when the lobby flow exits back to this menu

    // ---- Settings screen (SETTINGS button -> AUDIO / VIDEO / CONTROLS) ----
    private GameObject buttonsColumn;         // the main menu button list; hidden while the Settings screen is up
    private Button settingsButton;            // the SETTINGS entry; re-focused when leaving the Settings screen
    private GameObject settingsCategoryPanel; // AUDIO / VIDEO / CONTROLS chooser
    private GameObject audioPanel, videoPanel, controlsPanel;
    private Button audioCatBtn;               // AUDIO chooser entry — focused when the chooser opens
    private Slider musicSlider, sfxSlider;
    private TextMeshProUGUI musicValueText, sfxValueText;

    // Video/Graphics option cyclers + backing data.
    private OptionSelector resolutionSelector, fullscreenSelector, qualitySelector, vsyncSelector;
    private List<Vector2Int> resolutionOptions;   // width/height per resolution index, aligned with the selector
    private static readonly FullScreenMode[] FullscreenModes =
        { FullScreenMode.ExclusiveFullScreen, FullScreenMode.FullScreenWindow, FullScreenMode.Windowed };

    // Controls (rebinding): the menu rebinds its OWN GeneracerControls instance and persists the diff;
    // gameplay instances load it on creation (see InputRebinding + CarController/CameraSwitcher).
    private GameObject controlsFirstButton;
    private GeneracerControls controlsForRebind;
    private InputActionRebindingExtensions.RebindingOperation rebindOp;
    private bool rebinding;
    private struct BindingRow { public InputAction action; public int bindingIndex; public TextMeshProUGUI valueLabel; }
    private readonly List<BindingRow> bindingRows = new List<BindingRow>();

    private enum SettingsScreen { Root, Categories, Audio, Video, Controls }
    private SettingsScreen currentScreen = SettingsScreen.Root;

    void Start()
    {
        EnsureCamera();
        EnsureEventSystem();
        BuildUI();
    }

    void LateUpdate()
    {
        // The lobby flow runs its own selection rescue + move SFX while it's up.
        if (lobbyUI != null && lobbyUI.IsOpen) return;

        // If nothing is highlighted (e.g. a mouse click cleared the selection), pressing Up/Down
        // re-highlights the top item so D-pad / arrow navigation never soft-locks.
        MenuNavigation.EnsureSelectionOnNavigate(firstButton);

        // Play the "move" SFX whenever the highlighted button changes (list navigation).
        MenuNavigation.PlayMoveSfxOnSelectionChange(ref lastSelectedForSfx);
    }

    void Update()
    {
        // The lobby flow owns all input (including B/Escape) while it's open.
        if (lobbyUI != null && lobbyUI.IsOpen) return;

        // While an interactive rebind is listening, ignore normal menu input — the rebind operation is
        // capturing the next control press. Gamepad Start (excluded from binding) or Escape cancels it.
        if (rebinding)
        {
            var gp = Gamepad.current;
            if (gp != null && gp.startButton.wasPressedThisFrame) rebindOp?.Cancel();
            return;
        }

        // B (gamepad) / Escape steps back out of the Settings screen, one level at a time. The main
        // menu list itself has nothing to back out to, so the press is ignored there.
        if (currentScreen == SettingsScreen.Root) return;
        if (BackPressed()) GoBack();
    }

    void OnDestroy()
    {
        if (rebindOp != null) { rebindOp.Dispose(); rebindOp = null; }
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = true;
        controlsForRebind?.Dispose();   // frees the menu's rebinding InputActionAsset
    }

    static bool BackPressed()
    {
        var gp = Gamepad.current;
        if (gp != null && gp.buttonEast.wasPressedThisFrame) return true;
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;
        return false;
    }

    void GoBack()
    {
        AudioManager.PlayMenuBack();
        if (currentScreen == SettingsScreen.Categories) ShowRoot();   // chooser -> main menu list
        else ShowCategories();                                        // a settings panel -> chooser
    }

    // -------------------------------------------------------
    //  Settings screen navigation
    // -------------------------------------------------------

    void ShowRoot()
    {
        currentScreen = SettingsScreen.Root;
        SetSettingsPanels(false, false, false, false);
        if (buttonsColumn != null) buttonsColumn.SetActive(true);
        FocusFirst(settingsButton != null ? settingsButton.gameObject : null);   // land back on SETTINGS
    }

    void ShowCategories()
    {
        currentScreen = SettingsScreen.Categories;
        if (buttonsColumn != null) buttonsColumn.SetActive(false);
        SetSettingsPanels(true, false, false, false);
        FocusFirst(audioCatBtn != null ? audioCatBtn.gameObject : null);
    }

    void ShowAudio()
    {
        currentScreen = SettingsScreen.Audio;
        SetSettingsPanels(false, true, false, false);
        FocusFirst(musicSlider != null ? musicSlider.gameObject : null);
    }

    void ShowVideo()
    {
        currentScreen = SettingsScreen.Video;
        SetSettingsPanels(false, false, true, false);
        FocusFirst(resolutionSelector != null ? resolutionSelector.gameObject : null);
    }

    void ShowControls()
    {
        currentScreen = SettingsScreen.Controls;
        SetSettingsPanels(false, false, false, true);
        FocusFirst(controlsFirstButton);
    }

    void SetSettingsPanels(bool categories, bool audio, bool video, bool controls)
    {
        if (settingsCategoryPanel != null) settingsCategoryPanel.SetActive(categories);
        if (audioPanel != null)    audioPanel.SetActive(audio);
        if (videoPanel != null)    videoPanel.SetActive(video);
        if (controlsPanel != null) controlsPanel.SetActive(controls);
    }

    /// <summary>Selects <paramref name="go"/> (may be null for a placeholder screen), records it as this
    /// screen's navigation-rescue target, and primes the move-SFX tracker so the focus change itself is
    /// silent (only later navigation between items plays the "move" sound).</summary>
    void FocusFirst(GameObject go)
    {
        firstButton = go;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(go);
        lastSelectedForSfx = go;
    }

    void OnAudioCategory()    => ShowAudio();
    void OnVideoCategory()    => ShowVideo();
    void OnControlsCategory() => ShowControls();

    // -------------------------------------------------------
    //  Settings screen construction
    // -------------------------------------------------------

    void BuildSettingsScreens(Transform parent)
    {
        settingsCategoryPanel = BuildCategoryColumn(parent, out audioCatBtn);
        audioPanel    = BuildAudioPanel(parent);
        videoPanel    = BuildVideoPanel(parent);
        controlsPanel = BuildControlsPanel(parent);
    }

    GameObject BuildCategoryColumn(Transform parent, out Button audioBtn)
    {
        var column = BuildButtonColumn(parent);   // same upper-left placement + layout as the main list
        column.name = "SettingsCategories";

        audioBtn     = CreateButton("AUDIO", column, OnAudioCategory);
        var video    = CreateButton("VIDEO & GRAPHICS", column, OnVideoCategory);
        var controls = CreateButton("CONTROLS", column, OnControlsCategory);

        MenuNavigation.WireVerticalWrap(new[] { audioBtn, video, controls });

        column.gameObject.SetActive(false);
        return column.gameObject;
    }

    GameObject BuildAudioPanel(Transform parent)
    {
        var go = new GameObject("AudioPanel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);   // upper-left, like the button column
        rt.sizeDelta = new Vector2(700f, 220f);
        rt.anchoredPosition = new Vector2(96f, -330f);

        musicSlider = BuildAudioRow(go.transform, "MUSIC", -10f, InitialMusic(), OnMusicChanged, out musicValueText);
        sfxSlider   = BuildAudioRow(go.transform, "SFX",   -95f, InitialSfx(),   OnSfxChanged,   out sfxValueText);

        // Up/Down moves between the two sliders (wrapping); Left/Right adjusts the focused slider's value.
        WireSliderPair(musicSlider, sfxSlider);

        UpdateMusicValueText(musicSlider.value);
        UpdateSfxValueText(sfxSlider.value);

        go.SetActive(false);
        return go;
    }

    /// <summary>One labelled volume row: "LABEL  [====slider====]  75%". Returns the slider and outs its
    /// value readout. Positioned manually (top-left origin, y grows downward) so the slider's own anchor
    /// math isn't fighting a layout group.</summary>
    Slider BuildAudioRow(Transform panel, string label, float y, float initial,
                         UnityEngine.Events.UnityAction<float> onChanged, out TextMeshProUGUI valueText)
    {
        var lbl = NewText(panel, label + "Label", 34f, TextAlignmentOptions.MidlineLeft);
        lbl.text = label;
        lbl.color = buttonTextColor;
        var lrt = lbl.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0f, 1f);
        lrt.sizeDelta = new Vector2(150f, 44f);
        lrt.anchoredPosition = new Vector2(0f, y);

        var slider = CreateSlider(panel, initial, onChanged);
        var srt = slider.GetComponent<RectTransform>();
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
        srt.sizeDelta = new Vector2(380f, 30f);
        srt.anchoredPosition = new Vector2(170f, y - 7f);

        var val = NewText(panel, label + "Value", 34f, TextAlignmentOptions.MidlineRight);
        val.color = buttonTextColor;
        var vrt = val.rectTransform;
        vrt.anchorMin = vrt.anchorMax = vrt.pivot = new Vector2(0f, 1f);
        vrt.sizeDelta = new Vector2(110f, 44f);
        vrt.anchoredPosition = new Vector2(560f, y);

        valueText = val;
        return slider;
    }

    // ---- Audio slider callbacks ----

    void OnMusicChanged(float v)
    {
        if (AudioManager.Instance != null) AudioManager.Instance.SetMusicVolume(v);
        GameSettings.MusicVolume = v;               // persist across sessions
        UpdateMusicValueText(v);
    }

    void OnSfxChanged(float v)
    {
        if (AudioManager.Instance != null) AudioManager.Instance.SetSfxVolume(v);
        GameSettings.SfxVolume = v;                 // persist across sessions
        UpdateSfxValueText(v);
        AudioManager.PlayMenuMove();                // tick at the NEW level so the slider previews SFX loudness
    }

    void UpdateMusicValueText(float v) { if (musicValueText != null) musicValueText.text = Mathf.RoundToInt(v * 100f) + "%"; }
    void UpdateSfxValueText(float v)   { if (sfxValueText   != null) sfxValueText.text   = Mathf.RoundToInt(v * 100f) + "%"; }

    static float InitialMusic() => AudioManager.Instance != null ? AudioManager.Instance.MusicVolume : GameSettings.MusicVolume;
    static float InitialSfx()   => AudioManager.Instance != null ? AudioManager.Instance.SfxVolume   : GameSettings.SfxVolume;

    // -------------------------------------------------------
    //  Video / Graphics screen
    // -------------------------------------------------------

    GameObject BuildVideoPanel(Transform parent)
    {
        var go = new GameObject("VideoPanel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);   // upper-left, like the button column
        rt.sizeDelta = new Vector2(760f, 380f);
        rt.anchoredPosition = new Vector2(96f, -320f);

        var resLabels = BuildResolutionOptions(out resolutionOptions, out int resStart);
        resolutionSelector = BuildOptionRow(go.transform, "RESOLUTION",   -10f,  resLabels,          resStart,                            OnResolutionChanged);

        var fsLabels = new List<string> { "Fullscreen", "Borderless", "Windowed" };
        fullscreenSelector = BuildOptionRow(go.transform, "DISPLAY MODE", -80f,  fsLabels,           CurrentFullscreenIndex(),            OnFullscreenChanged);

        var qLabels = new List<string>(QualitySettings.names);
        int qStart = Mathf.Clamp(QualitySettings.GetQualityLevel(), 0, Mathf.Max(0, qLabels.Count - 1));
        qualitySelector = BuildOptionRow(go.transform, "QUALITY",         -150f, qLabels,            qStart,                              OnQualityChanged);

        var vLabels = new List<string> { "Off", "On" };
        vsyncSelector = BuildOptionRow(go.transform, "V-SYNC",            -220f, vLabels,            QualitySettings.vSyncCount > 0 ? 1 : 0, OnVSyncChanged);

        WireVerticalSelectors(new Selectable[] { resolutionSelector, fullscreenSelector, qualitySelector, vsyncSelector });

        go.SetActive(false);
        return go;
    }

    /// <summary>One labelled option row: "LABEL   &lt;  value  &gt;". Returns the cycler. Positioned manually
    /// (top-left origin, y grows downward), matching the audio rows.</summary>
    OptionSelector BuildOptionRow(Transform panel, string label, float y, IList<string> options,
                                  int startIndex, System.Action<int> onChanged)
    {
        var lbl = NewText(panel, label + "Label", 32f, TextAlignmentOptions.MidlineLeft);
        lbl.text = label;
        lbl.color = buttonTextColor;
        var lrt = lbl.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0f, 1f);
        lrt.sizeDelta = new Vector2(300f, 50f);
        lrt.anchoredPosition = new Vector2(0f, y);

        var sel = CreateOptionSelector(panel, options, startIndex, onChanged);
        var srt = (RectTransform)sel.transform;
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
        srt.sizeDelta = new Vector2(380f, 50f);
        srt.anchoredPosition = new Vector2(310f, y);

        return sel;
    }

    OptionSelector CreateOptionSelector(Transform parent, IList<string> options, int startIndex, System.Action<int> onChanged)
    {
        var go = new GameObject("OptionSelector", typeof(RectTransform), typeof(Image), typeof(OptionSelector));
        go.transform.SetParent(parent, false);

        var img = go.GetComponent<Image>();
        img.color = Color.white;   // ColorBlock states provide the real colours, like the buttons

        var sel = go.GetComponent<OptionSelector>();
        sel.transition = Selectable.Transition.ColorTint;
        sel.targetGraphic = img;
        var cb = sel.colors;
        cb.normalColor = buttonNormalColor;
        cb.highlightedColor = buttonHighlightedColor;
        cb.selectedColor = buttonSelectedColor;
        cb.pressedColor = buttonPressedColor;
        cb.disabledColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        cb.colorMultiplier = 1f; cb.fadeDuration = buttonColorFadeDuration;
        sel.colors = cb;

        var valueLabel = NewText(go.transform, "Value", 30f, TextAlignmentOptions.Center);
        valueLabel.color = buttonTextColor;
        var vrt = valueLabel.rectTransform;
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;

        sel.Configure(valueLabel, options, startIndex);
        sel.OnChanged += _ => AudioManager.PlayMenuMove();   // tick on each cycle (matches list/slider feedback)
        sel.OnChanged += onChanged;
        return sel;
    }

    /// <summary>Builds the de-duplicated (by width×height) resolution labels, outs the matching
    /// width/height list and the index of the current (or saved) resolution.</summary>
    List<string> BuildResolutionOptions(out List<Vector2Int> options, out int currentIndex)
    {
        options = new List<Vector2Int>();
        var labels = new List<string>();
        var seen = new HashSet<long>();

        foreach (var r in Screen.resolutions)
        {
            long key = ((long)r.width << 32) | (uint)r.height;
            if (!seen.Add(key)) continue;
            options.Add(new Vector2Int(r.width, r.height));
            labels.Add(r.width + " x " + r.height);
        }

        if (options.Count == 0)   // editor / headless fallback: at least the current resolution
        {
            options.Add(new Vector2Int(Screen.width, Screen.height));
            labels.Add(Screen.width + " x " + Screen.height);
        }

        int targetW = GameSettings.HasResolution ? GameSettings.ResolutionWidth  : Screen.width;
        int targetH = GameSettings.HasResolution ? GameSettings.ResolutionHeight : Screen.height;
        currentIndex = 0;
        for (int i = 0; i < options.Count; i++)
            if (options[i].x == targetW && options[i].y == targetH) { currentIndex = i; break; }

        return labels;
    }

    int CurrentFullscreenIndex()
    {
        var mode = Screen.fullScreenMode;
        for (int i = 0; i < FullscreenModes.Length; i++)
            if (FullscreenModes[i] == mode) return i;
        return 1;   // MaximizedWindow (mac) or anything unlisted → Borderless
    }

    FullScreenMode SelectedFullscreenMode()
    {
        int i = fullscreenSelector != null ? fullscreenSelector.Index : CurrentFullscreenIndex();
        return FullscreenModes[Mathf.Clamp(i, 0, FullscreenModes.Length - 1)];
    }

    // ---- Video change handlers (apply live + persist) ----

    void OnResolutionChanged(int index)
    {
        if (resolutionOptions == null || index < 0 || index >= resolutionOptions.Count) return;
        var r = resolutionOptions[index];
        GameSettings.SetResolution(r.x, r.y);
        Screen.SetResolution(r.x, r.y, SelectedFullscreenMode());
    }

    void OnFullscreenChanged(int index)
    {
        var mode = FullscreenModes[Mathf.Clamp(index, 0, FullscreenModes.Length - 1)];
        GameSettings.FullScreenModeValue = (int)mode;
        Screen.fullScreenMode = mode;
    }

    void OnQualityChanged(int index)
    {
        GameSettings.QualityLevel = index;
        QualitySettings.SetQualityLevel(index, true);

        // Each quality level carries its own vSync; re-apply the player's choice so it isn't clobbered,
        // and keep the V-SYNC selector's shown value in sync with the result.
        if (GameSettings.HasVSync) QualitySettings.vSyncCount = GameSettings.VSync;
        if (vsyncSelector != null) vsyncSelector.SetIndexSilent(QualitySettings.vSyncCount > 0 ? 1 : 0);
    }

    void OnVSyncChanged(int index)
    {
        GameSettings.VSync = index;              // 0 = off, 1 = on
        QualitySettings.vSyncCount = index;
    }

    /// <summary>Explicit Up/Down wrap navigation for a column of selectables, with Left/Right cleared so
    /// each <see cref="OptionSelector"/> keeps them for cycling its own value.</summary>
    static void WireVerticalSelectors(Selectable[] items)
    {
        if (items == null) return;
        int n = items.Length;
        for (int i = 0; i < n; i++)
        {
            var s = items[i];
            if (s == null) continue;
            var nav = s.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp   = items[(i - 1 + n) % n];
            nav.selectOnDown = items[(i + 1) % n];
            nav.selectOnLeft = null; nav.selectOnRight = null;
            s.navigation = nav;
        }
    }

    // -------------------------------------------------------
    //  Controls screen (rebinding)
    // -------------------------------------------------------

    GameObject BuildControlsPanel(Transform parent)
    {
        // The menu rebinds its OWN instance (never enabled for input) and persists the override diff.
        controlsForRebind = new GeneracerControls();
        InputRebinding.ApplyOverridesTo(controlsForRebind.asset);   // start from the player's saved rebinds
        var map = controlsForRebind.Driving.Get();

        var go = new GameObject("ControlsPanel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);   // left-centre: the list centres vertically
        rt.anchoredPosition = new Vector2(96f, controlsPanelOffsetY);     // vertically centred (offset editable)

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = controlsRowSpacing;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // How-to line at the top.
        var hint = NewText(go.transform, "Hint", 22f, TextAlignmentOptions.MidlineLeft);
        hint.text = "A: Rebind      B: Back      Start / Esc: cancel a rebind";
        hint.color = new Color(0.75f, 0.78f, 0.85f, 1f);
        var hintLE = hint.gameObject.AddComponent<LayoutElement>();
        hintLE.preferredWidth = 680f; hintLE.minWidth = 680f;
        hintLE.preferredHeight = 30f; hintLE.minHeight = 30f;

        bindingRows.Clear();
        var buttons = new List<Button>();

        // One row per rebindable binding: normal bindings and each part of a composite (e.g. Throttle +/-),
        // but not the composite parent itself.
        foreach (var action in map.actions)
        {
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isComposite) continue;

                var capturedAction = action;   // capture per row for the click closure
                int bindingIndex = i;
                string rowName = FriendlyActionName(action.name) + (b.isPartOfComposite ? " (" + PartLabel(b.name) + ")" : "");

                var row = BuildBindingRow(go.transform, rowName, out TextMeshProUGUI valueLabel);
                valueLabel.text = capturedAction.GetBindingDisplayString(bindingIndex);
                row.onClick.AddListener(() => StartRebind(capturedAction, bindingIndex, valueLabel));

                buttons.Add(row);
                bindingRows.Add(new BindingRow { action = capturedAction, bindingIndex = bindingIndex, valueLabel = valueLabel });
            }
        }

        var reset = CreateButton("RESET TO DEFAULTS", go.transform, OnResetControls);
        var resetLE = reset.GetComponent<LayoutElement>();   // match the compact row size (CreateButton sizes for the big menu)
        if (resetLE != null)
        {
            resetLE.preferredWidth = 680f; resetLE.minWidth = 680f;
            resetLE.preferredHeight = controlsRowHeight; resetLE.minHeight = controlsRowHeight;
        }
        var resetLabel = reset.GetComponentInChildren<TextMeshProUGUI>();
        if (resetLabel != null) resetLabel.fontSize = controlsRowFontSize;
        buttons.Add(reset);

        MenuNavigation.WireVerticalWrap(buttons);
        controlsFirstButton = buttons.Count > 0 ? buttons[0].gameObject : null;

        go.SetActive(false);
        return go;
    }

    static string PartLabel(string partName)
    {
        if (partName == "positive") return "+";
        if (partName == "negative") return "-";
        return partName;
    }

    // Prettier display names for a few action ids in the CONTROLS list (the action's own name is the key).
    static string FriendlyActionName(string actionName)
    {
        switch (actionName)
        {
            case "SD":        return "SD Card";
            case "RearView":  return "Rear View";
            case "SelfLevel": return "Self-Level";
            default:          return actionName;
        }
    }

    /// <summary>A control row: action name on the left, current binding on the right, the whole row a
    /// Button that A rebinds. Outs the value label so it can be refreshed after a rebind/reset.</summary>
    Button BuildBindingRow(Transform parent, string rowName, out TextMeshProUGUI valueLabel)
    {
        var go = new GameObject(rowName + " Row", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Color.white;   // ColorBlock states provide the real colours

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 680f; le.minWidth = 680f;
        le.preferredHeight = controlsRowHeight; le.minHeight = controlsRowHeight;

        var btn = go.GetComponent<Button>();
        ApplyMenuButtonColors(btn);
        btn.onClick.AddListener(AudioManager.PlayMenuSelect);

        var nameLabel = NewText(go.transform, "Name", controlsRowFontSize, TextAlignmentOptions.MidlineLeft);
        nameLabel.text = rowName;
        nameLabel.color = buttonTextColor;
        var nrt = nameLabel.rectTransform;
        nrt.anchorMin = Vector2.zero; nrt.anchorMax = Vector2.one;
        nrt.offsetMin = new Vector2(24f, 0f); nrt.offsetMax = new Vector2(-24f, 0f);

        valueLabel = NewText(go.transform, "Value", controlsRowFontSize, TextAlignmentOptions.MidlineRight);
        valueLabel.color = Color.white;
        valueLabel.fontStyle = FontStyles.Bold;
        var vrt = valueLabel.rectTransform;
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = new Vector2(24f, 0f); vrt.offsetMax = new Vector2(-24f, 0f);

        return btn;
    }

    void ApplyMenuButtonColors(Selectable s)
    {
        var cb = s.colors;
        cb.normalColor = buttonNormalColor;
        cb.highlightedColor = buttonHighlightedColor;
        cb.selectedColor = buttonSelectedColor;
        cb.pressedColor = buttonPressedColor;
        cb.disabledColor = new Color(0.30f, 0.30f, 0.30f, 0.50f);
        cb.colorMultiplier = 1f; cb.fadeDuration = buttonColorFadeDuration;
        s.colors = cb;
    }

    void StartRebind(InputAction action, int bindingIndex, TextMeshProUGUI valueLabel)
    {
        if (rebinding || action == null) return;
        rebinding = true;

        // Stop the UI from reacting to the very button press we're about to capture (else it would also
        // read as a Submit/navigation on the menu).
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = false;
        if (valueLabel != null) valueLabel.text = "[ press ]";

        string prevOverride = action.bindings[bindingIndex].overridePath;   // captured so a conflict can revert
        action.Disable();   // interactive rebinding requires the action be disabled
        rebindOp = action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("<Mouse>")
            .WithControlsExcluding("<Gamepad>/start")   // reserved as the gamepad cancel (see Update), never bound
            .WithCancelingThrough("<Keyboard>/escape")
            .OnMatchWaitForAnother(0.1f)
            .OnComplete(op => FinishRebind(action, bindingIndex, valueLabel, prevOverride, true))
            .OnCancel(op => FinishRebind(action, bindingIndex, valueLabel, prevOverride, false))
            .Start();
    }

    void FinishRebind(InputAction action, int bindingIndex, TextMeshProUGUI valueLabel, string prevOverride, bool completed)
    {
        if (rebindOp != null) { rebindOp.Dispose(); rebindOp = null; }

        // Reject a rebind onto a control already used elsewhere in the map: revert it, flag it, buzz.
        if (completed && InputRebinding.IsBindingInConflict(action, bindingIndex, out string conflictName))
        {
            InputRebinding.RevertBinding(action, bindingIndex, prevOverride);
            if (controlsForRebind != null) InputRebinding.Save(controlsForRebind.asset);
            AudioManager.PlayStoreDenied();
            Debug.Log($"[Rebind] Control already used by '{conflictName}' — rebind rejected.");
            StartCoroutine(ShowConflictThenRestore(action, bindingIndex, valueLabel));
            return;
        }

        if (controlsForRebind != null) InputRebinding.Save(controlsForRebind.asset);   // persist the override
        if (valueLabel != null) valueLabel.text = action.GetBindingDisplayString(bindingIndex);
        StartCoroutine(EndRebindCooldown());
    }

    // Re-enable menu navigation a beat after the rebind, so releasing the captured button doesn't leak
    // through as a stray Submit on the row we just rebound.
    IEnumerator EndRebindCooldown()
    {
        yield return new WaitForSecondsRealtime(0.2f);
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = true;
        rebinding = false;
    }

    // Briefly shows "in use" on the row after a rejected (conflicting) rebind, then restores the label to
    // the binding it reverted to, and re-enables navigation.
    IEnumerator ShowConflictThenRestore(InputAction action, int bindingIndex, TextMeshProUGUI valueLabel)
    {
        if (valueLabel != null) valueLabel.text = "in use";
        yield return new WaitForSecondsRealtime(1.1f);
        if (valueLabel != null) valueLabel.text = action.GetBindingDisplayString(bindingIndex);
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = true;
        rebinding = false;
    }

    void OnResetControls()
    {
        if (rebinding || controlsForRebind == null) return;
        InputRebinding.ResetToDefaults(controlsForRebind.asset);
        foreach (var row in bindingRows)
            if (row.valueLabel != null) row.valueLabel.text = row.action.GetBindingDisplayString(row.bindingIndex);
    }

    // ---- Slider construction ----

    Slider CreateSlider(Transform parent, float value, UnityEngine.Events.UnityAction<float> onChanged)
    {
        var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        var slider = go.GetComponent<Slider>();

        // Background bar.
        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        var bgrt = bg.GetComponent<RectTransform>();
        bgrt.anchorMin = new Vector2(0f, 0.25f); bgrt.anchorMax = new Vector2(1f, 0.75f);
        bgrt.offsetMin = Vector2.zero; bgrt.offsetMax = Vector2.zero;

        // Fill — the slider drives its right edge from the value (anchors are overwritten each Set).
        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        var fart = fillArea.GetComponent<RectTransform>();
        fart.anchorMin = new Vector2(0f, 0.25f); fart.anchorMax = new Vector2(1f, 0.75f);
        fart.offsetMin = new Vector2(8f, 0f); fart.offsetMax = new Vector2(-18f, 0f);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        fill.GetComponent<Image>().color = buttonHighlightedColor;   // orange accent
        var fillrt = fill.GetComponent<RectTransform>();
        fillrt.anchorMin = new Vector2(0f, 0f); fillrt.anchorMax = new Vector2(1f, 1f);
        fillrt.offsetMin = Vector2.zero; fillrt.offsetMax = Vector2.zero;

        // Handle — the slider drives its position from the value.
        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(go.transform, false);
        var hart = handleArea.GetComponent<RectTransform>();
        hart.anchorMin = new Vector2(0f, 0f); hart.anchorMax = new Vector2(1f, 1f);
        hart.offsetMin = new Vector2(10f, 0f); hart.offsetMax = new Vector2(-10f, 0f);

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        var handleImg = handle.GetComponent<Image>();
        handleImg.color = Color.white;
        var handlert = handle.GetComponent<RectTransform>();
        handlert.sizeDelta = new Vector2(22f, 0f);

        slider.fillRect = fillrt;
        slider.handleRect = handlert;
        slider.targetGraphic = handleImg;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f; slider.maxValue = 1f; slider.wholeNumbers = false;
        slider.value = value;                          // set BEFORE the listener so init doesn't fire onChanged
        slider.onValueChanged.AddListener(onChanged);

        var cb = slider.colors;                        // make gamepad focus visible on the handle
        cb.normalColor = Color.white;
        cb.highlightedColor = buttonHighlightedColor;
        cb.selectedColor = buttonSelectedColor;
        cb.pressedColor = buttonPressedColor;
        cb.disabledColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        cb.colorMultiplier = 1f; cb.fadeDuration = buttonColorFadeDuration;
        slider.colors = cb;

        return slider;
    }

    /// <summary>Wires two sliders for Up/Down wrap navigation between them, leaving Left/Right free so
    /// each slider consumes those to change its own value.</summary>
    static void WireSliderPair(Slider a, Slider b)
    {
        SetSliderVertNav(a, b);
        SetSliderVertNav(b, a);
    }

    static void SetSliderVertNav(Slider s, Selectable other)
    {
        var nav = s.navigation;
        nav.mode = Navigation.Mode.Explicit;
        nav.selectOnUp = other; nav.selectOnDown = other;   // only two rows, so up and down both wrap to the other
        nav.selectOnLeft = null; nav.selectOnRight = null;  // stay on the slider for value change
        s.navigation = nav;
    }

    static TextMeshProUGUI NewText(Transform parent, string name, float fontSize, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        t.enableWordWrapping = false;
        return t;
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

        buttonsColumn = menu.gameObject;
        settingsButton = settings;
        onlineButton = online;

        // The SETTINGS screens (category chooser + Audio/Video/Controls panels) — hidden until used.
        BuildSettingsScreens(canvasGO.transform);

        // The Online Multiplayer lobby flow (hidden until the button opens it).
        lobbyUI = canvasGO.AddComponent<MultiplayerLobbyUI>();
        lobbyUI.Configure(this, canvasGO.transform, multiplayerCars, OnLobbyExit);

        // Remote players' puppet cars are built from this same catalog (prefab references are asset
        // references, so the catalog outlives this menu scene).
        PlayerRegistry.SetCarCatalog(multiplayerCars);

        // Pre-select Start so a gamepad/keyboard can navigate the menu immediately.
        FocusFirst(start != null ? start.gameObject : null);
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

    void OnOnlineMultiplayer()
    {
        if (lobbyUI == null) return;
        if (buttonsColumn != null) buttonsColumn.SetActive(false);
        lobbyUI.Open();
    }

    /// <summary>The lobby flow backed all the way out — restore the main list, land on ONLINE MULTIPLAYER.</summary>
    void OnLobbyExit()
    {
        if (buttonsColumn != null) buttonsColumn.SetActive(true);
        FocusFirst(onlineButton != null ? onlineButton.gameObject : null);
    }

    void OnSettings() => ShowCategories();

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
