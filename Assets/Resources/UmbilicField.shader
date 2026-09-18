Shader "Resonance/UmbilicField"
{
    Properties { _Opacity("Field density",Range(0,2))=.7 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float4 _Anchors[12];
            float4 _Colors[12];
            float4 _Excitation[12]; // x energy, y diatonic membership
            float _Opacity, _Flow, _Diatonic, _SoundingOnly;
            CBUFFER_END
            struct A { float4 positionOS:POSITION; float2 uv:TEXCOORD0; float2 coverage:TEXCOORD1; };
            struct V { float4 positionCS:SV_POSITION; float3 positionOS:TEXCOORD0; float2 uv:TEXCOORD1; float coverage:TEXCOORD2; };
            V Vert(A i) { V o; o.positionCS=TransformObjectToHClip(i.positionOS.xyz);o.positionOS=i.positionOS.xyz;o.uv=i.uv;o.coverage=i.coverage.x;return o; }
            half4 Frag(V i):SV_Target
            {
                float3 hue=0, light=0;
                float weights=0, energy=0, membership=0;
                [unroll] for(int n=0;n<12;n++)
                {
                    float3 delta=i.positionOS-_Anchors[n].xyz;
                    float d2=dot(delta,delta);
                    float w=1/pow(.10+d2,2);
                    hue+=_Colors[n].rgb*w; weights+=w;
                    membership+=w*_Excitation[n].y;
                    float influence=exp(-2.7*d2)*_Excitation[n].x;
                    energy+=influence;
                    light+=_Colors[n].rgb*influence;
                }
                hue/=weights;
                membership/=weights;
                // Streamlines follow the ruled umbilic surface, never displacing its vertices.
                // Three turns make the visual phase periodic at the t=0/1 seam.
                float phase=6.2831853*(i.uv.x*3-_Time.y*.075*_Flow);
                float ripple=(sin(phase)+.35*sin(2*phase)+.16*sin(3*phase))*.035;
                float ribbon=pow(saturate(.5+.5*cos(6.2831853*(i.uv.y*7+ripple))),18);
                float density=lerp(1,.18+.82*membership,_Diatonic);
                float ambient=lerp(.18,0,_SoundingOnly);
                float3 field=hue*(ambient+.025*ribbon*lerp(1,saturate(energy),_SoundingOnly))+light*(.55+.25*ribbon);
                return half4(field*_Opacity*density*saturate(i.coverage),1);
            }
            ENDHLSL
        }
    }
}
