using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

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

    [Header("Camera Swivel (right stick, grounded only)")]
    [Tooltip("Let the right stick orbit the camera around the car while it's on the ground, easing " +
             "back to neutral when the stick is released. Both axes work together, so a diagonal push " +
             "orbits diagonally. The moment the car is genuinely airborne the stick is handed to the " +
             "car's own aerial rotation and the camera glides home; it picks the stick back up on landing.")]
    public bool enableSwivel = true;
    [Range(0f, 180f)]
    [Tooltip("How far the camera orbits AROUND the car (left/right) at full stick deflection.")]
    [FormerlySerializedAs("maxSwivelAngle")]
    public float maxSwivelYawAngle = 90f;
    [Range(0f, 85f)]
    [Tooltip("How far the camera orbits UP over the car (looking down on it) at full stick deflection.")]
    public float maxSwivelPitchUpAngle = 60f;
    [Range(0f, 85f)]
    [Tooltip("How far the camera orbits DOWN under the car (looking up at it) at full stick deflection. " +
             "Kept small by default: the camera has no collision handling, and the default offset only " +
             "sits ~23deg above the car, so a big value dips it through the ground on flat terrain. " +
             "Raise it if the offset is higher or you don't mind the clip.")]
    public float maxSwivelPitchDownAngle = 25f;
    [Tooltip("Lag easing INTO the swivel while the stick is held. 0 = instant.")]
    public float swivelSmoothTime = 0.12f;
    [Tooltip("Lag easing BACK to neutral — on release, and when the car goes airborne. 0 = instant.")]
    public float swivelReturnSmoothTime = 0.25f;
    [Tooltip("When a swivel starts, Rotation Smooth Time is taken out of the aim over this many seconds " +
             "rather than being cut straight to 0, so whatever aim lag had built up closes smoothly " +
             "instead of snapping (most visible starting a swivel mid-corner). 0 = cut instantly. " +
             "Handing the standard easing BACK at neutral needs no ramp — by then the aim is unsmoothed " +
             "and already sitting on its target, so there's nothing to jump.")]
    public float swivelAimEaseTime = 0.15f;
    [Tooltip("Stick deflection below this is ignored, so a resting stick never nudges the camera. " +
             "Measured on the stick's overall push, not per-axis, so it can't skew a diagonal.")]
    public float swivelDeadzone = 0.15f;
    [Tooltip("Flips which way the camera orbits horizontally. OFF: pushing RIGHT swings the camera " +
             "around to the car's right side, so you see its right flank. ON: pushing RIGHT pans the " +
             "VIEW right (camera swings to the car's left and looks across it) — the usual look-stick feel.")]
    [FormerlySerializedAs("invertSwivel")]
    public bool invertSwivelHorizontal = false;
    [Tooltip("Flips the vertical orbit. OFF: pushing UP lifts the camera above the car, looking down " +
             "on it. ON: pushing UP drops the camera below, looking up at it (classic invert-Y).")]
    public bool invertSwivelVertical = false;

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

    // Grounded look-around. Signed orbit angles in degrees — x = yaw around the car, y = pitch over/under
    // it — applied to BOTH the offset and the look-ahead point so the whole rig orbits rigidly and the
    // car stays framed. Smoothed as a pair (not per-axis) so a diagonal eases along a straight line.
    private Vector2 swivel;
    private Vector2 swivelVel;              // SmoothDamp velocity for swivel
    // Latched the moment the swivel leaves neutral and held all the way through the return home, so the
    // aim easing stays off for the whole gesture (see FollowRotation). SmoothDamp only approaches its
    // target asymptotically, so "fully home" needs a threshold — below it the swivel is snapped to exact
    // zero, which makes the neutral state real instead of an ever-shrinking tail.
    private bool swivelEngaged;
    private const float SwivelNeutralEpsilon = 0.05f;   // degrees of total swivel that still counts as home
    // 0 = aim uses the full rotationSmoothTime, 1 = aim is unsmoothed. Ramps up over swivelAimEaseTime
    // while engaged; drops straight back on release, which is safe (see FollowRotation).
    private float aimEase;

    private Transform cachedTarget;         // what targetRb/targetCar were resolved from
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
            cachedTarget = target;
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
        RefreshTargetCache();

        UpdateSmoothedRotation();
        UpdateSwivel();
        FollowPosition();
        FollowRotation();
        UpdateFOV();
        UpdateSpeedBarrierAudio();
    }

    /// <summary>
    /// PlayerCarSwapper (car select) and TrackGenerator (track spawn) re-point <see cref="target"/> at a
    /// freshly spawned car after Start has already run, which would otherwise leave the cached Rigidbody
    /// and CarController pointing at the destroyed car — or null. Re-resolve whenever it actually changes.
    /// </summary>
    void RefreshTargetCache()
    {
        if (target == cachedTarget) return;
        cachedTarget = target;
        targetRb = target.GetComponent<Rigidbody>();
        targetCar = target.GetComponent<CarController>();
        smoothedRot = target.rotation;   // re-align so the swap doesn't swing the camera around

        swivel = Vector2.zero;           // and start the new car from a clean, unengaged neutral
        swivelVel = Vector2.zero;
        swivelEngaged = false;
        aimEase = 0f;
    }

    /// <summary>
    /// Grounded look-around: the right stick orbits the camera around the car, easing back to neutral
    /// on release. The instant the car is genuinely airborne (CarController.IsAirborne — the very flag
    /// that unlocks its air rotation) the stick belongs to the car, so the target angle drops to 0 and
    /// the camera glides home even if the player never let go; it picks the stick back up on landing.
    /// The two conditions are exact complements, so the stick never drives both at once, and never
    /// drives neither — including through the air-abilities grace window, where a crest or short hop
    /// keeps the camera under player control instead of snapping it back.
    /// </summary>
    void UpdateSwivel()
    {
        Vector2 targetAngles = Vector2.zero;

        if (enableSwivel && targetCar != null && !targetCar.IsAirborne && !MenuState.AnyOpen)
        {
            // The car's own readings, so rebinds are free. X = Yaw axis, Y = Pitch axis (stick up = +1).
            Vector2 stick = new Vector2(targetCar.ManualYawInput, targetCar.ManualPitchInput);

            // RADIAL deadzone — measured on the whole push, not per-axis. Deadzoning the axes
            // separately would clip one of them on a gentle diagonal and bend it back toward a
            // cardinal; taking the magnitude keeps a northeast push pointing northeast.
            float mag = stick.magnitude;
            if (mag > swivelDeadzone)
            {
                // Rescale past the deadzone so the orbit grows from 0 at its edge instead of jumping.
                float t = Mathf.Clamp01((mag - swivelDeadzone) / Mathf.Max(0.001f, 1f - swivelDeadzone));
                Vector2 dir = stick / mag;   // unit push direction — this is what preserves diagonals

                // A +Y rotation sweeps the offset from behind the car toward its LEFT, so pushing
                // RIGHT (+x) takes a NEGATIVE angle to orbit the camera round to the car's right.
                targetAngles.x = -dir.x * t * maxSwivelYawAngle;
                if (invertSwivelHorizontal) targetAngles.x = -targetAngles.x;

                // Up and down get their own limits (down is tighter — see the field tooltip), so the
                // envelope is an ellipse: a full diagonal reaches ~71% of each axis's limit.
                float vertical = dir.y * t;
                if (invertSwivelVertical) vertical = -vertical;
                targetAngles.y = vertical * (vertical >= 0f ? maxSwivelPitchUpAngle : maxSwivelPitchDownAngle);
            }
        }

        // Separate easing for reaching out to the held angles vs. coming home, so the return can be
        // softer than the swing without making the swivel itself feel sluggish. Smoothing the pair as
        // one vector keeps a diagonal transition straight instead of letting the axes arrive apart.
        bool wantsSwivel = targetAngles.sqrMagnitude > 1e-6f;
        float smooth = wantsSwivel ? swivelSmoothTime : swivelReturnSmoothTime;
        swivel = smooth <= 0f
            ? targetAngles
            : Vector2.SmoothDamp(swivel, targetAngles, ref swivelVel, smooth);

        // Engage on the first frame off neutral; disengage ONLY once the camera is all the way home,
        // so the whole gesture — swing out, hold, and the glide back — is treated as one swivel.
        if (wantsSwivel) swivelEngaged = true;
        else if (swivel.magnitude < SwivelNeutralEpsilon)
        {
            swivel = Vector2.zero;      // land on exact neutral instead of an asymptotic tail
            swivelVel = Vector2.zero;
            swivelEngaged = false;
        }

        // Ramp the aim easing out over swivelAimEaseTime on engage (a linear MoveTowards, so the field
        // reads as a real duration in seconds), and hand it straight back on release.
        float easeRate = swivelAimEaseTime <= 0f ? 1f : Time.deltaTime / swivelAimEaseTime;
        aimEase = swivelEngaged ? Mathf.MoveTowards(aimEase, 1f, easeRate) : 0f;
    }

    /// <summary>
    /// The current look-around orbit, in the car's local frame: pitch about its right axis, then yaw
    /// about its up — the standard orbit composition (yaw outermost), so a diagonal push lands where
    /// the stick points instead of skewing as the two angles compound.
    /// </summary>
    Quaternion SwivelRotation =>
        Quaternion.AngleAxis(swivel.x, Vector3.up) * Quaternion.AngleAxis(swivel.y, Vector3.right);

    void FollowPosition()
    {
        // Place the offset behind the per-axis-lagged camera rotation. The offset distance is
        // always maintained — only the angular position around the car (yaw/pitch/roll) lags.
        // The swivel orbits that offset inside the lagged frame, so the look-around rides on top of
        // the lag rather than fighting it — and because it's a pure rotation, the distance survives
        // it too: the camera swings around the car on a sphere, never toward or away from it.
        Vector3 orbitOffset = SwivelRotation * EffectiveOffset;
        transform.position = target.position + smoothedRot * orbitOffset;
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

        // Swing the look-ahead by the SAME orbit that moved the camera — expressed in the car's frame,
        // which is exactly what `target.rotation * (swivel * forward)` is — so the rig orbits as one
        // piece and the car stays framed instead of sliding out of shot as the camera swings around.
        // The rear camera needs no special case: its offset and its look-ahead are both mirrored, so an
        // identical angle pans its view the same way on screen.
        Vector3 lookForward = target.rotation * (SwivelRotation * Vector3.forward);

        Vector3 lookTarget = target.position
                           + lookForward * (lookAheadDistance * lookSign)
                           + camUp * 1.5f;

        Vector3 lookDir = (lookTarget - transform.position);
        if (lookDir.sqrMagnitude < 1e-6f) lookDir = lookForward;
        Quaternion desiredRotation = Quaternion.LookRotation(lookDir.normalized, camUp);

        // Ease the camera's aim onto the car. The lazy orbit lives in FollowPosition; this
        // just keeps the car framed as the camera slowly swings around behind it.
        //
        // While a swivel is engaged the aim tracks with NO easing at all. The swivel already carries its
        // own smoothing (swivelSmoothTime / swivelReturnSmoothTime), so leaving this lag layered on top
        // double-smooths the look-around and makes it feel like it's dragging behind the stick. The
        // standard easing only returns once the swivel is fully back at neutral — deliberately including
        // the glide home, so the return stays as crisp as the swing out.
        //
        // It's blended out over swivelAimEaseTime rather than cut, because at the instant a swivel starts
        // the aim may be lagging the desired rotation (mid-corner) and dropping to 0 in one frame would
        // snap that gap shut. Coming back the other way needs no blend: unsmoothed aim leaves the camera
        // exactly ON its target every frame, so there's no gap for the restored easing to reveal.
        float aimSmoothTime = rotationSmoothTime * (1f - aimEase);
        float t = (aimSmoothTime <= 0f)
            ? 1f
            : 1f - Mathf.Exp(-Time.deltaTime / aimSmoothTime);

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