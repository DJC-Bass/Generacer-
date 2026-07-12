using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;

/// <summary>
/// Full-screen, real-time inventory readout toggled with the gamepad Select /
/// View button. Persistent (lives on the same DontDestroyOnLoad object as
/// PlayerInventory) so it works in every scene with no setup. Builds its own
/// Canvas the first time it's opened.
///
/// Late execution order: see <see cref="MenuState"/> — running after CarController
/// keeps the closing button press from leaking into a driving action.
/// </summary>
[DefaultExecutionOrder(1000)]
public class InventoryView : MonoBehaviour
{
    public static InventoryView Instance { get; private set; }

    private GameObject root;          // canvas root, toggled active
    private TextMeshProUGUI bodyText;
    private bool isOpen;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Update()
    {
        var gp = Gamepad.current;
        if (gp == null) return;

        // Select (View button) toggles the inventory. Only open if no other menu
        // is already up; always allow closing our own.
        if (gp.selectButton.wasPressedThisFrame)
        {
            if (isOpen) Close();
            else if (!MenuState.AnyOpen) Open();
            return;
        }

        // B also closes while open.
        if (isOpen && gp.buttonEast.wasPressedThisFrame)
            Close();
    }

    void Open()
    {
        EnsureUI();
        Refresh();
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;
        AudioManager.PlayMenuOpen();
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        isOpen = false;
        MenuState.AnyOpen = false;
        AudioManager.PlayMenuClose();
    }

    void Refresh()
    {
        var inv = PlayerInventory.Instance;
        var sb = new StringBuilder();

        if (inv != null && inv.Order.Count > 0)
        {
            bool any = false;
            foreach (string name in inv.Order)
            {
                int count = inv.GetCount(name);
                if (count <= 0) continue;
                sb.AppendLine($"{name}   x{count}");
                any = true;
            }
            if (!any) sb.Append("(empty)");
        }
        else
        {
            sb.Append("(empty)");
        }

        bodyText.text = sb.ToString();
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void EnsureUI()
    {
        if (root != null) return;

        root = new GameObject("InventoryCanvas");
        DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 250;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Dim panel centered on screen
        var panel = NewUI("Panel", root.transform);
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0f, 0f, 0f, 0.8f);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(700f, 600f);
        prt.anchoredPosition = Vector2.zero;

        var title = NewText(panel.transform, "Title", 52, TextAlignmentOptions.Top);
        title.text = "INVENTORY";
        title.color = Color.white;
        StretchTop(title.rectTransform, 660f, 70f, 24f);

        bodyText = NewText(panel.transform, "Body", 34, TextAlignmentOptions.TopLeft);
        bodyText.color = Color.white;
        StretchTop(bodyText.rectTransform, 620f, 420f, 110f);

        var hint = NewText(panel.transform, "Hint", 26, TextAlignmentOptions.Bottom);
        hint.text = "Select / B : Close";
        hint.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hint.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(660f, 50f);
        hrt.anchoredPosition = new Vector2(0f, 20f);

        root.SetActive(false);
    }

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static TextMeshProUGUI NewText(Transform parent, string name, int fontSize,
                                   TextAlignmentOptions align)
    {
        var go = NewUI(name, parent);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        return t;
    }

    // Anchors a rect to its parent's top-center, offset downward by topOffset.
    static void StretchTop(RectTransform rt, float width, float height, float topOffset)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = new Vector2(0f, -topOffset);
    }
}
