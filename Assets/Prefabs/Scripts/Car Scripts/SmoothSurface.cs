using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reads a ray hit on a mesh as the smooth, curved surface that the mesh's flat panels stand in for.
///
/// The generated track is built from flat panels 12–20 m long. Each meets the next at a slight crease
/// (about 1° round a loop, up to a few degrees over hills and dips), and a rolled road's panels also fold
/// along their diagonals. Driven as-is, every crease is a sudden change in the road's angle: at 300 mph
/// it jolts the car's vertical speed by a couple of m/s, about ten times a second, which is the shake
/// over hills, through dips and round loops.
///
/// So instead of the flat panel's own normal, the normal is blended across the hit triangle from smoothed
/// normals at its corners (the average of the panels meeting at each corner, leaving out faces folded
/// sharply away, such as the slab's sides). The hit point is lifted onto the matching curved surface, and
/// the same blend tells how fast that normal turns as the car moves. The car can then follow the curve
/// the panels were cut from.
/// </summary>
public static class SmoothSurface
{
    /// <summary>The smooth surface at one ray hit.</summary>
    public struct Sample
    {
        /// <summary>Smooth surface normal at the hit.</summary>
        public Vector3 normal;
        /// <summary>The hit, moved off the flat panel onto the curved surface.</summary>
        public Vector3 point;

        // Corner normals and barycentric gradients of the hit triangle, for Turn.
        Vector3 nA, nB, nC, gA, gB, gC;
        float blendLength;

        /// <summary>How fast the normal turns (per second) for a point moving over the surface at
        /// <paramref name="velocity"/>: the change in the normal per second, at right angles to it.</summary>
        public Vector3 Turn(Vector3 velocity)
        {
            Vector3 d = nA * Vector3.Dot(gA, velocity) + nB * Vector3.Dot(gB, velocity) + nC * Vector3.Dot(gC, velocity);
            return (d - normal * Vector3.Dot(normal, d)) / blendLength;
        }

        /// <summary>The smooth surface at a hit with barycentric coordinates <paramref name="bary"/> on
        /// triangle A-B-C, given each corner's smoothed normal (all in world space). False when the
        /// triangle or the blend is degenerate.</summary>
        public static bool FromTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc,
                                        Vector3 bary, Vector3 hitPoint, out Sample s)
        {
            s = default;
            Vector3 blend = na * bary.x + nb * bary.y + nc * bary.z;
            float length = blend.magnitude;
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float twiceArea = cross.magnitude;
            if (length < 1e-3f || twiceArea < 1e-6f) return false;

            s.normal = blend / length;
            s.blendLength = length;
            s.nA = na; s.nB = nb; s.nC = nc;

            // Lift onto the curve: half the Phong-tessellation offset. For panels cut from a circular arc,
            // that puts the point back on the arc (to second order), so the height above the road changes
            // smoothly instead of kinking at every crease.
            float lift = 0.5f * (bary.x * Vector3.Dot(a - hitPoint, na)
                               + bary.y * Vector3.Dot(b - hitPoint, nb)
                               + bary.z * Vector3.Dot(c - hitPoint, nc));
            s.point = hitPoint + s.normal * lift;

            // Gradients of the barycentric coordinates across the triangle: each points from the opposite
            // edge toward its corner, with length 1/height.
            Vector3 face = cross / twiceArea;
            s.gA = Vector3.Cross(face, c - b) / twiceArea;
            s.gB = Vector3.Cross(face, a - c) / twiceArea;
            s.gC = Vector3.Cross(face, b - a) / twiceArea;
            return true;
        }
    }

    /// <summary>Faces folded further than this from the hit triangle are left out of its corner
    /// normals, so the road top never blends with the slab's sides (90°) while its creases (a few
    /// degrees) are smoothed.</summary>
    const float CreaseAngle = 45f;

    /// <summary>Smoothed normal at each triangle corner (mesh space), per mesh, built on first contact.
    /// Track meshes don't change after they are generated, so each is worked out once.</summary>
    static readonly Dictionary<Mesh, Vector3[]> cornerNormals = new Dictionary<Mesh, Vector3[]>();
    static readonly Dictionary<Mesh, (Vector3[] verts, int[] tris)> geometry = new Dictionary<Mesh, (Vector3[], int[])>();

    /// <summary>The smooth surface at <paramref name="hit"/>, or false when it isn't a readable,
    /// non-convex MeshCollider (use the hit's own normal then).</summary>
    public static bool TryGet(in RaycastHit hit, out Sample s)
    {
        s = default;
        var mc = hit.collider as MeshCollider;
        if (mc == null || mc.convex) return false;
        Mesh mesh = mc.sharedMesh;
        if (mesh == null || !mesh.isReadable) return false;

        if (!geometry.TryGetValue(mesh, out var g))
        {
            if (geometry.Count >= 128) Prune();
            g = (mesh.vertices, mesh.triangles);
            geometry[mesh] = g;
            cornerNormals[mesh] = BuildCornerNormals(g.verts, g.tris, CreaseAngle);
        }
        Vector3[] corners = cornerNormals[mesh];

        int t = hit.triangleIndex * 3;
        if (t < 0 || t + 2 >= g.tris.Length) return false;

        Transform tr = mc.transform;
        return Sample.FromTriangle(
            tr.TransformPoint(g.verts[g.tris[t]]), tr.TransformPoint(g.verts[g.tris[t + 1]]), tr.TransformPoint(g.verts[g.tris[t + 2]]),
            tr.TransformDirection(corners[t]), tr.TransformDirection(corners[t + 1]), tr.TransformDirection(corners[t + 2]),
            hit.barycentricCoordinate, hit.point, out s);
    }

    /// <summary>Forgets meshes that have since been destroyed (each new track builds new ones).</summary>
    static void Prune()
    {
        var dead = new List<Mesh>();
        foreach (Mesh m in geometry.Keys) if (m == null) dead.Add(m);
        foreach (Mesh m in dead) { geometry.Remove(m); cornerNormals.Remove(m); }
        if (geometry.Count >= 128) { geometry.Clear(); cornerNormals.Clear(); }
    }

    /// <summary>
    /// For every triangle corner, the average normal of the faces sharing that vertex, weighted by each
    /// face's angle at the vertex (so a corner shared by two thin triangles on one side and one wide one
    /// on the other still gets an even blend), counting only faces within <paramref name="creaseAngle"/>
    /// of the triangle itself.
    /// </summary>
    public static Vector3[] BuildCornerNormals(Vector3[] verts, int[] tris, float creaseAngle)
    {
        int triCount = tris.Length / 3;
        var faceNormal = new Vector3[triCount];
        var cornerAngle = new float[tris.Length];
        for (int f = 0; f < triCount; f++)
        {
            Vector3 a = verts[tris[f * 3]], b = verts[tris[f * 3 + 1]], c = verts[tris[f * 3 + 2]];
            faceNormal[f] = Vector3.Cross(b - a, c - a).normalized;
            cornerAngle[f * 3] = Vector3.Angle(b - a, c - a);
            cornerAngle[f * 3 + 1] = Vector3.Angle(c - b, a - b);
            cornerAngle[f * 3 + 2] = Vector3.Angle(a - c, b - c);
        }

        // Corners touching each vertex, packed flat: vertex v's run is [start[v], start[v + 1]).
        var start = new int[verts.Length + 1];
        foreach (int v in tris) start[v + 1]++;
        for (int v = 0; v < verts.Length; v++) start[v + 1] += start[v];
        var fill = (int[])start.Clone();
        var cornersAt = new int[tris.Length];
        for (int k = 0; k < tris.Length; k++) cornersAt[fill[tris[k]]++] = k;

        float minDot = Mathf.Cos(creaseAngle * Mathf.Deg2Rad);
        var result = new Vector3[tris.Length];
        var coverage = new float[tris.Length];   // degrees of surface round the corner's vertex that were averaged
        for (int k = 0; k < tris.Length; k++)
        {
            Vector3 own = faceNormal[k / 3];
            Vector3 sum = Vector3.zero;
            int v = tris[k];
            for (int j = start[v]; j < start[v + 1]; j++)
            {
                int other = cornersAt[j];
                Vector3 n = faceNormal[other / 3];
                if (Vector3.Dot(n, own) < minDot) continue;
                sum += n * cornerAngle[other];
                coverage[k] += cornerAngle[other];
            }
            result[k] = sum.sqrMagnitude > 1e-12f ? sum.normalized : own;
        }

        // A corner of the surface (a quarter or less of the way round its vertex, like each end of a road
        // piece) sees panels on one side only, so its average is really the normal halfway along the end
        // panel, half a crease short of the surface at the corner. Where the next piece's mesh carries on
        // from there, that half-crease mismatch on both sides would be a bump at the joint. Instead, carry the
        // curve on: the panel's normal sits midway between the corner and the neighbours one row back, so
        // the corner is the panel's normal plus the same step again.
        var extended = (Vector3[])result.Clone();
        for (int k = 0; k < tris.Length; k++)
        {
            if (coverage[k] >= 135f) continue;
            Vector3 own = faceNormal[k / 3];
            Vector3 back = Vector3.zero;
            int v = tris[k];
            for (int j = start[v]; j < start[v + 1]; j++)
            {
                int other = cornersAt[j];
                if (Vector3.Dot(faceNormal[other / 3], own) < minDot) continue;
                int f = other / 3 * 3;
                for (int m = f; m < f + 3; m++)
                    if (tris[m] != v && coverage[m] >= 135f) back += result[m];
            }
            if (back.sqrMagnitude < 1e-12f) continue;
            back.Normalize();
            if (Vector3.Dot(back, result[k]) < Mathf.Cos(15f * Mathf.Deg2Rad)) continue;   // not a gently curving surface
            extended[k] = (2f * result[k] - back).normalized;
        }
        return extended;
    }
}
