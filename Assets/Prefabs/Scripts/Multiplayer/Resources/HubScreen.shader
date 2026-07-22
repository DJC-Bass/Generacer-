// Unlit URP shader for the HUB spectator TVs. Displays the spectator camera's render texture, with a
// UV rotation about the centre (for 90° quarter-turns the sampling stays inside 0-1, so no clamping)
// on top of the standard tiling/offset — which HubSpectatorTV uses for horizontal/vertical flips. Lives
// under a Resources folder so Shader.Find resolves it in standalone builds (a shader only referenced by
// name would otherwise be stripped).
Shader "Generacer/HubScreen"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "black" {}
        _UVRotation ("UV Rotation (deg)", Float) = 0
        _UVScale ("UV Scale", Vector) = (1,1,0,0)
        _UVOffset ("UV Offset", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _UVRotation;
                float4 _UVScale;
                float4 _UVOffset;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);   // tiling/offset = flips from HubSpectatorTV
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float ang = radians(_UVRotation);
                float s = sin(ang);
                float c = cos(ang);
                float2 p = IN.uv - 0.5;                     // rotate about the centre
                float2 r = float2(p.x * c - p.y * s, p.x * s + p.y * c);
                float2 uv = r * _UVScale.xy + 0.5 + _UVOffset.xy;   // zoom (scale about centre) + pan
                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
            }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Unlit"
}
