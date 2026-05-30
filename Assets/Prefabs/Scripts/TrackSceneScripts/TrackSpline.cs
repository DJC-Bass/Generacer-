using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A Catmull-Rom spline that smoothly passes through all control points.
/// Requires at least 4 points — the first and last are phantom tangent guides
/// and are not part of the visible path.
/// </summary>
public class TrackSpline
{
    public List<Vector3> ControlPoints { get; private set; } = new List<Vector3>();

    public void AddPoint(Vector3 point) => ControlPoints.Add(point);

    /// <summary>Returns a world-space point on the spline. t = 0 is start, t = 1 is end.</summary>
    public Vector3 GetPoint(float t)
    {
        t = Mathf.Clamp01(t);
        int n = ControlPoints.Count;
        if (n < 4) return n > 0 ? ControlPoints[0] : Vector3.zero;

        int numSections = n - 3;
        int section = Mathf.Min(Mathf.FloorToInt(t * numSections), numSections - 1);
        float u = t * numSections - section;

        Vector3 p0 = ControlPoints[section];
        Vector3 p1 = ControlPoints[section + 1];
        Vector3 p2 = ControlPoints[section + 2];
        Vector3 p3 = ControlPoints[section + 3];

        // Catmull-Rom formula
        return 0.5f * (
              2f * p1
            + (-p0 + p2) * u
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (u * u)
            + (-p0 + 3f * p1 - 3f * p2 + p3) * (u * u * u)
        );
    }

    /// <summary>Returns the forward direction of the track at t.</summary>
    public Vector3 GetTangent(float t)
    {
        const float d = 0.001f;
        return (GetPoint(Mathf.Min(t + d, 1f)) - GetPoint(Mathf.Max(t - d, 0f))).normalized;
    }

    /// <summary>Approximates the total arc length by sampling.</summary>
    public float EstimateLength(int samples = 100)
    {
        float len = 0f;
        Vector3 prev = GetPoint(0f);
        for (int i = 1; i <= samples; i++)
        {
            Vector3 curr = GetPoint(i / (float)samples);
            len += Vector3.Distance(prev, curr);
            prev = curr;
        }
        return len;
    }
}