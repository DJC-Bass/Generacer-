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

        SpawnAllGroups();
        spawned = true;
    }

    void SpawnAllGroups()
    {
        for (int g = 0; g < groupCount; g++)
        {
            int groupSize = Random.Range(minGroupSize, maxGroupSize + 1);
            float spawnFraction = Random.Range(minSpawnFraction, maxSpawnFraction);
            SpawnGroup(spawnFraction, groupSize);
        }
    }

    void SpawnGroup(float spawnFraction, int count)
    {
        // Get a random path from start to finish for this group
        var pathPoints = trackGenerator.SampleRandomPath();
        if (pathPoints == null || pathPoints.Count < 2) return;

        var path = new TrackPath();
        path.BuildFromPoints(pathPoints);

        if (!path.IsReady) return;

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

            var droneCar = drone.GetComponent<DroneCar>();
            if (droneCar == null) droneCar = drone.AddComponent<DroneCar>();
            droneCar.Initialize(path, droneDistance, speed);

            if (drone.GetComponent<DroneFadeIn>() == null)
                drone.AddComponent<DroneFadeIn>();
        }
    }
}