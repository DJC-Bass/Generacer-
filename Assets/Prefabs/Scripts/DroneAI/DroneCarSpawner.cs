using System.Collections;
using UnityEngine;

public class DroneCarSpawner : MonoBehaviour
{
    [Header("References")]
    public GameObject droneCarPrefab;
    public TrackGenerator trackGenerator;

    [Header("Timing")]
    public float spawnDelay = 60f;
    public float targetFinishTime = 240f;

    [Header("Group Composition")]
    public int groupCount = 4;
    public int minGroupSize = 2;
    public int maxGroupSize = 6;
    public float intraGroupSpacing = 6f;

    [Header("Spawn Region")]
    [Range(0f, 1f)] public float minSpawnFraction = 0.1f;
    [Range(0f, 1f)] public float maxSpawnFraction = 0.7f;

    [Header("Layer")]
    [Tooltip("Layer assigned to each spawned drone and ALL of its children. Keeps drones " +
             "off the Default layer so vision (which filters on the Drone layer) and the " +
             "car's ground raycasts can treat them cleanly. Must match a layer that exists " +
             "in Project Settings > Tags and Layers.")]
    public string droneLayerName = "Drone";

    // Resolved from droneLayerName each spawn; -1 if the layer doesn't exist.
    private int droneLayer = -1;

    private bool spawned;

    void Update()
    {
        if (spawned) return;
        if (GameLoopManager.Instance == null) return;

        float elapsed = GameLoopManager.Instance.roundDuration
                      - GameLoopManager.Instance.RoundTimeRemaining;

        if (GameLoopManager.Instance.CurrentPhase != GameLoopManager.Phase.InTrack) return;
        if (elapsed < spawnDelay) return;
        if (trackGenerator == null) return;

        // Mark done up front so Update doesn't kick off the coroutine again on the
        // frames it's still running. The groups spawn one-per-frame from here.
        spawned = true;
        StartCoroutine(SpawnGroupsStaggered());
    }

    /// <summary>
    /// Spawns drones one PER FRAME instead of all at once. Walks each group in turn
    /// (building that group's shared path once), then hands off to SpawnGroup, which
    /// instantiates a single drone per frame. Keeps every frame cheap even with a high
    /// group count, so a heavy spawn never stalls the game.
    /// </summary>
    IEnumerator SpawnGroupsStaggered()
    {
        droneLayer = LayerMask.NameToLayer(droneLayerName);
        if (droneLayer < 0)
            Debug.LogWarning($"[DroneSpawner] Layer \"{droneLayerName}\" not found in " +
                             "Tags and Layers — spawned drones will keep their prefab layer.");

        for (int g = 0; g < groupCount; g++)
        {
            int groupSize = Random.Range(minGroupSize, maxGroupSize + 1);
            float spawnFraction = Random.Range(minSpawnFraction, maxSpawnFraction);

            // Spawn this group's drones one-per-frame, then move on to the next group.
            yield return StartCoroutine(SpawnGroup(spawnFraction, groupSize));
        }
    }

    IEnumerator SpawnGroup(float spawnFraction, int count)
    {
        // Get a random path from start to finish for this group
        var pathPoints = trackGenerator.SampleRandomPath();
        if (pathPoints == null || pathPoints.Count < 2) yield break;

        var path = new TrackPath();
        path.BuildFromPoints(pathPoints);

        if (!path.IsReady) yield break;

        // Compute spawn distance along THIS path
        float groupStartDistance = path.TotalLength * spawnFraction;

        // Speed = remaining distance / remaining time, computed from this
        // path's actual length so drones travel at sensible speeds
        float remainingTime = targetFinishTime - spawnDelay;
        float distanceRemaining = path.TotalLength - groupStartDistance;
        float speed = distanceRemaining / Mathf.Max(remainingTime, 1f);

        Debug.Log($"[DroneSpawner] Group spawned at fraction {spawnFraction:F2}, " +
                  $"path length {path.TotalLength:F0}, speed {speed:F1} m/s");

        for (int i = 0; i < count; i++)
        {
            float droneDistance = groupStartDistance - (i * intraGroupSpacing);
            if (droneDistance < 0f) droneDistance = 0f;

            path.Sample(droneDistance, out Vector3 pos, out Vector3 tan);

            Quaternion rot = Quaternion.LookRotation(tan, Vector3.up);
            GameObject drone = Instantiate(droneCarPrefab, pos, rot, transform);

            // Put the whole drone (root + children) on the Drone layer.
            if (droneLayer >= 0) SetLayerRecursively(drone, droneLayer);

            var droneCar = drone.GetComponent<DroneCar>();
            if (droneCar == null) droneCar = drone.AddComponent<DroneCar>();
            droneCar.Initialize(path, droneDistance, speed);

            if (drone.GetComponent<DroneFadeIn>() == null)
                drone.AddComponent<DroneFadeIn>();

            // One drone per frame — the core of the stagger.
            yield return null;
        }
    }

    /// <summary>Sets the layer on a GameObject and every descendant under it.</summary>
    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }
}