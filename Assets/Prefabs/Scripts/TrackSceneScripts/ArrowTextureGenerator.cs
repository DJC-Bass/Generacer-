using UnityEngine;

public static class ArrowTextureGenerator
{
    /// <summary>
    /// Generates a tileable road texture with:
    ///  - Solid white stripes at both outer edges
    ///  - Dashed yellow lane lines just inside the edge stripes
    ///  - Forward-pointing chevron arrows down the centre
    /// The texture tiles vertically (V-axis) along the road's length.
    /// </summary>
    public static Texture2D Generate(int size = 512)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };

        // Colour palette
        Color asphalt = new Color(0.16f, 0.16f, 0.18f);
        Color asphaltAlt = new Color(0.19f, 0.19f, 0.21f);   // Subtle variation for texture
        Color edgeLine = new Color(0.95f, 0.95f, 0.95f);   // Solid white edge
        Color laneLine = new Color(1f, 0.85f, 0.2f);    // Yellow dashed lane divider
        Color arrow = new Color(1f, 0.95f, 0.4f);    // Bright yellow arrow

        // Layout (as fractions of texture width)
        const float edgeStripeWidth = 0.05f;   // Outer 5% on each side = white stripe
        const float laneStripeOffset = 0.12f;   // Dashed yellow line lives at 12% from edge
        const float laneStripeWidth = 0.015f;  // Thin
        const float dashLengthV = 0.15f;   // Dash length in V (along the road)
        const float dashGapV = 0.15f;   // Gap between dashes

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Normalised coords: u runs left-right, v runs along the road
                float u = x / (float)size;
                float v = y / (float)size;

                // Distance from nearest edge in U
                float edgeDistU = Mathf.Min(u, 1f - u);

                // Default: asphalt with subtle band variation for visual texture
                Color c = (Mathf.PerlinNoise(u * 8f, v * 8f) > 0.5f) ? asphalt : asphaltAlt;

                // 1. Solid white edge stripes (always on)
                if (edgeDistU < edgeStripeWidth)
                {
                    c = edgeLine;
                }
                // 2. Dashed yellow lane dividers, just inside the edge stripes
                else if (Mathf.Abs(edgeDistU - laneStripeOffset) < laneStripeWidth)
                {
                    // Dash on/off along v
                    float vMod = (v % (dashLengthV + dashGapV));
                    if (vMod < dashLengthV) c = laneLine;
                }
                else
                {
                    // 3. Centre arrow — chevron pointing toward +V (forward)
                    // Arrow lives in middle 40% of V cycle so there's clear space between arrows
                    float vCycle = (v * 2f) % 1f;        // Two arrows per texture tile
                    if (vCycle > 0.3f && vCycle < 0.7f)
                    {
                        // Centre arrow horizontally — convert u to centre-relative
                        float uCenter = (u - 0.5f) * 2f;        // -1 to 1
                        float vCenter = (vCycle - 0.5f) * 2f;   // -1 to 1 within arrow band

                        // Chevron shape: V-line with adjustable thickness
                        // Equation: as we move outward in u, we move backward in v
                        float chevronV = -Mathf.Abs(uCenter) * 0.9f + 0.25f;
                        float dist = Mathf.Abs(vCenter - chevronV);

                        if (dist < 0.12f && Mathf.Abs(uCenter) < 0.4f)
                            c = arrow;
                    }
                }

                tex.SetPixel(x, y, c);
            }
        }

        tex.Apply();
        return tex;
    }
}