Shader "Resonance/ChordRegion"
{
    Properties { _ViewOpacity ("View opacity",Float)=1 _BaseColor ("Tint",Color)=(0.2,0.4,1,0.19) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+9" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            CBUFFER_END
            float _ViewOpacity;
            struct A { float4 positionOS:POSITION; };
            struct V { float4 positionCS:SV_POSITION; };
            V Vert(A i) { V o; o.positionCS=TransformObjectToHClip(i.positionOS.xyz); return o; }
            half4 Frag(V i):SV_Target { return half4(_BaseColor.rgb,_BaseColor.a*_ViewOpacity); }
            ENDHLSL
        }
    }
}
