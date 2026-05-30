using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds two kinds of lightning meshes: a vertical column for the warning
/// indicator, and a zigzag bolt for the actual strike.
/// </summary>
public static class LightningMeshBuilder
{
    /// <summary>
    /// Builds a thin vertical rectangular box from base to topY. Acts as
    /// the warning column — a clean, straight pillar that signals where
    /// the strike will land.
    /// </summary>
    public static Mesh BuildWarningColumn(float radius, float height)
    {
        // Build a 4-sided prism. Square cross-section is fine; the column
        // reads as a beam from any angle thanks to the emissive material.
        Vector3[] verts = new Vector3[8];
        Vector2[] uvs = new Vector2[8];

        // Bottom 4 corners
        verts[0] = new Vector3(-radius, 0f, -radius);
        verts[1] = new Vector3(radius, 0f, -radius);
        verts[2] = new Vector3(radius, 0f, radius);
        verts[3] = new Vector3(-radius, 0f, radius);

        // Top 4 corners
        verts[4] = new Vector3(-radius, height, -radius);
        verts[5] = new Vector3(radius, height, -radius);
        verts[6] = new Vector3(radius, height, radius);
        verts[7] = new Vector3(-radius, height, radius);

        for (int i = 0; i < 8; i++)
            uvs[i] = new Vector2(verts[i].x / (radius * 2f) + 0.5f,
                                  verts[i].y / height);

        // Triangles for 4 side faces (no caps needed — strike comes from above)
        int[] tris = new int[24]
        {
            0, 4, 1,  1, 4, 5,   // front
            1, 5, 2,  2, 5, 6,   // right
            2, 6, 3,  3, 6, 7,   // back
            3, 7, 0,  0, 7, 4    // left
        };

        Mesh mesh = new Mesh { name = "LightningWarning" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Builds a zigzag bolt that travels from base position upward to topY,
    /// with random horizontal jitter at each segment.
    /// Generated as a thin extruded ribbon: each zigzag point becomes a
    /// pair of vertices forming a quad with the next pair.
    /// </summary>
    public static Mesh BuildLightningBolt(float thickness, float height,
                                           int segments,
                                           float zigzagRadius,
                                           Vector3 facingDir)
    {
        if (segments < 4) segments = 4;

        // Generate centerline points up the bolt with random horizontal jitter.
        // The first and last points are unjittered so the bolt connects cleanly
        // to its bottom (the strike point) and tapers off naturally at the top.
        var centerline = new List<Vector3>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float y = t * height;

            float jitterX = 0f, jitterZ = 0f;
            if (i > 0 && i < segments)
            {
                jitterX = Random.Range(-zigzagRadius, zigzagRadius);
                jitterZ = Random.Range(-zigzagRadius, zigzagRadius);
            }
            centerline.Add(new Vector3(jitterX, y, jitterZ));
        }

        // Each centerline point gets a left-right pair perpendicular to facingDir.
        // facingDir is the direction the bolt should "face" the camera/player —
        // typically world XZ so the ribbon reads as a flat lightning bolt.
        Vector3[] verts = new Vector3[(segments + 1) * 2];
        Vector2[] uvs = new Vector2[(segments + 1) * 2];
        int[] tris = new int[segments * 6];

        Vector3 perp = Vector3.Cross(facingDir, Vector3.up).normalized;
        if (perp.sqrMagnitude < 0.0001f) perp = Vector3.right;

        for (int i = 0; i <= segments; i++)
        {
            Vector3 c = centerline[i];
            int vi = i * 2;
            verts[vi] = c - perp * thickness;
            verts[vi + 1] = c + perp * thickness;
            uvs[vi] = new Vector2(0f, i / (float)segments);
            uvs[vi + 1] = new Vector2(1f, i / (float)segments);

            if (i < segments)
            {
                int ti = i * 6;
                tris[ti] = vi;
                tris[ti + 1] = vi + 2;
                tris[ti + 2] = vi + 1;
                tris[ti + 3] = vi + 1;
                tris[ti + 4] = vi + 2;
                tris[ti + 5] = vi + 3;
            }
        }

        Mesh mesh = new Mesh { name = "LightningBolt" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}