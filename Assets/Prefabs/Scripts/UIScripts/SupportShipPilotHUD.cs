using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Support Ship pilot's instruments, shown only while somebody is actually flying a ship. They sit
/// in the slots the CAR's readouts vacate while piloting, so the two views agree on where information
/// lives on screen:
///
///  • HEALTH — a blue pool under the credits, top-left, where the SD readout normally sits.
///  • REPAIRS — a count bottom-left, where the Turbo stock normally sits.
///
/// It exists because the two camera views are two different vehicles. A pilot is looking out of a ship
/// while their car sits parked somewhere they cannot see, so the car's instruments hide (see
/// <see cref="GameplayHud"/>) and this takes their place. Credits are the one readout both views share.
///
/// ⚠️ It is also the ONLY way to read a ship's health. The damage tint is deliberately flash-only — a
/// hurt ship looks exactly like a fresh one, which is right for a drone in someone else's sky and quite
/// wrong for the vehicle you are flying. Before this, a pilot's only signal was counting the red
/// flashes and remembering.
///
/// Builds its own canvas and bootstraps itself on first use, so there is nothing to place in a scene or
/// wire in the inspector — the same approach the credits / turbo / SD readouts take.
/// </summary>
[DefaultExecutionOrder(1000)]
public class SupportShipPilotHUD : MonoBehaviour
{
    // Blue, to match the repair flash: the bar and the flash are two views of the same number, so a
    // pilot who has learnt one has learnt the other. Public because the INVENTORY draws the same bar
    // beside the Support Ship item - two screens showing one quantity should never disagree about what
    // its colours mean, so there is one definition of them.
    public static readonly Color FillColor = new Color(0.2f, 0.62f, 1f, 0.95f);
    public static readonly Color LowColor = new Color(1f, 0.35f, 0.2f, 0.95f);
    public static readonly Color TrackColor = new Color(0f, 0f, 0f, 0.45f);

    // Top-left, tucked under the credits readout. Those coordinates are not arbitrary: CreditsHUD sits
    // at (30, -24) and SDCardHUD at (30, -100), so this takes the same 30px indent and the slot directly
    // beneath the currency - the very slot the SD readout vacates while its owner is piloting.
    const float LeftIndent = 30f;
    const float TopOffset = 102f;
    const float BarWidth = 300f;
    const float BarHeight = 24f;

    // Bottom-left, matching TurboJetHUD's first slot exactly (30px in, 24px up, 300 wide, 44pt) so the
    // Repairs count lands where the pilot's eye already goes for a consumable count.
    const float CountIndent = 30f;
    const float CountBottom = 24f;
    const float CountWidth = 300f;

    /// <summary>Below this fraction the bar turns red — the one moment a pilot needs to be told rather
    /// than left to read a length.</summary>
    public const float LowFraction = 0.34f;

    static SupportShipPilotHUD instance;

    private GameObject canvasGO;
    private RectTransform fill;
    private Image fillImage;
    private TextMeshProUGUI repairsLabel;
    private SupportShip ship;
    private ulong ownerId;
    private bool showing;

    /// <summary>Show the bar for the ship belonging to <paramref name="owner"/>. Called by
    /// <see cref="PilotControlCenter"/> as the controls are taken.
    ///
    /// It takes an OWNER rather than a ship because the ship object is not stable: it can be dismissed
    /// and re-summoned, or rebuilt from a replicated state, and a captured reference would leave the
    /// bar frozen on a corpse. Resolving through the replicator every frame always finds the live
    /// one.</summary>
    public static void Show(ulong owner) => Ensure().Bind(owner, true);

    /// <summary>Put the bar away — the controls were handed back.</summary>
    public static void Hide()
    {
        if (instance != null) instance.Bind(0, false);   // never builds one just to hide it
    }

    static SupportShipPilotHUD Ensure()
    {
        if (instance == null)
        {
            var go = new GameObject("SupportShipPilotHUD");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<SupportShipPilotHUD>();
        }
        return instance;
    }

    void Bind(ulong owner, bool show)
    {
        ownerId = owner;
        showing = show;
        if (showing && canvasGO == null) BuildUI();
        Resolve();
    }

    void LateUpdate()
    {
        if (!showing && (canvasGO == null || !canvasGO.activeSelf)) return;
        Resolve();
    }

    /// <summary>Finds the live ship and shows or hides accordingly. A ship destroyed out from under the
    /// pilot mid-flight is exactly when a stale bar would mislead most, so the bar goes with it.</summary>
    void Resolve()
    {
        ship = showing ? SupportShipReplicator.GetShip(ownerId) : null;

        bool visible = ship != null;
        if (canvasGO != null && canvasGO.activeSelf != visible) canvasGO.SetActive(visible);
        if (visible) Refresh();
    }

    void Refresh()
    {
        if (ship == null || fill == null) return;

        float fraction = Mathf.Clamp01(ship.HealthFraction);
        fill.sizeDelta = new Vector2(BarWidth * fraction, BarHeight);
        fillImage.color = fraction <= LowFraction ? LowColor : FillColor;

        // ⚠️ The OWNER's stock, not ours — Y spends from their inventory, so our own count would be a
        // confident number with no bearing on whether the next press does anything.
        // "Repairs: N" — the same shape as Turbo / Jet / Shield / Grapple / SD, since this sits in the
        // slot those occupy and a lone item reading differently would look like a different KIND of thing.
        repairsLabel.text = "Repairs: " + SupportShipReplicator.RepairsFor(ownerId);
    }

    void BuildUI()
    {
        canvasGO = new GameObject("SupportShipHealthCanvas");
        DontDestroyOnLoad(canvasGO);

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        // Top-left, directly under the credits.
        var track = NewRect("Track", canvasGO.transform, BarWidth, BarHeight);
        track.anchoredPosition = new Vector2(LeftIndent, -TopOffset);
        var trackImage = track.gameObject.AddComponent<Image>();
        trackImage.color = TrackColor;

        // The fill is LEFT-pivoted inside the track, so shrinking its width drains it from the right
        // rather than from both ends.
        fill = NewRect("Fill", track, BarWidth, BarHeight);
        fill.anchorMin = new Vector2(0f, 0.5f);
        fill.anchorMax = new Vector2(0f, 0.5f);
        fill.pivot = new Vector2(0f, 0.5f);
        fill.anchoredPosition = Vector2.zero;
        fillImage = fill.gameObject.AddComponent<Image>();
        fillImage.color = FillColor;

        // No numeric readout on the bar: the LENGTH already says it, and a figure sitting on top of a
        // shrinking blue strip only competes with it. The Repairs count below is a different case — a
        // stock has no bar to read, so it has to be a number.

        // Bottom-left: the Repairs the OWNER is carrying, which are the ones this pilot can spend.
        var count = NewRect("Repairs", canvasGO.transform, CountWidth, 64f);
        count.anchorMin = count.anchorMax = count.pivot = new Vector2(0f, 0f);
        count.anchoredPosition = new Vector2(CountIndent, CountBottom);
        repairsLabel = count.gameObject.AddComponent<TextMeshProUGUI>();
        repairsLabel.fontSize = 44f;
        repairsLabel.color = FillColor;   // the same blue as the pool it refills
        repairsLabel.alignment = TextAlignmentOptions.BottomLeft;
        repairsLabel.raycastTarget = false;

        // Park the whole canvas on the UI layer. Code-built UI defaults to the DEFAULT layer, which is
        // how the grappling hook was once able to latch onto a HUD. Applied last, so the children are
        // already parented and get it too.
        UiLayer.Apply(canvasGO);
    }

    /// <summary>A rect anchored to its parent's TOP-LEFT, matching how the credits and SD readouts are
    /// placed - so the bar keeps its indent and its distance below the currency at every resolution.</summary>
    static RectTransform NewRect(string name, Transform parent, float width, float height)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        return rt;
    }
}
