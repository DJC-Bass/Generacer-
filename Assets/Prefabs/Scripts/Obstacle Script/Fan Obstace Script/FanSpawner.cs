using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns fan obstacles scattered through the latter 3/4 of the track.
/// Reads track geometry from the TrackGenerator and places fans near
/// (but not on) the road, with random size, rotation, and drift.
/// </summary>
public class FanSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag your TrackGenerator GameObject here.")]
    public TrackGenerator trackGenerator;
    [Tooltip("The fan prefab to spawn.")]
    public GameObject fanPrefab;

    [Header("Distribution")]
    [Tooltip("How many fans to spawn across the track.")]
    public int fanCount = 60;
    [Tooltip("Lateral random offset around track points (units). Higher = fans " +
             "scattered further off the track.")]
    public float lateralScatter = 200f;
    [Tooltip("Vertical random offset around track points (units).")]
    public float verticalScatter = 80f;
    [Tooltip("Fraction of track to skip at the start (0.25 = first 25% has no fans). " +
             "Gives the player time to orient before encountering obstacles.")]
    [Range(0f, 0.5f)] public float startSkipFraction = 0.25f;

    [Header("Fan Size")]
    [Tooltip("Smallest fan scale.")]
    public float minScale = 8f;
    [Tooltip("Largest fan scale.")]
    public float maxScale = 30f;

    [Header("Fan Behaviour")]
    [Tooltip("Spin speed for a 1-unit-scale fan (degrees per second). " +
             "Actual fan spin speed is this divided by the fan's scale.")]
    public float baseSpinSpeed = 360f;
    [Tooltip("Drift speed for a 1-unit-scale fan (units per second). 0 = no drift.")]
    public float baseDriftSpeed = 50f;
    [Tooltip("Probability that a fan will drift instead of staying static.")]
    [Range(0f, 1f)] public float driftProbability = 0.5f;

    [Header("Layer")]
    [Tooltip("Layer index for fan obstacles (must be configured in Physics matrix).")]
    public int obstacleLayer = 9;

    void Start()
    {
        if (trackGenerator == null)
        {
            Debug.LogError("[FanSpawner] TrackGenerator reference is missing.");
            return;
        }

        // Wait for track to finish generating, then spawn fans
        StartCoroutine(SpawnFansDelayed());
    }

    System.Collections.IEnumerator SpawnFansDelayed()
    {
        // Track generation completes during Start, so wait one frame for it
        yield return null;
        SpawnFans();
    }

    void SpawnFans()
    {
        // Collect all sample points from all track edges
        var allTrackPoints = GatherTrackPoints();
        if (allTrackPoints.Count == 0) return;

        // Skip the first portion of the track so the player has clear lead-in
        int skipCount = Mathf.RoundToInt(allTrackPoints.Count * startSkipFraction);
        int startIndex = Mathf.Min(skipCount, allTrackPoints.Count - 1);

        // Direction from end portal back to entrance — fans drift this way
        Vector3 entranceDirection = -trackGenerator.transform.forward;
        // Use track geometry instead — average direction from finish toward start
        Vector3 driftBackward = (allTrackPoints[0].position
                               - allTrackPoints[allTrackPoints.Count - 1].position).normalized;

        for (int i = 0; i < fanCount; i++)
        {
            // Pick a random track point in the spawnable region
            int pointIndex = Random.Range(startIndex, allTrackPoints.Count);
            var trackPoint = allTrackPoints[pointIndex];

            // Pick a random offset from the track centerline
            // Lateral offset is perpendicular to the track's forward direction
            Vector3 trackForward = trackPoint.forward;
            Vector3 right = Vector3.Cross(Vector3.up, trackForward).normalized;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;

            float lateral = Random.Range(-lateralScatter, lateralScatter);
            float vertical = Random.Range(-verticalScatter, verticalScatter);

            Vector3 spawnPos = trackPoint.position
                             + right * lateral
                             + Vector3.up * vertical;

            // Random Y rotation only — fans always upright
            Quaternion spawnRot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            // Random scale within range
            float fanScale = Random.Range(minScale, maxScale);

            // Spawn
            GameObject fanInstance = Instantiate(fanPrefab, spawnPos, spawnRot, transform);
            fanInstance.transform.localScale = Vector3.one * fanScale;
            fanInstance.layer = obstacleLayer;

            // Recursively set the layer on all children too — matters for
            // fan blade meshes that have their own colliders
            SetLayerRecursive(fanInstance, obstacleLayer);

            // Configure behaviour
            var fan = fanInstance.GetComponent<FanObstacle>();
            if (fan == null) fan = fanInstance.AddComponent<FanObstacle>();

            fan.baseSpinSpeed = baseSpinSpeed;

            // Some fans drift, some stay still — adds variety
            if (Random.value < driftProbability)
            {
                fan.baseDriftSpeed = baseDriftSpeed;
                fan.driftDirection = driftBackward;
                fan.maxDriftDistance = lateralScatter * 4f;
            }
            else
            {
                fan.baseDriftSpeed = 0f;
            }
        }

        Debug.Log($"[FanSpawner] Spawned {fanCount} fans.");
    }

    /// <summary>
    /// Pulls position+forward samples from every TrackEdge in the generator.
    /// Uses the edge's sampled mesh points to get track-following positions
    /// and forward directions for fan placement reference.
    /// </summary>
    List<TrackPointSample> GatherTrackPoints()
    {
        var samples = new List<TrackPointSample>();

        // Grab all child RoadEdge objects from the TrackGenerator
        // and read positions from their MeshFilter sharedMesh vertices
        foreach (Transform child in trackGenerator.transform)
        {
            if (!child.name.StartsWith("RoadEdge")) continue;

            var mf = child.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var verts = mf.sharedMesh.vertices;
            if (verts.Length < 4) continue;

            // The road mesh has 4 vertices per cross-section (TL, TR, BL, BR).
            // Sample every 8 cross-sections (32 verts) to get track centerline points.
            for (int v = 0; v < verts.Length - 4; v += 32)
            {
                Vector3 left = verts[v];
                Vector3 right = verts[v + 1];
                Vector3 center = (left + right) * 0.5f;

                // Forward direction: difference between this center and the next
                if (v + 4 >= verts.Length) break;
                Vector3 nextLeft = verts[v + 4];
                Vector3 nextRight = verts[v + 5];
                Vector3 nextCenter = (nextLeft + nextRight) * 0.5f;

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

    void SetLayerRecursive(GameObject obj, int layer)
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