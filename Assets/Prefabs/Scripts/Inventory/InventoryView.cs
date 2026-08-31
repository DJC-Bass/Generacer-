using System.Collections.Generic;
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
    private RectTransform rowsRoot;   // one child row per held item
    private bool isOpen;

    /// <summary>The item whose row carries a health bar. Matches SupportShipAbility.shipItem.</summary>
    const string ShipItem = "Support Ship";

    const float RowHeight = 46f;
    const float RowWidth = 620f;
    const float BarWidth = 190f;
    const float BarHeight = 18f;

    /// <summary>One built row, kept and reused. The list only ever grows to the largest inventory the
    /// player has had this session, which is a handful.</summary>
    private class Row
    {
        public GameObject go;
        public TextMeshProUGUI label;
        public GameObject bar;          // the whole bar, hidden on rows that are not the ship
        public RectTransform fill;
        public Image fillImage;
    }
    private readonly List<Row> rows = new List<Row>();
    private Row shipRow;                // the row currently showing the ship bar, if any

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
        else if (isOpen) UpdateShipBar();   // the ship can be taking hits while this is on screen
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
        int used = 0;
        shipRow = null;

        if (inv != null)
        {
            foreach (string name in inv.Order)
            {
                int count = inv.GetCount(name);
                if (count <= 0) continue;

                var row = RowAt(used++);
                row.label.text = $"{name}   x{count}";

                // Only the Support Ship line carries a bar, and only while a ship is actually up: the
                // health belongs to the LIVE ship, and a dismissed one is re-summoned at full health, so
                // a bar sitting on the item itself would be describing something that does not exist.
                bool isShip = name == ShipItem;
                if (isShip) shipRow = row;
                if (row.bar.activeSelf != isShip) row.bar.SetActive(isShip);
            }
        }

        if (used == 0)
        {
            var row = RowAt(used++);
            row.label.text = "(empty)";
            if (row.bar.activeSelf) row.bar.SetActive(false);
        }

        for (int i = used; i < rows.Count; i++)
            if (rows[i].go.activeSelf) rows[i].go.SetActive(false);

        UpdateShipBar();
    }

    /// <summary>Fills the Support Ship row's bar from the LOCAL player's own ship. Run every frame
    /// while the screen is open, not just on Refresh: the inventory is readable mid-race with a
    /// teammate flying, so the number can move while the player is looking straight at it.</summary>
    void UpdateShipBar()
    {
        if (shipRow == null) return;

        var ship = SupportShipAbility.Instance != null ? SupportShipAbility.Instance.Ship : null;
        bool show = ship != null;
        if (shipRow.bar.activeSelf != show) shipRow.bar.SetActive(show);
        if (!show) return;

        float fraction = Mathf.Clamp01(ship.HealthFraction);
        shipRow.fill.sizeDelta = new Vector2(BarWidth * fraction, BarHeight);
        shipRow.fillImage.color = fraction <= SupportShipPilotHUD.LowFraction
            ? SupportShipPilotHUD.LowColor
            : SupportShipPilotHUD.FillColor;
    }

    /// <summary>The row at this index, built on first use and reused thereafter.</summary>
    Row RowAt(int index)
    {
        while (rows.Count <= index) rows.Add(BuildRow());
        var row = rows[index];
        if (!row.go.activeSelf) row.go.SetActive(true);

        var rt = (RectTransform)row.go.transform;
        rt.anchoredPosition = new Vector2(0f, -index * RowHeight);
        return row;
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

        var rowsGO = NewUI("Rows", panel.transform);
        rowsRoot = rowsGO.GetComponent<RectTransform>();
        StretchTop(rowsRoot, RowWidth, 420f, 110f);

        var hint = NewText(panel.transform, "Hint", 26, TextAlignmentOptions.Bottom);
        hint.text = "Select / B : Close";
        hint.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hint.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(660f, 50f);
        hrt.anchoredPosition = new Vector2(0f, 20f);

        root.SetActive(false);
    }

    /// <summary>Builds one reusable row: a name on the left and a hidden health bar on the right.
    ///
    /// Rows exist at all because the list used to be a single text block, and "a bar beside the Support
    /// Ship item" cannot be positioned against a line inside one - it would mean measuring glyphs. One
    /// object per item makes the position a fact rather than a calculation, and any future per-item
    /// adornment becomes trivial.</summary>
    Row BuildRow()
    {
        var row = new Row();

        var go = NewUI("Row", rowsRoot);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(RowWidth, RowHeight);
        row.go = go;

        row.label = NewText(go.transform, "Name", 34, TextAlignmentOptions.Left);
        row.label.color = Color.white;
        var lrt = row.label.rectTransform;
        lrt.anchorMin = new Vector2(0f, 0f);
        lrt.anchorMax = new Vector2(1f, 1f);
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;

        // Right-aligned rather than butted against the name: item names vary in length, and a bar that
        // slid left and right with them would be far harder to read down the list than a column.
        var bar = NewUI("Bar", go.transform);
        var brt = bar.GetComponent<RectTransform>();
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 0.5f);
        brt.sizeDelta = new Vector2(BarWidth, BarHeight);
        brt.anchoredPosition = Vector2.zero;
        bar.AddComponent<Image>().color = SupportShipPilotHUD.TrackColor;
        row.bar = bar;

        // Left-pivoted inside the track, so shrinking drains it from the right rather than both ends.
        var fillGO = NewUI("Fill", bar.transform);
        row.fill = fillGO.GetComponent<RectTransform>();
        row.fill.anchorMin = row.fill.anchorMax = new Vector2(0f, 0.5f);
        row.fill.pivot = new Vector2(0f, 0.5f);
        row.fill.sizeDelta = new Vector2(BarWidth, BarHeight);
        row.fill.anchoredPosition = Vector2.zero;
        row.fillImage = fillGO.AddComponent<Image>();
        row.fillImage.color = SupportShipPilotHUD.FillColor;

        bar.SetActive(false);
        return row;
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
