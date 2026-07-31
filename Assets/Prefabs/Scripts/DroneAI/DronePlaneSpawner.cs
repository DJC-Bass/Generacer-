using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scatters patrolling <see cref="DronePlane"/>s through the sky over the track. Placement mirrors the
/// FanSpawner (random points sampled off the generated road, with lateral scatter) with one deliberate
/// difference: the vertical offset is a POSITIVE band (<see cref="minVerticalOffset"/>..
/// <see cref="maxVerticalOffset"/>), so planes always spawn ABOVE the track rather than around it —
/// and because the band is rolled per plane, some cruise the skyline while others buzz the road.
///
/// Multiplayer: unlike fans (static, spawned locally from the round seed), planes are ACTIVE AI, so
/// they follow the DroneCarSpawner model — the HOST alone simulates them and streams them to clients
/// as puppets via NpcReplicator. Clients spawn nothing themselves.
/// </summary>
public class DronePlaneSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag your TrackGenerator GameObject here.")]
    public TrackGenerator trackGenerator;
    [Tooltip("The DronePlane prefab to spawn.")]
    public GameObject dronePlanePrefab;

    [Header("Distribution")]
    [Tooltip("How many planes to spawn across the track.")]
    public int planeCount = 10;
    [Tooltip("Lateral random offset around track points (units). Higher = planes patrol further " +
             "out to the sides of the road.")]
    public float lateralScatter = 300f;
    [Tooltip("Fraction of track to skip at the start (0.25 = no planes over the first 25%). Gives " +
             "the player a clean run-up before anything hunts them.")]
    [Range(0f, 0.5f)] public float startSkipFraction = 0.2f;

    [Header("Altitude Band (always ABOVE the track)")]
    [Tooltip("Lowest a plane can spawn above its track point (units). Keep clear of the road itself.")]
    public float minVerticalOffset = 120f;
    [Tooltip("Highest a plane can spawn above its track point (units). The spread between this and " +
             "the minimum is what gives the flight varied altitudes.")]
    public float maxVerticalOffset = 600f;

    [Header("Patrol Tuning")]
    [Tooltip("Smallest patrol-circle radius (units).")]
    public float minPatrolRadius = 150f;
    [Tooltip("Largest patrol-circle radius (units).")]
    public float maxPatrolRadius = 320f;
    [Tooltip("Slowest patrol cruising speed (units/s).")]
    public float minPatrolSpeed = 40f;
    [Tooltip("Fastest patrol cruising speed (units/s).")]
    public float maxPatrolSpeed = 75f;

    [Header("Layer")]
    [Tooltip("Layer assigned to each plane and ALL of its children. Must exist in Project Settings > " +
             "Tags and Layers, and be included in the planes' own vision mask so they can see each other.")]
    public string planeLayerName = "DronePlane";

    [Header("Round Gating")]
    [Tooltip("Earliest game-loop round this spawner becomes active. 1 = always.")]
    public int minimumRound = 1;

    private bool spawned;

    void Start()
    {
        // Clients build their replicated plane puppets from this prefab. Harmless in single-player.
        NpcReplicator.RegisterPrefab(dronePlanePrefab);
    }

    void Update()
    {
        // Multiplayer: the HOST is the one true AI sim; clients only render replicated puppets.
        if (MultiplayerWorld.IsClientOnly) return;

        if (spawned) return;
        if (trackGenerator == null) return;
        if (GameLoopManager.Instance == null) return;
        if (GameLoopManager.Instance.RoundNumber < minimumRound) return;

        // Same "racing" gate the DroneCarSpawner uses: in multiplayer the track is (pre)loaded and
        // frozen before GO, and DronePlane holds still while MultiplayerWorld.TrackFrozen is set.
        bool racing = MultiplayerWorld.IsMultiplayerGame
            ? MultiplayerWorld.RoundLoadedLocally
            : GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.InTrack;
        if (!racing) return;

        SpawnPlanes();
        spawned = true;
    }

    void SpawnPlanes()
    {
        var trackPoints = GatherTrackPoints();
        if (trackPoints.Count == 0)
        {
            Debug.LogWarning("[DronePlaneSpawner] No track points found — no planes spawned.");
            return;
        }

        int planeLayer = LayerMask.NameToLayer(planeLayerName);
        if (planeLayer < 0)
            Debug.LogWarning($"[DronePlaneSpawner] Layer '{planeLayerName}' not found in Tags and " +
                             "Layers — planes left on the prefab's layer.");

        int skipCount = Mathf.RoundToInt(trackPoints.Count * startSkipFraction);
        int startIndex = Mathf.Min(skipCount, trackPoints.Count - 1);

        for (int i = 0; i < planeCount; i++)
        {
            var point = trackPoints[Random.Range(startIndex, trackPoints.Count)];

            Vector3 right = Vector3.Cross(Vector3.up, point.forward).normalized;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;

            // Vertical offset is a POSITIVE band — planes patrol above the track, never below it.
            Vector3 spawnPos = point.position
                             + right * Random.Range(-lateralScatter, lateralScatter)
                             + Vector3.up * Random.Range(minVerticalOffset, maxVerticalOffset);

            Quaternion spawnRot = Quaternion.LookRotation(point.forward, Vector3.up);
            GameObject plane = Instantiate(dronePlanePrefab, spawnPos, spawnRot, transform);
            if (planeLayer >= 0) SetLayerRecursive(plane, planeLayer);

            var ai = plane.GetComponent<DronePlane>();
            if (ai == null) ai = plane.AddComponent<DronePlane>();
            ai.Initialize(spawnPos,
                          Random.Range(minPatrolRadius, maxPatrolRadius),
                          Random.Range(minPatrolSpeed, maxPatrolSpeed));

            // Multiplayer host: stream this plane to the clients. No-op otherwise. Drone kind = 15 Hz
            // with a solid puppet, which is what an airborne hunter wants.
            NpcReplicator.Track(plane, NpcKind.Drone, dronePlanePrefab);
        }

        Debug.Log($"[DronePlaneSpawner] Spawned {planeCount} patrolling drone planes.");
    }

    /// <summary>Pulls position+forward samples from every RoadEdge mesh in the generator — the same
    /// track-following reference points the FanSpawner scatters around.</summary>
    List<TrackPointSample> GatherTrackPoints()
    {
        var samples = new List<TrackPointSample>();

        foreach (Transform child in trackGenerator.transform)
        {
            if (!child.name.StartsWith("RoadEdge")) continue;

            var mf = child.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var verts = mf.sharedMesh.vertices;
            if (verts.Length < 4) continue;

            // The road mesh has 4 vertices per cross-section; sample every 8 sections for centreline points.
            for (int v = 0; v < verts.Length - 4; v += 32)
            {
                Vector3 center = (verts[v] + verts[v + 1]) * 0.5f;
                if (v + 5 >= verts.Length) break;
                Vector3 nextCenter = (verts[v + 4] + verts[v + 5]) * 0.5f;

                Vector3 forward = (nextCenter - center).normalized;
                if (forward.sqrMagnitude < 0.0001f) continue;

                samples.Add(new TrackPointSample
                {
                    position = child.TransformPoint(center),
                    forward = child.TransformDirection(forward)
                });
            }
        }

        return samples;
    }

    static void SetLayerRecursive(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
            SetLayerRecursive(child.gameObject, layer);
    }

    struct TrackPointSample
    {
        public Vector3 position;
        public Vector3 forward;
    }
}
