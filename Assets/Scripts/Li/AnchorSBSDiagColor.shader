// AnchorSBS 图层隔离诊断用 Shader（3D Quad）
// 输出纯色（_Color），用于验证 Camera Culling Mask 图层隔离是否正常。
// 确认左眼=蓝/右眼=红后，将材质切换为 Custom/SampleRT 接入真实图传。
Shader "AnchorSBS/DiagColor"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0, 0, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            float4 vert(float4 v : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(v);
            }

            fixed4 frag() : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
}
