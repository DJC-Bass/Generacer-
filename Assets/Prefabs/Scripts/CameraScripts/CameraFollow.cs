using UnityEngine;
using UnityEngine.InputSystem;

public class CameraFollow : MonoBehaviour
{
    [Header("Target")]
    public Transform target;                  // Drag your Car here

    [Header("Position Settings")]
    public Vector3 offset = new Vector3(0f, 3f, -7f);  // Behind and above the car
    [Tooltip("Lazy-Susan lag: how slowly the camera's ORBIT angle follows the car's heading " +
             "(yaw). The offset DISTANCE to the car is always maintained — only the angular " +
             "position around the car eases. 0 = rigidly locked behind the car; higher = lazier.")]
    public float positionSmoothTime = 0.15f;

    [Header("Rotation Settings")]
    public float rotationSmoothTime = 0.1f;   // How fast the camera's aim eases onto the car
    public float lookAheadDistance = 5f;      // Looks toward where the car is heading

    [Header("Field of View")]
    public float baseFOV = 70f;               // FOV when idle
    public float maxFOV = 90f;                // FOV at max speed
    public float maxSpeed = 100f;             // Speed (km/h) at which maxFOV is reached
    public float fovSmoothTime = 0.3f;

    [Header("Turbo FOV Kick")]
    [Tooltip("Extra FOV added on top of maxFOV when turbo activates.")]
    public float turboFOVBoost = 30f;
    [Tooltip("How long the FOV kick lasts before returning to normal (seconds).")]
    public float turboFOVDuration = 1f;

    [Header("Roll Blend")]
    [Tooltip("Below this tilt (deg) from upright, the camera stays world-upright.")]
    public float rollBlendStart = 25f;
    [Tooltip("At/above this tilt (deg), the camera fully rolls with the car.")]
    public float rollBlendFull = 80f;

    [Header("Loop FOV Kick")]
    [Tooltip("Extra FOV added while the car is in the loop gravity-cut (past " +
             "vertical on a loop). Lasts as long as that state is active.")]
    public float loopFOVBoost = 30f;

    private Rigidbody targetRb;
    private float currentFOVVelocity;
    private Camera cam;

    // Lazy-Susan orbit: the camera's yaw eases toward the car's heading so it orbits the car
    // slowly while always keeping its offset distance. Pitch/roll follow instantly so slopes
    // and loops still frame correctly.
    private float smoothedYaw;
    private float lastYaw;

    private CarController targetCar;        // NEW   to read turbo state
    private float turboFOVTimer = 0f;       // NEW   counts down the kick
    private bool prevTurboState = false;   // NEW   detects the activation moment

    // Rear-view toggle. When true, the camera's local Z offset is flipped (placing
    // it in front of the car) AND the look-ahead direction is reversed, so the
    // camera looks backward at what's behind the car   like a rear-view mirror.
    private GeneracerControls controls;
    private bool rearView;

    void OnEnable()
    {
        if (controls == null) controls = new GeneracerControls();
        controls.Driving.Enable();
    }

    void OnDisable()
    {
        controls?.Driving.Disable();
    }

    void Update()
    {
        // Toggle rear view on R3 press. triggered fires once per press, not held.
        if (controls.Driving.RearView.triggered)
            rearView = !rearView;
    }

    void Start()
    {
        cam = GetComponent<Camera>();

        if (target != null)
        {
            targetRb = target.GetComponent<Rigidbody>();
            targetCar = target.GetComponent<CarController>();   // NEW
            lastYaw = smoothedYaw = CarYaw();   // start aligned so there's no initial swing
        }

        if (target != null)
        {
            transform.position = target.TransformPoint(EffectiveOffset);
            transform.LookAt(target, target.up);   // use car's up here too
        }
    }

    /// <summary>
    /// The car-local offset to follow, with Z flipped while rear view is active so
    /// the camera sits in front of the car instead of behind it.
    /// </summary>
    Vector3 EffectiveOffset =>
        rearView ? new Vector3(offset.x, offset.y, -offset.z) : offset;

    // LateUpdate runs after all movement is done   always use this for cameras
    void LateUpdate()
    {
        if (target == null) return;

        FollowPosition();
        FollowRotation();
        UpdateFOV();
    }

    void FollowPosition()
    {
        // Lazy-Susan orbit: ease the camera's heading (yaw) toward the car's while keeping the
        // car's current pitch/roll, then place the offset behind that lagged heading. The offset
        // distance is always maintained — only the angular position around the car lags.
        float carYaw = CarYaw();
        float t = (positionSmoothTime <= 0f) ? 1f : 1f - Mathf.Exp(-Time.deltaTime / positionSmoothTime);
        smoothedYaw = Mathf.LerpAngle(smoothedYaw, carYaw, t);
        lastYaw = carYaw;

        // Rewind the car's yaw to the smoothed value (around world up); pitch/roll untouched,
        // so slopes and loops keep framing correctly.
        Quaternion orbitRot = Quaternion.AngleAxis(smoothedYaw - carYaw, Vector3.up) * target.rotation;
        transform.position = target.position + orbitRot * EffectiveOffset;
    }

    /// <summary>
    /// The car's heading around world up (degrees). Returns the last value when the car's
    /// forward is near-vertical (e.g. mid-loop), where heading is undefined — so the orbit
    /// holds steady through loops instead of jittering.
    /// </summary>
    float CarYaw()
    {
        Vector3 horiz = Vector3.ProjectOnPlane(target.forward, Vector3.up);
        if (horiz.sqrMagnitude < 1e-4f) return lastYaw;
        return Mathf.Atan2(horiz.x, horiz.z) * Mathf.Rad2Deg;
    }

    void FollowRotation()
    {
        // How far is the car tilted from world-upright?
        float tiltAngle = Vector3.Angle(target.up, Vector3.up);

        // Blend factor: 0 = world-upright, 1 = full car roll. Smoothstep between
        // the two thresholds so the camera eases into rolling rather than snapping.
        float blend;
        if (tiltAngle <= rollBlendStart) blend = 0f;
        else if (tiltAngle >= rollBlendFull) blend = 1f;
        else
        {
            float x = (tiltAngle - rollBlendStart) / (rollBlendFull - rollBlendStart);
            blend = x * x * (3f - 2f * x);
        }

        // Camera up: blend from world up toward the car's up.
        Vector3 camUp = Vector3.Slerp(Vector3.up, target.up, blend);
        if (camUp.sqrMagnitude < 1e-6f) camUp = target.up;   // guard near 180deg

        // Look slightly ahead of the car   or BEHIND it while rear view is active,
        // so the camera frames what's behind instead of what's ahead. Raise the
        // focus by the SAME blended up so framing stays consistent with the chosen
        // orientation.
        float lookSign = rearView ? -1f : 1f;
        Vector3 lookTarget = target.position
                           + target.forward * (lookAheadDistance * lookSign)
                           + camUp * 1.5f;

        Vector3 lookDir = (lookTarget - transform.position);
        if (lookDir.sqrMagnitude < 1e-6f) lookDir = target.forward;
        Quaternion desiredRotation = Quaternion.LookRotation(lookDir.normalized, camUp);

        // Ease the camera's aim onto the car. The lazy orbit lives in FollowPosition; this
        // just keeps the car framed as the camera slowly swings around behind it.
        float t = (rotationSmoothTime <= 0f)
            ? 1f
            : 1f - Mathf.Exp(-Time.deltaTime / rotationSmoothTime);

        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, t);
    }

    void UpdateFOV()
    {
        if (targetRb == null) return;

        // Turbo kick (existing)   rising-edge one-shot timer
        if (targetCar != null)
        {
            bool turboNow = targetCar.IsTurboActive;
            if (turboNow && !prevTurboState)
                turboFOVTimer = turboFOVDuration;
            prevTurboState = turboNow;
        }
        if (turboFOVTimer > 0f)
            turboFOVTimer -= Time.deltaTime;

        // Base speed-scaled FOV
        float speed = targetRb.linearVelocity.magnitude * 3.6f;
        float targetFOV = Mathf.Lerp(baseFOV, maxFOV, speed / maxSpeed);

        // Turbo boost while the kick timer is active
        if (turboFOVTimer > 0f)
            targetFOV += turboFOVBoost;

        // Loop boost   sustained for as long as the car is in loop gravity-cut.
        // No timer: it tracks the state directly, on when cut begins, off when it ends.
        if (targetCar != null && targetCar.IsLoopGravityCut)
            targetFOV += loopFOVBoost;

        cam.fieldOfView = Mathf.SmoothDamp(
            cam.fieldOfView,
            targetFOV,
            ref currentFOVVelocity,
            fovSmoothTime
        );
    }
}