using System.Collections;
using UnityEngine;

/// <summary>
/// Applies a one-time forward velocity boost to the player car when the
/// hub scene loads. Boost is applied along the car's local Z+ direction
/// (positive forward) so the car launches in whatever direction it was
/// originally facing when placed in the scene.
/// </summary>
public class HubSpawnBoost : MonoBehaviour
{
    [Header("Boost Settings")]
    [Tooltip("Forward velocity to apply at hub load (mph).")]
    public float boostMph = 300f;
    [Tooltip("Tag of the player car to boost.")]
    public string playerTag = "Player";

    void Start()
    {
        StartCoroutine(BoostDelayed());
    }

    IEnumerator BoostDelayed()
    {
        // Wait two fixed updates so the car's Rigidbody is fully initialised
        // and any first-frame physics resolution has settled. Same pattern
        // used in TrackGenerator's spawn boost.
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        GameObject car = GameObject.FindWithTag(playerTag);
        if (car == null)
        {
            Debug.LogWarning("[HubSpawnBoost] No GameObject tagged Player found in hub scene.");
            yield break;
        }

        var rb = car.GetComponent<Rigidbody>();
        if (rb == null)
        {
            Debug.LogWarning("[HubSpawnBoost] Player car has no Rigidbody.");
            yield break;
        }

        // Reset velocity first so any physics activity from spawn doesn't
        // get added to the boost
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        yield return new WaitForFixedUpdate();

        // Apply boost along the car's LOCAL forward direction (transform.forward).
        // This is positive local Z by Unity convention, so wherever the car was
        // facing when placed in the scene, that's the direction it launches.
        const float MPH_TO_MS = 0.44704f;
        float boostMs = boostMph * MPH_TO_MS;

        rb.linearVelocity = car.transform.forward * boostMs;
    }
}