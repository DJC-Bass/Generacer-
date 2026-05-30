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

            Vector3 down = Vector3.down * thickness;

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

            tris[ti] = tL0; tris[ti + 1] = tL1; tris[ti + 2] = tR0;
            tris[ti + 3] = tR0; tris[ti + 4] = tL1; tris[ti + 5] = tR1;

            tris[ti + 6] = bL0; tris[ti + 7] = bR0; tris[ti + 8] = bL1;
            tris[ti + 9] = bR0; tris[ti + 10] = bR1; tris[ti + 11] = bL1;

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
    /// Builds a loop road mesh. Unlike the flat road builder, this orients each
    /// cross-section so the road's WIDTH axis stays aligned with the loop's
    /// rotation axis (the lateral horizontal direction), and the road SURFACE
    /// faces inward toward the loop center. This makes the car drive on the
    /// inside of the loop like a roller coaster.
    /// </summary>
    public static Mesh BuildLoopMesh(
            List<Vector3> points,
            Vector3 loopCenter,
            Vector3 rotationAxis,
            float roadWidth,
            float roadThickness,
            float uvTilingFactor = 0.04f,
            float flattenStartFraction = 1f)   // 1 = no flattening (default)
    {
        int sections = points.Count;
        if (sections < 2) return new Mesh();

        Vector3[] verts = new Vector3[sections * 4];
        Vector2[] uvs = new Vector2[sections * 4];
        int[] tris = new int[(sections - 1) * 24];

        Vector3 widthAxis = rotationAxis.normalized;
        float uvV = 0f;
        Vector3 prevPoint = points[0];

        for (int i = 0; i < sections; i++)
        {
            Vector3 center = points[i];
            float tt = i / (float)(sections - 1);

            if (i > 0) uvV += Vector3.Distance(prevPoint, center) * uvTilingFactor;
            prevPoint = center;

            // Surface normal: normally points toward the loop center (inward, so the
            // car drives on the inside of the loop). But over the last portion of
            // the spiral (after flattenStartFraction), blend the normal back toward
            // world up so the exit cross-section is FLAT — matching the post-loop
            // road's flat orientation and eliminating the wedge gap at the seam.
            Vector3 inwardNormal = (loopCenter - center).normalized;

            Vector3 surfaceNormal;
            if (flattenStartFraction < 1f && tt >= flattenStartFraction)
            {
                float blend = (tt - flattenStartFraction) / (1f - flattenStartFraction);
                blend = Mathf.SmoothStep(0f, 1f, blend);
                surfaceNormal = Vector3.Slerp(inwardNormal, Vector3.up, blend).normalized;
            }
            else
            {
                surfaceNormal = inwardNormal;
            }

            // Width axis stays along the rotation axis the whole way, so the road
            // never twists around its length — keeps seams on top and bottom.
            Vector3 topLeft = center - widthAxis * (roadWidth * 0.5f);
            Vector3 topRight = center + widthAxis * (roadWidth * 0.5f);
            Vector3 botLeft = topLeft - surfaceNormal * roadThickness;
            Vector3 botRight = topRight - surfaceNormal * roadThickness;

            int vi = i * 4;
            verts[vi] = topLeft;
            verts[vi + 1] = topRight;
            verts[vi + 2] = botLeft;
            verts[vi + 3] = botRight;

            uvs[vi] = new Vector2(0f, uvV);
            uvs[vi + 1] = new Vector2(1f, uvV);
            uvs[vi + 2] = new Vector2(0f, uvV);
            uvs[vi + 3] = new Vector2(1f, uvV);
        }

        // ... the triangle-winding loop and mesh assembly stay exactly the same

        int t = 0;
        for (int i = 0; i < sections - 1; i++)
        {
            int a = i * 4;
            int b = (i + 1) * 4;

            int tl0 = a, tr0 = a + 1, bl0 = a + 2, br0 = a + 3;
            int tl1 = b, tr1 = b + 1, bl1 = b + 2, br1 = b + 3;

            // Top (driving) surface — faces inward toward loop center
            tris[t++] = tl0; tris[t++] = tl1; tris[t++] = tr0;
            tris[t++] = tr0; tris[t++] = tl1; tris[t++] = tr1;

            // Bottom surface — faces outward
            tris[t++] = bl0; tris[t++] = br0; tris[t++] = bl1;
            tris[t++] = br0; tris[t++] = br1; tris[t++] = bl1;

            // Left wall
            tris[t++] = tl0; tris[t++] = bl0; tris[t++] = tl1;
            tris[t++] = bl0; tris[t++] = bl1; tris[t++] = tl1;

            // Right wall
            tris[t++] = tr0; tris[t++] = tr1; tris[t++] = br0;
            tris[t++] = br0; tris[t++] = tr1; tris[t++] = br1;
        }

        Mesh mesh = new Mesh { name = "LoopRoad" };
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
    /// Builds an emissive shoulder strip along one edge of a loop road. Uses the
    /// same inward-facing orientation as the loop road so the shoulders sit flush
    /// on the driving surface.
    /// </summary>
    public static Mesh BuildLoopShoulderMesh(
        List<Vector3> points,
        Vector3 loopCenter,
        Vector3 rotationAxis,
        float roadWidth,
        float shoulderWidth,
        float roadThickness,
        bool rightSide,
        float uvTilingFactor = 0.05f,
        float flattenStartFraction = 1f)
    {
        int sections = points.Count;
        if (sections < 2) return new Mesh();

        Vector3[] verts = new Vector3[sections * 4];
        Vector2[] uvs = new Vector2[sections * 4];
        int[] tris = new int[(sections - 1) * 24];

        Vector3 widthAxis = rotationAxis.normalized;
        float uvV = 0f;
        Vector3 prev = points[0];

        for (int i = 0; i < sections; i++)
        {
            Vector3 center = points[i];
            float tt = i / (float)(sections - 1);

            Vector3 inwardNormal = (loopCenter - center).normalized;
            Vector3 surfaceNormal;
            if (flattenStartFraction < 1f && tt >= flattenStartFraction)
            {
                float blend = (tt - flattenStartFraction) / (1f - flattenStartFraction);
                blend = Mathf.SmoothStep(0f, 1f, blend);
                surfaceNormal = Vector3.Slerp(inwardNormal, Vector3.up, blend).normalized;
            }
            else
            {
                surfaceNormal = inwardNormal;
            }
            // then use surfaceNormal wherever toCenter was used for the thickness extrusion

            if (i > 0) uvV += Vector3.Distance(prev, center) * uvTilingFactor;
            prev = center;

            // Inner edge sits at the road's outer edge; outer edge extends further
            float innerDist = roadWidth * 0.5f;
            float outerDist = roadWidth * 0.5f + shoulderWidth;

            Vector3 innerTop = rightSide
                ? center + widthAxis * innerDist
                : center - widthAxis * innerDist;
            Vector3 outerTop = rightSide
                ? center + widthAxis * outerDist
                : center - widthAxis * outerDist;

            Vector3 innerBot = innerTop - surfaceNormal * roadThickness;
            Vector3 outerBot = outerTop - surfaceNormal * roadThickness;

            int vi = i * 4;
            verts[vi] = innerTop;
            verts[vi + 1] = outerTop;
            verts[vi + 2] = innerBot;
            verts[vi + 3] = outerBot;

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
                tris[t++] = iT0; tris[t++] = iT1; tris[t++] = oT0;
                tris[t++] = oT0; tris[t++] = iT1; tris[t++] = oT1;
                tris[t++] = iB0; tris[t++] = oB0; tris[t++] = iB1;
                tris[t++] = oB0; tris[t++] = oB1; tris[t++] = iB1;
                tris[t++] = oT0; tris[t++] = oT1; tris[t++] = oB0;
                tris[t++] = oB0; tris[t++] = oT1; tris[t++] = oB1;
            }
            else
            {
                tris[t++] = iT0; tris[t++] = oT0; tris[t++] = iT1;
                tris[t++] = oT0; tris[t++] = oT1; tris[t++] = iT1;
                tris[t++] = iB0; tris[t++] = iB1; tris[t++] = oB0;
                tris[t++] = oB0; tris[t++] = iB1; tris[t++] = oB1;
                tris[t++] = oT0; tris[t++] = oB0; tris[t++] = oT1;
                tris[t++] = oB0; tris[t++] = oB1; tris[t++] = oT1;
            }
        }

        Mesh mesh = new Mesh { name = rightSide ? "LoopShoulderRight" : "LoopShoulderLeft" };
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

            Vector3 innerBot = innerTop + Vector3.down * thickness;
            Vector3 outerBot = outerTop + Vector3.down * thickness;

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
                // Top — normals up
                tris[ti] = iT0; tris[ti + 1] = iT1; tris[ti + 2] = oT0;
                tris[ti + 3] = oT0; tris[ti + 4] = iT1; tris[ti + 5] = oT1;

                // Bottom — normals down
                tris[ti + 6] = iB0; tris[ti + 7] = oB0; tris[ti + 8] = iB1;
                tris[ti + 9] = oB0; tris[ti + 10] = oB1; tris[ti + 11] = iB1;

                // Outer wall — normals point right (away from road)
                tris[ti + 12] = oT0; tris[ti + 13] = oT1; tris[ti + 14] = oB0;
                tris[ti + 15] = oB0; tris[ti + 16] = oT1; tris[ti + 17] = oB1;
            }
            else
            {
                // Top — flipped winding for left side so normals still point up
                tris[ti] = iT0; tris[ti + 1] = oT0; tris[ti + 2] = iT1;
                tris[ti + 3] = oT0; tris[ti + 4] = oT1; tris[ti + 5] = iT1;

                // Bottom — flipped for left side
                tris[ti + 6] = iB0; tris[ti + 7] = iB1; tris[ti + 8] = oB0;
                tris[ti + 9] = oB0; tris[ti + 10] = iB1; tris[ti + 11] = oB1;

                // Outer wall — normals point left
                tris[ti + 12] = oT0; tris[ti + 13] = oB0; tris[ti + 14] = oT1;
                tris[ti + 15] = oB0; tris[ti + 16] = oB1; tris[ti + 17] = oT1;
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