// Procedural skybox in the spirit of Unity's built-in Skybox/Procedural — a tinted zenith-to-horizon
// gradient over a flat ground colour, with a sun disk driven by the scene's directional light — plus
// two additions it doesn't have:
//   • ANIMATED CLOUDS  — fbm value-noise on a dome projection, drifting on a wind vector and slowly
//                        churning via a time-warped domain, so shapes evolve instead of just sliding.
//   • STATIC STARS     — hash-per-cell points fixed to the sky (they never move, by design).
// Entirely procedural: no textures, no external dependencies.
//
// Draw order is sky → stars → sun → clouds, so clouds correctly occlude both the stars and the sun.
//
// It deliberately exposes _SkyTint and _GroundColor with the same names the built-in shader uses, so
// SkyboxHueRandomizer can hue-shift it exactly like the existing SimpleSkybox (see that script — it
// matches on the material NAME starting with "SimpleSkybox").
Shader "Skybox/ProceduralSkyClouds"
{
    Properties
    {
        [Header(Sky)]
        _SkyTint("Sky Tint (zenith)", Color) = (0.34, 0.55, 0.9, 1)
        _HorizonColor("Horizon Color", Color) = (0.78, 0.87, 0.96, 1)
        _GroundColor("Ground Color", Color) = (0.369, 0.349, 0.341, 1)
        _AtmosphereThickness("Atmosphere Thickness", Range(0.1, 5)) = 1
        _Exposure("Exposure", Range(0, 8)) = 1.3

        [Header(Sun)]
        [Enum(Manual Angles,0,Scene Directional Light,1)]
        _SunDirectionSource("Sun Direction Source", Float) = 0
        _SunElevation("Sun Elevation (90 = overhead noon)", Range(-10, 90)) = 70
        _SunAzimuth("Sun Azimuth (compass degrees)", Range(0, 360)) = 0
        _SunColor("Sun Color", Color) = (1, 0.96, 0.86, 1)
        _SunSize("Sun Size", Range(0.005, 0.5)) = 0.06
        _SunIntensity("Sun Intensity", Range(0, 20)) = 6
        _SunGlowSize("Sun Glow Size", Range(0.01, 2)) = 0.35
        _SunGlowIntensity("Sun Glow Intensity", Range(0, 5)) = 0.5

        [Header(Clouds)]
        _CloudColor("Cloud Color (lit)", Color) = (1, 1, 1, 1)
        _CloudShadeColor("Cloud Color (shaded)", Color) = (0.55, 0.6, 0.7, 1)
        _CloudScale("Cloud Scale", Range(0.1, 20)) = 2.5
        _CloudCoverage("Cloud Coverage", Range(0, 1)) = 0.5
        _CloudSoftness("Cloud Edge Softness", Range(0.01, 0.6)) = 0.18
        _CloudOpacity("Cloud Opacity", Range(0, 1)) = 1
        _CloudSpeed("Cloud Drift Speed", Range(0, 1)) = 0.02
        _CloudDirection("Cloud Wind Direction (XY)", Vector) = (1, 0.35, 0, 0)
        _CloudTurbulence("Cloud Turbulence (shape churn)", Range(0, 2)) = 0.6
        _CloudDomeBias("Cloud Dome Bias", Range(0.05, 1)) = 0.25
        _CloudHorizonFade("Cloud Horizon Fade", Range(0, 0.6)) = 0.04

        [Header(Night)]
        [Enum(Auto (Directional Light),0,Manual,1)]
        _NightSource("Night Source", Float) = 0
        _NightBlend("Night Blend (Manual only)", Range(0, 1)) = 0
        _NightLightThreshold("Auto Threshold (light brightness)", Range(0.001, 1)) = 0.05
        _NightDarkness("Night Darkness (0 = black)", Range(0, 1)) = 0.06
        _NightTint("Night Tint", Color) = (0.55, 0.7, 1, 1)
        _NightExposure("Night Exposure", Range(0, 8)) = 0.9
        _DayStarVisibility("Star Visibility in Daylight", Range(0, 1)) = 0.15

        [Header(Ground Texture)]
        _GroundCloudColor("Ground Texture Color", Color) = (0.27, 0.25, 0.24, 1)
        _GroundCloudScale("Ground Texture Scale", Range(0.1, 20)) = 3
        _GroundCloudCoverage("Ground Texture Coverage", Range(0, 1)) = 0.55
        _GroundCloudSoftness("Ground Texture Softness", Range(0.01, 0.6)) = 0.25
        _GroundCloudOpacity("Ground Texture Opacity", Range(0, 1)) = 1
        _GroundCloudDomeBias("Ground Texture Dome Bias", Range(0.05, 1)) = 0.25
        _GroundCloudHorizonFade("Ground Texture Horizon Fade", Range(0, 0.6)) = 0.03

        [Header(Stars)]
        _StarColor("Star Color", Color) = (1, 1, 1, 1)
        _StarDensity("Star Density (cells)", Range(10, 400)) = 140
        _StarAmount("Star Amount", Range(0, 1)) = 0.09
        _StarSize("Star Size", Range(0.01, 0.5)) = 0.09
        _StarBrightness("Star Brightness", Range(0, 5)) = 1
        _StarHorizonFade("Star Horizon Fade", Range(0, 0.6)) = 0.05
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // Core.hlsl brings in Input.hlsl, which declares _MainLightPosition (the sun direction) and
            // _Time — so neither is redeclared below.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _SkyTint;
                half4 _HorizonColor;
                half4 _GroundColor;
                half  _AtmosphereThickness;
                half  _Exposure;

                half  _SunDirectionSource;
                half  _SunElevation;
                half  _SunAzimuth;
                half4 _SunColor;
                half  _SunSize;
                half  _SunIntensity;
                half  _SunGlowSize;
                half  _SunGlowIntensity;

                half4 _CloudColor;
                half4 _CloudShadeColor;
                half  _CloudScale;
                half  _CloudCoverage;
                half  _CloudSoftness;
                half  _CloudOpacity;
                half  _CloudSpeed;
                float4 _CloudDirection;
                half  _CloudTurbulence;
                half  _CloudDomeBias;
                half  _CloudHorizonFade;

                half  _NightSource;
                half  _NightBlend;
                half  _NightLightThreshold;
                half  _NightDarkness;
                half4 _NightTint;
                half  _NightExposure;
                half  _DayStarVisibility;

                half4 _GroundCloudColor;
                half  _GroundCloudScale;
                half  _GroundCloudCoverage;
                half  _GroundCloudSoftness;
                half  _GroundCloudOpacity;
                half  _GroundCloudDomeBias;
                half  _GroundCloudHorizonFade;

                half4 _StarColor;
                half  _StarDensity;
                half  _StarAmount;
                half  _StarSize;
                half  _StarBrightness;
                half  _StarHorizonFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDir    : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                // Unity draws the skybox with a mesh centred on the camera, so the object-space vertex
                // position IS the view direction for that pixel. Because it never depends on camera
                // POSITION, everything below (stars especially) stays pinned to the sky.
                OUT.viewDir = IN.positionOS.xyz;
                return OUT;
            }

            // ---- Hash / noise (no textures) ----

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // Smoothed value noise — one octave.
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);          // smoothstep-style interpolation
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Fractal Brownian motion — stacked octaves give the billowy cloud shape.
            float Fbm(float2 p)
            {
                float total = 0.0;
                float amp = 0.5;
                [unroll]
                for (int k = 0; k < 5; k++)
                {
                    total += amp * ValueNoise(p);
                    p = p * 2.02 + 17.3;              // lacunarity + offset breaks axis alignment
                    amp *= 0.5;
                }
                return total;
            }

            // Sparse point stars: quantise the direction into cells, give a fraction of them one star at
            // a random spot with a random brightness. Fixed to the direction ⇒ they never move.
            float StarField(float3 dir)
            {
                float3 p  = dir * _StarDensity;
                float3 id = floor(p);
                float3 gv = frac(p) - 0.5;

                float present = step(1.0 - _StarAmount, Hash31(id));
                float3 offset = float3(Hash31(id + 11.3), Hash31(id + 27.7), Hash31(id + 43.1)) - 0.5;
                offset *= 0.6;                        // keep the star inside its cell

                float d = length(gv - offset);
                // Written out rather than smoothstep(size, 0, d): a reversed-edge smoothstep (edge0 >
                // edge1) is undefined by the HLSL spec even though most compilers happen to handle it.
                float star = saturate(1.0 - d / max(_StarSize, 1e-4));
                star = star * star * (3.0 - 2.0 * star);      // smooth falloff to a soft point
                star *= lerp(0.35, 1.0, Hash31(id + 91.7));   // per-star brightness variation
                return star * present;
            }

            // Darkens a daylight colour toward night. Multiplying (rather than replacing with authored
            // night colours) deliberately PRESERVES the hue — so the per-scene randomised sky still
            // reads as its own colour after dark, just deep and cool instead of bright.
            float3 ApplyNight(float3 c, float night)
            {
                return lerp(c, c * _NightDarkness * _NightTint.rgb, night);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.viewDir);
                float up = dir.y;

                // ---- Night factor -------------------------------------------------
                // AUTO: URP sets _MainLightColor to BLACK when there's no main directional light (or
                // it's disabled), so its luminance is a reliable "the sun is gone" signal — this is how
                // the sky goes dark on its own, matching the built-in skybox losing its light.
                // MANUAL: drive _NightBlend yourself (0 = day, 1 = night) for a scripted day/night fade.
                float lightLuma = dot(_MainLightColor.rgb, float3(0.2126, 0.7152, 0.0722));
                float autoNight = 1.0 - smoothstep(0.0, max(_NightLightThreshold, 1e-4), lightLuma);
                float night = _NightSource > 0.5 ? saturate(_NightBlend) : autoNight;

                // ---- Sky gradient -------------------------------------------------
                // A thicker atmosphere spreads the horizon haze further up the dome, so the exponent
                // shrinks as thickness grows.
                float atmo = max(_AtmosphereThickness, 0.01);
                float horizonFalloff = pow(saturate(1.0 - saturate(up)), 6.0 / atmo);
                float3 sky = lerp(_SkyTint.rgb, _HorizonColor.rgb, horizonFalloff);

                // ---- Ground texture ------------------------------------------------
                // The same cloud noise projected onto the ground plane, but with NO time term, so it's
                // a fixed mottling that breaks up the otherwise flat ground colour rather than drifting.
                // Branched on the hemisphere: sky and ground pixels are spatially coherent (screen top
                // vs bottom), so whole waves take one side and genuinely skip the other's fbm.
                float3 ground = _GroundColor.rgb;
                if (up < 0.05)
                {
                    float down = -up;
                    float2 guv = dir.xz / (max(down, 0.0) + _GroundCloudDomeBias);
                    guv *= _GroundCloudScale;

                    float gn = Fbm(guv);
                    float gThreshold = 1.0 - _GroundCloudCoverage;
                    float gDensity = smoothstep(gThreshold, gThreshold + _GroundCloudSoftness, gn);
                    gDensity *= smoothstep(_GroundCloudHorizonFade, _GroundCloudHorizonFade + 0.15, down);
                    ground = lerp(ground, _GroundCloudColor.rgb, saturate(gDensity * _GroundCloudOpacity));
                }

                // Blend to the ground just under the horizon line, then take the sky and ground into
                // night. Done BEFORE the stars so they stay bright against a darkened sky.
                float aboveHorizon = smoothstep(-0.03, 0.03, up);
                float3 col = lerp(ground, sky, aboveHorizon);
                col = ApplyNight(col, night);

                // ---- Sun ----------------------------------------------------------
                // Two sources, picked by _SunDirectionSource:
                //   Manual Angles (default) — elevation/azimuth authored right here, INDEPENDENT of the
                //     scene lighting. 90° elevation puts the sun straight overhead (noon).
                //   Scene Directional Light — follows _MainLightPosition, so the visual sun matches
                //     where the light actually comes from (and moves if the light is rotated).
                float el = radians(_SunElevation);
                float az = radians(_SunAzimuth);
                float ce = cos(el);
                float3 manualSun = float3(ce * sin(az), sin(el), ce * cos(az));

                // Direction TO the main directional light. Guarded so a scene with no light (the vector
                // is zero) can't produce a NaN from normalize().
                float3 lightSun = _MainLightPosition.xyz;
                float sunLen = length(lightSun);
                lightSun = sunLen > 1e-4 ? lightSun / sunLen : float3(0.0, 1.0, 0.0);

                float3 sunDir = _SunDirectionSource > 0.5 ? lightSun : manualSun;
                float sunCos = dot(dir, sunDir);

                // ---- Stars (drawn under the sun and clouds) ------------------------
                float stars = StarField(dir);
                stars *= smoothstep(_StarHorizonFade, _StarHorizonFade + 0.2, up);   // none below/at horizon
                // Stars come into their own at night: full brightness there, scaled right down in
                // daylight (where a real sky would wash them out entirely).
                float starVis = lerp(_DayStarVisibility, 1.0, night);
                col += _StarColor.rgb * stars * _StarBrightness * starVis;

                // 1 - cos is a cheap small-angle measure: 0 exactly at the sun's centre.
                float angular = 1.0 - sunCos;
                float disk = 1.0 - smoothstep(_SunSize * _SunSize * 0.5, _SunSize * _SunSize, angular);
                float glow = pow(saturate(1.0 - angular / max(_SunGlowSize, 1e-3)), 3.0);
                // Masked by the horizon so the sun is OCCLUDED BY THE GROUND rather than drawn on top of
                // it — without this, a low sun renders as a bright disk sitting in the ground area.
                // Faded out by night as well: with no directional light there is no sun to draw.
                col += _SunColor.rgb * (disk * _SunIntensity + glow * _SunGlowIntensity)
                     * aboveHorizon * (1.0 - night);

                // ---- Clouds -------------------------------------------------------
                // Dome projection: dividing by the height gives a flat cloud plane overhead. The bias
                // keeps the divisor away from zero so the horizon doesn't smear to infinity.
                float2 uv = dir.xz / (max(up, 0.0) + _CloudDomeBias);
                uv *= _CloudScale;

                float2 wind = _CloudDirection.xy;
                float windLen = length(wind);
                wind = windLen > 1e-4 ? wind / windLen : float2(1.0, 0.0);
                float2 drift = wind * (_Time.y * _CloudSpeed);

                // Domain warp on a slow independent clock: the cloud SHAPES churn as they drift, instead
                // of the whole field sliding past rigidly.
                // Single-octave noise for the warp (not Fbm): the offset only needs to be smooth and
                // low-frequency, and this keeps the whole effect at ONE 5-octave Fbm per pixel instead
                // of three — the clouds are the expensive part of this shader.
                float2 wp = uv * 0.5 + drift * 0.5;
                float2 warp = float2(ValueNoise(wp), ValueNoise(wp + 31.7)) - 0.5;
                float n = Fbm(uv + drift + warp * _CloudTurbulence);

                // Higher coverage = more sky filled.
                float threshold = 1.0 - _CloudCoverage;
                float density = smoothstep(threshold, threshold + _CloudSoftness, n);
                density *= smoothstep(_CloudHorizonFade, _CloudHorizonFade + 0.15, up);   // fade at the horizon

                float3 cloudCol = lerp(_CloudShadeColor.rgb, _CloudColor.rgb, saturate(n * 1.6));
                cloudCol += _SunColor.rgb * pow(saturate(sunCos), 8.0) * 0.35 * (1.0 - night);  // silver lining
                // Clouds take the same night treatment, and are composited LAST so they still occlude
                // the stars behind them after dark.
                cloudCol = ApplyNight(cloudCol, night);
                col = lerp(col, cloudCol, saturate(density * _CloudOpacity));

                col *= lerp(_Exposure, _NightExposure, night);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
