using UnityEngine;

/// <summary>
/// Spinning, drifting fan obstacle. Spins around its local X axis and
/// optionally drifts forward (toward the level entrance). Larger fans
/// (bigger scale) spin and drift slower than smaller ones.
/// </summary>
public class FanObstacle : MonoBehaviour
{
    [Header("Spin")]
    [Tooltip("Base spin speed in degrees per second for a 1-unit-scale fan. " +
             "Actual speed is divided by this fan's scale, so larger fans spin slower.")]
    public float baseSpinSpeed = 360f;
    [Tooltip("Random direction of spin (1 or -1) chosen at startup.")]
    public bool randomizeSpinDirection = true;

    [Header("Drift")]
    [Tooltip("Base drift speed for a 1-unit fan. Divided by scale so larger fans drift slower.")]
    public float baseDriftSpeed = 0f;        // assigned by spawner
    [Tooltip("Direction of drift (set by spawner — points back toward entrance).")]
    public Vector3 driftDirection = Vector3.zero;
    [Tooltip("Distance traveled before the fan despawns or wraps. " +
             "Used to clean up off-track fans that have drifted away.")]
    public float maxDriftDistance = 2000f;

    private float spinDirection = 1f;
    private float scale;
    private Vector3 startPosition;

    void Start()
    {
        scale = transform.localScale.x;  // assume uniform scale
        if (randomizeSpinDirection && Random.value > 0.5f) spinDirection = -1f;
        startPosition = transform.position;
    }

    void Update()
    {
        // Spin around LOCAL X — the axis the blades rotate around.
        // Speed is inversely proportional to scale: bigger fan = slower spin.
        float spinSpeed = (baseSpinSpeed / scale) * spinDirection;
        transform.Rotate(Vector3.right, spinSpeed * Time.deltaTime, Space.Self);

        // Drift toward the entrance. Speed also inversely proportional to scale.
        if (baseDriftSpeed > 0f && driftDirection.sqrMagnitude > 0.0001f)
        {
            float driftSpeed = baseDriftSpeed / scale;
            transform.position += driftDirection.normalized * driftSpeed * Time.deltaTime;

            // Clean up fans that have drifted too far so we don't accumulate
            // hundreds of off-screen fans behind the player
            if (Vector3.Distance(transform.position, startPosition) > maxDriftDistance)
                Destroy(gameObject);
        }
    }
}