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
    [Tooltip("FLAT cap on how many of the product can be held, independent of any container item. " +
             "0 = no flat cap. Used by Shield (max 4). Applies on top of the container limit.")]
    public int maxProduct = 0;
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
    [Tooltip("Middle bar — charged with A.")]
    public CraftRecipe jet = new CraftRecipe
    {
        materialA = "Jet Fuel", materialB = "", product = "Jet",
        craftTime = 0.2f, barColor = new Color(0.55f, 0.75f, 1f), label = "A to craft Jet",
        capacityItem = "Jet Pack", capacityPerContainer = 5
    };
    [Tooltip("Bottom bar — charged with Y. Plasma → Shield, same 1 s craft as Turbo, hard cap of 4 held.")]
    public CraftRecipe shield = new CraftRecipe
    {
        materialA = "Plasma", materialB = "", product = "Shield",
        craftTime = 1f, barColor = new Color(0.35f, 1f, 0.8f), label = "Y To Craft Shield",
        capacityItem = "", capacityPerContainer = 0, maxProduct = 4
    };

    [Tooltip("Fourth slot — crafted by ROTATING the right stick, not by holding a button. One full " +
             "clockwise revolution = one Grappling Hook, and rotations keep chaining while materials last.")]
    public CraftRecipe grapple = new CraftRecipe
    {
        materialA = "Wire", materialB = "", product = "Grappling Hook",
        craftTime = 1f, barColor = new Color(0.62f, 0.64f, 0.67f),   // slightly dark silver
        label = "Rotate Right Stick To Craft Grappling Hook",
        capacityItem = "", capacityPerContainer = 0, maxProduct = 0
    };

    [Header("Rotary Craft (right stick)")]
    [Tooltip("How far the right stick must be pushed for its rotation to register (0-1). Below this " +
             "the stick counts as released and the revolution resets.")]
    public float stickDeadzone = 0.5f;
    [Tooltip("Largest per-frame stick movement treated as real rotation (degrees). Anything bigger is " +
             "a flick across the deadzone rather than a swept turn, and is ignored so it can't cheat " +
             "a revolution.")]
    public float maxStickStepDegrees = 90f;

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
    enum Craft { None, Turbo, Jet, Shield }
    private Craft active = Craft.None;
    private float progress;   // 0..1 of the currently charging recipe

    // ---- built UI ----
    private GameObject root;
    private RectTransform turboFill, jetFill, shieldFill;
    private TextMeshProUGUI turboCounts, jetCounts, shieldCounts;
    private Image grappleFill;                 // radial gauge for the rotary craft
    private TextMeshProUGUI grappleCounts;

    // ---- rotary craft state ----
    private float rotationDegrees;             // clockwise degrees swept toward the next revolution
    private Vector2 lastStickDir;
    private bool stickEngaged;
    // True while the stick is turning on a craftable recipe — the rotary slot's equivalent of a held
    // bar, and what SyncCraftLoop keys its loop off (the rotary craft never sets `active`).
    private bool rotaryActive;
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
        bool aHeld = gp.buttonSouth.isPressed;   // A -> Jet (middle bar)
        bool yHeld = gp.buttonNorth.isPressed;   // Y -> Shield (bottom bar); B is taken by Close

        TickCrafting(xHeld, aHeld, yHeld);
        TickRotaryCraft(gp);
        UpdateBars();
        SyncCraftLoop();
    }

    void TickCrafting(bool xHeld, bool aHeld, bool yHeld)
    {
        float dt = Time.unscaledDeltaTime;

        // Pick up a new craft only when idle. X (Turbo) takes priority, then A (Jet), then Y (Shield).
        if (active == Craft.None)
        {
            if (xHeld && CanCraft(turbo)) { active = Craft.Turbo; progress = 0f; }
            else if (aHeld && CanCraft(jet)) { active = Craft.Jet; progress = 0f; }
            else if (yHeld && CanCraft(shield)) { active = Craft.Shield; progress = 0f; }
            else return;
        }

        bool held = active == Craft.Turbo ? xHeld : active == Craft.Jet ? aHeld : yHeld;
        CraftRecipe r = ActiveRecipe();

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

    /// <summary>
    /// The rotary craft: sweeping the RIGHT STICK one full clockwise revolution makes one Grappling
    /// Hook, and holding the rotation going keeps chaining them while the Wire lasts. Progress is the
    /// ANGLE SWEPT, accumulated frame to frame — not the stick's absolute position — so the player can
    /// begin anywhere on the circle (the gauge just fills from 12 o'clock) and reversing the stick
    /// unwinds progress rather than granting it.
    /// </summary>
    void TickRotaryCraft(Gamepad gp)
    {
        // One craft at a time: a bar already charging owns the ramp.
        if (active != Craft.None) { ResetRotation(); return; }

        Vector2 stick = gp.rightStick.ReadValue();
        if (stick.magnitude < stickDeadzone) { ResetRotation(); return; }

        Vector2 dir = stick.normalized;
        if (!stickEngaged)
        {
            // First frame past the deadzone — establish a reference without crediting any sweep.
            stickEngaged = true;
            lastStickDir = dir;
            return;
        }

        // Vector2.SignedAngle is positive counter-clockwise, so negate it to make CLOCKWISE count up.
        float delta = -Vector2.SignedAngle(lastStickDir, dir);
        lastStickDir = dir;

        // A jump this large isn't a swept turn — it's the stick crossing the centre or snapping to a
        // new quadrant. Crediting it would let a player flick their way to a free craft.
        if (Mathf.Abs(delta) > maxStickStepDegrees) return;

        if (!CanCraft(grapple)) { rotationDegrees = 0f; rotaryActive = false; return; }

        rotaryActive = true;
        rotationDegrees = Mathf.Max(0f, rotationDegrees + delta);
        if (rotationDegrees >= 360f)
        {
            rotationDegrees -= 360f;   // carry the surplus so continuous spinning keeps producing
            DoCraft(grapple);
            if (!CanCraft(grapple)) rotationDegrees = 0f;
        }
    }

    void ResetRotation()
    {
        rotationDegrees = 0f;
        stickEngaged = false;
        rotaryActive = false;
    }

    /// <summary>The recipe the active bar is charging.</summary>
    CraftRecipe ActiveRecipe() =>
        active == Craft.Turbo ? turbo : active == Craft.Jet ? jet : shield;

    /// <summary>A craft is allowed only with the materials AND free capacity.</summary>
    bool CanCraft(CraftRecipe r) => HasMaterials(r) && HasCapacity(r);

    /// <summary>
    /// True while the player can hold one more product. Two independent limits, both enforced:
    /// the CONTAINER limit ((capacityItem owned) × capacityPerContainer; blank = unlimited) and the
    /// FLAT limit (maxProduct; 0 = none — this is what caps Shield at 4).
    /// </summary>
    bool HasCapacity(CraftRecipe r)
    {
        var inv = PlayerInventory.Instance;
        if (inv == null) return false;

        if (r.maxProduct > 0 && inv.GetCount(r.product) >= r.maxProduct) return false;

        if (string.IsNullOrEmpty(r.capacityItem)) return true;   // no container limit
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
        else if (r == grapple)
        {
            AudioManager.PlayGrappleCrafted(transform.position);
            // Cut the charge loop on each completion so a continuous rotation restarts its audio per
            // hook instead of running on seamlessly — same treatment Turbo and Shield get.
            if (craftLoopSource != null) craftLoopSource.Stop();
        }
        else if (r == shield)
        {
            AudioManager.PlayShieldCrafted(transform.position);
            // Same treatment as Turbo (both are 1 s crafts): cut the charge loop on each completion so
            // a consecutive craft restarts its audio from the top rather than running on seamlessly.
            if (craftLoopSource != null) craftLoopSource.Stop();
        }
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
            want = active == Craft.Turbo  ? lib.turboCraftLoop
                 : active == Craft.Jet    ? lib.jetCraftLoop
                 : active == Craft.Shield ? lib.shieldCraftLoop
                 : rotaryActive           ? lib.grappleCraftLoop   // rotary slot: no `active` to key off
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
        ResetRotation();
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
        ResetRotation();
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
        SetFill(shieldFill, active == Craft.Shield ? progress : 0f);
        if (grappleFill != null) grappleFill.fillAmount = Mathf.Clamp01(rotationDegrees / 360f);
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
        if (shieldCounts != null) shieldCounts.text = CountsLine(shield);
        if (grappleCounts != null) grappleCounts.text = CountsLine(grapple);
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
            if (r.maxProduct > 0) max = Mathf.Min(max, r.maxProduct);   // whichever limit binds first
            s += $"→    {r.product} {Count(r.product)}/{max}";
        }
        else if (r.maxProduct > 0)
        {
            s += $"→    {r.product} {Count(r.product)}/{r.maxProduct}";   // flat cap (Shield = 4)
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
        prt.anchoredPosition = Vector2.zero;

        var title = NewText(panel.transform, "Title", 50, TextAlignmentOptions.Top);
        title.text = "UPGRADE RAMP";
        title.color = Color.white;
        StretchTop(title.rectTransform, 820f, 64f, 20f);

        // The slots FLOW: each builder returns where the next one starts, so a blank label costs no
        // space and adding a slot can't silently overlap the hint the way fixed offsets did.
        float y = 110f;                                             // below the title
        y = BuildBar(panel.transform, turbo, y, out turboFill, out turboCounts);
        y = BuildBar(panel.transform, jet, y, out jetFill, out jetCounts);
        y = BuildBar(panel.transform, shield, y, out shieldFill, out shieldCounts);

        // Fourth slot: a radial gauge instead of a bar, because the input is a stick REVOLUTION.
        y = BuildRotaryGauge(panel.transform, grapple, y, 130f, out grappleFill, out grappleCounts);

        // Size the panel to whatever the slots actually needed, leaving room for the hint line.
        prt.sizeDelta = new Vector2(880f, y + 20f);

        var hint = NewText(panel.transform, "Hint", 22, TextAlignmentOptions.Bottom);
        hint.text = "Hold X: Turbo    Hold A: Jet    Hold Y: Shield    Rotate RS: Grapple    B: Close";
        hint.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hint.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(820f, 40f);
        hrt.anchoredPosition = new Vector2(0f, 18f);

        root.SetActive(false);
    }

    // Builds one bar block (background + L→R fill + label + counts) anchored to
    // the panel top, starting topOffset pixels down.
    // ---- Vertical rhythm. A slot is: bar → (small gap) → its text → (larger gap) → the next bar.
    //      The gap from a bar to ITS OWN text is deliberately much smaller than the gap to the NEXT
    //      slot, so each block reads as one group instead of the text floating between two bars.
    const float BarWidth = 740f;
    const float BarHeight = 50f;
    const float GapBarToText = 6f;        // bar → its own label/counts (was effectively ~48)
    const float LabelHeight = 34f;
    const float GapLabelToCounts = 2f;
    const float CountsHeight = 28f;
    const float GapSlotToSlot = 62f;      // one slot's text → the next slot's bar (unchanged)

    /// <summary>Builds one bar slot and returns the TOP OFFSET the next slot should start at, so the
    /// column flows instead of using hard-coded positions. A recipe with a BLANK label reserves no
    /// space for it — the empty labels on Turbo/Jet were what pushed their counts lines so far from
    /// their bars.</summary>
    float BuildBar(Transform parent, CraftRecipe r, float topOffset,
                   out RectTransform fill, out TextMeshProUGUI counts)
    {
        const float barW = BarWidth, barH = BarHeight;

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

        float y = topOffset + barH + GapBarToText;

        if (!string.IsNullOrEmpty(r.label))
        {
            var label = NewText(parent, "BarLabel", 28, TextAlignmentOptions.Top);
            label.text = r.label;
            label.color = Color.white;
            StretchTop(label.rectTransform, barW, LabelHeight, y);
            y += LabelHeight + GapLabelToCounts;
        }

        counts = NewText(parent, "BarCounts", 24, TextAlignmentOptions.Top);
        counts.color = new Color(1f, 1f, 1f, 0.7f);
        StretchTop(counts.rectTransform, barW, CountsHeight, y);

        return y + CountsHeight + GapSlotToSlot;
    }

    /// <summary>Builds the rotary slot: a dark ring with a radial fill that sweeps CLOCKWISE from
    /// 12 o'clock, plus the label and counts line. Unity's radial fill needs a SPRITE (a plain Image
    /// with no sprite can't be filled), so the ring is generated in code — keeping this UI
    /// asset-free like the rest of the ramp.</summary>
    float BuildRotaryGauge(Transform parent, CraftRecipe r, float topOffset, float diameter,
                           out Image fill, out TextMeshProUGUI counts)
    {
        Sprite ring = RingSprite();

        var bgGO = NewUI("RotaryBG", parent);
        var bg = bgGO.AddComponent<Image>();
        bg.sprite = ring;
        bg.color = new Color(0f, 0f, 0f, 0.6f);
        StretchTop(bgGO.GetComponent<RectTransform>(), diameter, diameter, topOffset);

        var fillGO = NewUI("RotaryFill", bgGO.transform);
        var fillImg = fillGO.AddComponent<Image>();
        fillImg.sprite = ring;
        fillImg.color = r.barColor;
        fillImg.type = Image.Type.Filled;
        fillImg.fillMethod = Image.FillMethod.Radial360;
        fillImg.fillOrigin = (int)Image.Origin360.Top;   // starts at 12 o'clock, where the stick starts
        fillImg.fillClockwise = true;
        fillImg.fillAmount = 0f;
        var frt = fillGO.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero;
        frt.offsetMax = Vector2.zero;
        fill = fillImg;

        float y = topOffset + diameter + GapBarToText;

        if (!string.IsNullOrEmpty(r.label))
        {
            var label = NewText(parent, "RotaryLabel", 28, TextAlignmentOptions.Top);
            label.text = r.label;
            label.color = Color.white;
            StretchTop(label.rectTransform, BarWidth, LabelHeight, y);
            y += LabelHeight + GapLabelToCounts;
        }

        counts = NewText(parent, "RotaryCounts", 24, TextAlignmentOptions.Top);
        counts.color = new Color(1f, 1f, 1f, 0.7f);
        StretchTop(counts.rectTransform, BarWidth, CountsHeight, y);

        return y + CountsHeight + GapSlotToSlot;
    }

    // Generated once and shared by the background and the fill.
    private static Sprite ringSprite;

    /// <summary>A white annulus texture turned into a Sprite. White so the Image's own colour tints it,
    /// with the inner and outer edges feathered a couple of pixels so the ring doesn't alias.</summary>
    static Sprite RingSprite()
    {
        if (ringSprite != null) return ringSprite;

        const int size = 256;
        const float feather = 2f;
        float centre = (size - 1) * 0.5f;
        float outer = centre * 0.98f;
        float inner = centre * 0.74f;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - centre, dy = y - centre;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                // Inside the band = 1, fading out across `feather` pixels at BOTH edges.
                float a = Mathf.Min(Mathf.Clamp01((outer - d) / feather),
                                    Mathf.Clamp01((d - inner) / feather));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();

        ringSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        return ringSprite;
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
