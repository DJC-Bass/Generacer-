using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// GRAPPLING HOOK. <b>RB</b> fires a hook out of the front of the car; it flies up to
/// <see cref="maxRange"/> (200 m) and latches onto the first thing it touches. <b>RB again</b> releases.
/// While tethered the car swings on normal physics, and <b>RT + Y</b> reels — pulling the CAR toward the
/// anchor, or pulling the ANCHOR toward the car when the hooked object is lighter than the car.
///
/// Physics model: ONE inextensible distance constraint on the car's rigidbody, not a chain of joints.
/// The constraint only removes velocity moving AWAY from the anchor once the rope is taut, which leaves
/// the tangential component untouched — that IS the pendulum swing, for free and stable at 600 mph.
/// The many-jointed look is <see cref="GrappleRope"/>, a purely visual verlet rope.
///
/// Facing: while AIRBORNE the car is torqued to point its nose at the anchor. Torque rather than a hard
/// rotation, so it naturally does its best and gives up when physics won't allow it — exactly the case
/// of grappling a car that's over a ledge while you're still on the track. Grounded steering is never
/// overridden, so a mid-race grapple doesn't make the car undrivable.
///
/// Persistent + bootstrapped on the PlayerSystems object (like ShieldAbility / SDAbilityController), so
/// it needs no scene setup and re-finds the car after a scene load or car swap.
/// </summary>
[DefaultExecutionOrder(1000)]
public class GrappleHook : MonoBehaviour
{
    public static GrappleHook Instance { get; private set; }

    [Header("Firing")]
    [Tooltip("Maximum rope length (metres). The hook is recalled if it flies further than this.")]
    public float maxRange = 1000f;
    [Tooltip("Hook travel speed (m/s), on top of the car's own velocity so it still outruns the car.")]
    public float fireSpeed = 640f;
    [Tooltip("Seconds the hook may fly without hitting anything before it's recalled.")]
    public float flightTimeout = 2f;
    [Tooltip("Radius of the hook's sweep test. A little thickness makes it far easier to catch edges.")]
    public float hookRadius = 10f;

    [Header("Muzzle (front of the car)")]
    public float muzzleForward = 2.5f;
    public float muzzleUp = 0.5f;

    [Header("Blocked Layers")]
    [Tooltip("Layers the hook passes straight through instead of latching onto. Defaults to Portal, " +
             "Projectile and Lightning. The car's own colliders are always ignored regardless.")]
    public string[] blockedLayerNames = { "Portal", "Projectile", "Lightning", "UI" };

    [Header("Tether")]
    [Tooltip("How hard the rope cancels outward motion once taut. 1 = perfectly inextensible.")]
    [Range(0f, 1f)] public float ropeStiffness = 1f;
    [Tooltip("Pull-back acceleration applied per metre the rope is overstretched.")]
    public float ropeSpring = 30f;
    [Tooltip("Shortest the rope can be reeled to (metres) — stops the car burying itself in the anchor.")]
    public float minRopeLength = 1f;

    [Header("Reeling (RT + Y)")]
    [Tooltip("Acceleration applied while reeling.")]
    public float reelForce = 45f;
    [Tooltip("Metres per second the rope shortens while reeling the car in.")]
    public float reelSpeed = 18f;
    [Tooltip("A trigger past this value (0-1) counts as held.")]
    public float triggerThreshold = 0.5f;

    [Header("Facing (airborne only)")]
    [Tooltip("Torque strength turning the nose toward the anchor while airborne. 0 disables facing.")]
    public float faceTorque = 6f;
    [Tooltip("Angular damping applied while facing, so the car settles instead of spinning past.")]
    public float faceDamping = 2f;

    /// <summary>What the hook is doing right now. Read by the replicator to stream rope state.</summary>
    public enum State { Idle, Firing, Attached }
    public State CurrentState { get; private set; } = State.Idle;

    /// <summary>Live hook-head position — the far end of the rope. Meaningless while Idle.</summary>
    public Vector3 HookPosition { get; private set; }

    private GameObject carGO;
    private Rigidbody carRb;
    private CarController carController;

    private int blockedMask;
    private float flightTimer;
    private Vector3 hookVelocity;

    // Anchor: a rigidbody + local offset when we caught something that moves, otherwise a world point.
    private Rigidbody anchorRb;
    private Vector3 anchorLocal;
    private Vector3 anchorWorld;
    private bool anchorWasBody;    // we latched onto a rigidbody — if it dies, so does the tether
    private float ropeLength;

    private GrappleRope rope;
    private GameObject ropeGO;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        RebuildBlockedMask();
    }

    void RebuildBlockedMask()
    {
        blockedMask = 0;
        if (blockedLayerNames == null) return;
        foreach (var n in blockedLayerNames)
        {
            if (string.IsNullOrEmpty(n)) continue;
            int layer = LayerMask.NameToLayer(n);
            if (layer >= 0) blockedMask |= 1 << layer;
            else Debug.LogWarning($"[Grapple] Layer '{n}' not found in Tags and Layers — not blocked.");
        }
    }

    void Update()
    {
        EnsureCar();
        if (carGO == null) { if (CurrentState != State.Idle) Release(); return; }

        if (!MenuState.AnyOpen)
        {
            var gp = Gamepad.current;
            if (gp != null && gp.rightShoulder.wasPressedThisFrame)
            {
                if (CurrentState == State.Idle) Fire();
                else Release();                      // RB again releases, whether flying or attached
            }
        }

        UpdateRopeVisual();
    }

    void FixedUpdate()
    {
        if (carGO == null) return;
        if (CurrentState == State.Firing) TickFlight();
        else if (CurrentState == State.Attached) TickTether();
    }

    // -------------------------------------------------------
    //  Fire / flight
    // -------------------------------------------------------

    Vector3 MuzzlePosition() =>
        carGO.transform.position + carGO.transform.forward * muzzleForward + carGO.transform.up * muzzleUp;

    void Fire()
    {
        CurrentState = State.Firing;
        flightTimer = 0f;
        HookPosition = MuzzlePosition();
        // Inherit the car's velocity so the hook isn't left behind when fired at speed.
        hookVelocity = carGO.transform.forward * fireSpeed + carRb.linearVelocity;
        if (rope != null) rope.ResetShape();
        AudioManager.PlaySdActivate(HookPosition);
    }

    /// <summary>Advances the hook and sweeps for a catch. A SPHERE cast along the travel segment (not a
    /// point test at the new position) is what stops the hook tunnelling clean through thin geometry —
    /// it covers hundreds of metres per second.</summary>
    void TickFlight()
    {
        float dt = Time.fixedDeltaTime;
        flightTimer += dt;

        Vector3 from = HookPosition;
        Vector3 step = hookVelocity * dt;
        float stepLen = step.magnitude;

        if (stepLen > 1e-4f)
        {
            // Everything except the blocked layers; self-hits are filtered below (the car and another
            // player share the Player layer, so this can't be done with a mask).
            var hits = Physics.SphereCastAll(from, hookRadius, step / stepLen, stepLen,
                                             ~blockedMask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                // DEGENERATE HIT — the sweep began already OVERLAPPING this collider. Unity reports
                // those with distance 0 and, critically, **point = Vector3.zero**: there is no real
                // contact point to give. Honouring one anchored the hook to the WORLD ORIGIN, which
                // (with the track sitting at TrackAreaOffset, 100 km out) read as "it grabbed something
                // impossibly far away". It happened going UPHILL, where the muzzle — pushed out in
                // front of the car — buries itself in the rising track mesh. Skip and keep flying; the
                // hook leaves the geometry within a step or two.
                if (hit.distance <= 0f) continue;

                if (IsOwnCar(hit.collider.transform)) continue;   // never hook ourselves
                if (IsUserInterface(hit.collider.transform)) continue;
                Attach(hit);
                return;
            }
        }

        HookPosition = from + step;

        // Recall on range or timeout. Range is measured from the muzzle, so it's true rope length.
        if (flightTimer >= flightTimeout ||
            Vector3.Distance(MuzzlePosition(), HookPosition) >= maxRange)
        {
            Release();
        }
    }

    bool IsOwnCar(Transform t)
    {
        while (t != null)
        {
            if (t.gameObject == carGO) return true;
            t = t.parent;
        }
        return false;
    }

    /// <summary>True for anything belonging to a UI Canvas. The blocked-layer mask already excludes the
    /// UI layer, but this is the guarantee that doesn't depend on someone remembering to set it: the
    /// HUDs are built in code and land on the Default layer unless <see cref="UiLayer.Apply"/> ran, and
    /// UI added in future would silently become grappleable again. A Canvas parent is never a legitimate
    /// grapple target, so this covers all of it — current and future — for one lookup per candidate hit.</summary>
    static bool IsUserInterface(Transform t) => t != null && t.GetComponentInParent<Canvas>() != null;

    void Attach(RaycastHit hit)
    {
        HookPosition = hit.point;

        // Latch to the body if it has one, so the anchor tracks a moving target (another car, a
        // boulder); otherwise store a fixed world point on the static geometry.
        anchorRb = hit.collider.attachedRigidbody;
        anchorWasBody = anchorRb != null;
        if (anchorWasBody) anchorLocal = anchorRb.transform.InverseTransformPoint(hit.point);
        else anchorWorld = hit.point;

        ropeLength = Mathf.Max(Vector3.Distance(MuzzlePosition(), hit.point), minRopeLength);
        CurrentState = State.Attached;
        Debug.Log($"[Grapple] Attached to '{hit.collider.name}' at {ropeLength:0.#} m.");
    }

    public void Release()
    {
        if (CurrentState == State.Idle) return;
        CurrentState = State.Idle;
        anchorRb = null;
        anchorWasBody = false;
        if (carGO != null) AudioManager.PlaySdDeactivate(carGO.transform.position);
    }

    // -------------------------------------------------------
    //  Tether physics
    // -------------------------------------------------------

    Vector3 CurrentAnchor()
    {
        if (anchorRb != null) return anchorRb.transform.TransformPoint(anchorLocal);
        return anchorWorld;
    }

    /// <summary>True when the anchor is ANOTHER player's car, outing their client id. Their puppet here
    /// is a kinematic copy, so forces must be messaged to the machine that actually owns that car —
    /// pushing the puppet would just be overwritten by their next state update.</summary>
    bool AnchorIsRemotePlayer(out ulong ownerId)
    {
        ownerId = 0;
        if (anchorRb == null || !MultiplayerWorld.IsMultiplayerGame) return false;
        return MultiplayerWorld.TryGetCarOwner(anchorRb.transform, out ownerId, out bool isLocal) && !isLocal;
    }

    /// <summary>Applies an acceleration to the hooked body, whichever machine owns it. A remote player's
    /// car is reached by message; anything else (boulder, drone, scenery body) is pushed directly.</summary>
    void PushAnchor(Vector3 acceleration)
    {
        if (AnchorIsRemotePlayer(out ulong ownerId))
        {
            GrappleReplicator.SendPullToOwner(ownerId, acceleration);
            return;
        }
        if (anchorRb != null && !anchorRb.isKinematic)
            anchorRb.AddForce(acceleration, ForceMode.Acceleration);
    }

    void TickTether()
    {
        // The thing we hooked can be destroyed mid-swing (a drone despawning, a player disconnecting).
        // Explicit flag rather than testing anchorWorld against Vector3.zero — origin is a legitimate
        // place to grapple, and that sentinel would silently drop the tether there.
        if (anchorWasBody && anchorRb == null) { Release(); return; }

        Vector3 anchor = CurrentAnchor();
        HookPosition = anchor;

        Vector3 muzzle = MuzzlePosition();
        Vector3 toAnchor = anchor - muzzle;
        float dist = toAnchor.magnitude;
        if (dist < 1e-4f) return;
        Vector3 dir = toAnchor / dist;

        HandleReel(dir, dist);

        // --- The rope constraint. Only acts when taut; slack rope does nothing at all. ---
        float excess = dist - ropeLength;
        if (excess > 0f)
        {
            Vector3 anchorVel = anchorRb != null ? anchorRb.linearVelocity : Vector3.zero;
            float radial = Vector3.Dot(carRb.linearVelocity - anchorVel, dir);

            // radial < 0 means we're moving AWAY from the anchor. Removing exactly that component
            // leaves the tangential motion untouched — which is the swing.
            if (radial < 0f)
                carRb.linearVelocity -= dir * (radial * ropeStiffness);

            // Soft pull-back for any overstretch that slipped through in one step.
            carRb.AddForce(dir * (excess * ropeSpring), ForceMode.Acceleration);

            // Equal-and-opposite on the anchor, scaled by mass ratio so a heavy anchor barely notices
            // and a light one gets yanked around.
            if (anchorRb != null)
            {
                float ratio = Mathf.Clamp01(carRb.mass / Mathf.Max(anchorRb.mass, 0.01f));
                PushAnchor(-dir * (excess * ropeSpring * ratio));
            }
        }

        FaceAnchor(dir);
    }

    /// <summary>RT + Y. Which end moves is decided by MASS: a hooked object lighter than the car gets
    /// dragged in, anything heavier (or static, like the track) pulls the car instead.</summary>
    void HandleReel(Vector3 dir, float dist)
    {
        if (MenuState.AnyOpen) return;
        var gp = Gamepad.current;
        if (gp == null) return;
        if (gp.rightTrigger.ReadValue() <= triggerThreshold || !gp.buttonNorth.isPressed) return;

        // Mass decides which end moves. A remote player's puppet is kinematic but still carries the
        // real car's MASS from the prefab, so the comparison is valid there too — it just has to be
        // excluded from the isKinematic test, which would otherwise disqualify every remote car.
        bool anchorIsRemoteCar = AnchorIsRemotePlayer(out _);
        bool objectIsLighter = anchorRb != null
                            && anchorRb.mass < carRb.mass
                            && (anchorIsRemoteCar || !anchorRb.isKinematic);

        if (objectIsLighter)
        {
            // Drag the object to us; the rope shortens to match so it can't drift back out.
            PushAnchor(-dir * reelForce);
            ropeLength = Mathf.Max(minRopeLength, Mathf.Min(ropeLength, dist));
        }
        else
        {
            carRb.AddForce(dir * reelForce, ForceMode.Acceleration);
            ropeLength = Mathf.Max(minRopeLength, ropeLength - reelSpeed * Time.fixedDeltaTime);
        }
    }

    /// <summary>Points the nose at the anchor while AIRBORNE. Applied as torque, so it does its best and
    /// simply falls short when the geometry makes facing impossible — and grounded driving is untouched.</summary>
    void FaceAnchor(Vector3 dir)
    {
        if (faceTorque <= 0f) return;
        if (carController != null && !carController.IsAirborne) return;   // grounded steering wins

        Quaternion delta = Quaternion.FromToRotation(carGO.transform.forward, dir);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f) return;

        carRb.AddTorque(axis.normalized * (angle * Mathf.Deg2Rad * faceTorque), ForceMode.Acceleration);
        carRb.AddTorque(-carRb.angularVelocity * faceDamping, ForceMode.Acceleration);
    }

    // -------------------------------------------------------
    //  Car + rope plumbing
    // -------------------------------------------------------

    void EnsureCar()
    {
        if (carGO != null && carRb != null) return;

        carGO = PlayerRegistry.LocalCar;
        carRb = carGO != null ? carGO.GetComponent<Rigidbody>() : null;
        carController = carGO != null ? carGO.GetComponent<CarController>() : null;
        if (carGO == null) CurrentState = State.Idle;   // car gone (scene load) — drop any tether
    }

    void UpdateRopeVisual()
    {
        if (CurrentState == State.Idle)
        {
            if (ropeGO != null) ropeGO.SetActive(false);
            return;
        }

        if (ropeGO == null)
        {
            ropeGO = new GameObject("GrappleRope");
            DontDestroyOnLoad(ropeGO);
            ropeGO.AddComponent<LineRenderer>();
            rope = ropeGO.AddComponent<GrappleRope>();
        }

        if (!ropeGO.activeSelf) { ropeGO.SetActive(true); rope.ResetShape(); }
        rope.SetEnds(MuzzlePosition(), HookPosition);
    }

    void OnDestroy()
    {
        if (ropeGO != null) Destroy(ropeGO);
    }
}
