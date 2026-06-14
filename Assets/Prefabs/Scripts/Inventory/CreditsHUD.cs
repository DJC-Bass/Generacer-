using UnityEngine;
using UnityEngine.UI;
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
        Refresh();
    }

    void OnDisable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged -= Refresh;
    }

    void Refresh()
    {
        if (label == null) return;
        int credits = PlayerInventory.Instance != null ? PlayerInventory.Instance.Credits : 0;
        label.text = prefix + credits;
    }

    void BuildUI()
    {
        var canvasGO = new GameObject("CreditsHUDCanvas");
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
    }
}
