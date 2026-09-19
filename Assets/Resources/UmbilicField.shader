Shader "Resonance/UmbilicField"
{
    Properties { _ViewOpacity ("View opacity",Float)=1 _Opacity("Field density",Range(0,2))=.7 }
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
            float _Unfold,_Key;
            float _Opacity, _Flow, _Diatonic, _SoundingOnly;
            float4 _Primary;
            float _Dominance;
            CBUFFER_END
            float _ViewOpacity;
            struct A { float4 positionOS:POSITION; float2 uv:TEXCOORD0; float2 coverage:TEXCOORD1; float4 color:COLOR;float3 original:TEXCOORD2; };
            struct V { float4 positionCS:SV_POSITION; float3 positionOS:TEXCOORD0; float2 uv:TEXCOORD1; float2 coverage:TEXCOORD2; float3 local:TEXCOORD3; };
            V Vert(A i) { V o; o.positionCS=TransformObjectToHClip(i.positionOS.xyz);o.positionOS=i.original;o.uv=i.uv;o.coverage=i.coverage;o.local=i.color.rgb;return o; }
            half4 Frag(V i):SV_Target
            {
                float3 hue=0, light=0;
                float weights=0, energy=0, membership=0;
                [unroll] for(int n=0;n<12;n++)
                {
                    float3 delta=i.positionOS-_Anchors[n].xyz;
                    float d2=dot(delta,delta);
                    // Colors are indexed by pitch; center the circle at the current tonic.
                    float relative=frac((n-_Key)*5.0/12.0+.5)-.5;
                    float distance=abs(i.uv.x-.5-relative);
                    float ringDistance=distance*distance*32;
                    d2=lerp(d2,ringDistance,_Unfold);
                    float w=1/pow(.10+d2,2);
                    hue+=_Colors[n].rgb*w; weights+=w;
                    membership+=w*_Excitation[n].y;
                    float influence=exp(-2.7*d2)*_Excitation[n].x;
                    energy+=influence;
                    light+=_Colors[n].rgb*influence;
                }
                float3 localHue=lerp(i.local,hue/max(weights,.00001),_Unfold);
                hue=lerp(hue/max(weights,.00001),localHue,.88);
                membership/=weights;
                // Persistent energy changes the local hue as well as its brightness.
                float3 wash=light/max(energy,.00001);
                float dominance=_Dominance*_Primary.a/(.65+_Primary.a);
                wash=lerp(wash,_Primary.rgb,dominance);
                // Primary major regions retain local identity under the moving color wash.
                hue=lerp(hue,wash,saturate(energy/(.35+energy))*lerp(.85,.28,i.coverage.y));
                // Compress brightness after mixing, preserving relative energy/color ratios.
                light*=log(1+energy)/max(energy,.00001);
                light=lerp(light,_Primary.rgb*log(1+energy),dominance);
                light=lerp(light,localHue*log(1+energy),i.coverage.y*.42);
                // Streamlines follow the ruled umbilic surface, never displacing its vertices.
                // Three turns make the visual phase periodic at the t=0/1 seam.
                float phase=6.2831853*(i.uv.x*3-_Time.y*.075*_Flow);
                float ripple=(sin(phase)+.35*sin(2*phase)+.16*sin(3*phase))*.035;
                float ribbon=pow(saturate(.5+.5*cos(6.2831853*(i.uv.y*7+ripple))),18);
                float density=lerp(1,.18+.82*membership,_Diatonic);
                float ambient=lerp(.18,0,_SoundingOnly);
                float3 field=hue*(ambient+.025*ribbon*lerp(1,saturate(energy),_SoundingOnly))+light*(.55+.25*ribbon);
                field+=localHue*(.28*_Unfold);
                return half4(field*_Opacity*_ViewOpacity*density*lerp(saturate(i.coverage.x),smoothstep(0,.05,i.uv.y)*smoothstep(0,.07,1-i.uv.y)*.22,_Unfold),1);
            }
            ENDHLSL
        }
    }
}
