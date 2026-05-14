// AnchorSBS 锚定画布专用 UI Shader
// 根据 unity_StereoEyeIndex（0=左眼 / 1=右眼）自动采样 SBS 纹理的左/右半幅，
// 无需图层隔离，兼容 Multi-Pass、Single-Pass Instanced、Multiview 立体渲染模式。
Shader "UI/AnchorSBS_Stereo"
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
            "Queue"             = "Transparent"
            "IgnoreProjector"   = "True"
            "RenderType"        = "Transparent"
            "PreviewType"       = "Plane"
            "CanUseSpriteAtlas" = "True"
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
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4    _Color;
            float4    _ClipRect;

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex    = UnityObjectToClipPos(v.vertex);
                o.texcoord  = v.texcoord;
                o.worldPos  = v.vertex;
                o.color     = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // unity_StereoEyeIndex: 0 = 左眼, 1 = 右眼
                // 左眼 → u ∈ [0, 0.5]（SBS 左半幅）
                // 右眼 → u ∈ [0.5, 1]（SBS 右半幅）
                float2 uv = i.texcoord;
                uv.x = uv.x * 0.5 + (float)unity_StereoEyeIndex * 0.5;

                fixed4 col = tex2D(_MainTex, uv) * i.color;

                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                #endif

                return col;
            }
            ENDCG
        }
    }
}
