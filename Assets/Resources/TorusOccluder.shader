// Depth only: the torus surface, drawn after the torus's own additive glow and before the drum
// wheel, so what lies behind the torus (the drum plate below it) is hidden while the torus
// itself looks unchanged.
Shader "Resonance/TorusOccluder"
{
    Properties { _ViewOpacity ("View opacity",Float)=1 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+50" "RenderType"="Transparent" }
        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS:POSITION; };
            struct V { float4 positionCS:SV_POSITION; };
            V Vert(A i) { V o; o.positionCS=TransformObjectToHClip(i.positionOS.xyz); return o; }
            half4 Frag(V i):SV_Target { return 0; }
            ENDHLSL
        }
    }
}
