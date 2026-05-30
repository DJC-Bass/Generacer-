using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Continuously spawns lightning strikes at random points along the track.
/// Reads track geometry from RoadEdge children of the TrackGenerator, picks
/// a random sample point, and triggers a LightningStrike there.
/// </summary>
public class LightningSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag your TrackGenerator GameObject here.")]
    public TrackGenerator trackGenerator;
    [Tooltip("Material for the warning column. Use URP/Lit Transparent or " +
             "Unlit Transparent so alpha fade works.")]
    public Material warningMaterial;
    [Tooltip("Material for the actual lightning bolt. Should be brighter and " +
             "use Emission for a glowing effect.")]
    public Material boltMaterial;

    [Header("Strike Frequency")]
    [Tooltip("Average time between strikes (seconds).")]
    public float spawnInterval = 1.5f;
    [Tooltip("Random variance on interval — actual spawn time is " +
             "spawnInterval ± this fraction.")]
    [Range(0f, 1f)] public float intervalJitter = 0.4f;

    [Header("Strike Geometry")]
    [Tooltip("Minimum strike height (units).")]
    public float minStrikeHeight = 8000f;
    [Tooltip("Maximum strike height (units).")]
    public float maxStrikeHeight = 10000f;

    [Header("Strike Appearance")]
    public float warningRadius = 30f;
    public float boltThickness = 60f;
    public int boltSegments = 12;
    public float zigzagRadius = 200f;

    private List<Vector3> trackPoints;
    private float nextStrikeTime;

    [Header("Lateral Offset")]
    [Tooltip("Maximum distance the strike can spawn left or right of the track centerline (units). " +
         "Each strike picks a random offset within ±this value.")]
    public float lateralOffsetRange = 5f;

    void Start()
    {
        StartCoroutine(InitialiseDelayed());
    }

    System.Collections.IEnumerator InitialiseDelayed()
    {
        // Wait one frame for track generation to finish
        yield return null;

        GatherTrackPoints();
        nextStrikeTime = Time.time + GetInterval();
    }

    void Update()
    {
        if (trackSamples == null || trackSamples.Count == 0) return;

        if (Time.time >= nextStrikeTime)
        {
            SpawnStrike();
            nextStrikeTime = Time.time + GetInterval();
        }
    }

    float GetInterval()
    {
        float jitter = spawnInterval * intervalJitter;
        return spawnInterval + Random.Range(-jitter, jitter);
    }

    struct TrackPointSample
    {
        public Vector3 position;
        public Vector3 forward;
    }

    private List<TrackPointSample> trackSamples;

    void GatherTrackPoints()
    {
        trackSamples = new List<TrackPointSample>();

        // Pull centerline samples plus forward direction from each RoadEdge mesh.
        // Forward is computed by comparing this cross-section's center with the
        // next one, giving us the local track direction at each sample.
        foreach (Transform child in trackGenerator.transform)
        {
            if (!child.name.StartsWith("RoadEdge")) continue;

            var mf = child.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var verts = mf.sharedMesh.vertices;

            // Sample every 16 verts (~4 cross-sections), needs +4 ahead for forward
            for (int v = 0; v < verts.Length - 5; v += 16)
            {
                Vector3 left = verts[v];
                Vector3 right = verts[v + 1];
                Vector3 center = (left + right) * 0.5f;

                // Forward direction: difference between this center and the next
                Vector3 nextLeft = verts[v + 4];
                Vector3 nextRight = verts[v + 5];
                Vector3 nextCenter = (nextLeft + nextRight) * 0.5f;

                Vector3 forward = (nextCenter - center).normalized;
                if (forward.sqrMagnitude < 0.0001f) continue;

                trackSamples.Add(new TrackPointSample
                {
                    position = child.TransformPoint(center),
                    forward = child.TransformDirection(forward)
                });
            }
        }
    }

    void SpawnStrike()
    {
        var sample = trackSamples[Random.Range(0, trackSamples.Count)];

        // Compute lateral axis perpendicular to the track's forward direction.
        // Cross with world up gives a horizontal "right" vector even on banked tracks.
        Vector3 lateralAxis = Vector3.Cross(sample.forward, Vector3.up).normalized;
        if (lateralAxis.sqrMagnitude < 0.0001f) lateralAxis = Vector3.right;

        // Random offset within range, applied perpendicular to the track
        float offset = Random.Range(-lateralOffsetRange, lateralOffsetRange);
        Vector3 strikePoint = sample.position + lateralAxis * offset;

        var strikeObj = new GameObject("LightningStrike");
        strikeObj.transform.SetParent(transform);

        var strike = strikeObj.AddComponent<LightningStrike>();
        strike.strikeHeight = Random.Range(minStrikeHeight, maxStrikeHeight);
        strike.warningRadius = warningRadius;
        strike.boltThickness = boltThickness;
        strike.boltSegments = boltSegments;
        strike.zigzagRadius = zigzagRadius;

        strike.Trigger(strikePoint, warningMaterial, boltMaterial);
    }
}