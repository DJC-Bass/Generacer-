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

    [Header("Requirement")]
    [Tooltip("Inventory item the player must OWN to fire at all — the tool, not ammo, so it is never " +
             "consumed. Without it RB does nothing. Blank = no requirement.")]
    public string requiredItem = "Grappling Gun";
    [Tooltip("Item SPENT on every shot (the ammo). One is consumed the moment the hook launches, hit " +
             "or miss. Blank = shots are free.")]
    public string ammoItem = "Grappling Hook";

    [Header("Firing")]
    [Tooltip("Maximum rope length (metres). The hook is recalled if it flies further than this.")]
    public float maxRange = 1500f;
    [Tooltip("Hook travel speed (m/s), on top of the car's own velocity so it still outruns the car.")]
    public float fireSpeed = 1500f;
    [Tooltip("Seconds the hook may fly without hitting anything before it's recalled.")]
    public float flightTimeout = 2f;
    [Tooltip("Thickness of the SECONDARY sweep, used only to catch edges the thin primary ray misses. " +
             "This is a sweep radius, NOT a catch range — keep it small (≈0.5–2). A radius large enough " +
             "to engulf the road around the car makes every sphere hit a useless start-overlap; " +
             "detection then rests entirely on the ray. 0 disables the secondary test.")]
    public float hookRadius = 1.5f;

    [Header("Muzzle (front of the car)")]
    public float muzzleForward = 3f;
    public float muzzleUp = 0.5f;

    [Header("Shield")]
    [Tooltip("Layer of a summoned player Shield. A hook that reaches one FAILS outright — the shot is " +
             "swatted away and recalled, rather than passing through or latching on. Your own shield " +
             "is never a candidate (it's part of your car, which the hook always skips).")]
    public string shieldLayerName = "Shield";

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
    public float reelForce = 50f;
    [Tooltip("Metres per second the rope shortens while reeling the car in.")]
    public float reelSpeed = 50f;
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

    /// <summary>WHAT the rope is anchored to. The replicator sends this rather than a world position:
    /// a Static point is identical on every machine, and a PlayerCar is already replicated + smoothed
    /// by RemoteCarPuppet, so each viewer can glue the rope to THEIR copy of that car instead of
    /// chasing a stale point sampled on someone else's machine.</summary>
    public enum AnchorKind : byte
    {
        None = 0,
        Static = 1,      // fixed world point (track geometry) — correct everywhere, never moves
        PlayerCar = 2,   // another player's car: send their client id + a local offset
        Dynamic = 3,     // some other rigidbody (drone, boulder) — still needs a streamed position
    }
    public AnchorKind CurrentAnchorKind { get; private set; } = AnchorKind.None;

    /// <summary>Owning client of a <see cref="AnchorKind.PlayerCar"/> anchor.</summary>
    public ulong AnchorClientId { get; private set; }
    /// <summary>Attach point in the anchor's LOCAL space (PlayerCar / Dynamic).</summary>
    public Vector3 AnchorLocalOffset => anchorLocal;
    /// <summary>Attach point in world space (Static), or the live position for Dynamic.</summary>
    public Vector3 AnchorWorldPoint => anchorRb != null ? CurrentAnchor() : anchorWorld;

    // ---- Flight, published so viewers can SIMULATE it instead of being fed 15 positions a second ----
    /// <summary>Where the hook was launched from.</summary>
    public Vector3 FlightOrigin { get; private set; }
    /// <summary>Constant velocity of the hook in flight — it travels in a straight line.</summary>
    public Vector3 FlightVelocity => hookVelocity;
    /// <summary>Seconds the hook has been flying, so a viewer can start mid-flight.</summary>
    public float FlightElapsed => flightTimer;

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
                // RELEASING is never gated — losing the gun (or the last hook) mid-swing must not
                // strand the player on a tether they can't cut. Only FIRING has requirements.
                if (CurrentState != State.Idle) Release();
                else TryFire();
            }

            // L3 — BREAK FREE of anyone grappling us. Polled here rather than called from
            // ShieldAbility so the two stay independent: the same press summons a shield (if one is
            // held) AND shrugs off the tether, and breaking free still works with an empty inventory.
            // Y HELD makes this the Support Ship chord instead (see SupportShipAbility), so it must not
            // also fire the break-free — same exclusion ShieldAbility applies to the shield.
            if (gp != null && gp.leftStickButton.wasPressedThisFrame && !gp.buttonNorth.isPressed
                && MultiplayerWorld.IsMultiplayerGame)
                GrappleReplicator.SendBreakFree();
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

    /// <summary>Where the rope is DRAWN from — the nose of the car.</summary>
    Vector3 MuzzlePosition() =>
        carGO.transform.position + carGO.transform.forward * muzzleForward + carGO.transform.up * muzzleUp;

    /// <summary>Where the physics sweep STARTS — the car's centre: muzzle height, but WITHOUT the
    /// forward offset. That forward projection is what put the origin inside rising track mesh when
    /// driving uphill, and a sweep beginning inside a collider reports point (0,0,0) — which anchored
    /// the hook to the world origin. Starting at the centre keeps the origin in open air inside the car
    /// body, whose own colliders are skipped anyway (degenerate distance-0 hits, plus the IsOwnCar
    /// test). The rope still visually leaves the nose; the hook clears the car within one step.</summary>
    Vector3 SweepOrigin() =>
        carGO.transform.position + carGO.transform.up * muzzleUp;

    /// <summary>True when the player owns the Grappling Gun. It's a TOOL: checked on every shot but
    /// never consumed, so one purchase enables the hook for good.</summary>
    bool HasGrappleGun()
    {
        if (string.IsNullOrEmpty(requiredItem)) return true;   // blank = ungated
        var inv = PlayerInventory.Instance;
        return inv != null && inv.GetCount(requiredItem) > 0;
    }

    /// <summary>Fires only if the player owns the gun AND has a hook to spend. The hook is consumed at
    /// LAUNCH, hit or miss — a wasted shot costs one, which is what makes range and aim matter.</summary>
    void TryFire()
    {
        if (!HasGrappleGun())
        {
            Debug.Log($"[Grapple] No '{requiredItem}' owned — RB does nothing.");
            return;
        }

        if (!string.IsNullOrEmpty(ammoItem))
        {
            var inv = PlayerInventory.Instance;
            if (inv == null || !inv.Consume(ammoItem, 1))
            {
                Debug.Log($"[Grapple] Out of '{ammoItem}' — craft more at the Upgrade Ramp.");
                return;
            }
        }

        Fire();
    }

    void Fire()
    {
        CurrentState = State.Firing;
        CurrentAnchorKind = AnchorKind.None;
        flightTimer = 0f;
        HookPosition = SweepOrigin();
        FlightOrigin = HookPosition;
        // Inherit the car's velocity so the hook isn't left behind when fired at speed.
        hookVelocity = carGO.transform.forward * fireSpeed + carRb.linearVelocity;
        if (rope != null) rope.ResetShape();
        AudioManager.PlayGrappleFire(MuzzlePosition());
        RemoteCarManager.ReportCarSound(RemoteCarManager.CarSound.GrappleFire, MuzzlePosition());
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
            Vector3 dir = step / stepLen;

            // PRIMARY TEST — a thin RAY. It gives exact contact points and, crucially, does not begin
            // its sweep already overlapping the road: a fat sphere centred near the car engulfs the
            // track surface underneath it, so the track came back as a distance-0 start-overlap on
            // EVERY step and was skipped — the hook flew straight through the world. A ray from inside
            // the car body is in open air and reports the road properly.
            if (TryPickHit(Physics.RaycastAll(from, dir, stepLen, ~blockedMask,
                                              QueryTriggerInteraction.Ignore), out RaycastHit rayHit))
            {
                ResolveHit(rayHit);
                return;
            }

            // SECONDARY TEST — the sphere, purely to widen the catch onto edges the thin ray slipped
            // past. Start-overlaps are still discarded here (their point is Vector3.zero, which once
            // anchored the hook to the world origin 100 km away), but that no longer costs us the
            // track, because the ray above owns the real detection.
            if (hookRadius > 0f &&
                TryPickHit(Physics.SphereCastAll(from, hookRadius, dir, stepLen, ~blockedMask,
                                                 QueryTriggerInteraction.Ignore), out RaycastHit sphereHit))
            {
                ResolveHit(sphereHit);
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

    /// <summary>Nearest usable hit from a sweep result. Rejects: DEGENERATE hits (`distance == 0` means
    /// the sweep began inside that collider — Unity has no contact point to report and hands back
    /// `Vector3.zero`, i.e. the world origin), our own car, and UI canvases.</summary>
    bool TryPickHit(RaycastHit[] hits, out RaycastHit best)
    {
        best = default;
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (hit.distance <= 0f) continue;
            if (IsOwnCar(hit.collider.transform)) continue;
            if (IsUserInterface(hit.collider.transform)) continue;
            best = hit;
            return true;
        }
        return false;
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

    /// <summary>Decides what a landed hit means. A player's SHIELD defeats the hook outright — the
    /// attempt is recalled rather than latching on, so raising a shield is a real counter to being
    /// grappled (and pairs with the shield already blocking drone fire).</summary>
    void ResolveHit(RaycastHit hit)
    {
        if (IsShield(hit.collider))
        {
            Debug.Log("[Grapple] Hook deflected by a player's shield.");
            Release();
            return;
        }
        Attach(hit);
    }

    bool IsShield(Collider hit)
    {
        if (hit == null || string.IsNullOrEmpty(shieldLayerName)) return false;
        int layer = LayerMask.NameToLayer(shieldLayerName);
        return layer >= 0 && hit.gameObject.layer == layer;
    }

    void Attach(RaycastHit hit)
    {
        HookPosition = hit.point;

        // Latch to the body if it has one, so the anchor tracks a moving target (another car, a
        // boulder); otherwise store a fixed world point on the static geometry.
        anchorRb = hit.collider.attachedRigidbody;
        anchorWasBody = anchorRb != null;
        if (anchorWasBody) anchorLocal = anchorRb.transform.InverseTransformPoint(hit.point);
        else anchorWorld = hit.point;

        // Classify the anchor for replication. A PlayerCar is the important case: it's already
        // replicated and smoothed on every machine, so viewers should derive the rope end from THEIR
        // copy of that car rather than be fed a position sampled on ours.
        if (!anchorWasBody) CurrentAnchorKind = AnchorKind.Static;
        else if (AnchorIsRemotePlayer(out ulong ownerId))
        {
            CurrentAnchorKind = AnchorKind.PlayerCar;
            AnchorClientId = ownerId;
        }
        else CurrentAnchorKind = AnchorKind.Dynamic;   // drone, boulder — still needs streaming

        ropeLength = Mathf.Max(Vector3.Distance(MuzzlePosition(), hit.point), minRopeLength);
        CurrentState = State.Attached;
        AudioManager.PlayGrappleAttach(hit.point);   // out where it landed, not at the car
        RemoteCarManager.ReportCarSound(RemoteCarManager.CarSound.GrappleAttach, hit.point);
        Debug.Log($"[Grapple] Attached to '{hit.collider.name}' at {ropeLength:0.#} m.");
    }

    public void Release()
    {
        if (CurrentState == State.Idle) return;
        CurrentState = State.Idle;
        CurrentAnchorKind = AnchorKind.None;
        anchorRb = null;
        anchorWasBody = false;
        if (carGO != null)
        {
            AudioManager.PlayGrappleRelease(carGO.transform.position);
            RemoteCarManager.ReportCarSound(RemoteCarManager.CarSound.GrappleRelease, carGO.transform.position);
        }
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
