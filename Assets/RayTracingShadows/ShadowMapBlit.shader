Shader "RayTracing/ShadowMapBlit"
{
    Properties
    {
        _MainTex("", 2D) = "" {}
    }

    Subshader
    {
        ZTest Always Cull Off ZWrite Off
        Blend Zero SrcAlpha

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }
            
            sampler2D _MainTex;

            half4 frag(v2f i) : SV_Target
            {
                half shadowMap = saturate(tex2D(_MainTex, i.uv).r + 0.1);
                return half4(1, 1, 1, shadowMap);
            }

            ENDCG
        }
    }

    Fallback off
}
