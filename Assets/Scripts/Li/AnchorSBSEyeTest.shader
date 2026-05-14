// 诊断用 Shader：左眼显示蓝色，右眼显示红色
// 用于验证 unity_StereoEyeIndex 在 WorldSpace Canvas 上能否正确区分左右眼。
// 确认有效后替换为 AnchorSBSStereo.shader。
Shader "UI/AnchorSBS_EyeTest"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // unity_StereoEyeIndex: 0 = 左眼 → 蓝色
                //                      1 = 右眼 → 红色
                if (unity_StereoEyeIndex == 0)
                    return fixed4(0.0, 0.0, 1.0, 1.0);
                else
                    return fixed4(1.0, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
