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

    [Header("Round Gating")]
    [Tooltip("Earliest game-loop round this spawner becomes active. 1 = always (default). " +
             "Set to 3 on the ChallengerCarSpawner so challengers only appear from round 3 on.")]
    public int minimumRound = 1;

    private bool spawned;

    void Start()
    {
        // Clients build their replicated-drone puppets from this prefab (registering it also
        // registers the drone's projectile prefab). Harmless no-op in single-player.
        NpcReplicator.RegisterPrefab(droneCarPrefab);
    }

    void Update()
    {
        // Multiplayer: the HOST is the one true AI sim; clients only render replicated puppets.
        if (MultiplayerWorld.IsClientOnly) return;

        if (spawned) return;
        if (GameLoopManager.Instance == null) return;
        if (GameLoopManager.Instance.RoundNumber < minimumRound) return;   // not active until this round

        float elapsed = GameLoopManager.Instance.roundDuration
                      - GameLoopManager.Instance.RoundTimeRemaining;

        // "Racing" phase differs by mode: single-player flips to InTrack when the player enters; the
        // multiplayer puppet loop keeps HubPortalActive for the whole round (per-player presence
        // isn't a global phase) — the round being active IS the racing window.
        bool racing = MultiplayerWorld.IsMultiplayerGame
            ? GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.HubPortalActive
            : GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.InTrack;
        if (!racing) return;
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

            // Multiplayer host: stream this racer to the clients. No-op otherwise.
            NpcReplicator.Track(drone, NpcKind.Drone, droneCarPrefab);
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
