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
    [Tooltip("Add gravity to the forward projection — for ballistic entities (boulders) whose " +
             "velocity curves between updates. Player cars are hover-physics: leave false.")]
    public bool projectGravity;

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

    /// <summary>Feeds a freshly received owner state. Out-of-order packets are dropped.</summary>
    public void ApplyState(ushort sequence, Vector3 position, Quaternion rotation,
                           Vector3 linVel, Vector3 angVel, byte effectFlags)
    {
        if (hasState && !IsNewer(sequence, lastSequence)) return;   // stale/out-of-order packet

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
            if (projectGravity)
                snapPos += linVel * positionTau + 0.5f * positionTau * positionTau * Physics.gravity;
            transform.SetPositionAndRotation(snapPos, baseRot);
            hasState = true;
            if (fx != null) fx.ClearTrails();   // don't streak a trail ribbon across the teleport
        }

        if (fx != null) fx.ApplyFlags(effectFlags);
    }

    void Update()
    {
        if (!hasState) return;

        float age = Mathf.Min(Time.time - stateTime, maxExtrapolation);

        // Dead-reckon the state forward — leading by the blend's own tau so the exponential chase
        // doesn't trail a fast car (see class comment).
        float lead = age + positionTau;
        Vector3 targetPos = basePos + linearVelocity * lead;
        if (projectGravity) targetPos += 0.5f * lead * lead * Physics.gravity;
        Quaternion targetRot = IntegrateRotation(baseRot, angularVelocity, age + rotationTau);

        if (Vector3.Distance(transform.position, targetPos) > snapDistance)
        {
            transform.SetPositionAndRotation(targetPos, targetRot);
            var fx = Effects;
            if (fx != null) fx.ClearTrails();   // extrapolated past a teleport — don't streak
            return;
        }

        // Exponential error blend — framerate-independent, absorbs correction smoothly.
        float posBlend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, positionTau));
        float rotBlend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, rotationTau));
        transform.SetPositionAndRotation(
            Vector3.Lerp(transform.position, targetPos, posBlend),
            Quaternion.Slerp(transform.rotation, targetRot, rotBlend));
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
