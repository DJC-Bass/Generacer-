using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A single path through the track, sampled from start to finish along one
/// branch chain. Each drone gets its own TrackPath instance so different
/// drones can take different fork choices.
/// </summary>
public class TrackPath
{
    private List<Vector3> points = new List<Vector3>();
    private List<Vector3> tangents = new List<Vector3>();
    private List<float> distances = new List<float>();
    private float totalLength;

    public float TotalLength => totalLength;
    public bool IsReady => points.Count > 1;

    /// <summary>
    /// Build from a pre-sampled list of world-space points (from
    /// TrackGenerator.SampleRandomPath).
    /// </summary>
    public void BuildFromPoints(List<Vector3> sampledPoints)
    {
        points.Clear();
        tangents.Clear();
        distances.Clear();
        totalLength = 0f;

        if (sampledPoints == null || sampledPoints.Count < 2) return;

        for (int i = 0; i < sampledPoints.Count; i++)
        {
            points.Add(sampledPoints[i]);
            if (i == 0)
            {
                distances.Add(0f);
                tangents.Add(Vector3.forward);
            }
            else
            {
                Vector3 delta = sampledPoints[i] - sampledPoints[i - 1];
                float d = delta.magnitude;
                totalLength += d;
                distances.Add(totalLength);

                Vector3 dir = d > 0.0001f ? delta / d : Vector3.forward;
                tangents.Add(dir);
                // Update previous tangent too — first point's tangent matches first segment
                if (i == 1) tangents[0] = dir;
            }
        }
    }

    public void Sample(float distance, out Vector3 position, out Vector3 tangent)
    {
        if (points.Count == 0)
        {
            position = Vector3.zero;
            tangent = Vector3.forward;
            return;
        }

        distance = Mathf.Clamp(distance, 0f, totalLength);

        int lo = 0, hi = distances.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (distances[mid] < distance) lo = mid + 1;
            else hi = mid;
        }

        int i = Mathf.Max(0, lo - 1);
        int j = Mathf.Min(points.Count - 1, i + 1);

        float segLen = distances[j] - distances[i];
        float t = segLen > 0.0001f ? (distance - distances[i]) / segLen : 0f;

        position = Vector3.Lerp(points[i], points[j], t);
        tangent = tangents[Mathf.Min(i, tangents.Count - 1)];
    }
}