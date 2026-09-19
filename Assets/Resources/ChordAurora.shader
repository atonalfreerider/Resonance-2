Shader "Resonance/ChordAurora"
{
    Properties { _ViewOpacity ("View opacity",Float)=1 _Hue ("Wave color",Color)=(0,0.4,1,1) _Wave ("Phase energy release dying",Vector)=(0,0,0,0) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+11" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float _ViewOpacity;float4 _Hue,_Wave,_Drive,_Wind;float _Pulse;
            struct A { float4 positionOS:POSITION;float3 normalOS:NORMAL;float2 uv:TEXCOORD0;float4 mode:TEXCOORD1;float4 field:TEXCOORD2; };
            struct V { float4 positionCS:SV_POSITION;float4 color:COLOR;float2 uv:TEXCOORD0; };
            V Vert(A i)
            {
                V o;float t=_Wave.x,energy=_Wave.y;
                // Parcels actually stream away from their footpoints, with a soft birth.
                float h=frac(i.mode.x+t*.22);
                float harmonic=i.mode.y*cos(t*2.1)+.28*i.mode.z*cos(t*4.2);
                float x=i.field.x,u=i.field.y,seed=i.field.z;
                // A cloud of air parcels fills a widening volume around each curved
                // emitter. Circular grains reveal moving nodal interference surfaces.
                float3 normal=normalize(i.normalOS);
                float3 tangent=normalize(cross(abs(normal.y)<.9?float3(0,1,0):float3(1,0,0),normal));
                float3 bitangent=cross(normal,tangent);
                float angle=seed*6.283185;
                float3 weights=float3(1-x,x*(1-u),x*u);
                float localEnergy=saturate(dot(weights,_Drive.xyz))*energy;
                float peak=lerp(.6+.4*abs(i.mode.y),pow(saturate(1-x),.4),_Wind.w);
                float height=.35+4.5*sqrt(localEnergy)*peak;
                float spread=h*lerp(.38,.16,_Wind.w)*(.4+.6*x);
                float distance=h*height;
                float pressure=sin(distance*17-t*5.5);
                float shock=exp(-pow((distance-_Pulse*16)*3,2))*_Drive.w;
                float displacement=(.13*harmonic+.1*pressure+.18*shock)*localEnergy;
                float rise=_Wave.w*_Wave.z*.35*h;
                float3 local=i.positionOS.xyz+normal*(.018+distance+displacement+rise)
                    +(tangent*cos(angle+t*.45)+bitangent*sin(angle+t*.45))*spread
                    +_Wind.xyz*h*h*(.55+.45*sin(t*3-distance*2));
                // Changing interference lobes, rather than rows of stretched rays.
                float a=sin(x*12+u*7+distance*5)*cos(t*1.1);
                float b=cos(u*13-x*4-distance*6)*sin(t*1.1);
                float nodal=exp(-abs(a+b+.35*harmonic)*4.5);
                float front=pow(saturate(.5+.5*pressure),4);
                float density=(.16+.84*nodal)*(.16+.84*front)+shock*1.2;
                float death=lerp(1,smoothstep(0,.22,1-h-_Wave.z*3),_Wave.w);
                float3 p=TransformObjectToWorld(local);
                float2 offset=(i.uv*2-1)*(.016+.01*seed);
                p+=UNITY_MATRIX_I_V._m00_m10_m20*offset.x+UNITY_MATRIX_I_V._m01_m11_m21*offset.y;
                float envelope=exp(-h*5)*pow(saturate(1-h),2)*smoothstep(0,.025,h);
                // Recompute the height envelope for advecting parcels; retain only
                // the baked lateral boundary mask from the original distribution.
                float baseEnvelope=pow(1-i.mode.x,1.5)*smoothstep(0,.09,i.mode.x+.025);
                float edge=i.field.w/max(.000001,baseEnvelope);
                o.color=float4(_Hue.rgb,edge*localEnergy*envelope*death*density*3.8*_ViewOpacity);
                o.positionCS=TransformWorldToHClip(p);o.uv=i.uv;return o;
            }
            half4 Frag(V i):SV_Target {float2 p=i.uv*2-1;float r=dot(p,p);float density=(exp(-r*5)+.18*exp(-r*1.5))*saturate(1-r);return half4(i.color.rgb*5,i.color.a*density);}
            ENDHLSL
        }
    }
}
