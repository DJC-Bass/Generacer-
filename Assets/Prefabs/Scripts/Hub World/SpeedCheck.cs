using UnityEngine;

/// <summary>
/// Speed-gated barrier. The collider stays SOLID and blocks the player car unless the car is moving
/// FASTER than <see cref="minSpeedMph"/>, in which case it opens (becomes a trigger) and the car
/// passes straight through. Put this on the "SpeedCheck" object with its BoxCollider — a car at or
/// under the threshold bounces off, only a fast-enough car gets through.
///
/// The gate tracks the player's CURRENT speed every physics step, so it's already open by the time a
/// fast car reaches it (rather than reacting after contact, which would be too late).
/// </summary>
[RequireComponent(typeof(Collider))]
public class SpeedCheck : MonoBehaviour
{
    [Tooltip("The player car must be going FASTER than this (mph) to pass. At or under it, the barrier " +
             "is solid and blocks them.")]
    public float minSpeedMph = 400f;

    [Tooltip("Tag on the player car.")]
    public string playerTag = "Player";

    private Collider barrier;
    private CarController playerCar;

    void Awake()
    {
        barrier = GetComponent<Collider>();
        if (barrier != null) barrier.isTrigger = false;   // solid until a fast-enough player opens it
    }

    void FixedUpdate()
    {
        if (barrier == null) return;

        // Cache the player car; re-find it if it's gone (it can be spawned / swapped after scene load).
        if (playerCar == null)
        {
            var go = PlayerRegistry.LocalCar;
            if (go != null) playerCar = go.GetComponent<CarController>();
        }

        // Open only while the player is above the speed threshold; solid otherwise. Toggle only on a
        // change so we aren't churning the static collider every step.
        bool open = playerCar != null && playerCar.SpeedMph > minSpeedMph;
        if (barrier.isTrigger != open) barrier.isTrigger = open;
    }
}
