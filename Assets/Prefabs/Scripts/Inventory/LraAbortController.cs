using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Lets the player bail out of the current race early — a manual alternative to falling all the way to
/// the kill floor. Hold Left Trigger + Right Trigger + A for one second; a progress bar fills in the
/// centre of the screen, and on completion the player is sent back to the hub.
///
/// TWO TIERS, and the bar's COLOUR is the tell:
///   • DEFAULT (RED bar) — always available to every car, no item required and nothing to run out of.
///     Costs the run though: the inventory is wiped back to the starting defaults, exactly as if the
///     player had hit the kill floor or timed out. It only buys them the time they'd have spent falling.
///   • PREMIUM (GREEN bar) — active whenever the player holds an "LRA Premium" (bought from the store).
///     Consumes one and returns them with their inventory INTACT: the original LRA behaviour. Still
///     one-use, so another must be bought to get the safe exit again.
/// The default tier deliberately has NO inventory item behind it — it's an innate ability of the car,
/// so nothing appears in the inventory view and it can never be depleted.
///
/// So the four ways to leave the track now rank:
///   - End Portal            -> success: keep inventory + completion/first-place rewards.
///   - LRA Premium abort     -> keep inventory, but NO rewards (the drones win the round).
///   - LRA default abort     -> inventory wiped, same as a failure, but on the player's own terms.
///   - Kill floor / timeout  -> failure: inventory wiped back to the starting defaults.
///
/// Persistent and bootstrapped on the PlayerSystems object (like the HUDs), so it
/// needs zero scene setup. It only acts while the game loop is in the InTrack phase,
/// so the combo does nothing back in the hub.
///
/// The combo is read straight off <see cref="Gamepad.current"/> because the generated
/// input actions only expose Throttle as a single RT-minus-LT axis, which can't tell
/// "both triggers held" apart from "neither held".
/// </summary>
[DefaultExecutionOrder(1000)]
public class LraAbortController : MonoBehaviour
{
    public static LraAbortController Instance { get; private set; }

    [Header("Item")]
    [Tooltip("Item that UPGRADES the abort to keep the player's inventory. Consumed on use. Must match " +
             "the store's item name EXACTLY — a mismatch silently drops every abort to the default " +
             "(inventory-wiping) tier. The default tier needs no item at all.")]
    public string premiumItemName = "LRA Premium";

    [Header("Timing")]
    [Tooltip("Seconds the L + R + A combo must be held to complete the abort.")]
    public float holdDuration = 1f;
    [Tooltip("A trigger past this value (0-1) counts as held.")]
    public float triggerThreshold = 0.5f;

    [Header("Bar Style")]
    public float barWidth = 320f;
    public float barHeight = 44f;
    public Color barBackColor = new Color(0f, 0f, 0f, 0.6f);
    [Tooltip("Fill colour for the DEFAULT abort — RED, warning that this one wipes the inventory.")]
    public Color barFillColorDefault = new Color(0.95f, 0.18f, 0.18f, 0.95f);
    [Tooltip("Fill colour while an LRA Premium is held — GREEN, the safe abort that keeps everything.")]
    public Color barFillColorPremium = new Color(0.18f, 0.9f, 0.32f, 0.95f);

    private float holdTimer;
    private GameObject barRoot;
    private RectTransform fillRect;
    private Image fillImage;
    private AudioSource lraLoopSource;   // looping "activating" sound while the L+R+A combo is held

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        // 2D looping "charge" sound for the hold — the progress bar it accompanies is screen UI.
        lraLoopSource = gameObject.AddComponent<AudioSource>();
        lraLoopSource.loop = true;
        lraLoopSource.playOnAwake = false;
        lraLoopSource.spatialBlend = 0f;

        BuildUI();
        HideBar();
    }

    void Update()
    {
        // Abortable whenever the player is actually racing with no menu open. NO item check — the
        // default tier is innate to the car; holding an LRA Premium only upgrades what it costs.
        bool canAbort = IsInTrack() && !MenuState.AnyOpen;

        if (canAbort && IsComboHeld())
        {
            holdTimer += Time.deltaTime;
            ShowBar(holdTimer / holdDuration);
            StartLoop();

            if (holdTimer >= holdDuration)
                CompleteAbort();
        }
        else
        {
            // Releasing any part of the combo resets the hold from scratch.
            holdTimer = 0f;
            HideBar();
            StopLoop();
        }
    }

    void StartLoop()
    {
        if (lraLoopSource == null) return;

        // Fetch the clip lazily — AudioManager may bootstrap after us.
        if (lraLoopSource.clip == null)
        {
            var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
            if (lib != null) lraLoopSource.clip = lib.lraActivateLoop;
        }

        if (lraLoopSource.clip != null && !lraLoopSource.isPlaying)
        {
            lraLoopSource.volume = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 0.9f;
            lraLoopSource.Play();
        }
    }

    void StopLoop()
    {
        if (lraLoopSource != null && lraLoopSource.isPlaying) lraLoopSource.Stop();
    }

    bool IsInTrack()
    {
        // Multiplayer: presence is per-player (the shared world's phase never goes InTrack — the
        // server keeps it HubPortalActive for the whole round), so ask MultiplayerWorld instead.
        if (MultiplayerWorld.IsMultiplayerGame)
            return MultiplayerWorld.Instance.InTrackLocally;

        return GameLoopManager.Instance != null
            && GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.InTrack;
    }

    /// <summary>True while the player holds an LRA Premium — the abort will keep their inventory, and
    /// the bar shows green. Checked live during the hold so the colour is always honest.</summary>
    bool HasPremium()
    {
        return PlayerInventory.Instance != null
            && PlayerInventory.Instance.GetCount(premiumItemName) > 0;
    }

    bool IsComboHeld()
    {
        var gp = Gamepad.current;
        if (gp == null) return false;
        return gp.leftTrigger.ReadValue() > triggerThreshold
            && gp.rightTrigger.ReadValue() > triggerThreshold
            && gp.buttonSouth.isPressed;   // A (Xbox) / Cross (PlayStation)
    }

    void CompleteAbort()
    {
        holdTimer = 0f;
        HideBar();
        StopLoop();

        var inv = PlayerInventory.Instance;
        if (inv == null) return;

        // Try to spend a Premium. Getting one = the safe exit (inventory kept). Otherwise this is the
        // DEFAULT abort, which costs the run: wipe back to the starting defaults exactly like the kill
        // floor does. Consume-or-wipe in one place, so the two can never both happen.
        bool premium = inv.Consume(premiumItemName, 1);
        if (!premium) inv.ResetToStarting();

        Debug.Log(premium
            ? "[LRA] Premium abort — returning to hub with inventory intact."
            : "[LRA] Default abort — returning to hub; inventory reset (same as a kill-floor failure).");

        // Multiplayer: the abort is a per-player TELEPORT back to the hub (no scene load; the round
        // keeps running for everyone else). The teleport plays the portal-exit sound itself.
        if (MultiplayerWorld.IsMultiplayerGame)
        {
            MultiplayerWorld.Instance.ReturnToHubLocally();
            return;
        }

        // Play the Portal Exit sound off the player car when it lands back in the hub, same as a real
        // portal return (the car's PortalExitAudio consumes this on spawn).
        AudioManager.ArmPortalExit();

        // End the round with NO reward, then load the hub. Whether the inventory survived was already
        // decided above (Premium keeps it; the default tier has already wiped it like the kill floor).
        string hubScene = "HubWorld";
        if (GameLoopManager.Instance != null)
        {
            GameLoopManager.Instance.NotifyReturnedToHub();
            hubScene = GameLoopManager.Instance.hubSceneName;
        }
        SceneManager.LoadScene(hubScene);
    }

    // -------------------------------------------------------
    //  UI — small centred bar built in code, like the other HUDs
    // -------------------------------------------------------

    void BuildUI()
    {
        var canvasGO = new GameObject("LraAbortCanvas");
        DontDestroyOnLoad(canvasGO);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;   // above the HUDs (150)
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Bar root — centred on screen.
        barRoot = new GameObject("LraBar", typeof(RectTransform));
        barRoot.transform.SetParent(canvasGO.transform, false);
        var rootRT = (RectTransform)barRoot.transform;
        rootRT.anchorMin = rootRT.anchorMax = rootRT.pivot = new Vector2(0.5f, 0.5f);
        rootRT.sizeDelta = new Vector2(barWidth, barHeight);
        rootRT.anchoredPosition = Vector2.zero;

        // Background (added first = drawn behind).
        var bg = new GameObject("Background", typeof(RectTransform));
        bg.transform.SetParent(barRoot.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = barBackColor;
        StretchToParent(bgImg.rectTransform);

        // Fill — anchored to the left edge, width driven by hold progress. Its COLOUR is set per-frame
        // in ShowBar (red = default/inventory-wiping, green = premium/safe).
        var fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(barRoot.transform, false);
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = barFillColorDefault;
        fillImage = fillImg;
        fillRect = fillImg.rectTransform;
        fillRect.anchorMin = new Vector2(0f, 0f);
        fillRect.anchorMax = new Vector2(0f, 1f);
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.anchoredPosition = Vector2.zero;
        fillRect.sizeDelta = new Vector2(0f, 0f);   // width set per-frame in ShowBar

        // "LRA" label centred over the bar (added last = drawn on top).
        var labelGO = new GameObject("Label", typeof(RectTransform));
        labelGO.transform.SetParent(barRoot.transform, false);
        var label = labelGO.AddComponent<TextMeshProUGUI>();
        label.text = "LRA";
        label.fontSize = 28;
        label.fontStyle = FontStyles.Bold;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center;
        StretchToParent(label.rectTransform);
    }

    static void StretchToParent(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    void ShowBar(float t)
    {
        t = Mathf.Clamp01(t);
        if (barRoot != null && !barRoot.activeSelf) barRoot.SetActive(true);
        if (fillRect != null) fillRect.sizeDelta = new Vector2(barWidth * t, 0f);

        // Recoloured live rather than once at show: it tells the player, mid-hold, exactly which abort
        // they're about to commit to — green (keeps everything) or red (wipes the run).
        if (fillImage != null)
        {
            Color want = HasPremium() ? barFillColorPremium : barFillColorDefault;
            if (fillImage.color != want) fillImage.color = want;
        }
    }

    void HideBar()
    {
        if (barRoot != null && barRoot.activeSelf) barRoot.SetActive(false);
    }
}
