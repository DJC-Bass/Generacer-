using UnityEngine;
using UnityEngine.InputSystem;

public class CameraFollow : MonoBehaviour
{
    [Header("Target")]
    public Transform target;                  // Drag your Car here

    [Header("Rear View")]
    [Tooltip("Tick to configure THIS camera as the rear-view camera: it sits in FRONT of the " +
             "car and looks backward. Leave unticked for the normal forward camera. Switching " +
             "between the two cameras is handled by CameraSwitcher on R3.")]
    public bool rearView;

    [Header("Position Settings")]
    public Vector3 offset = new Vector3(0f, 3f, -7f);  // Behind and above the car
    [Tooltip("Lazy-Susan YAW lag: how slowly the camera's heading orbits to follow the car's. " +
             "The offset DISTANCE is always maintained — only the orbit angle eases. " +
             "0 = locked behind the car; higher = lazier.")]
    public float positionSmoothTime = 0.15f;
    [Tooltip("PITCH lag: how slowly the camera's nose-up/down position follows the car " +
             "(climbs/dives). 0 = instant; higher = lazier.")]
    public float pitchSmoothTime = 0.15f;
    [Tooltip("ROLL lag: how slowly the camera's bank position follows the car (banking/loops). " +
             "0 = instant; higher = lazier.")]
    public float rollSmoothTime = 0.15f;

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

    [Header("Speed Barrier FOV Kick")]
    [Tooltip("At or above this speed (mph) a sustained FOV kick engages, as if the car has broken " +
             "through the speed barrier.")]
    public float speedBarrierMph = 750f;
    [Tooltip("Once engaged, the kick holds until speed drops below THIS (mph). Kept a touch under " +
             "the engage speed so cruising right around the barrier doesn't flicker the kick on/off.")]
    public float speedBarrierReleaseMph = 700f;
    [Tooltip("Extra FOV added while the speed-barrier kick is engaged.")]
    public float speedBarrierFOVBoost = 30f;
    [Tooltip("The effect only runs while the car is grounded. It tolerates this many seconds of airtime " +
             "(crests, short hops) before force-exiting; sustained airtime drops it until the car lands " +
             "again. Also fades the muffle out before a high-speed fall to the kill floor reloads the " +
             "hub, so there's no audio pop.")]
    public float speedBarrierGroundedGrace = 1f;

    [Header("Speed Barrier Audio (low-pass muffle)")]
    [Tooltip("While the speed-barrier kick is engaged, heavily muffle everything THIS player hears " +
             "with a low-pass filter on their AudioListener. It's per-listener, so when multiplayer " +
             "lands only the player who broke the barrier is muffled; everyone else hears normally.")]
    public bool speedBarrierMuffle = true;
    [Tooltip("Low-pass cutoff (Hz) at full muffle. Lower = more muffled / underwater. ~500-1000 is heavy.")]
    public float barrierMuffleCutoff = 700f;

    private Rigidbody targetRb;
    private float currentFOVVelocity;
    private Camera cam;
    private const float MS_TO_MPH = 2.23694f;   // matches CarController.SpeedMph

    private AudioListener barrierListener;        // this camera's listener (only the ACTIVE one drives barrier audio)
    private AudioLowPassFilter barrierLowPass;   // on this camera's AudioListener; muffles the LOCAL mix at the barrier
    private float barrierAudioBlend;             // smoothed 0..1 muffle amount (eased with the FOV smooth time)
    private float barrierAudioVel;               // SmoothDamp velocity for barrierAudioBlend
    private const float BarrierCutoffOpen = 22000f;   // fully open — effectively no filtering

    // Per-axis camera rotation lag. The camera's reference rotation eases toward the car's,
    // with separate smooth times for yaw / pitch / roll. The offset is placed using this
    // lagged rotation, so the camera orbits the car like a lazy Susan in each axis.
    private Quaternion smoothedRot = Quaternion.identity;

    private CarController targetCar;        // NEW   to read turbo state
    private float turboFOVTimer = 0f;       // NEW   counts down the kick
    private bool prevTurboState = false;   // NEW   detects the activation moment
    private bool speedBarrierActive = false;   // hysteresis: on at speedBarrierMph, off below speedBarrierReleaseMph

    /// <summary>
    /// Fires the same one-shot FOV kick a Turbo activation produces. For external boosts that
    /// bypass CarController's turbo state (e.g. driving through a BoostGate), which the
    /// rising-edge poll in UpdateFOV can't see.
    /// </summary>
    public void TriggerTurboFOVKick() => turboFOVTimer = turboFOVDuration;

    void Start()
    {
        cam = GetComponent<Camera>();

        // Speed-barrier muffle lives on THIS camera's AudioListener, so it only affects the local
        // player's mix. The main camera holds the (single) active listener; the rear camera's is
        // disabled by CameraSwitcher, so a filter there is a harmless no-op.
        barrierListener = GetComponent<AudioListener>();
        if (barrierListener != null)
        {
            barrierLowPass = GetComponent<AudioLowPassFilter>();
            if (barrierLowPass == null) barrierLowPass = gameObject.AddComponent<AudioLowPassFilter>();
            barrierLowPass.cutoffFrequency = BarrierCutoffOpen;   // start open (no muffle)
            barrierLowPass.enabled = false;
        }

        if (target != null)
        {
            targetRb = target.GetComponent<Rigidbody>();
            targetCar = target.GetComponent<CarController>();   // NEW
            smoothedRot = target.rotation;   // start aligned so there's no initial swing
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

        UpdateSmoothedRotation();
        FollowPosition();
        FollowRotation();
        UpdateFOV();
        UpdateSpeedBarrierAudio();
    }

    void FollowPosition()
    {
        // Place the offset behind the per-axis-lagged camera rotation. The offset distance is
        // always maintained — only the angular position around the car (yaw/pitch/roll) lags.
        transform.position = target.position + smoothedRot * EffectiveOffset;
    }

    /// <summary>
    /// Eases the camera's reference rotation toward the car's, with an independent lag per
    /// axis: heading/yaw (positionSmoothTime), pitch (pitchSmoothTime) and roll (rollSmoothTime).
    /// Each correction is applied around its own axis from a signed angle, which stays robust
    /// through steep hills and loops — no Euler gimbal flips.
    /// </summary>
    void UpdateSmoothedRotation()
    {
        Quaternion targetRot = target.rotation;

        // Yaw — ease heading around WORLD up. Skipped when the car's forward is near-vertical
        // (e.g. a loop apex), where heading is undefined, so it holds steady there.
        Vector3 smFwdH = Vector3.ProjectOnPlane(smoothedRot * Vector3.forward, Vector3.up);
        Vector3 tgFwdH = Vector3.ProjectOnPlane(targetRot * Vector3.forward, Vector3.up);
        if (smFwdH.sqrMagnitude > 1e-5f && tgFwdH.sqrMagnitude > 1e-5f)
        {
            float yawErr = Vector3.SignedAngle(smFwdH, tgFwdH, Vector3.up);
            smoothedRot = Quaternion.AngleAxis(yawErr * Approach(positionSmoothTime), Vector3.up) * smoothedRot;
        }

        // Pitch — ease nose up/down around the camera frame's RIGHT axis.
        Vector3 smRight = smoothedRot * Vector3.right;
        float pitchErr = Vector3.SignedAngle(smoothedRot * Vector3.forward, targetRot * Vector3.forward, smRight);
        smoothedRot = Quaternion.AngleAxis(pitchErr * Approach(pitchSmoothTime), smRight) * smoothedRot;

        // Roll — ease bank around the camera frame's FORWARD axis.
        Vector3 smFwd = smoothedRot * Vector3.forward;
        float rollErr = Vector3.SignedAngle(smoothedRot * Vector3.up, targetRot * Vector3.up, smFwd);
        smoothedRot = Quaternion.AngleAxis(rollErr * Approach(rollSmoothTime), smFwd) * smoothedRot;
    }

    // Exponential approach factor (0-1) for a smooth time. 0 = instant (snaps each frame).
    float Approach(float smoothTime) =>
        smoothTime <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / smoothTime);

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

        // Speed-barrier kick: sustained "broke through the barrier" FOV boost with hysteresis, so
        // cruising right at the threshold doesn't flicker it. Engages at speedBarrierMph and holds
        // until speed falls below the slightly-lower speedBarrierReleaseMph.
        float speedMph = targetRb.linearVelocity.magnitude * MS_TO_MPH;

        // The effect only runs while grounded, with a grace window so crests / short hops don't drop
        // it. Sustained airtime force-exits it — which also fades the muffle out BEFORE a high-speed
        // fall reaches the kill floor, so the hub reload doesn't pop the filter off mid-muffle.
        bool groundedEnough = targetCar == null || targetCar.AirborneTime <= speedBarrierGroundedGrace;

        bool wasBarrier = speedBarrierActive;
        if (!speedBarrierActive && groundedEnough && speedMph >= speedBarrierMph) speedBarrierActive = true;
        else if (speedBarrierActive && (!groundedEnough || speedMph < speedBarrierReleaseMph)) speedBarrierActive = false;
        if (speedBarrierActive)
            targetFOV += speedBarrierFOVBoost;

        // On the break/leave edge, fire the 3D stinger — but ONLY from the camera that owns the active
        // AudioListener, so the two always-running CameraFollows (main + rear) don't double-trigger it.
        // The clip rides the car and bypasses the muffle, so it's heard clean over the low-pass.
        if (speedBarrierActive != wasBarrier && target != null
            && barrierListener != null && barrierListener.enabled)
        {
            if (speedBarrierActive) AudioManager.PlaySpeedBarrierBreak(target);
            else                    AudioManager.PlaySpeedBarrierLeave(target);
        }

        cam.fieldOfView = Mathf.SmoothDamp(
            cam.fieldOfView,
            targetFOV,
            ref currentFOVVelocity,
            fovSmoothTime
        );
    }

    /// <summary>
    /// Drives a low-pass filter on this camera's AudioListener so the whole local mix goes heavily
    /// muffled while the speed-barrier kick is engaged, then clears as it releases. The muffle amount
    /// is eased with the SAME smooth time as the FOV kick so the two blend in and out together — no
    /// sudden cut. Because it's on the listener, it only affects THIS player; a remote player who
    /// hasn't broken the barrier keeps hearing normally.
    /// </summary>
    void UpdateSpeedBarrierAudio()
    {
        // Only the camera holding the ACTIVE listener drives the muffle (the rear camera's listener is
        // disabled by CameraSwitcher, so its filter stays a transparent no-op).
        if (barrierLowPass == null || barrierListener == null || !barrierListener.enabled) return;

        // 0 = open, 1 = fully muffled. Same fovSmoothTime as the FOV kick → matched blend.
        float targetMuffle = (speedBarrierMuffle && speedBarrierActive) ? 1f : 0f;
        barrierAudioBlend = Mathf.SmoothDamp(barrierAudioBlend, targetMuffle,
                                             ref barrierAudioVel, fovSmoothTime);

        if (barrierAudioBlend < 0.001f)
        {
            barrierLowPass.enabled = false;   // fully open: switch the filter off so it's truly transparent
            return;
        }
        barrierLowPass.enabled = true;

        // Sweep the cutoff in LOG (octave) space so the darkening is perceptually even across the
        // blend, instead of the audible change all bunching up at the very end of a linear sweep.
        float logOpen = Mathf.Log(BarrierCutoffOpen);
        float logMuffled = Mathf.Log(Mathf.Max(10f, barrierMuffleCutoff));
        barrierLowPass.cutoffFrequency = Mathf.Exp(Mathf.Lerp(logOpen, logMuffled, barrierAudioBlend));
    }
}