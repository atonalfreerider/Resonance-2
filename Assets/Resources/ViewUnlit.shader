Shader "Resonance/ViewUnlit"
{
    Properties { [HDR] _BaseColor ("Color",Color)=(1,1,1,1) _ViewOpacity ("View opacity",Float)=1 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 _BaseColor;float _ViewOpacity;
            struct A { float4 positionOS:POSITION; };
            struct V { float4 positionCS:SV_POSITION; };
            V Vert(A i){V o;o.positionCS=TransformObjectToHClip(i.positionOS.xyz);return o;}
            half4 Frag(V i):SV_Target{return half4(_BaseColor.rgb,saturate(_BaseColor.a)*_ViewOpacity);}
            ENDHLSL
        }
    }
}
