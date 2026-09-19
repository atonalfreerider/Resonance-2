Shader "Resonance/UncoiledAurora"
{
    Properties { _ViewOpacity("View opacity",Float)=1 _Hue("Hue",Color)=(0,.4,1,1) _Energy("Energy",Float)=0 }
    SubShader {
        Tags {"RenderPipeline"="UniversalPipeline" "Queue"="Transparent+12" "RenderType"="Transparent"}
        Pass {
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 _Hue;float _Energy,_ViewOpacity;
            struct A {float4 positionOS:POSITION;float2 uv:TEXCOORD0;};
            struct V {float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
            V Vert(A i){
                V o;float x=i.uv.x*2-1,h=i.uv.y;
                float peak=pow(saturate(1-abs(x)),2.3);
                float height=(.15+1.65*sqrt(_Energy))*peak;
                float3 p=float3(x*(.13+h*.13),h*height,0);
                p.x+=sin(h*22-_Time.y*15+x*5)*.018*h*peak*_Energy;
                o.positionCS=TransformObjectToHClip(p);o.uv=i.uv;return o;
            }
            half4 Frag(V i):SV_Target {
                float x=i.uv.x*2-1,h=i.uv.y;
                float bands=.32+.68*pow(.5+.5*sin(h*35-_Time.y*22),3);
                float shimmer=.55+.45*sin(x*85+sin(h*8-_Time.y*3));
                float envelope=pow(saturate(1-abs(x)),1.4)*exp(-h*3.1)*pow(1-h,1.4)*smoothstep(0,.035,h);
                return half4(_Hue.rgb*3.5,envelope*bands*shimmer*_Energy*_ViewOpacity);
            }
            ENDHLSL
        }
    }
}
