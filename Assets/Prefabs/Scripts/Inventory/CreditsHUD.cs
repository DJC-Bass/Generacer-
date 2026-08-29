using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Always-visible credits readout anchored to the top-left of the screen.
/// Persistent (lives on the same DontDestroyOnLoad object as PlayerInventory),
/// so it shows in every scene and the displayed balance carries over from the
/// TrackScene back to the hub. Builds its own Canvas — no scene setup required.
///
/// Reacts to <see cref="PlayerInventory.OnChanged"/>, so awarding currency
/// anywhere (e.g. a track pickup calling
/// <c>PlayerInventory.Instance.AddCredits(amount)</c>) updates this instantly.
/// </summary>
[DefaultExecutionOrder(1000)]
public class CreditsHUD : MonoBehaviour
{
    public static CreditsHUD Instance { get; private set; }

    [Tooltip("Text shown before the amount.")]
    public string prefix = "Credits: ";

    private TextMeshProUGUI label;
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
        GameplayHud.OnVisibilityChanged += ApplyVisibility;
        Refresh();
        ApplyVisibility();
    }

    void OnDisable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged -= Refresh;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        GameplayHud.OnVisibilityChanged -= ApplyVisibility;
    }

    // Gameplay HUDs are hidden outside gameplay scenes (e.g. the main menu).
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ApplyVisibility();

    /// <summary>Credits are the ONE readout shared by both camera views: currency belongs to the
    /// player, not to whichever vehicle they are looking out of. So this asks ShowShared and stays up
    /// while its owner is off flying a Support Ship.</summary>
    void ApplyVisibility()
    {
        if (canvasGO != null) canvasGO.SetActive(GameplayHud.ShowShared);
    }

    void Refresh()
    {
        if (label == null) return;
        int credits = PlayerInventory.Instance != null ? PlayerInventory.Instance.Credits : 0;
        label.text = prefix + credits;
    }

    void BuildUI()
    {
        canvasGO = new GameObject("CreditsHUDCanvas");
        DontDestroyOnLoad(canvasGO);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 150;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var go = new GameObject("CreditsText", typeof(RectTransform));
        go.transform.SetParent(canvasGO.transform, false);
        label = go.AddComponent<TextMeshProUGUI>();
        label.fontSize = 44;
        label.color = new Color(1f, 0.9f, 0.4f);
        label.alignment = TextAlignmentOptions.TopLeft;

        // Top-left anchored, with a little padding in from the corner.
        var rt = label.rectTransform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(500f, 70f);
        rt.anchoredPosition = new Vector2(30f, -24f);

        // Park the whole canvas on the UI layer. Code-built UI defaults to the DEFAULT layer, which is
        // how the grappling hook was able to latch onto this HUD. Applied last, so the children are
        // already parented and get it too.
        UiLayer.Apply(canvasGO);
    }
}
