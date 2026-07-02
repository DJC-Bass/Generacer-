using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using TMPro;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Car-selection screen, built entirely in code (same style as the main menu). A vertical list
/// of vehicle names runs down the left; hovering/navigating one shows that car's prefab on the
/// right, rendered to a panel and spinning 360° on its Y axis.
///
/// Instead of on-screen buttons, the bottom-middle shows the prompts "A: START   B: BACK":
///   • A (gamepad South) — stores the highlighted car and loads Bootstrap (begins the game loop).
///   • B (gamepad East)  — returns to the main menu.
///
/// Drop on one GameObject in the CarSelection scene and fill the Cars list in the Inspector.
/// Spawns its own camera/light/EventSystem, so no extra scene setup is needed.
/// </summary>
public class CarSelectionController : MonoBehaviour
{
    [System.Serializable]
    public struct CarEntry
    {
        public string name;       // shown in the list
        public GameObject prefab; // shown rotating on the right
    }

    [Header("Vehicles")]
    [Tooltip("The selectable cars: a display name + the car prefab shown rotating on the right. " +
             "Drag your car prefabs here and give each a name.")]
    public CarEntry[] cars;

    [Header("Background")]
    [Tooltip("Optional full-screen background sprite. Empty = solid Background Color.")]
    public Sprite backgroundSprite;
    public Color backgroundColor = new Color(0.06f, 0.07f, 0.10f, 1f);

    [Header("Preview")]
    [Tooltip("Backdrop colour behind the rotating car preview panel.")]
    public Color previewBackdropColor = new Color(0.12f, 0.13f, 0.16f, 1f);
    [Tooltip("Preview spin speed around the car's Y axis (degrees/second).")]
    public float rotationSpeed = 35f;
    [Tooltip("Preview camera yaw — a 3/4 viewing angle (degrees).")]
    public float previewYaw = 22f;
    [Tooltip("Preview camera pitch — how far it looks down on the car (degrees).")]
    public float previewPitch = 10f;
    [Tooltip("Extra room around the car when framing it (1 = tight).")]
    public float framingMargin = 1.2f;
    [Tooltip("On-screen size of the square preview panel (pixels).")]
    public float previewPanelSize = 880f;

    [Header("List")]
    public float listFontSize = 34f;
    public Vector2 listEntrySize = new Vector2(440f, 64f);
    public float listSpacing = 10f;

    [Header("Bottom Prompt")]
    [Tooltip("Font size of the 'A: START  B: BACK' prompt at the bottom.")]
    public float buttonFontSize = 34f;
    [Tooltip("Unused now that the bottom uses a text prompt (kept for compatibility).")]
    public Vector2 buttonSize = new Vector2(260f, 76f);

    [Header("List Entry Colors")]
    public Color normalColor = new Color(0f, 0f, 0f, 0.55f);
    public Color highlightedColor = new Color(0.90f, 0.45f, 0.12f, 0.95f);
    public Color pressedColor = new Color(0.70f, 0.32f, 0.07f, 1f);
    public Color selectedColor = new Color(0.90f, 0.45f, 0.12f, 0.95f);
    public Color textColor = Color.white;

    [Header("Scene Routing")]
    [Tooltip("Scene B (BACK) returns to.")]
    public string mainMenuSceneName = "MainMenu";
    [Tooltip("Scene A (START) loads to begin the game loop (the Bootstrap scene).")]
    public string bootstrapSceneName = "Bootstrap";

    // ---- runtime ----
    private Camera previewCamera;
    private RenderTexture previewRT;
    private Transform previewRoot;
    private GameObject previewInstance;
    private Vector3 previewCenter;
    private int previewIndex = -1;
    private int selectedIndex = -1;
    private GameObject firstEntry;
    private GameObject lastSelectedForSfx;   // tracks the highlighted car so we can fire the "move" SFX

    void Start()
    {
        EnsureEventSystem();
        BuildPreviewRig();
        BuildUI();

        // Default the pick to the first car so the preview isn't empty and A has something to start.
        if (cars != null && cars.Length > 0)
        {
            SelectCar(0);
            if (firstEntry != null && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(firstEntry);
        }
    }

    void Update()
    {
        // Spin the preview around the car's vertical centre line.
        if (previewInstance != null && rotationSpeed != 0f)
            previewInstance.transform.RotateAround(previewCenter, Vector3.up, rotationSpeed * Time.deltaTime);

#if ENABLE_INPUT_SYSTEM
        var gp = Gamepad.current;
        if (gp != null)
        {
            if (gp.buttonSouth.wasPressedThisFrame) StartGame();        // A -> Bootstrap (start)
            else if (gp.buttonEast.wasPressedThisFrame) GoToMainMenu();  // B -> Main Menu (back)
        }
#endif
    }

    void LateUpdate()
    {
        // If a mouse click cleared the selection, pressing Up/Down re-highlights the first car so
        // D-pad / arrow navigation never soft-locks.
        MenuNavigation.EnsureSelectionOnNavigate(firstEntry);

        // Play the "move" SFX whenever the highlighted car changes (list navigation).
        MenuNavigation.PlayMoveSfxOnSelectionChange(ref lastSelectedForSfx);
    }

    void OnDestroy()
    {
        if (previewCamera != null) previewCamera.targetTexture = null;
        if (previewRT != null) { previewRT.Release(); Destroy(previewRT); }
    }

    // -------------------------------------------------------
    //  3D preview rig (camera + light + render texture)
    // -------------------------------------------------------

    void BuildPreviewRig()
    {
        previewRoot = new GameObject("PreviewRoot").transform;
        previewRoot.position = Vector3.zero;

        var lightGO = new GameObject("PreviewLight");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGO.transform.rotation = Quaternion.Euler(45f, 0f, 0f);

        previewRT = new RenderTexture(1024, 1024, 16) { name = "CarPreviewRT" };

        var camGO = new GameObject("PreviewCamera");
        camGO.AddComponent<AudioListener>();
        previewCamera = camGO.AddComponent<Camera>();
        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = previewBackdropColor;
        previewCamera.fieldOfView = 35f;
        previewCamera.targetTexture = previewRT;
    }

    // -------------------------------------------------------
    //  UI
    // -------------------------------------------------------

    void BuildUI()
    {
        var canvasGO = new GameObject("CarSelectionCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();
        var root = canvasGO.transform;

        BuildBackground(root);
        BuildPreviewPanel(root);   // right
        BuildTitle(root);          // top-left
        BuildList(root);           // left
        BuildInstructions(root);   // bottom-middle prompts
    }

    void BuildBackground(Transform parent)
    {
        var go = new GameObject("Background", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        var rt = img.rectTransform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        if (backgroundSprite != null) { img.sprite = backgroundSprite; img.type = Image.Type.Simple; img.color = Color.white; }
        else img.color = backgroundColor;
    }

    void BuildPreviewPanel(Transform parent)
    {
        var go = new GameObject("PreviewPanel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var raw = go.AddComponent<RawImage>();
        raw.texture = previewRT;
        var rt = raw.rectTransform;
        rt.anchorMin = new Vector2(1f, 0.5f);   // pinned to the right edge, vertically centred
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(previewPanelSize, previewPanelSize);
        rt.anchoredPosition = new Vector2(-80f, 40f);
    }

    void BuildTitle(Transform parent)
    {
        var go = new GameObject("Title", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = "SELECT VEHICLE";
        tmp.fontSize = 64f;
        tmp.color = textColor;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableWordWrapping = false;
        var rt = tmp.rectTransform;
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(800f, 90f);
        rt.anchoredPosition = new Vector2(80f, -70f);
    }

    void BuildList(Transform parent)
    {
        var go = new GameObject("CarList", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(80f, -200f);

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = listSpacing;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (cars == null || cars.Length == 0)
        {
            MakeListMessage(go.transform, "No vehicles configured.\nAdd them to 'Cars' in the Inspector.");
            return;
        }

        var entries = new Button[cars.Length];
        for (int i = 0; i < cars.Length; i++)
        {
            int idx = i;
            string carName = string.IsNullOrEmpty(cars[i].name) ? $"Car {i + 1}" : cars[i].name;
            var btn = CreateListButton(carName, go.transform, () => SelectCar(idx));
            btn.gameObject.AddComponent<CarListEntry>().Init(this, idx);
            entries[i] = btn;
            if (i == 0) firstEntry = btn.gameObject;
        }

        // Up on the top car wraps to the bottom and vice-versa.
        MenuNavigation.WireVerticalWrap(entries);
    }

    void BuildInstructions(Transform parent)
    {
        var go = new GameObject("Instructions", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = "A: START      B: BACK";
        tmp.fontSize = buttonFontSize;
        tmp.color = textColor;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        var rt = tmp.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0f); rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(900f, 70f);
        rt.anchoredPosition = new Vector2(0f, 40f);
    }

    Button CreateListButton(string label, Transform parent, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Color.white;   // white graphic so ColorBlock tints show true

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = listEntrySize.x; le.preferredHeight = listEntrySize.y;
        le.minWidth = listEntrySize.x; le.minHeight = listEntrySize.y;

        var btn = go.GetComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = normalColor;
        cb.highlightedColor = highlightedColor;
        cb.selectedColor = selectedColor;
        cb.pressedColor = pressedColor;
        cb.colorMultiplier = 1f; cb.fadeDuration = 0.1f;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);

        var textGO = new GameObject("Label", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = listFontSize; tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.Center; tmp.enableWordWrapping = false;
        var trt = tmp.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

        return btn;
    }

    void MakeListMessage(Transform parent, string text)
    {
        var go = new GameObject("ListMessage", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.fontSize = listFontSize * 0.7f; tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = listEntrySize.x; le.preferredHeight = listEntrySize.y * 2f;
    }

    // -------------------------------------------------------
    //  Selection + actions
    // -------------------------------------------------------

    /// <summary>Makes a car the current pick and shows its rotating preview. Driven by hovering
    /// (mouse) or navigating (gamepad) the list; A then starts the game with this car.</summary>
    public void SelectCar(int index)
    {
        if (cars == null || index < 0 || index >= cars.Length) return;
        selectedIndex = index;
        PreviewCar(index);
    }

    void PreviewCar(int index)
    {
        if (cars == null || index < 0 || index >= cars.Length) return;
        if (index == previewIndex && previewInstance != null) return;   // already shown

        previewIndex = index;
        if (previewInstance != null) Destroy(previewInstance);

        var prefab = cars[index].prefab;
        if (prefab == null) return;

        previewInstance = Instantiate(prefab, previewRoot);
        previewInstance.transform.localPosition = Vector3.zero;
        previewInstance.transform.localRotation = Quaternion.identity;
        MakeInert(previewInstance);

        previewCenter = ComputeBounds(previewInstance).center;
        FramePreviewCamera(previewInstance);
    }

    /// <summary>A: store the chosen car and load Bootstrap to begin the game loop.</summary>
    void StartGame()
    {
        AudioManager.PlayMenuSelect();   // "select" SFX on A / START
        if (selectedIndex >= 0 && selectedIndex < cars.Length)
            SelectedCarStore.Set(cars[selectedIndex].name, cars[selectedIndex].prefab);
        LoadScene(bootstrapSceneName, "Start");
    }

    /// <summary>B: return to the main menu.</summary>
    void GoToMainMenu()
    {
        AudioManager.PlayMenuBack();   // "back" SFX on B / BACK
        LoadScene(mainMenuSceneName, "Back");
    }

    void LoadScene(string sceneName, string label)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogWarning($"[CarSelection] {label}: no scene name set.");
            return;
        }
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning($"[CarSelection] {label}: scene '{sceneName}' isn't in Build Settings yet.");
            return;
        }
        SceneManager.LoadScene(sceneName);
    }

    // -------------------------------------------------------
    //  Preview helpers
    // -------------------------------------------------------

    void FramePreviewCamera(GameObject car)
    {
        if (previewCamera == null) return;
        Bounds b = ComputeBounds(car);
        float radius = Mathf.Max(0.1f, b.extents.magnitude);   // bounding sphere — rotation-safe framing
        float fov = previewCamera.fieldOfView * Mathf.Deg2Rad;
        float dist = radius / Mathf.Sin(fov * 0.5f) * framingMargin;

        Vector3 dir = Quaternion.Euler(previewPitch, previewYaw, 0f) * Vector3.back;
        previewCamera.transform.position = b.center + dir * dist;
        previewCamera.transform.LookAt(b.center);
        previewCamera.nearClipPlane = Mathf.Max(0.05f, dist - radius * 2f);
        previewCamera.farClipPlane = dist + radius * 4f;
    }

    static Bounds ComputeBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one);
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }

    /// <summary>Strips a preview clone down to a static display: no scripts, physics or colliders,
    /// so the gameplay car prefab can be shown without driving itself around the menu.</summary>
    static void MakeInert(GameObject go)
    {
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }
        foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
        foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) { a.Stop(); a.enabled = false; }   // silent preview (engine, etc.)
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        go.AddComponent<InputSystemUIInputModule>();
#else
        go.AddComponent<StandaloneInputModule>();
#endif
    }
}
