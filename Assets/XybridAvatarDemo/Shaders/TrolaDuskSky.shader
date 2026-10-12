Shader "Xybrid/Trola Dusk Sky"
{
    Properties
    {
        _Top ("Zenith", Color) = (.009, .016, .038, 1)
        _Horizon ("Horizon", Color) = (.055, .072, .12, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Top, _Horizon;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 vertex : SV_POSITION; float3 direction : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.direction = v.vertex.xyz; return o; }
            half4 frag(v2f i) : SV_Target { float y = normalize(i.direction).y; return lerp(_Horizon, _Top, smoothstep(-.04, .65, y)); }
            ENDHLSL
        }
    }
}
