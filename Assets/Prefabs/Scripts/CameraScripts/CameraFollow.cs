using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    [Header("Target")]
    public Transform target;                  // Drag your Car here

    [Header("Position Settings")]
    public Vector3 offset = new Vector3(0f, 3f, -7f);  // Behind and above the car
    public float positionSmoothTime = 0.15f;            // Lower = snappier follow

    [Header("Rotation Settings")]
    public float rotationSmoothTime = 0.1f;   // How fast camera rotates with car
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

    private Rigidbody targetRb;
    private Vector3 currentVelocity;
    private float currentRotationVelocity;
    private float currentFOVVelocity;
    private Camera cam;

    private CarController targetCar;        // NEW — to read turbo state
    private float turboFOVTimer = 0f;       // NEW — counts down the kick
    private bool prevTurboState = false;   // NEW — detects the activation moment

    void Start()
    {
        cam = GetComponent<Camera>();

        if (target != null)
        {
            targetRb = target.GetComponent<Rigidbody>();
            targetCar = target.GetComponent<CarController>();   // NEW
        }

        if (target != null)
        {
            transform.position = target.TransformPoint(offset);
            transform.LookAt(target);
        }
    }

    // LateUpdate runs after all movement is done — always use this for cameras
    void LateUpdate()
    {
        if (target == null) return;

        FollowPosition();
        FollowRotation();
        UpdateFOV();
    }

    void FollowPosition()
    {
        // Calculate desired position in world space based on car's local offset
        Vector3 desiredPosition = target.TransformPoint(offset);

        // Smoothly move toward desired position
        transform.position = Vector3.SmoothDamp(
            transform.position,
            desiredPosition,
            ref currentVelocity,
            positionSmoothTime
        );
    }

    void FollowRotation()
    {
        // Get the car's Y rotation (yaw only — ignore pitch/roll so camera stays upright)
        float targetAngle = target.eulerAngles.y;

        // Smoothly rotate the camera's Y to match
        float smoothAngle = Mathf.SmoothDampAngle(
            transform.eulerAngles.y,
            targetAngle,
            ref currentRotationVelocity,
            rotationSmoothTime
        );

        // Build look target: slightly ahead of the car in its forward direction
        Vector3 lookTarget = target.position + target.forward * lookAheadDistance;

        // Apply Y rotation first, then look at the car with a slight ahead offset
        transform.rotation = Quaternion.Euler(0f, smoothAngle, 0f);
        transform.LookAt(lookTarget + Vector3.up * 1.5f);  // +Y keeps car in frame
    }

    void UpdateFOV()
    {
        if (targetRb == null) return;

        // Detect the moment turbo activates (rising edge) and start the FOV kick
        if (targetCar != null)
        {
            bool turboNow = targetCar.IsTurboActive;
            if (turboNow && !prevTurboState)
                turboFOVTimer = turboFOVDuration;
            prevTurboState = turboNow;
        }

        // Tick the kick timer down
        if (turboFOVTimer > 0f)
            turboFOVTimer -= Time.deltaTime;

        // Base speed-scaled FOV
        float speed = targetRb.linearVelocity.magnitude * 3.6f;
        float targetFOV = Mathf.Lerp(baseFOV, maxFOV, speed / maxSpeed);

        // Add the turbo boost while the kick timer is active
        if (turboFOVTimer > 0f)
            targetFOV += turboFOVBoost;

        cam.fieldOfView = Mathf.SmoothDamp(
            cam.fieldOfView,
            targetFOV,
            ref currentFOVVelocity,
            fovSmoothTime
        );
    }
}