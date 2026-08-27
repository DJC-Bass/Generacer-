using UnityEngine;

/// <summary>
/// Phase 3's hard requirement: the EXTRAPOLATING transform sync for remote cars. At 600 mph
/// (~268 m/s) a 100 ms network gap is ~27 m — snapshot interpolation (stock NetworkTransform)
/// renders remote cars a full car-length-stack behind and rubber-bands them on every packet.
/// This instead DEAD-RECKONS: each received state carries position, rotation, linear AND angular
/// velocity, and every frame the puppet projects that state forward by its age (velocity × time,
/// angular velocity integrated into the rotation) and eases its visible pose toward the projection
/// with an exponential blend — correction error is absorbed smoothly over ~a tenth of a second
/// instead of ever lerping between two stale snapshots.
///
/// Details that matter:
///  • The projection targets age + tau (the blend's own time constant): a pure exponential chase of
///    a moving target sits tau·v behind it (~32 m at 600 mph!) — leading the target by tau cancels
///    that steady-state lag to first order.
///  • Extrapolation age is CAPPED: past ~half a second of silence the car holds (a stalled sender
///    shouldn't sail a ghost car kilometres off the track).
///  • A snap threshold (100 m) handles discontinuities — portal teleports move a car 35 km between
///    two updates; blending across that would be a hyperspeed streak across the map.
///  • Sequence numbers drop stale packets (the state stream is sent UNRELIABLE + unordered).
///
/// The puppet GameObject is a stripped visual clone (no CarController, colliders, Rigidbody or
/// audio — see RemoteCarManager.StripPuppet), so this component owns its transform outright.
/// </summary>
public class RemoteCarPuppet : MonoBehaviour
{
    [Tooltip("Position discontinuity (units) treated as a teleport: snap instead of blending.")]
    public float snapDistance = 100f;
    [Tooltip("Time constant (s) of the exponential position correction.")]
    public float positionTau = 0.12f;
    [Tooltip("Time constant (s) of the exponential rotation correction.")]
    public float rotationTau = 0.10f;
    [Tooltip("Maximum seconds a state is projected forward — beyond this the car holds position.")]
    public float maxExtrapolation = 0.5f;
    [Tooltip("Constant acceleration to fold into the forward projection — for ballistic entities " +
             "(boulders) whose velocity curves between updates. Player cars are hover-physics: leave zero.")]
    public Vector3 projectAcceleration;

    /// <summary>Was this state sent while the sender had its own gravity switched OFF? A homing boulder
    /// does exactly that - it kills gravity and thrusts at its target instead - so projecting a
    /// ballistic arc through that phase predicts a fall that is not happening. The host reads it
    /// straight off <c>Rigidbody.useGravity</c>, so it needs no per-entity special-casing.</summary>
    private bool senderWeightless;

    /// <summary>The acceleration to project with THIS tick: none while the sender is weightless.</summary>
    Vector3 ProjectionAcceleration => senderWeightless ? Vector3.zero : projectAcceleration;

    [Tooltip("Drive the kinematic Rigidbody with MovePosition instead of writing the transform. " +
             "Only for puppets that must physically SHOVE the local car: a transform write teleports, " +
             "which depenetrates without transferring any momentum.")]
    public bool moveByPhysics;
    private Rigidbody body;

    private Vector3 basePos;
    private Quaternion baseRot = Quaternion.identity;
    private Vector3 linearVelocity;
    private Vector3 angularVelocity;   // rad/s, axis-scaled (Rigidbody.angularVelocity convention)
    private float stateTime;           // local Time.time when the state landed
    private ushort lastSequence;
    private bool hasState;

    // Sibling effects driver (turbo trails / jet flare / SD burst), resolved lazily — it's added to
    // this GameObject AFTER this component, so it isn't there yet at our Awake.
    private RemoteCarEffects effects;
    private bool effectsResolved;
    RemoteCarEffects Effects
    {
        get
        {
            if (!effectsResolved) { effects = GetComponent<RemoteCarEffects>(); effectsResolved = true; }
            return effects;
        }
    }

    /// <summary>The last replicated linear velocity — RemoteCarAudio drives the engine rev off it.</summary>
    public Vector3 CurrentVelocity => linearVelocity;

    /// <summary>Whether this remote player is off the ground, from bit 6 of the state byte. The stand-in
    /// for <c>CarController.IsAirborne</c>, which the puppet strip destroyed - see
    /// <see cref="MultiplayerWorld.IsPlayerAirborne"/>, which every hunter should ask instead of
    /// reaching for a CarController that only ever exists on the local car.</summary>
    public bool Airborne { get; private set; }

    /// <summary>Feeds a freshly received owner state. Out-of-order packets are dropped. The two drift
    /// drives default to zero because NPC puppets share this method and no drone or boulder has tires -
    /// only the CAR stream carries them.</summary>
    public void ApplyState(ushort sequence, Vector3 position, Quaternion rotation,
                           Vector3 linVel, Vector3 angVel, byte effectFlags,
                           float driftLevel = 0f, float driftSteer = 0f, bool weightless = false)
    {
        if (hasState && !IsNewer(sequence, lastSequence)) return;   // stale/out-of-order packet

        senderWeightless = weightless;

        lastSequence = sequence;
        basePos = position;
        baseRot = NormalizeSafe(rotation);
        linearVelocity = linVel;
        angularVelocity = angVel;
        stateTime = Time.time;

        var fx = Effects;
        if (!hasState || Vector3.Distance(transform.position, position) > snapDistance)
        {
            // First state, or a teleport-sized jump (portal): appear there, no blending. Gravity puppets
            // (boulders) LAUNCH the instant they spawn, so snap to the same projected flight position the
            // first Update would compute (lead = positionTau) — otherwise a boulder pops in at its raw
            // ground spawn point, half-buried in the track scenery, until the projection lifts it out.
            Vector3 snapPos = position;
            if (ProjectionAcceleration.sqrMagnitude > 0.0001f)
                snapPos += linVel * positionTau + 0.5f * positionTau * positionTau * ProjectionAcceleration;
            WritePose(snapPos, baseRot, snap: true);
            hasState = true;
            if (fx != null) fx.ClearTrails();   // don't streak a trail ribbon across the teleport
        }

        Airborne = (effectFlags & RemoteCarEffects.FlagAirborne) != 0;

        if (fx != null)
        {
            fx.ApplyFlags(effectFlags);
            fx.ApplyDrift(driftLevel, driftSteer);
        }
    }

    void Start()
    {
        if (!moveByPhysics) return;
        body = GetComponent<Rigidbody>();
        if (body == null) { moveByPhysics = false; return; }

        // StripPuppet turns interpolation OFF for every puppet, and rightly so while the pose is a
        // TRANSFORM write: physics would keep managing the transform for rendering and the visible mesh
        // would drift away from the collider. MovePosition inverts that argument - the body is now the
        // thing being moved, mesh and collider travel together, and interpolation is the only way to
        // render its 50 Hz steps smoothly at any frame rate. Without this, switching a puppet to
        // MovePosition would trade a weak shove for visible stepping.
        body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    void Update()
    {
        if (moveByPhysics) return;   // driven from FixedUpdate instead — see Advance
        Advance(Time.deltaTime);
    }

    /// <summary>MovePosition has to be issued from FixedUpdate: it takes effect at the NEXT physics
    /// step, so a puppet driven from Update gets it wrong at both ends — under 50 fps some physics steps
    /// receive no move at all and the body stalls for a step then jumps, and over 50 fps every call but
    /// the last is simply overwritten. Transform-written puppets stay on Update, where they belong:
    /// their pose is a rendering concern and should run at the render rate.</summary>
    void FixedUpdate()
    {
        if (!moveByPhysics) return;
        Advance(Time.fixedDeltaTime);
    }

    void Advance(float dt)
    {
        if (!hasState) return;

        float age = Mathf.Min(Time.time - stateTime, maxExtrapolation);

        // Dead-reckon the state forward — leading by the blend's own tau so the exponential chase
        // doesn't trail a fast car (see class comment).
        float lead = age + positionTau;
        Vector3 targetPos = basePos + linearVelocity * lead;
        Vector3 accel = ProjectionAcceleration;
        if (accel.sqrMagnitude > 0.0001f) targetPos += 0.5f * lead * lead * accel;
        Quaternion targetRot = IntegrateRotation(baseRot, angularVelocity, age + rotationTau);

        if (Vector3.Distance(CurrentPosition, targetPos) > snapDistance)
        {
            WritePose(targetPos, targetRot, snap: true);
            var fx = Effects;
            if (fx != null) fx.ClearTrails();   // extrapolated past a teleport — don't streak
            return;
        }

        // Exponential error blend — timestep-independent, absorbs correction smoothly.
        float posBlend = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, positionTau));
        float rotBlend = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, rotationTau));
        WritePose(Vector3.Lerp(CurrentPosition, targetPos, posBlend),
                  Quaternion.Slerp(CurrentRotation, targetRot, rotBlend), snap: false);
    }

    /// <summary>Where this puppet is RIGHT NOW, for the blend to correct from.
    ///
    /// ⚠️ For a MovePosition puppet that is the BODY, not the transform. Those bodies interpolate, and
    /// interpolation means the transform carries a render pose that sits between two physics steps —
    /// blending from it inside FixedUpdate would feed the interpolation offset back into the correction
    /// every step and make the puppet chase its own smoothing.</summary>
    Vector3 CurrentPosition => (moveByPhysics && body != null) ? body.position : transform.position;
    Quaternion CurrentRotation => (moveByPhysics && body != null) ? body.rotation : transform.rotation;

    /// <summary>The one place a puppet's pose is written.
    ///
    /// ⚠️ Writing <c>transform.position</c> on a kinematic Rigidbody TELEPORTS it: the solver sees a body
    /// that was never moving, so a contact is resolved by depenetration alone and NO momentum crosses.
    /// That is why a drone could sweep through the local car and barely disturb it, while the same drone
    /// on the host hit like a truck. <c>MovePosition</c> gives the move an implied velocity, which is
    /// what the solver needs to actually shove.
    ///
    /// It is opt-in (<see cref="moveByPhysics"/>) because it is not free: MovePosition takes effect at
    /// the next physics step rather than immediately, so it is only worth it for puppets that must hit
    /// something. A genuine discontinuity (spawn, portal) still writes the transform directly — you
    /// cannot SWEEP across 35 km, and asking the solver to try would be a hyperspeed streak through
    /// every collider in between.</summary>
    void WritePose(Vector3 position, Quaternion rotation, bool snap)
    {
        if (moveByPhysics && !snap)
        {
            if (body == null) body = GetComponent<Rigidbody>();
            if (body != null)
            {
                body.MovePosition(position);
                body.MoveRotation(rotation);
                return;
            }
        }
        transform.SetPositionAndRotation(position, rotation);
    }

    static Quaternion IntegrateRotation(Quaternion rotation, Vector3 angVel, float dt)
    {
        float radians = angVel.magnitude * dt;
        if (radians < 1e-5f) return rotation;
        return Quaternion.AngleAxis(radians * Mathf.Rad2Deg, angVel.normalized) * rotation;
    }

    static Quaternion NormalizeSafe(Quaternion q)
    {
        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (mag < 1e-6f) return Quaternion.identity;
        return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
    }

    /// <summary>Serial-number arithmetic so the ushort sequence survives wraparound.</summary>
    static bool IsNewer(ushort next, ushort last) => (ushort)(next - last) < 32768;
}
