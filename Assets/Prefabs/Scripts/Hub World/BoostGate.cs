using UnityEngine;

/// <summary>
/// Trigger volume that boosts the player car when driven through. Boost
/// direction is determined by the car's forward direction at the moment
/// of contact — driving forward gives a forward boost, driving backward
/// gives a backward boost.
/// </summary>
[RequireComponent(typeof(Collider))]
public class BoostGate : MonoBehaviour
{
    [Header("Boost Settings")]
    [Tooltip("Speed of the boost in mph.")]
    public float boostMph = 300f;
    [Tooltip("Tag of the object to boost.")]
    public string playerTag = "Player";
    [Tooltip("Cooldown after triggering before this gate can fire again. " +
             "Prevents repeated boosts as the car passes through the trigger volume.")]
    public float cooldown = 1f;

    private float lastBoostTime = -999f;

    void Reset()
    {
        // Auto-configure as trigger when added in editor
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (Time.time - lastBoostTime < cooldown) return;

        // Walk up the hierarchy looking for the Player tag
        Transform t = other.transform;
        GameObject playerRoot = null;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                playerRoot = t.gameObject;
                break;
            }
            t = t.parent;
        }

        if (playerRoot == null) return;

        var rb = playerRoot.GetComponent<Rigidbody>();
        if (rb == null) return;

        ApplyBoost(playerRoot, rb);
        lastBoostTime = Time.time;
    }

    void ApplyBoost(GameObject player, Rigidbody rb)
    {
        const float MPH_TO_MS = 0.44704f;
        float boostMs = boostMph * MPH_TO_MS;

        // Determine if the car is moving forward or backward relative to its
        // own facing direction. Dot product of velocity and forward gives a
        // positive number when moving forward, negative when reversing.
        Vector3 forward = player.transform.forward;
        float forwardDot = Vector3.Dot(rb.linearVelocity, forward);

        // Direction of boost: same direction as the car's current motion
        // along its local Z axis. If the car is barely moving (e.g. drove in
        // sideways), default to forward so the boost still does something
        // sensible rather than zeroing out.
        Vector3 boostDirection;
        if (Mathf.Abs(forwardDot) < 0.5f)
        {
            // Car has near-zero forward/backward velocity — default to forward
            boostDirection = forward;
        }
        else
        {
            // Sign of forwardDot tells us which way along local Z the car is going
            boostDirection = forward * Mathf.Sign(forwardDot);
        }

        // Replace the local-Z component of velocity with the boost while
        // preserving any lateral or vertical motion. This way driving through
        // the gate with sideways or vertical momentum doesn't lose that motion.
        Vector3 currentVel = rb.linearVelocity;
        Vector3 forwardComponent = forward * Vector3.Dot(currentVel, forward);
        Vector3 perpendicularVel = currentVel - forwardComponent;

        rb.linearVelocity = perpendicularVel + boostDirection * boostMs;
    }
}