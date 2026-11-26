using UnityEngine;

/// <summary>
/// The car body's own suspension — VISUAL ONLY. The body bounces and leans on the wheels the way a
/// sprung body does, from what the car's motion would make a real one feel:
///
///   • Landings compress it and it bounces back, deeper the faster the car hits the road.
///   • It leans OUT of turns, by the cornering force — so a slow turn barely leans and a hard one at
///     speed leans the most.
///   • It squats under acceleration and dips its nose under braking.
///   • It sinks into dips and loops and lifts over crests, and hangs a little off the wheels in the air.
///
/// Only the body's mesh (the prefab's CarBody child) moves. The car's Rigidbody, colliders and handling
/// are untouched — the physics car is still the rigid box it always was — and the wheels, the camera and
/// anything else on the car's root stay where they were.
///
/// It reads the car's acceleration the way an accelerometer would (gravity included), so it's the same
/// physics everywhere: on a side slope the body leans downhill, in a loop it sinks under the g-force.
/// Each axis is a damped spring — the landing compression is an impulse into the vertical one, which is
/// why the dip grows with the landing speed. Simulated at the physics rate, drawn interpolated.
///
/// Tuned on CarController (Body Suspension). Added at runtime: by CarController to the player's car, and
/// by RemoteCarManager to every other player's puppet — driven by the replicated velocity there, with the
/// tuning read off the prefab asset's CarController, since the puppet's own is stripped.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]   // after CarController, so each step sees its ground probe and velocity writes
public class CarBodySuspension : MonoBehaviour
{
    /// <summary>What a car body is called in the car prefabs when CarController's Car Body is empty.</summary>
    public const string DefaultBodyName = "CarBody";

    /// <summary>Landing Bounce is the dip for a landing at this speed into the road (m/s).</summary>
    public const float ReferenceLandingSpeed = 10f;

    /// <summary>A step that changes the velocity by more than this (m/s) is a teleport or a respawn, not
    /// something the car went through: the body settles back to rest instead of reacting.</summary>
    const float MaxStepVelocityChange = 80f;
    /// <summary>...and so is moving this much further (m) in a step than the velocity accounts for.</summary>
    const float MaxStepJump = 10f;

    /// <summary>The most vertical load (in g, beyond the car's weight) the bounce takes from the car's
    /// motion. Sustained loads — loops, dips, crests — sit well inside it; what it cuts are the one- or
    /// two-step spikes of hundreds of g: a jump's instant pop, a knock, and CarController's hover
    /// catching the car at a landing. That hover yanks the car down from where its 2 m ground ray first
    /// touches to ride height, so its spike is about as big for a 0.3 m hop as for a 30 m drop — taken
    /// as it comes, every landing bounced nearly the same. Landings get their own kick instead, from the
    /// speed the car actually hit the road at.</summary>
    const float MaxHeaveLoadG = 6f;

    CarController tuning;     // Body Suspension settings — the prefab ASSET's for a remote puppet
    CarController car;        // this car's own controller (null on a puppet), for IsGrounded
    RemoteCarPuppet puppet;   // a remote player's car: replicated velocity and airborne flag
    Rigidbody rb;

    Transform body;
    Vector3 restPosition;     // the body's pose at rest, in its parent's space
    Quaternion restRotation;
    Vector3 pivot, upAxis, rollAxis, pitchAxis;   // the car's axes and the lean pivot, same space
    bool ready;

    // Springs: heave in metres (+ = up), lean and pitch in degrees. prev* are the last physics step's
    // values, for drawing between steps.
    float heave, heaveSpeed, lean, leanSpeed, pitch, pitchSpeed;
    float prevHeave, prevLean, prevPitch;

    Vector3 lastVelocity, lastPosition;
    bool hasLast;
    bool wasGrounded = true;
    Vector3 airVelocity;      // the last velocity seen in the air — a puppet's landing speed

    /// <summary>Gives <paramref name="carRoot"/> a body suspension tuned by <paramref name="tuningSource"/>
    /// — its own CarController, or for a remote puppet the prefab asset's.</summary>
    public static CarBodySuspension Attach(GameObject carRoot, CarController tuningSource)
    {
        if (carRoot == null || tuningSource == null) return null;
        var suspension = carRoot.GetComponent<CarBodySuspension>();
        if (suspension == null) suspension = carRoot.AddComponent<CarBodySuspension>();
        suspension.tuning = tuningSource;
        return suspension;
    }

    void Start()
    {
        car = GetComponent<CarController>();
        puppet = GetComponent<RemoteCarPuppet>();
        rb = GetComponent<Rigidbody>();
        body = ResolveBody();
        if (tuning == null || body == null || (puppet == null && rb == null)) return;
        if (body == transform)
        {
            Debug.LogWarning($"[CarBodySuspension] {name}: Car Body is the car itself — it has to be a child " +
                             "(moving the root would move the physics car). No body suspension.");
            return;
        }

        Transform parent = body.parent;
        restPosition = body.localPosition;
        restRotation = body.localRotation;
        upAxis = parent.InverseTransformDirection(transform.up).normalized;
        rollAxis = parent.InverseTransformDirection(transform.forward).normalized;
        pitchAxis = parent.InverseTransformDirection(transform.right).normalized;
        pivot = parent.InverseTransformPoint(transform.TransformPoint(BodyBottomCentre()
                    + Vector3.up * tuning.bodyPivotHeight));
        ready = true;
    }

    /// <summary>The body to move: CarController's Car Body, found by its path under THIS car (for a
    /// puppet the setting points into the prefab asset), else the child named CarBody.</summary>
    Transform ResolveBody()
    {
        if (tuning == null) return null;
        if (tuning.carBody == null) return transform.Find(DefaultBodyName);

        string path = "";
        for (Transform t = tuning.carBody; t != tuning.transform; t = t.parent)
        {
            if (t == null) return transform.Find(DefaultBodyName);   // not under that car — use the default
            path = path.Length == 0 ? t.name : t.name + "/" + path;
        }
        return path.Length == 0 ? transform : transform.Find(path);
    }

    /// <summary>The middle of the underside of the body's meshes, in the car's own space.</summary>
    Vector3 BodyBottomCentre()
    {
        Matrix4x4 toCar = transform.worldToLocalMatrix;
        bool any = false;
        Bounds bounds = default;
        foreach (var filter in body.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            Matrix4x4 m = toCar * filter.transform.localToWorldMatrix;
            Bounds local = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }
        }
        if (!any) return transform.InverseTransformPoint(body.position);
        return new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
    }

    Vector3 Velocity => puppet != null ? puppet.CurrentVelocity : rb.linearVelocity;
    bool Grounded => car != null ? car.IsGrounded : puppet == null || !puppet.Airborne;

    void FixedUpdate()
    {
        if (!ready) return;
        float dt = Time.fixedDeltaTime;
        prevHeave = heave; prevLean = lean; prevPitch = pitch;

        Vector3 velocity = Velocity;
        Vector3 position = transform.position;
        bool continuous = hasLast
            && (velocity - lastVelocity).sqrMagnitude < MaxStepVelocityChange * MaxStepVelocityChange
            && (position - lastPosition - lastVelocity * dt).sqrMagnitude < MaxStepJump * MaxStepJump;
        Vector3 change = velocity - lastVelocity;
        lastVelocity = velocity;
        lastPosition = position;
        hasLast = true;
        if (!continuous)
        {
            Settle();
            return;
        }

        // What the suspension carries: the car's acceleration as an accelerometer reads it (gravity
        // included), in the car's own axes, less what it reads at rest on level ground — 1 g straight up.
        // Off the ground the wheels carry nothing, whichever way up the car is.
        float g = Physics.gravity.magnitude;
        bool grounded = Grounded;
        Vector3 load = grounded ? transform.InverseTransformDirection(change / dt - Physics.gravity) : Vector3.zero;
        Vector3 extra = load - Vector3.up * g;

        // Heave: extra vertical load pushes the body down into its spring, and a landing kicks it down at
        // the speed the car hit the road — a kick that size dips it Landing Bounce per 10 m/s, so the dip
        // grows with the landing speed (and with the square root of the drop).
        float wHeave = Omega(tuning.bodyBounceFrequency, dt);
        float zHeave = Mathf.Clamp(tuning.bodyBounceDamping, 0.05f, 1f);
        float gain = tuning.landingBounce * wHeave / (ReferenceLandingSpeed * ImpulsePeak(zHeave));
        if (grounded && !wasGrounded)
        {
            // Our car: this step's velocity, from before the hover's catch (we run after CarController).
            // A puppet's airborne flag lands with an update that may already hold the catch.
            Vector3 hit = car != null ? velocity : airVelocity;
            heaveSpeed -= gain * Mathf.Max(0f, -Vector3.Dot(hit, transform.up));
        }
        if (!grounded) airVelocity = velocity;
        wasGrounded = grounded;
        float heaveLoad = Mathf.Clamp(extra.y, -MaxHeaveLoadG * g, MaxHeaveLoadG * g);
        heaveSpeed += (-wHeave * wHeave * heave - 2f * zHeave * wHeave * heaveSpeed - gain * heaveLoad) * dt;
        heave += heaveSpeed * dt;

        // Lean and pitch: each springs toward its share of the sideways / fore-aft force. Cornering pushes
        // toward the inside of the turn, so the body leans the other way; a positive angle about the car's
        // forward tips its roof left, about its right tips its nose down.
        float wLean = Omega(tuning.bodyLeanFrequency, dt);
        float zLean = Mathf.Clamp(tuning.bodyLeanDamping, 0.05f, 1f);
        Spring(ref lean, ref leanSpeed, tuning.bodyLeanPerG * extra.x / g, wLean, zLean, dt);
        Spring(ref pitch, ref pitchSpeed, -tuning.bodyPitchPerG * extra.z / g, wLean, zLean, dt);
    }

    static void Spring(ref float x, ref float speed, float target, float w, float zeta, float dt)
    {
        speed += (w * w * (target - x) - 2f * zeta * w * speed) * dt;
        x += speed * dt;
    }

    /// <summary>Angular frequency (rad/s) for <paramref name="hz"/>, held where a physics step can still
    /// integrate it stably.</summary>
    static float Omega(float hz, float dt) => Mathf.Min(2f * Mathf.PI * Mathf.Max(hz, 0.1f), 1f / dt);

    /// <summary>The deepest a damped spring dips after a kick of unit speed, times its angular frequency:
    /// how much of a landing's speed ends up as dip, at this damping.</summary>
    static float ImpulsePeak(float zeta)
    {
        if (zeta >= 0.999f) return 1f / (float)System.Math.E;   // critically damped
        float r = Mathf.Sqrt(1f - zeta * zeta);
        return Mathf.Exp(-zeta / r * Mathf.Atan2(r, zeta));
    }

    void Settle()
    {
        heave = heaveSpeed = lean = leanSpeed = pitch = pitchSpeed = 0f;
        prevHeave = prevLean = prevPitch = 0f;
    }

    void LateUpdate()
    {
        if (!ready) return;

        // Drawn between the last two physics steps, like the interpolated Rigidbody it rides on.
        float t = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
        float h = Soft(Mathf.Lerp(prevHeave, heave, t), tuning.maxBodyBounce);
        float r = Soft(Mathf.Lerp(prevLean, lean, t), tuning.maxBodyLean);
        float p = Soft(Mathf.Lerp(prevPitch, pitch, t), tuning.maxBodyPitch);

        Quaternion tilt = Quaternion.AngleAxis(p, pitchAxis) * Quaternion.AngleAxis(r, rollAxis);
        body.localPosition = pivot + tilt * (restPosition - pivot) + upAxis * h;
        body.localRotation = tilt * restRotation;
    }

    /// <summary>Eases <paramref name="x"/> into ±<paramref name="limit"/> like a bump stop: unchanged when
    /// small, never past the limit.</summary>
    static float Soft(float x, float limit) => limit > 0f ? limit * (float)System.Math.Tanh(x / limit) : 0f;

    void OnDisable()
    {
        if (!ready || body == null) return;
        body.localPosition = restPosition;
        body.localRotation = restRotation;
        Settle();
        hasLast = false;
        wasGrounded = true;
    }
}
