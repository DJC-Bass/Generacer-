using UnityEngine;

/// <summary>
/// Continuously spawns boulders at random positions on the ground floor.
/// Each boulder is launched with a random upward velocity that produces a
/// natural arcing trajectory.
/// </summary>
public class BoulderSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The boulder prefab — must have BoulderObstacle script and Rigidbody.")]
    public GameObject boulderPrefab;

    [Header("Spawn Region")]
    [Tooltip("Reference to a flat plane mesh that defines the spawn area. The plane's " +
             "world position and scale are read automatically — boulders spawn anywhere " +
             "within the plane's footprint, at the plane's Y position.")]
    public Transform spawnPlane;
    [Tooltip("Fallback bounds if no spawn plane is assigned (units, half-width).")]
    public float fallbackRangeX = 5000f;
    [Tooltip("Fallback bounds if no spawn plane is assigned (units, half-width).")]
    public float fallbackRangeZ = 15000f;
    [Tooltip("Fallback Y position if no spawn plane is assigned.")]
    public float fallbackGroundY = 0f;

    [Header("Spawn Rate")]
    [Tooltip("Average time between boulder spawns (seconds).")]
    public float spawnInterval = 0.5f;
    [Tooltip("Random variance on spawn interval — actual interval is " +
             "spawnInterval ± this fraction.")]
    [Range(0f, 1f)] public float spawnIntervalJitter = 0.5f;

    [Header("Boulder Size & Mass")]
    [Tooltip("Smallest boulder scale.")]
    public float minScale = 30f;
    [Tooltip("Largest boulder scale.")]
    public float maxScale = 120f;
    [Tooltip("Mass per unit scale — larger boulders get proportionally more mass. " +
             "Mass = scale × this value.")]
    public float massPerScale = 50f;

    [Header("Launch Power")]
    [Tooltip("Minimum upward launch speed (m/s).")]
    public float minLaunchSpeed = 80f;
    [Tooltip("Maximum upward launch speed (m/s).")]
    public float maxLaunchSpeed = 200f;
    [Tooltip("Maximum random horizontal drift added to each launch (m/s). " +
             "Adds variety so boulders don't all fly straight up.")]
    public float horizontalLaunchVariance = 15f;

    [Header("Spin")]
    [Tooltip("Minimum spin speed in radians per second.")]
    public float minSpinSpeed = 2f;
    [Tooltip("Maximum spin speed in radians per second.")]
    public float maxSpinSpeed = 8f;

    private float nextSpawnTime;

    void Start()
    {
        nextSpawnTime = Time.time + GetNextInterval();
    }

    void Update()
    {
        if (Time.time >= nextSpawnTime)
        {
            SpawnBoulder();
            nextSpawnTime = Time.time + GetNextInterval();
        }
    }

    float GetNextInterval()
    {
        float jitterRange = spawnInterval * spawnIntervalJitter;
        return spawnInterval + Random.Range(-jitterRange, jitterRange);
    }

    void SpawnBoulder()
    {
        if (boulderPrefab == null) return;

        // Determine spawn region — use the assigned plane if available, otherwise
        // fall back to the configured rectangle around this transform.
        Vector3 regionCenter;
        float halfX, halfZ;

        if (spawnPlane != null)
        {
            // Unity's default plane mesh is 10×10 units at scale 1, so the
            // half-width in world units is scale × 5.
            regionCenter = spawnPlane.position;
            halfX = spawnPlane.lossyScale.x * 5f;
            halfZ = spawnPlane.lossyScale.z * 5f;
        }
        else
        {
            regionCenter = new Vector3(transform.position.x, fallbackGroundY, transform.position.z);
            halfX = fallbackRangeX;
            halfZ = fallbackRangeZ;
        }

        // Random position within the region
        Vector3 spawnPos = regionCenter + new Vector3(
            Random.Range(-halfX, halfX),
            0f,
            Random.Range(-halfZ, halfZ));

        // Boulder properties
        float scale = Random.Range(minScale, maxScale);
        float mass = scale * massPerScale;

        float verticalSpeed = Random.Range(minLaunchSpeed, maxLaunchSpeed);
        Vector3 launchVelocity = new Vector3(
            Random.Range(-horizontalLaunchVariance, horizontalLaunchVariance),
            verticalSpeed,
            Random.Range(-horizontalLaunchVariance, horizontalLaunchVariance));

        Vector3 spinAxis = Random.onUnitSphere;
        float spinSpeed = Random.Range(minSpinSpeed, maxSpinSpeed);

        Quaternion spawnRot = Random.rotation;

        GameObject boulder = Instantiate(boulderPrefab, spawnPos, spawnRot, transform);

        var script = boulder.GetComponent<BoulderObstacle>();
        if (script != null)
            script.Launch(scale, mass, launchVelocity, spinAxis, spinSpeed);
    }

    // Gizmo so you can visualize the spawn region in the Scene view
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);

        Vector3 center;
        float halfX, halfZ;

        if (spawnPlane != null)
        {
            center = spawnPlane.position;
            halfX = spawnPlane.lossyScale.x * 5f;
            halfZ = spawnPlane.lossyScale.z * 5f;
        }
        else
        {
            center = new Vector3(transform.position.x, fallbackGroundY, transform.position.z);
            halfX = fallbackRangeX;
            halfZ = fallbackRangeZ;
        }

        Gizmos.DrawWireCube(center, new Vector3(halfX * 2f, 1f, halfZ * 2f));
    }
}