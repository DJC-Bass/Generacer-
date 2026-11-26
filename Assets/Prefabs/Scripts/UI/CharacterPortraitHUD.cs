using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The driver's portrait, top-left: a box outlined in the local team's colour (red Team 1, blue Team 2,
/// black single-player) over a faded square of the same colour, with the selected car's character
/// animating on top and reacting to what the car is doing.
///
/// The character is NOT clipped to the box. The art is drawn around it, and some animations reach
/// outside on purpose, so the character draws on top of the outline and over anything nearby.
///
/// Which character shows comes from the <see cref="PortraitCharacter"/> assets in
/// <c>Resources/Portraits</c>: the one listing the selected car's name, else the one ticked Fallback.
///
/// What it shows, highest priority first:
///  1. A REACTION while one is running: boulder hit &gt; lightning hit &gt; turbo &gt; money get. An equal or
///     higher reaction cuts in; a lower one arriving mid-reaction is dropped.
///  2. AIRBORNE while the car is genuinely in the air (CarController.IsAirborne, which already ignores crests).
///  3. DRIFTING HARD left/right: drifting with the stick at (nearly) full lock — the transition once,
///     then the hold loop for as long as the stick stays over.
///  4. LIMIT BREAK while the car is past the speed barrier.
///  5. IDLE, blinking at random.
/// Steps 2–4 read the car, so they pause while the player is flying a Support Ship (the box itself stays
/// up: it is the player, not the vehicle). Reactions still play then — credits still land, and the
/// parked car can still be struck.
///
/// Clips play through a small PlayableGraph rather than an Animator Controller, so each character is
/// just a list of clips and there are no transitions to wire per character. A clip keyed on a
/// SpriteRenderer (what dragging sprite frames into the Scene makes) drives a hidden SpriteRenderer
/// whose sprite is copied onto the UI Image each frame; a clip keyed on a UI Image drives the Image
/// directly.
///
/// Persistent and code-built like the other HUDs: added to the PlayerSystems object by
/// <see cref="PlayerInventory"/>, builds its own canvas, shown in gameplay scenes only.
/// </summary>
[DefaultExecutionOrder(1000)]
public class CharacterPortraitHUD : MonoBehaviour
{
    public static CharacterPortraitHUD Instance { get; private set; }

    // ---- Layout, in reference pixels (1920×1080). Measured from the user's concept mock-up. ----
    public const float BoxLeft = 47f;
    public const float BoxTop = 18f;
    public const float BoxSize = 155f;
    public const float BorderWidth = 6f;
    /// <summary>Bottom edge of the box, measured down from the top of the screen.</summary>
    public const float BoxBottom = BoxTop + BoxSize;
    /// <summary>Left edge for the top-left readouts that sit BESIDE the box (credits, SD, the Support
    /// Ship health bar), so they all clear it by the same gap.</summary>
    public const float SideIndent = 240f;

    static readonly Color Team1Color = new Color(0.80f, 0.11f, 0.10f);
    static readonly Color Team2Color = new Color(0.13f, 0.36f, 0.92f);
    static readonly Color SoloColor = new Color(0.04f, 0.04f, 0.04f);
    const float FillAlpha = 0.45f;   // the inner square: the team colour, see-through

    [Header("Drifting hard")]
    [Tooltip("How far over the steer stick must be (0..1) while drifting to count as drifting HARD.")]
    [Range(0.1f, 1f)] public float hardDriftSteer = 0.9f;
    [Tooltip("Once drifting hard, the stick must come back inside this before the portrait lets go, so a " +
             "stick wobbling at full lock doesn't flicker the animation.")]
    [Range(0f, 1f)] public float hardDriftRelease = 0.7f;

    // ---- UI ----
    private GameObject canvasGO;
    private Image fill;
    private readonly Image[] edges = new Image[4];
    private RectTransform characterRT;
    private Image characterImage;
    private SpriteRenderer spriteProxy;   // hidden: the target of SpriteRenderer-keyed clips
    private Animator animator;
    private Sprite lastProxySprite;
    private Sprite sizedSprite;           // the sprite the character rect was last sized for
    private float boxFrameHeight;         // height (sprite units) of the frame that fills the box; 0 = not measured yet
    private Color appliedColor = new Color(-1f, 0f, 0f, 0f);

    // ---- Playback ----
    private PlayableGraph graph;
    private AnimationPlayableOutput output;
    private AnimationClipPlayable playable;
    private AnimationClip playingClip;
    private float clipTime;

    // ---- Who ----
    private PortraitCharacter[] roster;
    private PortraitCharacter character;
    private string resolvedCarName;
    private GameObject boundCarGO;
    private CarController car;

    // ---- State ----
    // Reaction kinds in ascending priority (a higher value interrupts a lower one).
    enum ReactionKind { None = 0, Money = 1, Turbo = 2, Lightning = 3, Boulder = 4 }
    private ReactionKind reaction = ReactionKind.None;
    private float reactionTimer;
    private bool restartClip;          // the next Play() starts its clip over even if it's the same clip

    private int driftDir;              // -1 left, +1 right, 0 not drifting hard
    private bool driftInTransition;    // playing the transition (true) or the hold (false)

    private float idleTime;            // grounded-idle time since the last blink
    private float nextBlinkAt;
    private bool blinking;
    private float blinkTime;

    // =====================================================================
    //  Lifecycle
    // =====================================================================

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        BuildUI();
        BuildGraph();
    }

    void OnEnable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.CreditsEarned += OnCreditsEarned;
        SceneManager.sceneLoaded += OnSceneLoaded;
        GameplayHud.OnVisibilityChanged += ApplyVisibility;
        ApplyVisibility();
    }

    void OnDisable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.CreditsEarned -= OnCreditsEarned;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        GameplayHud.OnVisibilityChanged -= ApplyVisibility;
        BindCar(null);
    }

    void OnDestroy()
    {
        if (graph.IsValid()) graph.Destroy();
        if (Instance == this) Instance = null;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ApplyVisibility();

    /// <summary>Shown in every gameplay scene, in BOTH views — the portrait is the player, not the car,
    /// so it stays up while they fly a Support Ship.</summary>
    void ApplyVisibility()
    {
        if (canvasGO != null) canvasGO.SetActive(GameplayHud.ShowShared);
    }

    void LateUpdate()
    {
        if (canvasGO == null || !canvasGO.activeSelf) return;

        // A new car is spawned per scene, so keep re-resolving the local one.
        var carGO = PlayerRegistry.LocalCar;
        if (carGO != boundCarGO) BindCar(carGO);

        string carName = SelectedCarName();
        if (character == null || carName != resolvedCarName) ResolveCharacter(carName);

        ApplyTeamColour();
        Step(Time.deltaTime);
    }

    // =====================================================================
    //  Who's driving
    // =====================================================================

    void BindCar(GameObject carGO)
    {
        if (car != null)
        {
            car.BoulderHit -= OnBoulderHit;
            car.LightningHit -= OnLightningHit;
            car.TurboActivated -= OnTurbo;
        }

        boundCarGO = carGO;
        car = carGO != null ? carGO.GetComponent<CarController>() : null;
        driftDir = 0;

        if (car != null)
        {
            car.BoulderHit += OnBoulderHit;
            car.LightningHit += OnLightningHit;
            car.TurboActivated += OnTurbo;
        }

        // Play-testing a scene directly has no Car Selection pick, so the car itself is the hint.
        if (carGO != null) ResolveCharacter(SelectedCarName());
    }

    static string SelectedCarName() =>
        SelectedCarStore.Instance != null ? SelectedCarStore.Instance.SelectedCarName ?? "" : "";

    void ResolveCharacter(string carName)
    {
        resolvedCarName = carName;
        if (roster == null) roster = Resources.LoadAll<PortraitCharacter>("Portraits");

        PortraitCharacter pick = null;

        // 1. The character listing the selected car.
        foreach (var c in roster)
            if (c != null && c.Drives(carName)) { pick = c; break; }

        // 2. No pick (e.g. a scene played directly): a character whose car name is in the car object's name.
        if (pick == null && boundCarGO != null)
        {
            string objName = boundCarGO.name;
            foreach (var c in roster)
            {
                if (c == null || c.carNames == null) continue;
                foreach (var n in c.carNames)
                    if (!string.IsNullOrWhiteSpace(n) &&
                        objName.IndexOf(n.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0) { pick = c; break; }
                if (pick != null) break;
            }
        }

        // 3. The fallback character, else whoever exists.
        if (pick == null)
            foreach (var c in roster)
                if (c != null && c.fallback) { pick = c; break; }
        if (pick == null && roster.Length > 0) pick = roster[0];

        if (pick == character) return;
        character = pick;

        // Fresh character, fresh state.
        reaction = ReactionKind.None;
        reactionTimer = 0f;
        driftDir = 0;
        blinking = false;
        idleTime = 0f;
        RollNextBlink();
        playingClip = null;
        sizedSprite = null;

        // Show the idle's first frame straight away: it is the frame the scale is measured from (see
        // SizeCharacter), so measure it now rather than from whatever state happens to play first.
        boxFrameHeight = 0f;
        if (character != null) Play(character.idle, true, 0f);
    }

    void ApplyTeamColour()
    {
        Color c = SoloColor;
        if (MultiplayerWorld.IsMultiplayerGame && NetworkSessionManager.Instance != null)
        {
            // 0 = not assigned yet (it can lag a moment at match start); stays neutral until it lands.
            int team = NetworkSessionManager.Instance.LocalTeam();
            if (team == 1) c = Team1Color;
            else if (team == 2) c = Team2Color;
        }

        if (c == appliedColor) return;
        appliedColor = c;
        foreach (var e in edges) e.color = c;
        fill.color = new Color(c.r, c.g, c.b, FillAlpha);
    }

    // =====================================================================
    //  Reactions
    // =====================================================================

    void OnBoulderHit() => React(ReactionKind.Boulder);
    void OnLightningHit() => React(ReactionKind.Lightning);
    void OnTurbo() => React(ReactionKind.Turbo);
    void OnCreditsEarned(int amount) => React(ReactionKind.Money);

    PortraitCharacter.Reaction ReactionFor(ReactionKind kind)
    {
        if (character == null) return null;
        switch (kind)
        {
            case ReactionKind.Boulder: return character.boulderHit;
            case ReactionKind.Lightning: return character.lightningHit;
            case ReactionKind.Turbo: return character.turbo;
            case ReactionKind.Money: return character.moneyGet;
        }
        return null;
    }

    void React(ReactionKind kind)
    {
        var r = ReactionFor(kind);
        if (r == null || !r.HasClip || r.Length <= 0f) return;            // no art for it yet
        if (reactionTimer > 0f && kind < reaction) return;                 // a bigger reaction is playing

        reaction = kind;
        reactionTimer = r.Length;
        restartClip = true;
    }

    // =====================================================================
    //  State → clip
    // =====================================================================

    void Step(float dt)
    {
        if (character == null) { ShowCharacter(false); return; }

        // 1. Reactions.
        if (reactionTimer > 0f)
        {
            reactionTimer -= dt;
            var r = ReactionFor(reaction);
            if (reactionTimer > 0f && r != null && r.HasClip)
            {
                CancelBlink();
                Play(r.clip, r.clip.isLooping, dt);
                return;
            }
            reaction = ReactionKind.None;
            reactionTimer = 0f;
        }

        // Car-driven states only while actually driving it.
        bool driving = car != null && !GameplayHud.Piloting;

        // 2. Airborne. No airborne art yet = the plain idle (no blink: blinking is a grounded idle).
        UpdateDriftDir(driving);
        if (driving && car.IsAirborne)
        {
            CancelBlink();
            Play(character.airborne != null ? character.airborne : character.idle, true, dt);
            return;
        }

        // 3. Drifting hard.
        if (driftDir != 0 && PlayDrift(dt)) { CancelBlink(); return; }

        // 4. Limit break.
        if (driving && character.limitBreak != null && CameraFollow.SpeedBarrierBrokenFor(car))
        {
            CancelBlink();
            Play(character.limitBreak, true, dt);
            return;
        }

        // 5. Idle, with a blink now and then.
        if (blinking)
        {
            blinkTime += dt;
            if (character.blink != null && blinkTime < character.blink.length)
            {
                Play(character.blink, false, dt);
                return;
            }
            blinking = false;
            idleTime = 0f;
            RollNextBlink();
        }
        else
        {
            idleTime += dt;
            if (character.blink != null && idleTime >= nextBlinkAt)
            {
                blinking = true;
                blinkTime = 0f;
                restartClip = true;
                Play(character.blink, false, dt);
                return;
            }
        }
        Play(character.idle, true, dt);
    }

    /// <summary>Tracks which way (if any) the player is drifting hard, with release hysteresis. Left and
    /// right mean the STICK's direction — the way the driver is hauling the wheel — so a reverse drift
    /// leans the same way as a forward one for the same stick.</summary>
    void UpdateDriftDir(bool driving)
    {
        int prev = driftDir;
        if (!driving || !car.IsDrifting)
        {
            driftDir = 0;
        }
        else
        {
            float s = car.SteerInput;
            if (s <= -hardDriftSteer) driftDir = -1;
            else if (s >= hardDriftSteer) driftDir = 1;
            else if (Mathf.Abs(s) < hardDriftRelease) driftDir = 0;
            // between release and full lock: keep whatever it was
        }

        if (driftDir != 0 && driftDir != prev)
        {
            driftInTransition = true;   // a new lean (or a flick to the other side) starts with its transition
            restartClip = true;
        }
    }

    /// <summary>Plays the current drift lean. False when this character has no art for that side.</summary>
    bool PlayDrift(float dt)
    {
        AnimationClip transition = driftDir < 0 ? character.driftLeftTransition : character.driftRightTransition;
        AnimationClip hold = driftDir < 0 ? character.driftLeftHold : character.driftRightHold;
        if (transition == null && hold == null) return false;

        if (driftInTransition)
        {
            if (transition != null && !(playingClip == transition && clipTime >= transition.length))
            {
                Play(transition, false, dt);
                return true;
            }
            driftInTransition = false;
            restartClip = true;
        }

        if (hold != null) Play(hold, true, dt);
        else Play(transition, false, dt);   // no hold art: the transition's last frame holds
        return true;
    }

    void CancelBlink()
    {
        if (!blinking) return;
        blinking = false;
        idleTime = 0f;
        RollNextBlink();
    }

    void RollNextBlink()
    {
        float min = character != null ? character.blinkIntervalMin : 1f;
        float max = character != null ? character.blinkIntervalMax : 3f;
        nextBlinkAt = Random.Range(min, Mathf.Max(min, max));
    }

    // =====================================================================
    //  Playback
    // =====================================================================

    /// <summary>Shows <paramref name="clip"/> this frame, advancing it by <paramref name="dt"/>. Switching
    /// clips (or a pending restart) starts the clip from its first frame. A looping clip wraps; a
    /// one-shot holds its last frame.</summary>
    void Play(AnimationClip clip, bool loop, float dt)
    {
        if (clip == null) { ShowCharacter(false); return; }

        if (clip != playingClip || restartClip)
        {
            restartClip = false;
            if (playable.IsValid()) playable.Destroy();
            playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetApplyFootIK(false);
            output.SetSourcePlayable(playable);
            playingClip = clip;
            clipTime = 0f;
            // Forget the proxy's last frame so the new clip's first sprite always lands on the Image,
            // even when it happens to be the same sprite the last clip ended on.
            spriteProxy.sprite = null;
            lastProxySprite = null;
        }
        else
        {
            clipTime += dt;
        }

        float len = clip.length;
        float t = len <= 0f ? 0f
                : loop ? Mathf.Repeat(clipTime, len)
                : Mathf.Min(clipTime, Mathf.Max(0f, len - 0.0001f));
        playable.SetTime(t);
        graph.Evaluate(0f);

        // SpriteRenderer-keyed clips land on the hidden proxy; carry the frame over to the Image.
        if (spriteProxy.sprite != lastProxySprite)
        {
            lastProxySprite = spriteProxy.sprite;
            if (lastProxySprite != null) characterImage.sprite = lastProxySprite;
        }

        ShowCharacter(characterImage.sprite != null);
        SizeCharacter();
    }

    void ShowCharacter(bool show)
    {
        if (characterImage.enabled != show) characterImage.enabled = show;
    }

    /// <summary>Draws every frame at one scale per character, placed by the sprite's own pivot, so frames
    /// of any size line up and anything bigger than the idle frame spills out of the box.
    ///
    /// Measured in sprite UNITS (pixels ÷ Pixels Per Unit), not pixels: a sheet bigger than its import
    /// Max Size is shrunk on import, which shrinks its sprites' pixel rects AND their pixels-per-unit by
    /// the same factor — George's 1140 px frames arrive as 1024 px — while the units stay put.</summary>
    void SizeCharacter()
    {
        Sprite s = characterImage.sprite;
        if (s == null || s == sizedSprite) return;
        sizedSprite = s;

        Vector2 px = s.rect.size;
        Vector2 units = px / Mathf.Max(0.0001f, s.pixelsPerUnit);
        if (boxFrameHeight <= 0f) boxFrameHeight = Mathf.Max(0.0001f, units.y);   // the idle's first frame

        float inner = BoxSize - 2f * BorderWidth;
        float scale = inner * character.size / boxFrameHeight;   // reference px per sprite unit
        characterRT.sizeDelta = units * scale;
        characterRT.pivot = new Vector2(s.pivot.x / Mathf.Max(1f, px.x), s.pivot.y / Mathf.Max(1f, px.y));
        characterRT.anchoredPosition = character.offset;
    }

    // =====================================================================
    //  Build
    // =====================================================================

    void BuildGraph()
    {
        graph = PlayableGraph.Create("CharacterPortrait");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);   // advanced by hand in Play()
        output = AnimationPlayableOutput.Create(graph, "Portrait", animator);
        graph.Play();
    }

    void BuildUI()
    {
        canvasGO = new GameObject("CharacterPortraitCanvas");
        DontDestroyOnLoad(canvasGO);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 151;   // just above the other gameplay HUDs (150), so art that spills out sits on top
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // The box, anchored to the top-left corner like the credits / SD readouts.
        var box = NewRect("PortraitBox", canvasGO.transform);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0f, 1f);
        box.sizeDelta = new Vector2(BoxSize, BoxSize);
        box.anchoredPosition = new Vector2(BoxLeft, -BoxTop);

        // Inner square: the team colour, faded and see-through.
        var fillRT = NewRect("Fill", box);
        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = Vector2.one;
        fillRT.offsetMin = new Vector2(BorderWidth, BorderWidth);
        fillRT.offsetMax = new Vector2(-BorderWidth, -BorderWidth);
        fill = fillRT.gameObject.AddComponent<Image>();
        fill.raycastTarget = false;

        // Outline: four solid edges, so the see-through fill never shows a second layer of colour.
        edges[0] = Edge(box, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, BorderWidth));
        edges[1] = Edge(box, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, BorderWidth));
        edges[2] = Edge(box, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(BorderWidth, 0f));
        edges[3] = Edge(box, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(BorderWidth, 0f));

        // The character, last so it draws over the outline. No mask: it is allowed to spill out.
        characterRT = NewRect("Character", box);
        characterRT.anchorMin = characterRT.anchorMax = new Vector2(0.5f, 0.5f);
        characterImage = characterRT.gameObject.AddComponent<Image>();
        characterImage.raycastTarget = false;
        characterImage.enabled = false;

        // Sprite clips made by dragging frames into the Scene key a SpriteRenderer, which a UI Image
        // isn't. This hidden one takes those keys; Play() copies its sprite onto the Image. It must stay
        // disabled — on an overlay canvas it would otherwise draw in the 3D world at pixel coordinates.
        spriteProxy = characterRT.gameObject.AddComponent<SpriteRenderer>();
        spriteProxy.enabled = false;

        animator = characterRT.gameObject.AddComponent<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        ApplyTeamColour();
        UiLayer.Apply(canvasGO);   // keep code-built UI off the Default layer (see UiLayer)
    }

    static Image Edge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = new Vector2(anchorMin.x == anchorMax.x ? anchorMin.x : 0.5f,
                               anchorMin.y == anchorMax.y ? anchorMin.y : 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = Vector2.zero;
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }
}
