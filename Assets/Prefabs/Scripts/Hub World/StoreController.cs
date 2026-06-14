using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;

/// <summary>
/// One purchasable line in the store. Name on the left, price on the right.
/// Price is editable in the inspector for balance testing and defaults to 0.
/// </summary>
[System.Serializable]
public class StoreItemDef
{
    public string itemName = "Item";
    [Tooltip("Cost in credits. Defaults to 0. Edit freely for balance testing. " +
             "Only blocks a purchase when the store's Enforce Credits is on.")]
    public int price = 0;
    [Tooltip("Most the player may own of the granted item. 0 (or less) = unlimited.")]
    public int maxOwned = 0;
    [Tooltip("Inventory item this row actually grants. Leave EMPTY to grant the " +
             "row's own name. Set it to grant something different — e.g. a " +
             "\"Jet Fuel Pack\" row that grants \"Jet Fuel\".")]
    public string grantItem = "";
    [Tooltip("How many of the granted item one purchase gives. e.g. 5 for a pack.")]
    public int grantAmount = 1;

    /// <summary>The item name actually added to the inventory on purchase.</summary>
    public string GrantedName => string.IsNullOrEmpty(grantItem) ? itemName : grantItem;
    /// <summary>True when this row grants something other than a single copy of itself.</summary>
    public bool GrantsDifferent =>
        !string.IsNullOrEmpty(grantItem) && (grantItem != itemName || grantAmount != 1);
}

/// <summary>
/// Drop this on the store prefab (the one with the box collider the player
/// drives into). The box collider must have "Is Trigger" enabled — adding this
/// component sets that automatically via Reset().
///
/// The real-time store menu (the game is NOT paused) pops up automatically as
/// soon as the player car enters the trigger. Navigate rows with the D-pad / left
/// stick, press A to buy the highlighted row, press B — or simply drive out of the
/// collider — to close. Closing with B keeps the menu shut for the rest of that
/// visit: it won't auto-reopen until the car fully leaves the collider and
/// re-enters. Purchases go into the persistent <see cref="PlayerInventory"/>,
/// which follows the player between scenes.
/// </summary>
[RequireComponent(typeof(Collider))]
[DefaultExecutionOrder(1000)]   // see MenuState: run after CarController reads input
public class StoreController : MonoBehaviour
{
    [Header("Items For Sale (listed top to bottom)")]
    public List<StoreItemDef> items = new List<StoreItemDef>
    {
        new StoreItemDef { itemName = "Turbo Canister", price = 0, maxOwned = 8 },
        new StoreItemDef { itemName = "Jet Pack",       price = 0, maxOwned = 4 },
        new StoreItemDef { itemName = "Turbo Juice",    price = 0, maxOwned = 0 },
        new StoreItemDef { itemName = "Jet Fuel Pack",  price = 0, maxOwned = 0,
                           grantItem = "Jet Fuel", grantAmount = 5 },
        new StoreItemDef { itemName = "LRA",            price = 0, maxOwned = 0 },
    };

    [Header("Economy")]
    [Tooltip("If true, purchases cost credits and are blocked when the player " +
             "can't afford them. If false (default), pressing A always grants the " +
             "item as long as it's under its max — the price is just a label.")]
    public bool enforceCredits = false;
    [Tooltip("Credits the player starts the session with (seeded once). " +
             "Only used when Enforce Credits is on.")]
    public int startingCredits = 0;

    [Header("Detection")]
    [Tooltip("Tag on the player car (or any of its colliders).")]
    public string playerTag = "Player";

    [Header("Row Style")]
    public Color rowNormal   = new Color(0f, 0f, 0f, 0.55f);
    public Color rowSelected = new Color(1f, 0.78f, 0.12f, 0.95f);
    public Color textNormal   = Color.white;
    public Color textSelected = Color.black;

    // ---- detection state ----
    // Track every player collider currently inside so multiple child colliders
    // (e.g. a body collider) don't flip "inside" off when just one exits.
    private readonly HashSet<Collider> playerColliders = new HashSet<Collider>();
    private bool PlayerInside => playerColliders.Count > 0;

    // ---- menu state ----
    private bool isOpen;
    private int selected;
    private float feedbackUntil;
    // Set when the player closes with B while still inside, so the menu doesn't
    // immediately auto-reopen. Cleared only when the car fully leaves the trigger.
    private bool suppressedUntilExit;

    // ---- built UI references ----
    private GameObject root;
    private readonly List<Image> rowBackgrounds = new List<Image>();
    private readonly List<TextMeshProUGUI> rowNames = new List<TextMeshProUGUI>();
    private readonly List<TextMeshProUGUI> rowPrices = new List<TextMeshProUGUI>();
    private TextMeshProUGUI creditsText;
    private TextMeshProUGUI feedbackText;

    void Reset()
    {
        // Auto-configure the collider as a trigger when the component is added.
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void Start()
    {
        if (enforceCredits && PlayerInventory.Instance != null)
            PlayerInventory.Instance.SeedCreditsOnce(startingCredits);
    }

    // -------------------------------------------------------
    //  Trigger detection
    // -------------------------------------------------------

    void OnTriggerEnter(Collider other)
    {
        if (IsPlayer(other)) playerColliders.Add(other);
    }

    void OnTriggerExit(Collider other)
    {
        if (playerColliders.Remove(other) && !PlayerInside)
        {
            // Fully left the trigger: clear the B-suppression so the menu can
            // auto-open again on re-entry, and close it if it was still up.
            suppressedUntilExit = false;
            if (isOpen) Close();
        }
    }

    bool IsPlayer(Collider other)
    {
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag)) return true;
            t = t.parent;
        }
        return false;
    }

    // -------------------------------------------------------
    //  Input
    // -------------------------------------------------------

    void Update()
    {
        if (!isOpen)
        {
            // Auto-open the moment the car is inside the trigger — unless it was
            // closed with B this visit, or another menu is already up. Done here
            // (not just on trigger-enter) so it also opens after, e.g., closing
            // the inventory while parked inside.
            if (PlayerInside && !suppressedUntilExit && !MenuState.AnyOpen)
                Open();
            return;
        }

        // ---- store is open ----

        // Safety: if the car rolled out without an exit event, close.
        if (!PlayerInside) { Close(); return; }

        var gp = Gamepad.current;
        if (gp == null) return;

        // B closes the menu and keeps it shut until the car fully leaves.
        if (gp.buttonEast.wasPressedThisFrame) { suppressedUntilExit = true; Close(); return; }

        // Up / down navigation (D-pad or left stick).
        if (gp.dpad.up.wasPressedThisFrame || gp.leftStick.up.wasPressedThisFrame)
            Move(-1);
        if (gp.dpad.down.wasPressedThisFrame || gp.leftStick.down.wasPressedThisFrame)
            Move(1);

        // A buys the highlighted row.
        if (gp.buttonSouth.wasPressedThisFrame)
            Purchase();

        // Clear transient feedback text.
        if (feedbackText != null && Time.unscaledTime > feedbackUntil)
            feedbackText.text = "";
    }

    void Move(int dir)
    {
        if (items.Count == 0) return;
        selected = (selected + dir + items.Count) % items.Count;
        RefreshRows();
    }

    void Purchase()
    {
        var inv = PlayerInventory.Instance;
        if (inv == null || items.Count == 0) return;

        var def = items[selected];
        int amount = Mathf.Max(1, def.grantAmount);
        bool ok = inv.TryPurchase(def.GrantedName, amount, def.maxOwned, def.price, enforceCredits);

        if (ok)
        {
            ShowFeedback($"Bought {def.itemName}");
        }
        else
        {
            int owned = inv.GetCount(def.GrantedName);
            if (def.maxOwned > 0 && owned + amount > def.maxOwned)
                ShowFeedback("Max owned");
            else
                ShowFeedback("Not enough credits");
        }

        RefreshRows();
    }

    void ShowFeedback(string msg)
    {
        if (feedbackText == null) return;
        feedbackText.text = msg;
        feedbackUntil = Time.unscaledTime + 1.5f;
    }

    // -------------------------------------------------------
    //  Open / close
    // -------------------------------------------------------

    void Open()
    {
        EnsureUI();
        selected = Mathf.Clamp(selected, 0, Mathf.Max(0, items.Count - 1));
        RefreshRows();
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        isOpen = false;
        MenuState.AnyOpen = false;
    }

    // If the hub scene unloads (e.g. entering the track portal) while the menu is
    // somehow still open, don't leave the shared flag stuck on.
    void OnDisable()
    {
        if (isOpen)
        {
            isOpen = false;
            MenuState.AnyOpen = false;
        }
    }

    // -------------------------------------------------------
    //  Refresh
    // -------------------------------------------------------

    void RefreshRows()
    {
        var inv = PlayerInventory.Instance;

        for (int i = 0; i < rowNames.Count; i++)
        {
            var def = items[i];
            int owned = inv != null ? inv.GetCount(def.GrantedName) : 0;

            string ownedStr;
            if (def.GrantsDifferent)   ownedStr = $"   (+{def.grantAmount} {def.GrantedName})";
            else if (def.maxOwned > 0) ownedStr = $"   ({owned}/{def.maxOwned})";
            else if (owned > 0)        ownedStr = $"   x{owned}";
            else                       ownedStr = "";

            rowNames[i].text = def.itemName + ownedStr;
            rowPrices[i].text = def.price.ToString();

            bool sel = (i == selected);
            rowBackgrounds[i].color = sel ? rowSelected : rowNormal;
            rowNames[i].color  = sel ? textSelected : textNormal;
            rowPrices[i].color = sel ? textSelected : textNormal;
        }

        if (creditsText != null)
            creditsText.text = enforceCredits && inv != null ? $"Credits: {inv.Credits}" : "";
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void EnsureUI()
    {
        if (root != null) return;

        const float panelWidth = 760f;
        const float rowHeight = 64f;
        const float headerHeight = 130f;
        const float footerHeight = 90f;
        float panelHeight = headerHeight + items.Count * rowHeight + footerHeight;

        root = new GameObject("StoreCanvas");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Panel
        var panel = NewUI("Panel", root.transform);
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.05f, 0.05f, 0.08f, 0.92f);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(panelWidth, panelHeight);
        prt.anchoredPosition = Vector2.zero;

        // Title
        var title = NewText(panel.transform, "Title", 56, TextAlignmentOptions.Top);
        title.text = "STORE";
        title.color = Color.white;
        StretchTop(title.rectTransform, panelWidth - 60f, 70f, 22f);

        // Credits (only populated when enforceCredits)
        creditsText = NewText(panel.transform, "Credits", 30, TextAlignmentOptions.Top);
        creditsText.color = new Color(1f, 0.9f, 0.4f);
        StretchTop(creditsText.rectTransform, panelWidth - 60f, 40f, 86f);

        // Rows
        rowBackgrounds.Clear();
        rowNames.Clear();
        rowPrices.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var rowGO = NewUI($"Row{i}", panel.transform);
            var bg = rowGO.AddComponent<Image>();
            bg.color = rowNormal;
            StretchTop(rowGO.GetComponent<RectTransform>(),
                       panelWidth - 40f, rowHeight - 8f, headerHeight + i * rowHeight);

            // Name (left)
            var nameTxt = NewText(rowGO.transform, "Name", 34, TextAlignmentOptions.MidlineLeft);
            FillWithPadding(nameTxt.rectTransform, 22f, 22f);

            // Price (right)
            var priceTxt = NewText(rowGO.transform, "Price", 34, TextAlignmentOptions.MidlineRight);
            FillWithPadding(priceTxt.rectTransform, 22f, 22f);

            rowBackgrounds.Add(bg);
            rowNames.Add(nameTxt);
            rowPrices.Add(priceTxt);
        }

        // Feedback line (above footer)
        feedbackText = NewText(panel.transform, "Feedback", 28, TextAlignmentOptions.Bottom);
        feedbackText.color = new Color(0.6f, 1f, 0.6f);
        var frt = feedbackText.rectTransform;
        frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0f);
        frt.sizeDelta = new Vector2(panelWidth - 60f, 40f);
        frt.anchoredPosition = new Vector2(0f, 56f);

        // Hint footer
        var hint = NewText(panel.transform, "Hint", 26, TextAlignmentOptions.Bottom);
        hint.text = "↑↓ Select    A Buy    B Close";
        hint.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hint.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(panelWidth - 60f, 40f);
        hrt.anchoredPosition = new Vector2(0f, 18f);

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

    // Stretches a rect to fill its parent with left/right padding.
    static void FillWithPadding(RectTransform rt, float left, float right)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(left, 0f);
        rt.offsetMax = new Vector2(-right, 0f);
    }
}
