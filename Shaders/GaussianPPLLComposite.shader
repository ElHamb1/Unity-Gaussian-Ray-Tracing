// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/PPLL Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert(uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _BlitTexture;

half4 frag(v2f i) : SV_Target
{
    return _BlitTexture.Load(int3(i.vertex.xy, 0));
}
ENDCG
        }
    }
}
