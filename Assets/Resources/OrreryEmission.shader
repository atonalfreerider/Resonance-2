Shader "Resonance/OrreryEmission"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Pass
        {
            Blend One One ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A {float4 positionOS:POSITION;float4 color:COLOR;};
            struct V {float4 positionCS:SV_POSITION;float4 color:COLOR;};
            V Vert(A i){V o;o.positionCS=TransformObjectToHClip(i.positionOS.xyz);o.color=i.color;return o;}
            half4 Frag(V i):SV_Target{return i.color;}
            ENDHLSL
        }
    }
}
