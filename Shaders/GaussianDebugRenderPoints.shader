// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Debug/Render Points"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite On
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "GaussianSplatting.hlsl"

struct v2f
{
    half3 color : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

float _SplatSize;
bool _DisplayIndex;
bool _DisplaySemantic;
int _SplatCount;

float3 SemanticDebugColor(uint semanticId)
{
    if (semanticId == 0)
        return float3(0.25, 0.25, 0.25);

    float3 seed = frac(float3(0.1031, 0.11369, 0.13787) * (semanticId + 1));
    seed += dot(seed, seed.yzx + 19.19);
    return saturate(frac((seed.xxy + seed.yzz) * seed.zyx));
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    uint splatIndex = instID;

    SplatData splat = LoadSplatData(splatIndex);

    float3 centerWorldPos = splat.pos;
    centerWorldPos = mul(unity_ObjectToWorld, float4(centerWorldPos,1)).xyz;

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));

    o.vertex = centerClipPos;
	uint idx = vtxID;
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
    o.vertex.xy += (quadPos * _SplatSize / _ScreenParams.xy) * o.vertex.w;

    o.color.rgb = saturate(splat.sh.col);
    if (_DisplayIndex)
    {
        o.color.r = frac((float)splatIndex / (float)_SplatCount * 100);
        o.color.g = frac((float)splatIndex / (float)_SplatCount * 10);
        o.color.b = (float)splatIndex / (float)_SplatCount;
    }
    if (_DisplaySemantic)
    {
        o.color.rgb = SemanticDebugColor(_SplatSemantic[splatIndex]);
    }
    return o;
}

half4 frag (v2f i) : SV_Target
{
    return half4(i.color.rgb, 1);
}
ENDCG
        }
    }
}
