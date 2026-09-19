Shader "Hidden/Resonance/OrreryTransparent"
{
    Properties { _MainTex("Bloom source",2D)="black"{} }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 frag(v2f_img i):SV_Target
            {
                float3 c=tex2D(_MainTex,i.uv).rgb;
                float a=saturate(max(c.r,max(c.g,c.b)));
                return float4(c/max(a,.00001),a);
            }
            ENDHLSL
        }
    }
}
