// Full-screen height fog for the TrackScene (TrackFog). Each pixel of scenery fades into the SKY IN ITS
// OWN DIRECTION — sampled from a cube render of this round's sky — rather than into one fog colour, so the
// far track melts into whatever is behind it: the green below the horizon, the white band at it, the
// paler sky above.
//
// The sky's GROUND is hazed here too: just under the horizon it fades up into the sky's horizon colour
// (_TrackFogHaze) instead of meeting it in a hard line, so far track just under the line fades into that
// haze rather than into dark ground beneath a near-white edge. Done in this pass rather than in the sky
// shader so it doesn't light the scene — the track's ambient light is taken from the sky material.
//
// Thickness follows altitude: _TrackFogDensity.x at the reference altitude, halving every
// ln2/_TrackFogDensity.y units higher and doubling every that many lower. The fog a ray collects is that
// density integrated along it, from _TrackFogStart out — exactly, since it's an exponential in height.
// Referenced from Resources/TrackFogSettings so builds include it.
Shader "Hidden/Generacer/TrackHeightFog"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "TrackHeightFog"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURECUBE(_TrackFogSky);
            SAMPLER(sampler_TrackFogSky);

            float4 _TrackFogDensity;   // x: density at the reference altitude (per unit)  y: height falloff (per unit)
                                       // z: reference altitude                              w: max opacity
            float _TrackFogStart;      // clear air before the fog starts gathering (units from the camera)
            float _TrackFogSkyMip;     // a slightly blurred sky: clouds and sun soften in the haze
            float4 _TrackFogHaze;      // rgb: the sky's horizon colour, as drawn  w: sine of the haze depth (0 = none)
            float3 _TrackFogGround;    // the sky's plain ground colour, as drawn

            // The sky in direction y (height of the unit view direction) with its ground hazed: all horizon
            // colour at the line, thinning to none _TrackFogHaze.w below it.
            //
            // ProceduralSkyClouds blends its ground into its sky across y -0.03..0.03, and the haze has to
            // replace exactly the GROUND's share of that blend, or a dark line is left along the horizon.
            // Below the line the sky's share is pure horizon colour, so a plain blend toward it does that.
            // Above the line the sky's share isn't, so the ground's share — the plain colour, its texture
            // faded out this close to the horizon — is swapped for horizon colour directly.
            half3 Hazed(half3 sky, float y)
            {
                if (_TrackFogHaze.w <= 0.0) return sky;
                if (y > 0.0)
                    return sky + (1.0 - smoothstep(-0.03, 0.03, y)) * (_TrackFogHaze.rgb - _TrackFogGround);
                float h = 1.0 - saturate(-y / _TrackFogHaze.w);
                return lerp(sky, _TrackFogHaze.rgb, h * h);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);

                float rawDepth = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                bool isSky = rawDepth <= 1e-7;               // nothing drawn here: it's the sky
                float depth = rawDepth;
            #else
                bool isSky = rawDepth >= 1.0 - 1e-7;
                float depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
            #endif
                if (isSky)
                {
                    // The direction to a point on the FAR plane, not the near one. A point 0.3 units from a
                    // camera kilometres out on the course is lost in float rounding — the direction came out
                    // a degree or so off, and off differently each frame as the camera moved, so the haze
                    // band flickered. Thousands of units out, the same rounding is nothing.
                    float3 view = ComputeWorldSpacePosition(uv, UNITY_RAW_FAR_CLIP_VALUE, UNITY_MATRIX_I_VP) - _WorldSpaceCameraPos;
                    return half4(Hazed(scene.rgb, normalize(view).y), scene.a);
                }

                float3 ray = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP) - _WorldSpaceCameraPos;
                float dist = length(ray);
                float3 dir = ray / max(dist, 1e-4);
                float travel = dist - _TrackFogStart;
                if (travel <= 0.0) return scene;

                // Density where the fog starts along this ray, then its exponential change with height over
                // the rest: (1 - e^-t) / t, which tends to 1 for a level ray.
                float k = _TrackFogDensity.y;
                float startHeight = _WorldSpaceCameraPos.y + dir.y * _TrackFogStart;
                float density = _TrackFogDensity.x * exp(-k * (startHeight - _TrackFogDensity.z));
                float t = k * dir.y * travel;
                float shape = abs(t) > 1e-4 ? (1.0 - exp(-t)) / t : 1.0 - 0.5 * t;
                float fog = min(1.0 - exp(-density * travel * shape), _TrackFogDensity.w);

                half3 sky = Hazed(SAMPLE_TEXTURECUBE_LOD(_TrackFogSky, sampler_TrackFogSky, dir, _TrackFogSkyMip).rgb, dir.y);
                return half4(lerp(scene.rgb, sky, fog), scene.a);
            }
            ENDHLSL
        }
    }
}
