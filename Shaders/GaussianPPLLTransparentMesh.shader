// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/PPLL Transparent Mesh"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (1, 1, 1, 0.35)
        _BaseMap("Base Map", 2D) = "white" {}
    }

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

HLSLPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 5.0
#pragma require randomwrite

#include "UnityCG.cginc"

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct v2f
{
    float2 uv : TEXCOORD0;
    float eyeDepth : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

float4 _BaseColor;
sampler2D _BaseMap;
float4 _BaseMap_ST;

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

v2f vert(appdata v)
{
    v2f o;
    o.vertex = UnityObjectToClipPos(v.vertex);
    o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
    o.eyeDepth = -UnityObjectToViewPos(v.vertex).z;
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
    float4 color = tex2D(_BaseMap, i.uv) * _BaseColor;
    if (color.a < 1.0 / 255.0)
        discard;

    StorePPLLFragment((uint2)i.vertex.xy, i.eyeDepth, color);
}
ENDHLSL
        }
    }
}
