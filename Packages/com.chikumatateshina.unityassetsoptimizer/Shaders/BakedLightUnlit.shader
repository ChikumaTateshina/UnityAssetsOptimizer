// UnityAssetsOptimizer: ライトマップ焼き込み用の Unlit シェーダー（Built-in レンダーパイプライン）。
// 色 = メインテクスチャ(UV0) × 色 × lerp(1, 光テクスチャ(UV1) × unity_ColorSpaceDouble, 強度) + 発光(UV0)
// 光テクスチャは Standard の Detail Albedo（×2 乗算）と同じ表現: Linear 色空間では sRGB で L / 4.59、Gamma では L / 2 を格納する。
Shader "UnityAssetsOptimizer/Baked Light Unlit"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Color", Color) = (1, 1, 1, 1)
        _LightTex ("Baked Light (UV1)", 2D) = "gray" {}
        _LightStrength ("Light Strength", Range(0, 1)) = 1
        _EmissionMap ("Emission", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0, 0, 0, 1)
        [Toggle(_ALPHATEST_ON)] _AlphaTest ("Alpha Test", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            sampler2D _LightTex;
            float4 _LightTex_ST;
            half _LightStrength;
            sampler2D _EmissionMap;
            float4 _EmissionMap_ST;
            half4 _EmissionColor;
            half _Cutoff;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uvEmission : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv0 = TRANSFORM_TEX(v.uv0, _MainTex);
                o.uv1 = TRANSFORM_TEX(v.uv1, _LightTex);
                o.uvEmission = TRANSFORM_TEX(v.uv0, _EmissionMap);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv0) * _Color;
                #ifdef _ALPHATEST_ON
                    clip(c.a - _Cutoff);
                #endif
                half3 light = tex2D(_LightTex, i.uv1).rgb * unity_ColorSpaceDouble.rgb;
                c.rgb *= lerp(half3(1, 1, 1), light, _LightStrength);
                c.rgb += tex2D(_EmissionMap, i.uvEmission).rgb * _EmissionColor.rgb;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    FallBack "Legacy Shaders/VertexLit"
}
