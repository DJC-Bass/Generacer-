using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Always-visible readout of the player's crafted Turbo and Jet counts, anchored
/// to the bottom-left of the screen (Turbo on the left, Jet to its right with a
/// gap). Persistent (lives on the same DontDestroyOnLoad object as
/// PlayerInventory), so it shows in every scene with no setup, and updates live
/// via <see cref="PlayerInventory.OnChanged"/>.
/// </summary>
[DefaultExecutionOrder(1000)]
public class TurboJetHUD : MonoBehaviour
{
    public static TurboJetHUD Instance { get; private set; }

    [Tooltip("Inventory item names to display.")]
    public string turboItem = "Turbo";
    public string jetItem = "Jet";
    public string shieldItem = "Shield";

    private TextMeshProUGUI turboLabel;
    private TextMeshProUGUI jetLabel;
    private TextMeshProUGUI shieldLabel;
    private GameObject canvasGO;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        BuildUI();
    }

    void OnEnable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged += Refresh;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Refresh();
        ApplyVisibility();
    }

    void OnDisable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged -= Refresh;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // Gameplay HUDs are hidden outside gameplay scenes (e.g. the main menu).
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ApplyVisibility();

    void ApplyVisibility()
    {
        if (canvasGO != null)
            canvasGO.SetActive(GameplayHud.VisibleInScene(SceneManager.GetActiveScene().name));
    }

    void Refresh()
    {
        var inv = PlayerInventory.Instance;
        if (turboLabel != null) turboLabel.text = $"Turbo: {(inv != null ? inv.GetCount(turboItem) : 0)}";
        if (jetLabel != null) jetLabel.text = $"Jet: {(inv != null ? inv.GetCount(jetItem) : 0)}";
        if (shieldLabel != null) shieldLabel.text = $"Shield: {(inv != null ? inv.GetCount(shieldItem) : 0)}";
    }

    void BuildUI()
    {
        canvasGO = new GameObject("TurboJetHUDCanvas");
        DontDestroyOnLoad(canvasGO);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 150;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Turbo on the left, Jet to its right, Shield to the right of that — each offset by a gap.
        turboLabel = MakeLabel(canvasGO.transform, "TurboText",
                               new Color(0.45f, 0.65f, 1f), anchoredX: 30f, width: 300f);
        jetLabel = MakeLabel(canvasGO.transform, "JetText",
                             new Color(0.65f, 0.82f, 1f), anchoredX: 360f, width: 300f);
        shieldLabel = MakeLabel(canvasGO.transform, "ShieldText",
                                new Color(0.55f, 1f, 0.85f), anchoredX: 690f, width: 300f);

        UiLayer.Apply(canvasGO);   // keep code-built UI off the Default layer (see UiLayer)
    }

    static TextMeshProUGUI MakeLabel(Transform parent, string name, Color color,
                                     float anchoredX, float width)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = 44;
        t.color = color;
        t.alignment = TextAlignmentOptions.BottomLeft;

        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);  // bottom-left
        rt.sizeDelta = new Vector2(width, 64f);
        rt.anchoredPosition = new Vector2(anchoredX, 24f);
        return t;
    }
}
