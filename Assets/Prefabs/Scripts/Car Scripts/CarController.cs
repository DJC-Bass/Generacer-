using UnityEngine;
using UnityEngine.InputSystem;   // NEW

[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    [Header("Wheel Colliders")]
    public WheelCollider wheelFL;
    public WheelCollider wheelFR;
    public WheelCollider wheelRL;
    public WheelCollider wheelRR;

    [Header("Wheel Meshes")]
    public Transform meshFL;
    public Transform meshFR;
    public Transform meshRL;
    public Transform meshRR;

    [Header("Speed")]
    [Tooltip("Top speed in mph.")]
    public float maxSpeedMph = 300f;
    [Tooltip("Peak motor torque applied at low speed; falls off as max is approached.")]
    public float maxMotorTorque = 4500f;

    [Header("Steering")]
    public float maxSteerAngleLowSpeed = 35f;
    public float maxSteerAngleHighSpeed = 8f;
    public float steerLerpSpeed = 5f;

    [Header("Braking")]
    public float maxBrakeTorque = 8000f;
    [Range(0.5f, 0.85f)] public float frontBrakeBias = 0.65f;
    public float engineBrakeTorque = 200f;

    [Header("Grip & Stability")]
    public Transform centerOfMass;
    [Tooltip("Forward tire stiffness (acceleration / braking grip).")]
    public float forwardGripStiffness = 2.5f;
    [Tooltip("Sideways tire stiffness (cornering grip). Higher = less drift.")]
    public float sidewaysGripStiffness = 3.5f;
    [Tooltip("Anti-roll bar — counters body roll on hard turns.")]
    public float antiRollForce = 8000f;

    [Header("Drivetrain")]
    [Tooltip("Torque bias between front and rear wheels. " +
         "0 = RWD (rear-wheel drive), 0.5 = balanced AWD, 1 = FWD (front-wheel drive). " +
         "0.4 = current default (slight rear bias).")]
    [Range(0f, 1f)] public float frontDriveBias = 0.4f;

    [Header("Downforce")]
    [Tooltip("Maximum downforce in Newtons at top speed. Scales with speed squared.")]
    public float maxDownforce = 12000f;

    [Tooltip("Extra downforce multiplier while braking. 1 = no extra, 2 = double downforce, " +
         "3 = triple. Helps keep the car planted when braking hard into turns or down hills.")]
    public float brakingDownforceMultiplier = 2.5f;

    [Header("Hill Climb Assist")]
    [Tooltip("Counteracts gravity along the car's forward direction when climbing. " +
             "1.0 = no speed loss going uphill, 0.0 = full physics.")]
    [Range(0f, 1f)] public float hillGravityCompensation = 0.9f;
    [Tooltip("Extra torque multiplier when climbing, scaling with steepness.")]
    public float climbTorqueBoost = 2f;
    [Tooltip("Slope angle (degrees) at which the assist reaches full strength.")]
    public float fullAssistAngle = 25f;

    [Header("Air Drift")]
    [Tooltip("How fast the car can drift sideways while airborne (m/s).")]
    public float airDriftSpeed = 15f;
    [Tooltip("How quickly the car reaches max drift speed when input is held. " +
             "Higher = snappier response.")]
    public float airDriftAcceleration = 40f;
    [Tooltip("Seconds the car must be airborne before air drift activates. " +
             "Prevents drift from triggering during high-speed crests and bumps.")]
    public float airDriftGracePeriod = 0.4f;

    [Header("Manual Air Pitch")]
    [Tooltip("How fast the player can manually pitch the car in midair (degrees per second). " +
         "Available after the car self-levels.")]
    public float manualPitchSpeed = 120f;
    [Tooltip("Key for pitching nose up.")]
    public KeyCode pitchUpKey = KeyCode.UpArrow;
    [Tooltip("Key for pitching nose down.")]
    public KeyCode pitchDownKey = KeyCode.DownArrow;

    [Tooltip("Extra gravity multiplier while braking midair. 1 = normal gravity, " +
         "2 = double, 3 = triple. Only activates when the car is level (same " +
         "condition as air drift). Useful for diving to lower track levels quickly.")]
    public float airBrakeGravityMultiplier = 3f;

    [Header("Airborne Self-Leveling")]
    [Tooltip("How quickly the car's X and Z rotation return to 0 while airborne (degrees per second).")]
    public float airLevelingSpeed = 90f;
    [Tooltip("Tilt threshold (degrees) below which air drift becomes available. " +
             "Lower = stricter requirement that car is fully level before drifting.")]
    public float airDriftLevelThreshold = 5f;

    [Header("Turbo Boost")]
    [Tooltip("Multiplier applied to top speed and motor torque during turbo.")]
    public float turboMultiplier = 2f;
    [Tooltip("How long turbo lasts when activated (seconds).")]
    public float turboDuration = 2f;
    [Tooltip("Cooldown before turbo can be used again (seconds). 0 = no cooldown.")]
    public float turboCooldown = 0f;

    [Header("Loop Gravity Assist")]
    [Tooltip("Tag on loop track meshes. Gravity is cut once the car passes " +
         "vertical while a wheel is on a loop, so it can complete the inverted half.")]
    public string loopTag = "Loop";
    [Tooltip("up.up dot below this (just past vertical) disables gravity.")]
    public float loopGravityDisableDot = -0.05f;
    [Tooltip("up.up dot above this re-enables gravity (hysteresis prevents flicker).")]
    public float loopGravityEnableDot = 0.10f;

    [Tooltip("Mild acceleration pressing the car onto the loop surface (along " +
             "-transform.up) while gravity is cut on a loop. Keeps the car glued " +
             "through the inverted apex. ~5-10 ≈ half-to-full gravity; 0 = off.")]
    public float loopStickForce = 6f;

    [Tooltip("Multiplier applied to max speed and motor torque while any wheel is " +
             "on a loop. 1 = no change, 2 = double speed and torque on loops.")]
    public double loopSpeedMultiplier = 2.0;

    // -------------------------------------------------------
    //  Internal
    // -------------------------------------------------------

    private Rigidbody rb;
    private float throttleInput;
    private float steerInput;
    private float brakeInput;
    private float currentSteerAngle;
    private float airborneTimer = 0f;
    private float turboTimer = 0f;       // counts down while turbo is active
    private float turboCooldownTimer = 0f;
    public bool IsTurboActive => turboTimer > 0f;
    /// <summary>True while the car is past vertical on a loop and gravity is cut.</summary>
    public bool IsLoopGravityCut => loopGravityCut;
    private float manualPitchInput;
    // True once the car has finished its initial airborne self-leveling and the
    // player is allowed to take manual pitch control
    private bool manualPitchUnlocked;
    // Exposed so the camera can check whether to lock its orientation
    // Add near the other private fields
    private GeneracerControls controls;

    void OnEnable()
    {
        if (controls == null) controls = new GeneracerControls();
        controls.Driving.Enable();
    }

    void OnDisable()
    {
        controls?.Driving.Disable();
    }

    public bool IsManuallyPitching { get; private set; }

    private const float MS_TO_MPH = 2.23694f;
    private const float MPH_TO_MS = 1f / MS_TO_MPH;

    bool AnyWheelGrounded()
    {
        return wheelFL.isGrounded || wheelFR.isGrounded
            || wheelRL.isGrounded || wheelRR.isGrounded;
    }

    public float SpeedMph => rb.linearVelocity.magnitude * MS_TO_MPH;

    // -------------------------------------------------------
    //  Setup
    // -------------------------------------------------------

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        if (centerOfMass != null)
            rb.centerOfMass = centerOfMass.localPosition;

        ApplyFrictionCurves(wheelFL);
        ApplyFrictionCurves(wheelFR);
        ApplyFrictionCurves(wheelRL);
        ApplyFrictionCurves(wheelRR);
    }

    /// <summary>
    /// Friction curves that grip hard at low slip but soften at extreme slip
    /// so the car never snaps into uncontrollable spinout.
    /// </summary>
    void ApplyFrictionCurves(WheelCollider wheel)
    {
        WheelFrictionCurve fwd = wheel.forwardFriction;
        fwd.extremumSlip = 0.4f;
        fwd.extremumValue = 1.2f;
        fwd.asymptoteSlip = 0.8f;
        fwd.asymptoteValue = 1.0f;
        fwd.stiffness = forwardGripStiffness;
        wheel.forwardFriction = fwd;

        WheelFrictionCurve side = wheel.sidewaysFriction;
        side.extremumSlip = 0.25f;
        side.extremumValue = 1.3f;
        side.asymptoteSlip = 0.5f;
        side.asymptoteValue = 1.1f;
        side.stiffness = sidewaysGripStiffness;
        wheel.sidewaysFriction = side;
    }

    // -------------------------------------------------------
    //  Input
    // -------------------------------------------------------

    void Update()
    {
        // Throttle — RT minus LT via the 1D composite, already -1..1
        throttleInput = controls.Driving.Throttle.ReadValue<float>();

        // Steering / air drift — left stick X
        steerInput = controls.Driving.Steer.ReadValue<float>();

        // Manual air pitch — left stick Y
        manualPitchInput = controls.Driving.Pitch.ReadValue<float>();

        // Brake — X button
        brakeInput = controls.Driving.Brake.IsPressed() ? 1f : 0f;

        // Add to the existing Update() method
        // Turbo — B button. triggered fires once on the press, not held.
        if (controls.Driving.Turbo.triggered)
            TryActivateTurbo();

        UpdateWheelMeshes();
    }

    void FixedUpdate()
    {
        // Turbo timers
        if (turboTimer > 0f)
            turboTimer -= Time.fixedDeltaTime;
        if (turboCooldownTimer > 0f)
            turboCooldownTimer -= Time.fixedDeltaTime;
        UpdateLoopGravity();   // set gravity state for this step

        if (AnyWheelGrounded())
            airborneTimer = 0f;
        else
            airborneTimer += Time.fixedDeltaTime;

        bool inRealAir = airborneTimer >= airDriftGracePeriod;

        ApplySteering();
        ApplyMotor();
        ApplyBrakes();
        ApplyDownforce();
        ApplyAntiRoll();

        if (inRealAir)
        {
            if (!manualPitchUnlocked)
            {
                ApplyAirLeveling();
                if (IsCarLevel())
                {
                    manualPitchUnlocked = true;
                    Debug.Log("[Pitch] Manual pitch UNLOCKED — car leveled");
                }
            }
            else
            {
                ApplyManualPitchAndRollLeveling();
            }

            if (IsRollLevel())
            {
                ApplyAirDrift();
                ApplyAirBrakeGravity();
            }
        }
        else
        {
            // Grounded — reset the manual pitch unlock so the next airtime starts
            // with a fresh auto-leveling phase
            manualPitchUnlocked = false;
            IsManuallyPitching = false;
        }
    }

    /// <summary>
    /// Starts turbo if it's not already active and not on cooldown.
    /// </summary>
    void TryActivateTurbo()
    {
        if (turboTimer > 0f) return;          // already boosting
        if (turboCooldownTimer > 0f) return;  // still cooling down

        turboTimer = turboDuration;
        turboCooldownTimer = turboCooldown + turboDuration; // cooldown counts from activation
    }

    /// <summary>
    /// Applies extra downward force while braking in midair, letting the player
    /// dive toward a lower section of track faster than natural gravity allows.
    /// Only activates when the car is level (X and Z rotation near zero), same
    /// condition required for air drift to fire.
    /// </summary>
    void ApplyAirBrakeGravity()
    {
        if (brakeInput < 0.05f) return;

        // Extra gravity beyond what Unity already applies. Multiplier of 3 means
        // total gravity is 3× normal — Unity's default plus 2× extra.
        // ForceMode.Acceleration applies equally regardless of mass, matching
        // how real gravity behaves.
        Vector3 extraGravity = Physics.gravity * (airBrakeGravityMultiplier - 1f) * brakeInput;
        rb.AddForce(extraGravity, ForceMode.Acceleration);
    }

    /// <summary>
    /// Combined airborne rotation: applies player pitch input around the car's
    /// right axis AND levels the roll (Z) toward zero — all in a single
    /// MoveRotation call so neither overwrites the other. Yaw is preserved.
    /// </summary>
    void ApplyManualPitchAndRollLeveling()
    {
        // --- Player pitch around the car's local right axis ---
        float pitchDelta = 0f;
        if (Mathf.Abs(manualPitchInput) > 0.05f)
        {
            IsManuallyPitching = true;
            pitchDelta = manualPitchInput * manualPitchSpeed * Time.fixedDeltaTime;
        }

        // Build the pitch rotation as a delta around the current right axis
        Quaternion pitchRot = Quaternion.AngleAxis(pitchDelta, transform.right);

        // Apply pitch to the current rotation
        Quaternion afterPitch = pitchRot * rb.rotation;

        // --- Now level the roll on the pitched orientation ---
        // Extract euler from the post-pitch rotation so we level roll without
        // discarding the pitch we just applied
        Vector3 euler = afterPitch.eulerAngles;
        float currentRoll = NormalizeAngle(euler.z);
        float newRoll = Mathf.MoveTowardsAngle(currentRoll, 0f,
                                                airLevelingSpeed * Time.fixedDeltaTime);

        // Reconstruct: keep post-pitch X and Y, replace Z with the leveled roll
        Quaternion finalRot = Quaternion.Euler(euler.x, euler.y, newRoll);

        // Single MoveRotation call — this is the only rotation write this frame
        rb.MoveRotation(finalRot);

        // Damp angular velocity around forward (roll) and right (pitch) so the
        // car doesn't keep tumbling against our explicit rotation. Preserve yaw
        // angular velocity so the car can still spin its heading.
        Vector3 angVel = rb.angularVelocity;
        float yComponent = angVel.y;
        rb.angularVelocity = Vector3.up * yComponent;
    }

    /// <summary>
    /// True when the car's ROLL (Z) is within threshold of level, regardless of
    /// pitch. Used to gate air drift and fast-fall during manual pitch flight.
    /// </summary>
    bool IsRollLevel()
    {
        float roll = Mathf.Abs(NormalizeAngle(transform.eulerAngles.z));
        return roll < airDriftLevelThreshold;
    }

    // -------------------------------------------------------
    //  Steering
    // -------------------------------------------------------

    /// <summary>
    /// While airborne, gently rotates the car so its X (pitch) and Z (roll)
    /// approach zero. Y (yaw / heading) is preserved so the car keeps facing
    /// whatever direction the player was driving. Uses MoveRotation for clean
    /// physics-aware rotation that respects fixed timestep.
    /// </summary>
    void ApplyAirLeveling()
    {
        Vector3 currentEuler = transform.eulerAngles;

        // Convert X and Z from 0–360 range to -180–180 so MoveTowardsAngle
        // takes the shortest path back to zero
        float currentPitch = NormalizeAngle(currentEuler.x);
        float currentRoll = NormalizeAngle(currentEuler.z);

        // Step pitch and roll toward zero at airLevelingSpeed degrees per second
        float newPitch = Mathf.MoveTowardsAngle(currentPitch, 0f,
                                                 airLevelingSpeed * Time.fixedDeltaTime);
        float newRoll = Mathf.MoveTowardsAngle(currentRoll, 0f,
                                                 airLevelingSpeed * Time.fixedDeltaTime);

        // Yaw stays whatever it was — preserves heading direction
        Quaternion newRotation = Quaternion.Euler(newPitch, currentEuler.y, newRoll);
        rb.MoveRotation(newRotation);

        // Cancel any angular velocity around X and Z so the car doesn't keep
        // tumbling against the leveling. Y angular velocity is preserved so
        // the car can still spin/yaw if it has rotational momentum.
        Vector3 angVel = rb.angularVelocity;
        Vector3 worldRight = Vector3.right;
        Vector3 worldForward = Vector3.forward;

        float yComponent = angVel.y;
        rb.angularVelocity = Vector3.up * yComponent;
    }

    /// <summary>
    /// Returns true when the car's pitch and roll are both within the threshold
    /// of zero — meaning the car is essentially level and air drift is safe.
    /// </summary>
    bool IsCarLevel()
    {
        float pitch = Mathf.Abs(NormalizeAngle(transform.eulerAngles.x));
        float roll = Mathf.Abs(NormalizeAngle(transform.eulerAngles.z));
        return pitch < airDriftLevelThreshold && roll < airDriftLevelThreshold;
    }

    /// <summary>
    /// Maps an angle from the 0–360 range to -180–180 so MoveTowardsAngle
    /// returns to 0 along the shortest path.
    /// </summary>
    float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }

    void ApplySteering()
    {
        float speedFactor = Mathf.Clamp01(SpeedMph / maxSpeedMph);
        float curvedFactor = Mathf.Pow(speedFactor, 0.6f);

        float currentMaxSteer = Mathf.Lerp(maxSteerAngleLowSpeed,
                                           maxSteerAngleHighSpeed,
                                           curvedFactor);

        float targetSteer = currentMaxSteer * steerInput;
        currentSteerAngle = Mathf.Lerp(currentSteerAngle, targetSteer,
                                       steerLerpSpeed * Time.fixedDeltaTime);

        wheelFL.steerAngle = currentSteerAngle;
        wheelFR.steerAngle = currentSteerAngle;
    }

    // -------------------------------------------------------
    //  Motor with Hill Assist
    // -------------------------------------------------------

    /// <summary>
    /// Applies wheel torque scaled by speed, plus extra grunt on climbs and a
    /// gravity-compensation force that prevents speed loss going uphill.
    /// </summary>
    void ApplyMotor()
    {
        // Apply turbo multiplier to both top speed and torque while active
        // Turbo multiplier (existing), then stack the loop multiplier on top so
        // speed and torque scale up while driving on a loop.
        float turbo = IsTurboActive ? turboMultiplier : 1f;
        float loopMult = loopGravityCut ? (float)loopSpeedMultiplier : 1f;

        float activeMaxSpeed = maxSpeedMph * turbo * loopMult;
        float activeMaxTorque = maxMotorTorque * turbo * loopMult;

        float maxSpeedMs = activeMaxSpeed * MPH_TO_MS;
        float speedRatio = Mathf.Clamp01(rb.linearVelocity.magnitude / maxSpeedMs);

        Vector3 forwardDir = transform.forward;
        float climbDot = forwardDir.y;
        float climbAngle = Mathf.Asin(Mathf.Clamp(climbDot, -1f, 1f)) * Mathf.Rad2Deg;

        float torqueScale = 1f - speedRatio * speedRatio;

        if (climbDot > 0f)
        {
            float climbFactor = Mathf.Clamp01(climbAngle / fullAssistAngle);
            torqueScale *= 1f + climbTorqueBoost * climbFactor;
        }

        float torque = activeMaxTorque * throttleInput * torqueScale;

        wheelFL.motorTorque = torque * frontDriveBias;
        wheelFR.motorTorque = torque * frontDriveBias;
        wheelRL.motorTorque = torque * (1f - frontDriveBias);
        wheelRR.motorTorque = torque * (1f - frontDriveBias);

        // Gravity compensation (unchanged)
        if (climbDot > 0f && Mathf.Abs(throttleInput) > 0.05f)
        {
            Vector3 gravityForce = Physics.gravity * rb.mass;
            float backwardPull = -Vector3.Dot(gravityForce, forwardDir);

            if (backwardPull > 0f)
            {
                float compensation = backwardPull
                                   * hillGravityCompensation
                                   * Mathf.Abs(throttleInput);
                rb.AddForce(forwardDir * compensation);
            }
        }
    }

    // -------------------------------------------------------
    //  Brakes
    // -------------------------------------------------------

    void ApplyBrakes()
    {
        float brake = brakeInput * maxBrakeTorque;

        if (Mathf.Abs(throttleInput) < 0.05f && brakeInput < 0.05f)
            brake = engineBrakeTorque;

        wheelFL.brakeTorque = brake * frontBrakeBias;
        wheelFR.brakeTorque = brake * frontBrakeBias;
        wheelRL.brakeTorque = brake * (1f - frontBrakeBias);
        wheelRR.brakeTorque = brake * (1f - frontBrakeBias);
    }

    // -------------------------------------------------------
    //  Downforce
    // -------------------------------------------------------

    /// <summary>
    /// Aerodynamic downforce — pushes the car straight down along world up.
    /// Scales with speed squared, like real aerodynamics, so high-speed crests
    /// generate enough downforce to keep the car planted.
    /// </summary>
    void ApplyDownforce()
    {
        float speedRatio = Mathf.Clamp01(SpeedMph / maxSpeedMph);
        float force = maxDownforce * speedRatio * speedRatio;

        // While the brake is held, scale up the downforce. The brake input is
        // 0-1, so multiplying it lets the bonus ramp in smoothly with brake
        // pressure rather than snapping on instantly.
        if (brakeInput > 0.05f)
        {
            float brakingBonus = (brakingDownforceMultiplier - 1f) * brakeInput;
            force *= 1f + brakingBonus;
        }

        rb.AddForce(Vector3.down * force);
    }

    // -------------------------------------------------------
    //  Anti-Roll
    // -------------------------------------------------------

    void ApplyAntiRoll()
    {
        AntiRollBar(wheelFL, wheelFR);
        AntiRollBar(wheelRL, wheelRR);
    }

    void AntiRollBar(WheelCollider left, WheelCollider right)
    {
        WheelHit hit;
        float travelL = 1f, travelR = 1f;

        bool groundedL = left.GetGroundHit(out hit);
        if (groundedL)
            travelL = (-left.transform.InverseTransformPoint(hit.point).y - left.radius)
                    / left.suspensionDistance;

        bool groundedR = right.GetGroundHit(out hit);
        if (groundedR)
            travelR = (-right.transform.InverseTransformPoint(hit.point).y - right.radius)
                    / right.suspensionDistance;

        float force = (travelL - travelR) * antiRollForce;

        if (groundedL) rb.AddForceAtPosition(left.transform.up * -force, left.transform.position);
        if (groundedR) rb.AddForceAtPosition(right.transform.up * force, right.transform.position);
    }

    /// <summary>
    /// While airborne (past grace period), A/D input translates the car
    /// horizontally perpendicular to its facing direction. Forward speed and
    /// vertical fall speed are preserved exactly — only the lateral component
    /// of velocity is modified.
    /// </summary>
    void ApplyAirDrift()
    {
        // Safety check — if the car is tilted past 45° from upright, skip air drift
        // entirely. Sideways or steeply-pitched cars have unreliable forward/right
        // axes for horizontal projection, so trying to drift in those orientations
        // produces erratic velocity calculations.
        float tilt = Vector3.Angle(transform.up, Vector3.up);
        if (tilt > 45f) return;

        // Project car's forward and right onto horizontal plane. Both are normalised
        // AFTER projection so they're proper unit vectors in the horizontal plane.
        Vector3 forwardAxis = transform.forward;
        forwardAxis.y = 0f;
        if (forwardAxis.sqrMagnitude < 0.01f) return;
        forwardAxis.Normalize();

        Vector3 driftAxis = transform.right;
        driftAxis.y = 0f;
        if (driftAxis.sqrMagnitude < 0.01f) return;
        driftAxis.Normalize();

        // Decompose CURRENT velocity onto these horizontal unit vectors.
        // The horizontal components of velocity are projected onto the unit
        // axes — which guarantees the recomposition produces a velocity of
        // matching magnitude rather than amplifying it.
        Vector3 vel = rb.linearVelocity;
        Vector3 horizontalVel = new Vector3(vel.x, 0f, vel.z);

        float forwardSpeed = Vector3.Dot(horizontalVel, forwardAxis);
        float currentDrift = Vector3.Dot(horizontalVel, driftAxis);
        float verticalSpeed = vel.y;

        float targetDrift = steerInput * airDriftSpeed;
        float newDrift = Mathf.MoveTowards(currentDrift, targetDrift,
                                            airDriftAcceleration * Time.fixedDeltaTime);

        // Recompose. Since forwardAxis and driftAxis are orthogonal unit vectors
        // in the horizontal plane, this rebuilds horizontal velocity exactly,
        // with only the drift component changed.
        Vector3 newHorizontal = forwardAxis * forwardSpeed + driftAxis * newDrift;
        rb.linearVelocity = new Vector3(newHorizontal.x, verticalSpeed, newHorizontal.z);
    }

    // Tracks whether loop gravity-cut is currently active (hysteresis state).
    private bool loopGravityCut;

    /// <summary>
    /// True if any wheel is resting on a surface tagged as a loop.
    /// </summary>
    bool AnyWheelOnLoop()
    {
        return WheelOnLoop(wheelFL) || WheelOnLoop(wheelFR)
            || WheelOnLoop(wheelRL) || WheelOnLoop(wheelRR);
    }

    bool WheelOnLoop(WheelCollider wheel)
    {
        if (wheel == null) return false;
        if (!wheel.GetGroundHit(out WheelHit hit)) return false;
        return hit.collider != null && hit.collider.CompareTag(loopTag);
    }

    /// <summary>
    /// Disables gravity only once the car has rotated PAST vertical (starting to
    /// invert) while on a loop, and restores it once it comes back up past
    /// vertical or leaves the loop. The climb into the loop keeps full gravity so
    /// the entry still feels weighty; the inverted top and descent run gravity-free
    /// so the car can't peel off.
    /// </summary>
    void UpdateLoopGravity()
    {
        bool onLoop = AnyWheelOnLoop();

        if (!onLoop)
        {
            loopGravityCut = false;
        }
        else
        {
            float uprightDot = Vector3.Dot(transform.up, Vector3.up);
            if (!loopGravityCut && uprightDot < loopGravityDisableDot)
                loopGravityCut = true;
            else if (loopGravityCut && uprightDot > loopGravityEnableDot)
                loopGravityCut = false;
        }

        rb.useGravity = !loopGravityCut;

        // While gravity is cut on the loop, press the car gently onto the track
        // surface (into the loop interior) so it doesn't drift off the inside at
        // the apex. Acceleration mode = mass-independent, like gravity.
        if (loopGravityCut && loopStickForce > 0f)
            rb.AddForce(-transform.up * loopStickForce, ForceMode.Acceleration);
    }

    // -------------------------------------------------------
    //  Wheel Mesh Sync
    // -------------------------------------------------------

    void UpdateWheelMeshes()
    {
        UpdateSingleWheel(wheelFL, meshFL);
        UpdateSingleWheel(wheelFR, meshFR);
        UpdateSingleWheel(wheelRL, meshRL);
        UpdateSingleWheel(wheelRR, meshRR);
    }

    void UpdateSingleWheel(WheelCollider col, Transform mesh)
    {
        col.GetWorldPose(out Vector3 pos, out Quaternion rot);
        mesh.SetPositionAndRotation(pos, rot);
    }
}