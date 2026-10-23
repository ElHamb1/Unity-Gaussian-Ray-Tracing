// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Unified Primitive Render"
{
    Properties
    {
        [Toggle]_UnifiedMeshUseFaceColors("Use Mesh Face Colors", Float) = 0
        _UnifiedMeshOutsideColor("Mesh Outside Color", Color) = (1, 1, 1, 0.35)
        _UnifiedMeshInsideColor("Mesh Inside Color", Color) = (0.1, 0.7, 1, 0.35)
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend OneMinusDstAlpha One

HLSLPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 5.0
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

#define UNIFIED_PRIMITIVE_MESH_BIT 0x80000000u
#define UNIFIED_PRIMITIVE_INDEX_MASK 0x7FFFFFFFu

StructuredBuffer<uint> _UnifiedPrimitiveValues;
StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
uint _UnifiedSplatCount;
uint _UnifiedMeshPrimCount;
float _UnifiedMeshUseFaceColors;
half4 _UnifiedMeshOutsideColor;
half4 _UnifiedMeshInsideColor;

struct UnifiedMeshVertex
{
    float3 position;
    float3 normal;
    float2 uv;
};

struct UnifiedMeshPrim
{
    uint index0;
    uint index1;
    uint index2;
    uint meshIndex;
};

struct UnifiedMeshInstance
{
    float4 objectToWorld0;
    float4 objectToWorld1;
    float4 objectToWorld2;
    float4 objectToWorld3;
    float4 worldToObject0;
    float4 worldToObject1;
    float4 worldToObject2;
    float4 worldToObject3;
    float4 baseColor;
    float4 baseMapST;
    float4 faceOutsideColor;
    float4 faceInsideColor;
    float4 faceColorParams;
};

StructuredBuffer<UnifiedMeshVertex> _UnifiedMeshVertices;
StructuredBuffer<UnifiedMeshPrim> _UnifiedMeshPrims;
StructuredBuffer<UnifiedMeshInstance> _UnifiedMeshInstances;

struct v2f
{
    half4 col : COLOR0;
    float2 param : TEXCOORD0;
    float primitiveKind : TEXCOORD1;
    half4 meshOutsideColor : TEXCOORD2;
    half4 meshInsideColor : TEXCOORD3;
    float meshUseFaceColors : TEXCOORD4;
    float4 vertex : SV_POSITION;
};

float4 TransformUnifiedMeshPoint(UnifiedMeshInstance instanceData, float3 localPosition)
{
    return instanceData.objectToWorld0 * localPosition.x +
        instanceData.objectToWorld1 * localPosition.y +
        instanceData.objectToWorld2 * localPosition.z +
        instanceData.objectToWorld3;
}

uint MeshPrimVertexIndex(UnifiedMeshPrim meshPrim, uint vtxID)
{
    if (vtxID == 0)
        return meshPrim.index0;
    if (vtxID == 1 || vtxID == 3)
        return meshPrim.index1;
    return meshPrim.index2;
}

v2f VertexNaN()
{
    v2f o = (v2f)0;
    o.vertex = asfloat(uint4(0x7fc00000, 0x7fc00000, 0x7fc00000, 0x7fc00000));
    return o;
}

v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    uint payload = _UnifiedPrimitiveValues[instID];
    bool isMesh = (payload & UNIFIED_PRIMITIVE_MESH_BIT) != 0;

    if (isMesh)
    {
        uint meshPrimIndex = payload & UNIFIED_PRIMITIVE_INDEX_MASK;
        if (meshPrimIndex >= _UnifiedMeshPrimCount)
            return VertexNaN();

        UnifiedMeshPrim meshPrim = _UnifiedMeshPrims[meshPrimIndex];
        UnifiedMeshInstance instanceData = _UnifiedMeshInstances[meshPrim.meshIndex];
        uint vertexIndex = MeshPrimVertexIndex(meshPrim, vtxID);
        UnifiedMeshVertex meshVertex = _UnifiedMeshVertices[vertexIndex];

        float4 worldPos = TransformUnifiedMeshPoint(instanceData, meshVertex.position);

        v2f o = (v2f)0;
        o.vertex = mul(UNITY_MATRIX_VP, worldPos);
        o.col = (half4)instanceData.baseColor;
        o.param = meshVertex.uv * instanceData.baseMapST.xy + instanceData.baseMapST.zw;
        o.primitiveKind = 1.0;
        o.meshOutsideColor = (half4)instanceData.faceOutsideColor;
        o.meshInsideColor = (half4)instanceData.faceInsideColor;
        o.meshUseFaceColors = instanceData.faceColorParams.x;
        return o;
    }

    uint splatIndex = payload;
    if (splatIndex >= _UnifiedSplatCount)
        return VertexNaN();

    SplatViewData view = _SplatViewData[splatIndex];
    float4 centerClipPos = view.pos;
    if (centerClipPos.w <= 0)
        return VertexNaN();

    v2f o = (v2f)0;
    o.col.r = f16tof32(view.color.x >> 16);
    o.col.g = f16tof32(view.color.x);
    o.col.b = f16tof32(view.color.y >> 16);
    o.col.a = f16tof32(view.color.y);

    float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
    quadPos *= 2.0;
    o.param = quadPos;

    float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2.0 / _ScreenParams.xy;
    o.vertex = centerClipPos;
    o.vertex.xy += deltaScreenPos * centerClipPos.w;
    o.primitiveKind = 0.0;

    if (_SplatBitsValid)
    {
        uint wordIdx = splatIndex / 32;
        uint bitIdx = splatIndex & 31;
        uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
        if (selVal & (1 << bitIdx))
            o.col.a = -1;
    }

    return o;
}

half4 frag(v2f i, bool isFrontFace : SV_IsFrontFace) : SV_Target
{
    if (i.primitiveKind > 0.5)
    {
        half4 color = i.col;
        if (i.meshUseFaceColors > 0.5)
            color = isFrontFace ? i.meshOutsideColor : i.meshInsideColor;
        else if (_UnifiedMeshUseFaceColors > 0.5)
            color = isFrontFace ? _UnifiedMeshOutsideColor : _UnifiedMeshInsideColor;

        half alpha = saturate(color.a);
        if (alpha < 1.0 / 255.0)
            discard;
        return half4(color.rgb * alpha, alpha);
    }

    float power = -dot(i.param, i.param);
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

    return half4(color * alpha, alpha);
}
ENDHLSL
        }
    }
}
