using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;

/// <summary>
/// The HUB-world pilot station. Drop this on the PilotControlCenter prefab (the one with the box
/// collider the player drives into) — like the store and the upgrade ramp, the collider must be a
/// trigger, which Reset() sets automatically.
///
/// Driving in opens a list of every TEAMMATE currently flying a Support Ship. Pick one and this
/// machine takes the controls of that ship: the view cuts to a chase camera riding it, the left stick
/// slides it around inside its movement box, and the pilot's own hub car is frozen where it stands so
/// it can't wander off while they're looking somewhere else. B (or driving out of the pad) hands the
/// ship back.
///
/// The pilot flies a LOCAL copy of the ship — the same trick <see cref="HubSpectatorTV"/> uses to show
/// a racer on a hub screen. A remote player's actual camera can't cross the network, but the world is
/// generated identically on every machine, so a chase camera stood up here and pointed at our own copy
/// of their ship is a faithful live view. Only the resulting stick OFFSET goes on the wire; see
/// <see cref="SupportShipReplicator"/>.
///
/// The ship being flown belongs to someone in the TrackScene, ~100 km from the hub. Nothing special is
/// needed for that: the camera simply follows an object that far away, and the AudioListener rides
/// with it, so the pilot hears the racer's world rather than the hub they're standing in.
/// </summary>
[RequireComponent(typeof(Collider))]
[DefaultExecutionOrder(1000)]   // see MenuState: run after CarController reads input
public class PilotControlCenter : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Tag on the player car (or any of its colliders).")]
    public string playerTag = "Player";

    [Header("Who may be flown")]
    [Tooltip("Only list ships belonging to the local player's OWN team. Off = any active ship in the " +
             "session, which is really only useful for testing.")]
    public bool teamOnly = true;

    [Header("Pilot camera (mirrors the in-game camera feel)")]
    [Tooltip("Copy the local player's live camera rig (smooth times, look-ahead, roll blend) so flying " +
             "the ship feels like driving. Uncheck to hand-place it with the fields below.")]
    public bool matchPlayerCamera = true;
    [Tooltip("Camera offset behind/above the ship, in the ship's local frame — same as CameraFollow.")]
    public Vector3 cameraOffset = new Vector3(0f, 3f, -12f);
    public float positionSmoothTime = 0.35f;   // yaw/orbit lag
    public float pitchSmoothTime = 0.2f;
    public float rollSmoothTime = 0.2f;
    public float rotationSmoothTime = 0.1f;
    public float lookAheadDistance = 8f;
    public float fieldOfView = 75f;
    public float nearClip = 0.3f;
    [Tooltip("Far clip — generous, since the ship looks out over a whole generated track.")]
    public float farClip = 20000f;

    [Header("Row Style")]
    public Color rowNormal = new Color(0f, 0f, 0f, 0.55f);
    public Color rowSelected = new Color(0.35f, 0.9f, 1f, 0.95f);
    public Color rowBusy = new Color(0.25f, 0.1f, 0.1f, 0.7f);
    public Color textNormal = Color.white;
    public Color textSelected = Color.black;

    // ---- detection state ----
    private readonly HashSet<Collider> playerColliders = new HashSet<Collider>();
    private bool PlayerInside => playerColliders.Count > 0;

    // ---- menu state ----
    private bool isOpen;
    private int selected;
    private bool suppressedUntilExit;
    private readonly List<KeyValuePair<ulong, ulong>> available = new List<KeyValuePair<ulong, ulong>>();

    // ---- piloting state ----
    private bool piloting;
    private ulong pilotedOwner;
    private Camera pilotCam;
    private CameraFollow pilotFollow;
    private AudioListener pilotListener;
    private Camera suppressedCam;              // the hub camera we switched off
    private AudioListener suppressedListener;  // and its listener
    private Rigidbody frozenCar;
    private RigidbodyConstraints frozenCarConstraints;

    // ---- built UI ----
    private GameObject root;
    private readonly List<Image> rowBackgrounds = new List<Image>();
    private readonly List<TextMeshProUGUI> rowNames = new List<TextMeshProUGUI>();
    private readonly List<TextMeshProUGUI> rowStatus = new List<TextMeshProUGUI>();
    private TextMeshProUGUI emptyText;
    private TextMeshProUGUI hintText;
    private const int MaxRows = 8;

    void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
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
            suppressedUntilExit = false;
            if (piloting) StopPiloting();
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
        if (piloting) { TickPiloting(); return; }

        if (!isOpen)
        {
            if (PlayerInside && !suppressedUntilExit && !MenuState.AnyOpen) Open();
            return;
        }

        if (!PlayerInside) { Close(); return; }

        RefreshRows();

        var gp = Gamepad.current;
        if (gp == null) return;

        if (gp.buttonEast.wasPressedThisFrame) { suppressedUntilExit = true; Close(); return; }

        if (available.Count > 0)
        {
            if (gp.dpad.up.wasPressedThisFrame || gp.leftStick.up.wasPressedThisFrame) Move(-1);
            if (gp.dpad.down.wasPressedThisFrame || gp.leftStick.down.wasPressedThisFrame) Move(1);
            if (gp.buttonSouth.wasPressedThisFrame) TryTakeControls();
        }
    }

    void Move(int dir)
    {
        if (available.Count == 0) return;
        selected = (selected + dir + available.Count) % available.Count;
        AudioManager.PlayStoreMove();
        RefreshRows();
    }

    // -------------------------------------------------------
    //  Taking / handing back the controls
    // -------------------------------------------------------

    void TryTakeControls()
    {
        if (selected < 0 || selected >= available.Count) return;

        var entry = available[selected];
        if (entry.Value != SupportShipReplicator.NoClient)
        {
            AudioManager.PlayStoreDenied();   // already flown by someone else
            return;
        }

        // Optimism is not allowed here: two hub players can reach for the same ship in the same frame,
        // so the server decides. We ask, and StartPiloting only runs once the answer names us.
        SupportShipReplicator.RequestPilot(entry.Key, claim: true);
        AudioManager.PlayStoreSelect();
        pendingOwner = entry.Key;
        awaitingGrant = true;
        grantDeadline = Time.unscaledTime + GrantTimeout;
    }

    private ulong pendingOwner;
    private bool awaitingGrant;
    private float grantDeadline;
    // A REFUSAL looks identical to a slow network — the server just never names us — so the wait has
    // to end on a clock. Generous enough to cover a bad connection, short enough that the player can
    // try again without wondering whether the button did anything.
    private const float GrantTimeout = 3f;

    void LateUpdate()
    {
        // The grant arrives asynchronously (or instantly, offline). Watching the replicator rather than
        // assuming success is what makes a contested ship resolve to exactly one pilot.
        if (!awaitingGrant || piloting) return;

        if (SupportShipReplicator.LocalPilotOf == pendingOwner)
        {
            awaitingGrant = false;
            StartPiloting(pendingOwner);
            return;
        }

        if (Time.unscaledTime >= grantDeadline)
        {
            awaitingGrant = false;
            AudioManager.PlayStoreDenied();
        }
    }

    void StartPiloting(ulong ownerId)
    {
        var ship = SupportShipReplicator.GetShip(ownerId);
        if (ship == null)
        {
            SupportShipReplicator.RequestPilot(ownerId, claim: false);
            return;
        }

        piloting = true;
        pilotedOwner = ownerId;
        Close();

        // Suppress everything the sticks would otherwise do: turbo, jump, the shield, the grapple, and
        // any other trigger menu opening under the parked car. This is the same flag the store uses.
        MenuState.AnyOpen = true;
        FreezeLocalCar(true);
        EnsureRig();
        BindCamera(ship);
        ShowHint("Left Stick Fly     B Release");
    }

    void TickPiloting()
    {
        var ship = SupportShipReplicator.GetShip(pilotedOwner);

        // The ship was downed, dismissed by its owner, or the controls were taken away from us.
        if (ship == null || ship.IsRagdolling || SupportShipReplicator.LocalPilotOf != pilotedOwner)
        {
            StopPiloting();
            return;
        }

        if (pilotFollow != null) pilotFollow.target = ship.transform;

        var gp = Gamepad.current;
        if (gp == null) return;

        if (gp.buttonEast.wasPressedThisFrame)
        {
            suppressedUntilExit = true;
            StopPiloting();
            return;
        }

        // x = slide right, y = climb. Integrated locally so the pilot's own control has no latency;
        // the resulting offset is what goes on the wire.
        ship.ApplyPilotStick(gp.leftStick.ReadValue(), Time.deltaTime);
    }

    void StopPiloting()
    {
        if (!piloting) { awaitingGrant = false; return; }
        piloting = false;
        awaitingGrant = false;

        SupportShipReplicator.RequestPilot(pilotedOwner, claim: false);

        RestoreCamera();
        FreezeLocalCar(false);
        MenuState.AnyOpen = false;
        AudioManager.PlayStoreClose();
    }

    void OnDisable()
    {
        // The hub scene can unload out from under us (entering the track portal, teardown to menu).
        if (piloting) StopPiloting();
        if (isOpen)
        {
            isOpen = false;
            MenuState.AnyOpen = false;
        }
    }

    // -------------------------------------------------------
    //  The pilot's car
    // -------------------------------------------------------

    /// <summary>Pins the pilot's own car while they're flying. MenuState alone only suppresses the
    /// BUTTONS (turbo / jump / brake) — throttle and steering are read unconditionally, so without
    /// this the car would keep driving off under the same stick that's flying the ship.</summary>
    void FreezeLocalCar(bool freeze)
    {
        if (freeze)
        {
            var car = PlayerRegistry.LocalCar;
            frozenCar = car != null ? car.GetComponent<Rigidbody>() : null;
            if (frozenCar == null) return;

            frozenCarConstraints = frozenCar.constraints;
            frozenCar.linearVelocity = Vector3.zero;
            frozenCar.angularVelocity = Vector3.zero;
            frozenCar.constraints = RigidbodyConstraints.FreezeAll;
            return;
        }

        if (frozenCar != null) frozenCar.constraints = frozenCarConstraints;
        frozenCar = null;
    }

    // -------------------------------------------------------
    //  The pilot camera
    // -------------------------------------------------------

    void EnsureRig()
    {
        if (pilotCam != null) return;

        var camGo = new GameObject("SupportShipPilotCam");
        DontDestroyOnLoad(camGo);
        pilotCam = camGo.AddComponent<Camera>();
        pilotCam.clearFlags = CameraClearFlags.Skybox;
        pilotCam.fieldOfView = fieldOfView;
        pilotCam.nearClipPlane = nearClip;
        pilotCam.farClipPlane = farClip;
        pilotCam.enabled = false;

        pilotFollow = camGo.AddComponent<CameraFollow>();
        pilotFollow.enableSwivel = false;              // the stick flies the ship, it doesn't orbit the camera
        pilotFollow.offset = cameraOffset;
        pilotFollow.positionSmoothTime = positionSmoothTime;
        pilotFollow.pitchSmoothTime = pitchSmoothTime;
        pilotFollow.rollSmoothTime = rollSmoothTime;
        pilotFollow.rotationSmoothTime = rotationSmoothTime;
        pilotFollow.lookAheadDistance = lookAheadDistance;
        // The ship has a Rigidbody, so CameraFollow's speed-scaled FOV would breathe as it slides
        // around. Pin both ends to hold a constant framing.
        pilotFollow.baseFOV = pilotFollow.maxFOV = fieldOfView;
        if (matchPlayerCamera) CopyPlayerCameraSettings();

        // Added last: CameraFollow.Start hangs the speed-barrier low-pass off whatever listener it
        // finds, and we want that to be inert here.
        pilotListener = camGo.AddComponent<AudioListener>();
        pilotListener.enabled = false;
    }

    void CopyPlayerCameraSettings()
    {
        var main = Camera.main;
        var src = main != null ? main.GetComponent<CameraFollow>() : null;
        if (src == null) return;
        pilotFollow.positionSmoothTime = src.positionSmoothTime;
        pilotFollow.pitchSmoothTime = src.pitchSmoothTime;
        pilotFollow.rollSmoothTime = src.rollSmoothTime;
        pilotFollow.rotationSmoothTime = src.rotationSmoothTime;
        pilotFollow.rollBlendStart = src.rollBlendStart;
        pilotFollow.rollBlendFull = src.rollBlendFull;
        // Offset, look-ahead and FOV stay ours — the ship wants a wider, further-back framing than a car.
    }

    /// <summary>Cuts to the ship: our camera on, the hub camera (and its listener) off. Exactly one
    /// AudioListener may be enabled at a time, or Unity spams warnings and the mix goes undefined.</summary>
    void BindCamera(SupportShip ship)
    {
        var main = Camera.main;
        if (main != null && main != pilotCam)
        {
            suppressedCam = main;
            suppressedCam.enabled = false;

            suppressedListener = main.GetComponent<AudioListener>();
            if (suppressedListener != null && suppressedListener.enabled) suppressedListener.enabled = false;
            else suppressedListener = null;
        }

        pilotFollow.target = ship.transform;
        pilotCam.enabled = true;
        if (pilotListener != null) pilotListener.enabled = true;
    }

    void RestoreCamera()
    {
        if (pilotCam != null) pilotCam.enabled = false;
        if (pilotFollow != null) pilotFollow.target = null;
        if (pilotListener != null) pilotListener.enabled = false;

        // Guarded: the hub camera may have been destroyed under us by a scene load.
        if (suppressedCam != null) suppressedCam.enabled = true;
        if (suppressedListener != null) suppressedListener.enabled = true;
        suppressedCam = null;
        suppressedListener = null;
    }

    // -------------------------------------------------------
    //  Open / close + rows
    // -------------------------------------------------------

    void Open()
    {
        EnsureUI();
        selected = 0;
        RefreshRows();
        ShowHint("↑↓ Select    A Take Controls    B Close");
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;
        AudioManager.PlayStoreOpen();
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        // Abandon any request still in flight, so a grant that lands after the player has walked away
        // doesn't yank them into a cockpit they've stopped asking for.
        if (!piloting) awaitingGrant = false;
        if (!isOpen) return;
        isOpen = false;
        // Piloting keeps the flag raised on purpose — the sticks are still spoken for.
        if (!piloting) MenuState.AnyOpen = false;
        AudioManager.PlayStoreClose();
    }

    /// <summary>Rebuilds the list of flyable ships. Recomputed every frame the menu is up because it's
    /// live data: a teammate can summon or lose a ship, or someone else can grab the controls, while
    /// the player is sitting here reading it.</summary>
    void RefreshRows()
    {
        SupportShipReplicator.ListActiveShips(available);

        if (teamOnly)
        {
            int myTeam = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalTeam() : 0;
            if (myTeam != 0)
            {
                for (int i = available.Count - 1; i >= 0; i--)
                {
                    var remote = PlayerRegistry.FindRemote(available[i].Key);
                    // A null remote is US (our own ship is always listed) — never filter that out.
                    if (remote != null && remote.Team != myTeam) available.RemoveAt(i);
                }
            }
        }

        if (available.Count > MaxRows) available.RemoveRange(MaxRows, available.Count - MaxRows);
        selected = available.Count == 0 ? 0 : Mathf.Clamp(selected, 0, available.Count - 1);

        if (emptyText != null)
        {
            emptyText.gameObject.SetActive(available.Count == 0);
            emptyText.text = "No teammates are flying a Support Ship.";
        }

        for (int i = 0; i < rowNames.Count; i++)
        {
            bool used = i < available.Count;
            rowBackgrounds[i].gameObject.SetActive(used);
            if (!used) continue;

            var entry = available[i];
            bool busy = entry.Value != SupportShipReplicator.NoClient
                     && entry.Value != LocalClientIdOrZero;

            rowNames[i].text = DisplayName(entry.Key);
            rowStatus[i].text = busy ? "IN USE" : "READY";

            bool sel = (i == selected);
            rowBackgrounds[i].color = busy ? rowBusy : (sel ? rowSelected : rowNormal);
            rowNames[i].color = (sel && !busy) ? textSelected : textNormal;
            rowStatus[i].color = (sel && !busy) ? textSelected : textNormal;
        }
    }

    static ulong LocalClientIdOrZero =>
        Unity.Netcode.NetworkManager.Singleton != null
            ? Unity.Netcode.NetworkManager.Singleton.LocalClientId : 0;

    /// <summary>Who a ship belongs to, by player name where the roster knows it.</summary>
    static string DisplayName(ulong ownerId)
    {
        if (ownerId == LocalClientIdOrZero) return "Your Ship";
        var remote = PlayerRegistry.FindRemote(ownerId);
        return remote != null && !string.IsNullOrEmpty(remote.Name) ? remote.Name : $"Player {ownerId}";
    }

    void ShowHint(string text)
    {
        if (hintText != null) hintText.text = text;
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void EnsureUI()
    {
        if (root != null) return;

        const float panelWidth = 760f;
        const float rowHeight = 64f;
        const float headerHeight = 120f;
        const float footerHeight = 110f;
        float panelHeight = headerHeight + MaxRows * rowHeight + footerHeight;

        root = new GameObject("PilotControlCanvas");
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
        prt.sizeDelta = new Vector2(panelWidth, panelHeight);
        prt.anchoredPosition = Vector2.zero;

        var title = NewText(panel.transform, "Title", 52, TextAlignmentOptions.Top);
        title.text = "PILOT CONTROL";
        title.color = Color.white;
        StretchTop(title.rectTransform, panelWidth - 60f, 66f, 20f);

        var subtitle = NewText(panel.transform, "Subtitle", 26, TextAlignmentOptions.Top);
        subtitle.text = "Take the controls of a teammate's Support Ship";
        subtitle.color = new Color(0.7f, 0.75f, 0.85f);
        StretchTop(subtitle.rectTransform, panelWidth - 60f, 34f, 82f);

        rowBackgrounds.Clear();
        rowNames.Clear();
        rowStatus.Clear();
        for (int i = 0; i < MaxRows; i++)
        {
            var rowGO = NewUI($"Row{i}", panel.transform);
            var bg = rowGO.AddComponent<Image>();
            bg.color = rowNormal;
            StretchTop(rowGO.GetComponent<RectTransform>(),
                       panelWidth - 40f, rowHeight - 8f, headerHeight + i * rowHeight);

            var nameTxt = NewText(rowGO.transform, "Name", 34, TextAlignmentOptions.MidlineLeft);
            FillWithPadding(nameTxt.rectTransform, 22f, 22f);

            var statusTxt = NewText(rowGO.transform, "Status", 28, TextAlignmentOptions.MidlineRight);
            FillWithPadding(statusTxt.rectTransform, 22f, 22f);

            rowBackgrounds.Add(bg);
            rowNames.Add(nameTxt);
            rowStatus.Add(statusTxt);
            rowGO.SetActive(false);
        }

        // Shown in place of the rows when nobody on the team has a ship out.
        emptyText = NewText(panel.transform, "Empty", 28, TextAlignmentOptions.Center);
        emptyText.color = new Color(0.85f, 0.86f, 0.92f);
        emptyText.enableWordWrapping = true;
        StretchTop(emptyText.rectTransform, panelWidth - 80f, 80f, headerHeight + 20f);

        hintText = NewText(panel.transform, "Hint", 26, TextAlignmentOptions.Bottom);
        hintText.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hintText.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(panelWidth - 60f, 40f);
        hrt.anchoredPosition = new Vector2(0f, 22f);

        UiLayer.Apply(root);   // keep it off the grappling hook's radar, like every other HUD
        root.SetActive(false);
    }

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static TextMeshProUGUI NewText(Transform parent, string name, int fontSize, TextAlignmentOptions align)
    {
        var go = NewUI(name, parent);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        return t;
    }

    static void StretchTop(RectTransform rt, float width, float height, float topOffset)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = new Vector2(0f, -topOffset);
    }

    static void FillWithPadding(RectTransform rt, float left, float right)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(left, 0f);
        rt.offsetMax = new Vector2(-right, 0f);
    }
}
