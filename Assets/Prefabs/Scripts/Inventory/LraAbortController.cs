using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Lets the player abort the current race from the TrackScene WITHOUT the failure
/// penalty, by spending one "LRA" item. Hold Left Trigger + Right Trigger + A for one
/// second; a small green progress bar (labelled "LRA") fills in the centre of the
/// screen. On completion one LRA is consumed and the player is returned to the hub
/// with all other credits and items left intact.
///
/// This is the middle ground between the three ways to leave the track:
///   - End Portal            -> success: keep inventory + completion/first-place rewards.
///   - LRA abort             -> keep inventory, but NO rewards (the drones win the round).
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
    [Tooltip("Inventory item consumed to abort. Must match the store's item name.")]
    public string lraItemName = "LRA";

    [Header("Timing")]
    [Tooltip("Seconds the L + R + A combo must be held to complete the abort.")]
    public float holdDuration = 1f;
    [Tooltip("A trigger past this value (0-1) counts as held.")]
    public float triggerThreshold = 0.5f;

    [Header("Bar Style")]
    public float barWidth = 320f;
    public float barHeight = 44f;
    public Color barBackColor = new Color(0f, 0f, 0f, 0.6f);
    public Color barFillColor = new Color(0.18f, 0.9f, 0.32f, 0.95f);

    private float holdTimer;
    private GameObject barRoot;
    private RectTransform fillRect;
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
        // Only abortable while actually racing, with an LRA to spend, and no menu open.
        bool canAbort = IsInTrack() && HasLra() && !MenuState.AnyOpen;

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

    bool HasLra()
    {
        return PlayerInventory.Instance != null
            && PlayerInventory.Instance.GetCount(lraItemName) > 0;
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

        // Spend the LRA. If it somehow vanished mid-hold, bail without leaving.
        var inv = PlayerInventory.Instance;
        if (inv == null || !inv.Consume(lraItemName, 1)) return;

        Debug.Log("[LRA] Race aborted — returning to hub with inventory intact");

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

        // End the round with NO reward and NO inventory reset, then load the hub.
        // (Kill floor / timeout would call ResetToStarting here; the LRA abort does not.)
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

        // Green fill — anchored to the left edge, width driven by hold progress.
        var fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(barRoot.transform, false);
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = barFillColor;
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
    }

    void HideBar()
    {
        if (barRoot != null && barRoot.activeSelf) barRoot.SetActive(false);
    }
}
