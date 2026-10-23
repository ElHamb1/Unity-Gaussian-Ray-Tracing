// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/PPLL Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            Tags { "LightMode"="GaussianPPLL" }
            ZWrite Off
            ZTest LEqual
            Cull Off
            ColorMask 0

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 5.0
#pragma require compute
#pragma require randomwrite
#pragma use_dxc

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;
StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
uint _OptimizeForQuest;

float4 _GaussianPPLLDimensions;
uint _GaussianPPLLMaxNodes;

struct GaussianPPLLNode
{
    float4 color;
    float depth;
    uint next;
};

globallycoherent RWTexture2D<uint> _GaussianPPLLHeads : register(u1);
RWStructuredBuffer<GaussianPPLLNode> _GaussianPPLLNodes : register(u2);

#define GAUSSIAN_PPLL_EMPTY 0xFFFFFFFFu

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float eyeDepth : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];

    SplatViewData view = _SplatViewData[instID];
    float4 centerClipPos = view.pos;

    if (_OptimizeForQuest)
    {
        SplatData splat = LoadSplatData(instID);
        float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
        centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
    }

    if (centerClipPos.w <= 0)
    {
        o.vertex = asfloat(0x7fc00000);
        return o;
    }

    o.col.r = f16tof32(view.color.x >> 16);
    o.col.g = f16tof32(view.color.x);
    o.col.b = f16tof32(view.color.y >> 16);
    o.col.a = f16tof32(view.color.y);

    uint idx = vtxID;
    float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;
    quadPos *= 2;
    o.pos = quadPos;

    float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
    o.vertex = centerClipPos;
    o.vertex.xy += deltaScreenPos * centerClipPos.w;
    o.eyeDepth = LinearEyeDepth(o.vertex.z / o.vertex.w);

    if (_SplatBitsValid)
    {
        uint wordIdx = instID / 32;
        uint bitIdx = instID & 31;
        uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
        if (selVal & (1 << bitIdx))
            o.col.a = -1;
    }

    return o;
}

void StorePPLLFragment(uint2 pixel, float depth, float4 color)
{
    if (pixel.x >= (uint)_GaussianPPLLDimensions.x || pixel.y >= (uint)_GaussianPPLLDimensions.y)
        return;

    uint nodeIndex = _GaussianPPLLNodes.IncrementCounter();
    if (nodeIndex >= _GaussianPPLLMaxNodes)
        return;

    uint oldHead;
    InterlockedExchange(_GaussianPPLLHeads[pixel], nodeIndex, oldHead);

    GaussianPPLLNode node;
    node.color = color;
    node.depth = depth;
    node.next = oldHead;
    _GaussianPPLLNodes[nodeIndex] = node;
}

void frag(v2f i)
{
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);
    half3 color = i.col.rgb;

    if (i.col.a >= 0)
    {
        alpha = saturate(alpha * i.col.a);
    }
    else
    {
        half3 selectedColor = half3(1, 0, 1);
        if (alpha > 7.0 / 255.0)
        {
            if (alpha < 10.0 / 255.0)
            {
                alpha = 1;
                color = selectedColor;
            }
            alpha = saturate(alpha + 0.3);
        }
        color = lerp(color, selectedColor, 0.5);
    }

    if (alpha < 1.0 / 255.0)
        discard;

    StorePPLLFragment((uint2)i.vertex.xy, i.eyeDepth, float4(color, alpha));
}
ENDCG
        }
    }
}
