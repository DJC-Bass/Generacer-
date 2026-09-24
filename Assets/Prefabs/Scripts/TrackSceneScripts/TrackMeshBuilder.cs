using System.Collections.Generic;
using UnityEngine;

public static class TrackMeshBuilder
{
    public static Mesh BuildRoadMesh(
        TrackSpline spline,
        float width,
        int resolution,
        float thickness = 2f,
        float uvTilingFactor = 0.05f)
    {
        int sections = resolution + 1;

        Vector3[] verts = new Vector3[sections * 4];
        Vector2[] uvs = new Vector2[sections * 4];
        Vector4[] tangents = new Vector4[sections * 4];
        int[] tris = new int[resolution * 24];

        float uvV = 0f;
        Vector3 prevCenter = spline.GetPoint(0f);
        Vector3 fallbackRight = Vector3.right;

        for (int i = 0; i <= resolution; i++)
        {
            float t = i / (float)resolution;
            Vector3 center = spline.GetPoint(t);
            Vector3 tangent = spline.GetTangent(t);

            // Project tangent to horizontal plane before computing right.
            // This prevents the road from banking sideways on climbs/descents.
            Vector3 horizTangent = new Vector3(tangent.x, 0f, tangent.z);

            Vector3 right;
            if (horizTangent.sqrMagnitude > 0.0001f)
            {
                right = Vector3.Cross(horizTangent.normalized, Vector3.up).normalized;
                fallbackRight = right;
            }
            else
            {
                right = fallbackRight;  // vertical tangent — keep previous right
            }

            // Always extrude the slab DOWNWARD by the thickness magnitude so the
            // driving surface (tL/tR) is always the top of the box and all faces
            // wind outward. This makes the collider a watertight solid regardless
            // of the sign of `thickness`, so the car always rests on the top
            // surface (fixes phasing through with positive thickness).
            Vector3 down = Vector3.down * Mathf.Abs(thickness);

            if (i > 0) uvV += Vector3.Distance(prevCenter, center) * uvTilingFactor;
            prevCenter = center;

            Vector3 tL = center - right * (width * 0.5f);
            Vector3 tR = center + right * (width * 0.5f);
            Vector3 bL = tL + down;
            Vector3 bR = tR + down;

            int vi = i * 4;
            verts[vi] = tL;
            verts[vi + 1] = tR;
            verts[vi + 2] = bL;
            verts[vi + 3] = bR;

            uvs[vi] = new Vector2(0f, uvV);
            uvs[vi + 1] = new Vector2(1f, uvV);
            uvs[vi + 2] = new Vector2(0f, uvV);
            uvs[vi + 3] = new Vector2(1f, uvV);

            Vector4 tan = new Vector4(tangent.x, tangent.y, tangent.z, -1f);
            tangents[vi] = tan;
            tangents[vi + 1] = tan;
            tangents[vi + 2] = tan;
            tangents[vi + 3] = tan;
        }

        for (int i = 0; i < resolution; i++)
        {
            int vi = i * 4;
            int ti = i * 24;

            int tL0 = vi, tR0 = vi + 1, bL0 = vi + 2, bR0 = vi + 3;
            int tL1 = vi + 4, tR1 = vi + 5, bL1 = vi + 6, bR1 = vi + 7;

            // Top surface — faces UP (drivable). Winding swapped to match the
            // downward slab extrusion so the normal points +Y.
            tris[ti] = tL0; tris[ti + 1] = tR0; tris[ti + 2] = tL1;
            tris[ti + 3] = tR0; tris[ti + 4] = tR1; tris[ti + 5] = tL1;

            // Bottom surface — faces DOWN.
            tris[ti + 6] = bL0; tris[ti + 7] = bL1; tris[ti + 8] = bR0;
            tris[ti + 9] = bR0; tris[ti + 10] = bL1; tris[ti + 11] = bR1;

            tris[ti + 12] = tL0; tris[ti + 13] = bL0; tris[ti + 14] = tL1;
            tris[ti + 15] = tL1; tris[ti + 16] = bL0; tris[ti + 17] = bL1;

            tris[ti + 18] = tR0; tris[ti + 19] = tR1; tris[ti + 20] = bR0;
            tris[ti + 21] = bR0; tris[ti + 22] = tR1; tris[ti + 23] = bR1;
        }

        Mesh mesh = new Mesh { name = "RoadMesh" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.tangents = tangents;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Builds a loop road mesh from a continuous centerline using a rotation-
    /// minimizing frame. No constant width axis and no loopCenter normal, so the
    /// road never twists along its length and the end cross-sections come out
    /// horizontal — matching the flat road at both entry and exit.
    /// </summary>
    public static Mesh BuildLoopMeshExplicit(
           List<Vector3> points,
           List<Vector3> normals,
           float roadWidth,
           float roadThickness,
           float uvTilingFactor = 0.04f)
    {
        int sections = points.Count;
        if (sections < 2) return new Mesh();

        Vector3[] verts = new Vector3[sections * 4];
        Vector2[] uvs = new Vector2[sections * 4];
        int[] tris = new int[(sections - 1) * 24];

        float uvV = 0f;
        Vector3 prev = points[0];

        for (int i = 0; i < sections; i++)
        {
            Vector3 center = points[i];
            Vector3 normal = normals[i];

            // tangent for the width axis
            Vector3 tan;
            if (i == 0) tan = points[1] - points[0];
            else if (i == sections - 1) tan = points[sections - 1] - points[sections - 2];
            else tan = points[i + 1] - points[i - 1];
            tan = tan.sqrMagnitude > 1e-12f ? tan.normalized : Vector3.forward;

            Vector3 width = Vector3.Cross(tan, normal).normalized;

            if (i > 0) uvV += Vector3.Distance(prev, center) * uvTilingFactor;
            prev = center;

            Vector3 topLeft = center - width * (roadWidth * 0.5f);
            Vector3 topRight = center + width * (roadWidth * 0.5f);
            Vector3 botLeft = topLeft - normal * Mathf.Abs(roadThickness);
            Vector3 botRight = topRight - normal * Mathf.Abs(roadThickness);

            int vi = i * 4;
            verts[vi] = topLeft; verts[vi + 1] = topRight;
            verts[vi + 2] = botLeft; verts[vi + 3] = botRight;
            uvs[vi] = new Vector2(0f, uvV);
            uvs[vi + 1] = new Vector2(1f, uvV);
            uvs[vi + 2] = new Vector2(0f, uvV);
            uvs[vi + 3] = new Vector2(1f, uvV);
        }

        int t = 0;
        for (int i = 0; i < sections - 1; i++)
        {
            int a = i * 4, b = (i + 1) * 4;
            int tl0 = a, tr0 = a + 1, bl0 = a + 2, br0 = a + 3;
            int tl1 = b, tr1 = b + 1, bl1 = b + 2, br1 = b + 3;
            // Top (driving) surface — faces along the surface normal (loop interior)
            tris[t++] = tl0; tris[t++] = tr0; tris[t++] = tl1;
            tris[t++] = tr0; tris[t++] = tr1; tris[t++] = tl1;
            // Bottom surface
            tris[t++] = bl0; tris[t++] = bl1; tris[t++] = br0;
            tris[t++] = br0; tris[t++] = bl1; tris[t++] = br1;
            tris[t++] = tl0; tris[t++] = bl0; tris[t++] = tl1;
            tris[t++] = bl0; tris[t++] = bl1; tris[t++] = tl1;
            tris[t++] = tr0; tris[t++] = tr1; tris[t++] = br0;
            tris[t++] = br0; tris[t++] = tr1; tris[t++] = br1;
        }

        Mesh mesh = new Mesh { name = "LoopRoadExplicit" };
        mesh.indexFormat = sections * 4 > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    public static Mesh BuildLoopShoulderMeshExplicit(
        List<Vector3> points,
        List<Vector3> normals,
        float roadWidth,
        float shoulderWidth,
        float roadThickness,
        bool rightSide,
        float uvTilingFactor = 0.05f)
    {
        int sections = points.Count;
        if (sections < 2) return new Mesh();

        Vector3[] verts = new Vector3[sections * 4];
        Vector2[] uvs = new Vector2[sections * 4];
        int[] tris = new int[(sections - 1) * 18];   // top + bottom + outer wall

        float uvV = 0f;
        Vector3 prev = points[0];
        float innerDist = roadWidth * 0.5f;
        float outerDist = roadWidth * 0.5f + shoulderWidth;

        for (int i = 0; i < sections; i++)
        {
            Vector3 center = points[i];
            Vector3 normal = normals[i];

            Vector3 tan;
            if (i == 0) tan = points[1] - points[0];
            else if (i == sections - 1) tan = points[sections - 1] - points[sections - 2];
            else tan = points[i + 1] - points[i - 1];
            tan = tan.sqrMagnitude > 1e-12f ? tan.normalized : Vector3.forward;

            Vector3 width = Vector3.Cross(tan, normal).normalized;

            if (i > 0) uvV += Vector3.Distance(prev, center) * uvTilingFactor;
            prev = center;

            Vector3 innerTop = rightSide ? center + width * innerDist : center - width * innerDist;
            Vector3 outerTop = rightSide ? center + width * outerDist : center - width * outerDist;
            Vector3 innerBot = innerTop - normal * Mathf.Abs(roadThickness);
            Vector3 outerBot = outerTop - normal * Mathf.Abs(roadThickness);

            int vi = i * 4;
            verts[vi] = innerTop; verts[vi + 1] = outerTop;
            verts[vi + 2] = innerBot; verts[vi + 3] = outerBot;
            uvs[vi] = new Vector2(0f, uvV);
            uvs[vi + 1] = new Vector2(1f, uvV);
            uvs[vi + 2] = new Vector2(0f, uvV);
            uvs[vi + 3] = new Vector2(1f, uvV);
        }

        int t = 0;
        for (int i = 0; i < sections - 1; i++)
        {
            int a = i * 4, b = (i + 1) * 4;
            int iT0 = a, oT0 = a + 1, iB0 = a + 2, oB0 = a + 3;
            int iT1 = b, oT1 = b + 1, iB1 = b + 2, oB1 = b + 3;
            if (rightSide)
            {
                tris[t++] = iT0; tris[t++] = oT0; tris[t++] = iT1;
                tris[t++] = oT0; tris[t++] = oT1; tris[t++] = iT1;
                tris[t++] = iB0; tris[t++] = iB1; tris[t++] = oB0;
                tris[t++] = oB0; tris[t++] = iB1; tris[t++] = oB1;
                tris[t++] = oT0; tris[t++] = oB0; tris[t++] = oT1;
                tris[t++] = oB0; tris[t++] = oB1; tris[t++] = oT1;
            }
            else
            {
                tris[t++] = iT0; tris[t++] = iT1; tris[t++] = oT0;
                tris[t++] = oT0; tris[t++] = iT1; tris[t++] = oT1;
                tris[t++] = iB0; tris[t++] = oB0; tris[t++] = iB1;
                tris[t++] = oB0; tris[t++] = oB1; tris[t++] = iB1;
                tris[t++] = oT0; tris[t++] = oT1; tris[t++] = oB0;
                tris[t++] = oB0; tris[t++] = oT1; tris[t++] = oB1;
            }
        }

        Mesh mesh = new Mesh { name = rightSide ? "LoopShoulderRightExplicit" : "LoopShoulderLeftExplicit" };
        mesh.indexFormat = sections * 4 > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Builds a flat shoulder strip that extrudes outward from one side of
    /// the road. Sweeps the road's spline and emits a thin horizontal strip
    /// along the edge — same length as the road but offset to the side.
    ///
    /// rightSide = true → strip extends to the car's right
    /// rightSide = false → strip extends to the left
    /// </summary>
    /// <summary>
    /// Builds a thick shoulder slab that extrudes outward from one side of
    /// the road. Mirrors the road's box-shape (top + bottom + outer wall + end caps)
    /// so the shoulder matches the road's thickness and is visible from above.
    /// </summary>
    public static Mesh BuildShoulderMesh(
        TrackSpline spline,
        float roadWidth,
        float shoulderWidth,
        int resolution,
        float thickness,
        bool rightSide,
        float uvTilingFactor = 0.05f)
    {
        int sections = resolution + 1;

        // 4 verts per cross-section: inner-top, outer-top, inner-bot, outer-bot
        Vector3[] verts = new Vector3[sections * 4];
        Vector2[] uvs = new Vector2[sections * 4];
        Vector4[] tangents = new Vector4[sections * 4];

        // Per segment: top(6) + bottom(6) + outer wall(6) = 18 indices
        int[] tris = new int[resolution * 18];

        float uvV = 0f;
        Vector3 prevCenter = spline.GetPoint(0f);
        Vector3 fallbackRight = Vector3.right;

        for (int i = 0; i <= resolution; i++)
        {
            float t = i / (float)resolution;
            Vector3 center = spline.GetPoint(t);
            Vector3 tangent = spline.GetTangent(t);

            Vector3 horizTangent = new Vector3(tangent.x, 0f, tangent.z);
            Vector3 right;
            if (horizTangent.sqrMagnitude > 0.0001f)
            {
                right = Vector3.Cross(horizTangent.normalized, Vector3.up).normalized;
                fallbackRight = right;
            }
            else
            {
                right = fallbackRight;
            }

            if (i > 0) uvV += Vector3.Distance(prevCenter, center) * uvTilingFactor;
            prevCenter = center;

            // Inner edge sits at the road's outer edge; outer edge extends shoulderWidth further
            Vector3 innerTop, outerTop;
            if (rightSide)
            {
                innerTop = center + right * (roadWidth * 0.5f);
                outerTop = center + right * (roadWidth * 0.5f + shoulderWidth);
            }
            else
            {
                innerTop = center - right * (roadWidth * 0.5f);
                outerTop = center - right * (roadWidth * 0.5f + shoulderWidth);
            }

            Vector3 innerBot = innerTop + Vector3.down * Mathf.Abs(thickness);
            Vector3 outerBot = outerTop + Vector3.down * Mathf.Abs(thickness);

            int vi = i * 4;
            verts[vi] = innerTop;
            verts[vi + 1] = outerTop;
            verts[vi + 2] = innerBot;
            verts[vi + 3] = outerBot;

            uvs[vi] = new Vector2(0f, uvV);
            uvs[vi + 1] = new Vector2(1f, uvV);
            uvs[vi + 2] = new Vector2(0f, uvV);
            uvs[vi + 3] = new Vector2(1f, uvV);

            Vector4 tan = new Vector4(tangent.x, tangent.y, tangent.z, -1f);
            tangents[vi] = tan;
            tangents[vi + 1] = tan;
            tangents[vi + 2] = tan;
            tangents[vi + 3] = tan;
        }

        for (int i = 0; i < resolution; i++)
        {
            int vi = i * 4;
            int ti = i * 18;

            int iT0 = vi, oT0 = vi + 1, iB0 = vi + 2, oB0 = vi + 3;
            int iT1 = vi + 4, oT1 = vi + 5, iB1 = vi + 6, oB1 = vi + 7;

            // Triangle winding flips depending on side so normals always point
            // the right way: top normals up, bottom down, outer wall outward.
            if (rightSide)
            {
                // Top — normals up (winding swapped for downward extrusion)
                tris[ti] = iT0; tris[ti + 1] = oT0; tris[ti + 2] = iT1;
                tris[ti + 3] = oT0; tris[ti + 4] = oT1; tris[ti + 5] = iT1;

                // Bottom — normals down
                tris[ti + 6] = iB0; tris[ti + 7] = iB1; tris[ti + 8] = oB0;
                tris[ti + 9] = oB0; tris[ti + 10] = iB1; tris[ti + 11] = oB1;

                // Outer wall — normals point right (away from road)
                tris[ti + 12] = oT0; tris[ti + 13] = oB0; tris[ti + 14] = oT1;
                tris[ti + 15] = oB0; tris[ti + 16] = oB1; tris[ti + 17] = oT1;
            }
            else
            {
                // Top — normals up (winding swapped for downward extrusion)
                tris[ti] = iT0; tris[ti + 1] = iT1; tris[ti + 2] = oT0;
                tris[ti + 3] = oT0; tris[ti + 4] = iT1; tris[ti + 5] = oT1;

                // Bottom — normals down
                tris[ti + 6] = iB0; tris[ti + 7] = oB0; tris[ti + 8] = iB1;
                tris[ti + 9] = oB0; tris[ti + 10] = oB1; tris[ti + 11] = iB1;

                // Outer wall — normals point left
                tris[ti + 12] = oT0; tris[ti + 13] = oT1; tris[ti + 14] = oB0;
                tris[ti + 15] = oB0; tris[ti + 16] = oT1; tris[ti + 17] = oB1;
            }
        }

        Mesh mesh = new Mesh { name = rightSide ? "ShoulderRight" : "ShoulderLeft" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.tangents = tangents;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Builds the VISUAL mesh of a raised shoulder: the flat shoulder slab (from
    /// BuildShoulderMesh / BuildLoopShoulderMeshExplicit) with its top lifted `railHeight`
    /// straight up, plus an inner wall facing the road — a low, continuous curb rail. Each
    /// face (top, inner wall, outer wall, bottom) gets its own vertices so the edges shade
    /// sharp. Reads the flat slab's vertex layout (4 per cross-section: inner-top, outer-top,
    /// inner-bot, outer-bot), so it works the same for ordinary and loop shoulders — "up" is
    /// the slab's bottom-to-top direction (world up on ordinary road, the surface normal on
    /// loops). The flat slab stays the collider, so the rail is visual only.
    /// </summary>
    public static Mesh BuildRaisedShoulderMesh(Vector3[] slabVerts, Vector2[] slabUVs, float railHeight)
    {
        int sections = slabVerts.Length / 4;
        if (sections < 2) return new Mesh();

        // 8 vertices per cross-section: one pair for each face strip.
        const int Top = 0, Inner = 2, Outer = 4, Bottom = 6;
        Vector3[] verts = new Vector3[sections * 8];
        Vector2[] uvs = new Vector2[sections * 8];
        int[] tris = new int[(sections - 1) * 24];   // 4 faces x 2 triangles per segment

        for (int i = 0; i < sections; i++)
        {
            int s = i * 4, d = i * 8;
            Vector3 iT = slabVerts[s], oT = slabVerts[s + 1];
            Vector3 iB = slabVerts[s + 2], oB = slabVerts[s + 3];

            Vector3 up = SlabUp(slabVerts, s);
            Vector3 iR = iT + up * railHeight;   // raised inner-top
            Vector3 oR = oT + up * railHeight;   // raised outer-top

            verts[d + Top] = iR;    verts[d + Top + 1] = oR;
            verts[d + Inner] = iT;  verts[d + Inner + 1] = iR;   // road surface up to the rail top
            verts[d + Outer] = oR;  verts[d + Outer + 1] = oB;
            verts[d + Bottom] = oB; verts[d + Bottom + 1] = iB;

            float v = slabUVs.Length > s ? slabUVs[s].y : 0f;
            for (int k = 0; k < 8; k += 2)
            {
                uvs[d + k] = new Vector2(0f, v);
                uvs[d + k + 1] = new Vector2(1f, v);
            }
        }

        int t = 0;
        for (int i = 0; i < sections - 1; i++)
        {
            int s0 = i * 4, s1 = (i + 1) * 4;
            Vector3 up = SlabUp(slabVerts, s0) + SlabUp(slabVerts, s1);
            Vector3 across = (slabVerts[s0 + 1] + slabVerts[s1 + 1])
                           - (slabVerts[s0] + slabVerts[s1]);   // outward, away from the road

            int d0 = i * 8, d1 = (i + 1) * 8;
            t = AddStripQuad(tris, t, verts, d0 + Top, d1 + Top, up);
            t = AddStripQuad(tris, t, verts, d0 + Inner, d1 + Inner, -across);
            t = AddStripQuad(tris, t, verts, d0 + Outer, d1 + Outer, across);
            t = AddStripQuad(tris, t, verts, d0 + Bottom, d1 + Bottom, -up);
        }

        Mesh mesh = new Mesh { name = "RaisedShoulder" };
        mesh.indexFormat = verts.Length > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Bottom-to-top direction of the shoulder slab at the cross-section starting at vertex `vi`.</summary>
    static Vector3 SlabUp(Vector3[] slabVerts, int vi)
    {
        Vector3 d = slabVerts[vi] - slabVerts[vi + 2];   // inner-top minus inner-bot
        return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
    }

    /// <summary>
    /// Writes the quad between vertex pair (a, a+1) and the next cross-section's pair
    /// (b, b+1), wound so it faces `outward`. Returns the next free triangle index.
    /// </summary>
    static int AddStripQuad(int[] tris, int t, Vector3[] verts, int a, int b, Vector3 outward)
    {
        // Around the quad: a -> a+1 -> b+1 -> b.
        Vector3 n = Vector3.Cross(verts[a + 1] - verts[a], verts[b + 1] - verts[a]);
        if (Vector3.Dot(n, outward) >= 0f)
        {
            tris[t++] = a; tris[t++] = a + 1; tris[t++] = b + 1;
            tris[t++] = a; tris[t++] = b + 1; tris[t++] = b;
        }
        else
        {
            tris[t++] = a; tris[t++] = b + 1; tris[t++] = a + 1;
            tris[t++] = a; tris[t++] = b; tris[t++] = b + 1;
        }
        return t;
    }

    public static Mesh BuildJunctionMesh(float radius, int segments = 24)
    {
        Vector3[] verts = new Vector3[segments + 1];
        int[] tris = new int[segments * 3];

        verts[0] = Vector3.zero;
        for (int i = 0; i < segments; i++)
        {
            float angle = i / (float)segments * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            int next = (i + 1) % segments + 1;
            tris[i * 3] = 0;
            tris[i * 3 + 1] = next;
            tris[i * 3 + 2] = i + 1;
        }

        Mesh mesh = new Mesh { name = "JunctionMesh" };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}