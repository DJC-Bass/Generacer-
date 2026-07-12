using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;

/// <summary>
/// A crafting recipe: two materials combine into one product over craftTime
/// seconds. All fields are inspector-editable for balance testing.
/// </summary>
[System.Serializable]
public class CraftRecipe
{
    [Tooltip("First required material (consumed per craft). Leave BLANK if not needed.")]
    public string materialA = "Material A";
    [Tooltip("Second required material (consumed per craft). Leave BLANK for a single-material recipe.")]
    public string materialB = "Material B";
    public string product = "Product";
    [Tooltip("Seconds to craft one. Turbo = 1 (1/sec), Jet = 0.2 (5/sec).")]
    public float craftTime = 1f;

    [Header("Capacity (container) limit")]
    [Tooltip("Item whose owned count limits how many products can be held. NOT " +
             "consumed by crafting — it just acts as a container. Blank = no limit.")]
    public string capacityItem = "";
    [Tooltip("Products each capacity item holds. Max craftable/holdable = " +
             "(capacity items owned) × this. e.g. 1 Turbo per Turbo Canister, " +
             "5 Jets per Jet Pack.")]
    public int capacityPerContainer = 1;
    [Tooltip("Color of the progress fill for this recipe's bar.")]
    public Color barColor = new Color(0.2f, 0.45f, 1f);
    [Tooltip("Text shown under the bar.")]
    public string label = "Hold to craft";
}

/// <summary>
/// Drop on the UpgradeRamp prefab (the GameObject with the box collider — set
/// "Is Trigger", which Reset() does automatically). Works like the store: the
/// menu auto-opens when the car enters the trigger, closes on B (staying shut
/// until the car fully leaves) or when the car drives out.
///
/// Two stacked progress bars. Hold X to charge the top (Turbo) bar, hold A for
/// the bottom (Jet) bar — only one at a time. Releasing before the bar fills
/// resets it with no materials spent; holding through completion consumes the
/// recipe's materials, grants the product, then immediately starts the next one
/// if materials remain. Crafted products and consumed materials live in the
/// persistent <see cref="PlayerInventory"/>, so they carry between scenes.
/// </summary>
[RequireComponent(typeof(Collider))]
[DefaultExecutionOrder(1000)]   // see MenuState: run after CarController reads input
public class UpgradeRampController : MonoBehaviour
{
    [Header("Recipes")]
    [Tooltip("Top bar — charged with X.")]
    public CraftRecipe turbo = new CraftRecipe
    {
        materialA = "Turbo Juice", materialB = "", product = "Turbo",
        craftTime = 1f, barColor = new Color(0.2f, 0.45f, 1f), label = "X To Charge Turbo",
        capacityItem = "Turbo Canister", capacityPerContainer = 1
    };
    [Tooltip("Bottom bar — charged with A.")]
    public CraftRecipe jet = new CraftRecipe
    {
        materialA = "Jet Fuel", materialB = "", product = "Jet",
        craftTime = 0.2f, barColor = new Color(0.55f, 0.75f, 1f), label = "A to craft Jet",
        capacityItem = "Jet Pack", capacityPerContainer = 5
    };

    [Header("Detection")]
    [Tooltip("Tag on the player car (or any of its colliders).")]
    public string playerTag = "Player";

    // ---- detection ----
    private readonly HashSet<Collider> playerColliders = new HashSet<Collider>();
    private bool PlayerInside => playerColliders.Count > 0;

    // ---- menu ----
    private bool isOpen;
    private bool suppressedUntilExit;

    // ---- crafting ----
    enum Craft { None, Turbo, Jet }
    private Craft active = Craft.None;
    private float progress;   // 0..1 of the currently charging recipe

    // ---- built UI ----
    private GameObject root;
    private RectTransform turboFill, jetFill;
    private TextMeshProUGUI turboCounts, jetCounts;
    private AudioSource craftLoopSource;   // looping turbo-craft sound (plays while the turbo bar charges)

    void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void Awake()
    {
        SetUpCraftAudio();
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
            suppressedUntilExit = false;   // fully left -> allow auto-open on re-entry
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
    //  Input / crafting loop
    // -------------------------------------------------------

    void Update()
    {
        if (!isOpen)
        {
            if (PlayerInside && !suppressedUntilExit && !MenuState.AnyOpen)
                Open();
            return;
        }

        if (!PlayerInside) { Close(); return; }

        var gp = Gamepad.current;
        if (gp == null) return;

        // B closes and stays shut until the car fully leaves.
        if (gp.buttonEast.wasPressedThisFrame) { suppressedUntilExit = true; Close(); return; }

        bool xHeld = gp.buttonWest.isPressed;    // X -> Turbo (top bar)
        bool aHeld = gp.buttonSouth.isPressed;   // A -> Jet (bottom bar)

        TickCrafting(xHeld, aHeld);
        UpdateBars();
        SyncCraftLoop();
    }

    void TickCrafting(bool xHeld, bool aHeld)
    {
        float dt = Time.unscaledDeltaTime;

        // Pick up a new craft only when idle. X (Turbo) takes priority if both held.
        if (active == Craft.None)
        {
            if (xHeld && CanCraft(turbo)) { active = Craft.Turbo; progress = 0f; }
            else if (aHeld && CanCraft(jet)) { active = Craft.Jet; progress = 0f; }
            else return;
        }

        bool held = active == Craft.Turbo ? xHeld : aHeld;
        CraftRecipe r = active == Craft.Turbo ? turbo : jet;

        // Cancel (reset, no materials spent) if the button was released, the
        // materials ran out, or the inventory is at capacity for this product.
        if (!held || !CanCraft(r))
        {
            active = Craft.None;
            progress = 0f;
            return;
        }

        progress += dt / Mathf.Max(0.0001f, r.craftTime);
        if (progress >= 1f)
        {
            DoCraft(r);               // spend materials, grant product
            progress = 0f;            // reset bar for a consecutive craft
            // Stop if released, out of materials, or now at capacity.
            if (!held || !CanCraft(r)) active = Craft.None;
        }
    }

    /// <summary>A craft is allowed only with the materials AND free capacity.</summary>
    bool CanCraft(CraftRecipe r) => HasMaterials(r) && HasCapacity(r);

    /// <summary>
    /// True while the player can hold one more product. Max held =
    /// (capacityItem owned) × capacityPerContainer. Blank capacityItem = no limit.
    /// </summary>
    bool HasCapacity(CraftRecipe r)
    {
        if (string.IsNullOrEmpty(r.capacityItem)) return true;   // unlimited
        var inv = PlayerInventory.Instance;
        if (inv == null) return false;
        int max = inv.GetCount(r.capacityItem) * Mathf.Max(0, r.capacityPerContainer);
        return inv.GetCount(r.product) < max;
    }

    // A material slot left blank in the inspector isn't required. A recipe must
    // have at least one non-blank material so it isn't free.
    bool HasMaterials(CraftRecipe r)
    {
        var inv = PlayerInventory.Instance;
        if (inv == null) return false;

        bool hasA = !string.IsNullOrEmpty(r.materialA);
        bool hasB = !string.IsNullOrEmpty(r.materialB);
        if (!hasA && !hasB) return false;

        if (hasA && inv.GetCount(r.materialA) < 1) return false;
        if (hasB && inv.GetCount(r.materialB) < 1) return false;
        return true;
    }

    void DoCraft(CraftRecipe r)
    {
        var inv = PlayerInventory.Instance;
        if (inv == null || !CanCraft(r)) return;

        if (!string.IsNullOrEmpty(r.materialA)) inv.Consume(r.materialA, 1);
        if (!string.IsNullOrEmpty(r.materialB)) inv.Consume(r.materialB, 1);
        inv.Add(r.product, 1);
        RefreshCounts();

        // The recipe's "crafted & stored" one-shot — via an independent source so it isn't cut when
        // the loop stops.
        if (r == turbo)
        {
            AudioManager.PlayTurboCrafted(transform.position);
            // Cut the charge loop each turbo craft so the next progress bar restarts its audio fresh
            // (SyncCraftLoop replays it from the top next frame if still charging).
            if (craftLoopSource != null) craftLoopSource.Stop();
        }
        else if (r == jet) AudioManager.PlayJetCrafted(transform.position);
    }

    // -------------------------------------------------------
    //  Crafting audio
    // -------------------------------------------------------

    void SetUpCraftAudio()
    {
        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        craftLoopSource = gameObject.AddComponent<AudioSource>();
        craftLoopSource.loop = true;
        craftLoopSource.playOnAwake = false;
        craftLoopSource.spatialBlend = 1f;              // 3D at the ramp
        craftLoopSource.rolloffMode = AudioRolloffMode.Linear;
        craftLoopSource.minDistance = 5f;
        craftLoopSource.maxDistance = 60f;
        craftLoopSource.dopplerLevel = 0f;
        // Clip is set per-craft in SyncCraftLoop (turbo vs jet).
    }

    /// <summary>Plays the looping craft sound for whichever bar is charging — the Turbo loop for the
    /// Turbo bar, the Jet loop for the Jet bar — and cuts it the moment that bar stops (released,
    /// cancelled, out of materials, or the menu closed). One source, its clip swapped per recipe.</summary>
    void SyncCraftLoop()
    {
        if (craftLoopSource == null) return;

        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        AudioClip want = null;
        if (lib != null)
            want = active == Craft.Turbo ? lib.turboCraftLoop
                 : active == Craft.Jet   ? lib.jetCraftLoop
                 : null;

        if (want == null)
        {
            if (craftLoopSource.isPlaying) craftLoopSource.Stop();
            return;
        }

        if (craftLoopSource.clip != want)   // switched recipe — swap the loop clip
        {
            craftLoopSource.Stop();
            craftLoopSource.clip = want;
        }
        if (!craftLoopSource.isPlaying)
        {
            craftLoopSource.volume = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;
            craftLoopSource.Play();
        }
    }

    // -------------------------------------------------------
    //  Open / close
    // -------------------------------------------------------

    void Open()
    {
        EnsureUI();
        active = Craft.None;
        progress = 0f;
        RefreshCounts();
        UpdateBars();
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;
        AudioManager.PlayRampOpen();
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        active = Craft.None;
        progress = 0f;
        isOpen = false;
        MenuState.AnyOpen = false;
        if (craftLoopSource != null) craftLoopSource.Stop();   // cut the crafting loop
        AudioManager.PlayRampClose();
    }

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

    void UpdateBars()
    {
        SetFill(turboFill, active == Craft.Turbo ? progress : 0f);
        SetFill(jetFill, active == Craft.Jet ? progress : 0f);
    }

    static void SetFill(RectTransform fill, float p)
    {
        if (fill == null) return;
        var max = fill.anchorMax;
        max.x = Mathf.Clamp01(p);
        fill.anchorMax = max;
    }

    void RefreshCounts()
    {
        if (turboCounts != null) turboCounts.text = CountsLine(turbo);
        if (jetCounts != null) jetCounts.text = CountsLine(jet);
    }

    string CountsLine(CraftRecipe r)
    {
        var inv = PlayerInventory.Instance;
        int Count(string n) => (inv != null && !string.IsNullOrEmpty(n)) ? inv.GetCount(n) : 0;

        string s = "";
        if (!string.IsNullOrEmpty(r.materialA)) s += $"{r.materialA} {Count(r.materialA)}    ";
        if (!string.IsNullOrEmpty(r.materialB)) s += $"{r.materialB} {Count(r.materialB)}    ";
        if (!string.IsNullOrEmpty(r.capacityItem)) s += $"{r.capacityItem} {Count(r.capacityItem)}    ";

        if (!string.IsNullOrEmpty(r.capacityItem))
        {
            int max = Count(r.capacityItem) * Mathf.Max(0, r.capacityPerContainer);
            s += $"→    {r.product} {Count(r.product)}/{max}";
        }
        else
        {
            s += $"→    {r.product} {Count(r.product)}";
        }
        return s;
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void EnsureUI()
    {
        if (root != null) return;

        root = new GameObject("UpgradeCanvas");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var panel = NewUI("Panel", root.transform);
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.05f, 0.05f, 0.08f, 0.92f);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(880f, 520f);
        prt.anchoredPosition = Vector2.zero;

        var title = NewText(panel.transform, "Title", 50, TextAlignmentOptions.Top);
        title.text = "UPGRADE RAMP";
        title.color = Color.white;
        StretchTop(title.rectTransform, 820f, 64f, 20f);

        // Top bar (Turbo, X) and bottom bar (Jet, A), with space between.
        BuildBar(panel.transform, turbo, 130f, out turboFill, out turboCounts);
        BuildBar(panel.transform, jet, 320f, out jetFill, out jetCounts);

        var hint = NewText(panel.transform, "Hint", 26, TextAlignmentOptions.Bottom);
        hint.text = "Hold X: Turbo     Hold A: Jet     B: Close";
        hint.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hint.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(820f, 40f);
        hrt.anchoredPosition = new Vector2(0f, 18f);

        root.SetActive(false);
    }

    // Builds one bar block (background + L→R fill + label + counts) anchored to
    // the panel top, starting topOffset pixels down.
    void BuildBar(Transform parent, CraftRecipe r, float topOffset,
                  out RectTransform fill, out TextMeshProUGUI counts)
    {
        const float barW = 740f, barH = 50f;

        var bgGO = NewUI("BarBG", parent);
        var bg = bgGO.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.6f);
        StretchTop(bgGO.GetComponent<RectTransform>(), barW, barH, topOffset);

        // Fill: stretched to the bar's height, width driven by anchorMax.x (0..1).
        var fillGO = NewUI("Fill", bgGO.transform);
        var fillImg = fillGO.AddComponent<Image>();
        fillImg.color = r.barColor;
        fill = fillGO.GetComponent<RectTransform>();
        fill.anchorMin = new Vector2(0f, 0f);
        fill.anchorMax = new Vector2(0f, 1f);   // start empty
        fill.pivot = new Vector2(0f, 0.5f);
        fill.offsetMin = Vector2.zero;
        fill.offsetMax = Vector2.zero;

        var label = NewText(parent, "BarLabel", 30, TextAlignmentOptions.Top);
        label.text = r.label;
        label.color = Color.white;
        StretchTop(label.rectTransform, barW, 36f, topOffset + barH + 10f);

        counts = NewText(parent, "BarCounts", 24, TextAlignmentOptions.Top);
        counts.color = new Color(1f, 1f, 1f, 0.7f);
        StretchTop(counts.rectTransform, barW, 30f, topOffset + barH + 10f + 38f);
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
