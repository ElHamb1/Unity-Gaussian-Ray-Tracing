/*
 * The 3DGRT hit collection and integration in this file is based on
 * nvpro-samples/vk_gaussian_splatting.
 *
 * Copyright (c) 2019-2026, NVIDIA CORPORATION. All rights reserved.
 * Licensed under the Apache License, Version 2.0.
 * Unity/HDRP adaptation: Gaussian Ray Tracing for HDRP contributors.
 * Modified for Unity resource bindings, procedural/triangle hit integration,
 * layered/semantic data and custom lighting/shadow support. Packaged 2026-10-09.
 * SPDX-License-Identifier: Apache-2.0
 */
#ifndef GAUSSIAN_3DGRT_COMMON_INCLUDED
#define GAUSSIAN_3DGRT_COMMON_INCLUDED

#define GAUSSIAN_3DGRT_HITS_PER_PASS 8
#define GAUSSIAN_3DGRT_GAUSSIAN_MASK 0x01
#define GAUSSIAN_3DGRT_MESH_MASK 0x02
#define GAUSSIAN_3DGRT_PRIMARY_ONLY_MESH_MASK 0x04
#define GAUSSIAN_3DGRT_PRIMARY_MESH_MASK 0x06
// Occluders that only light-shadow rays see, such as the tool proxy built
// from the instrument masks. Never part of a primary or physical-mesh trace.
#define GAUSSIAN_3DGRT_SHADOW_ONLY_MESH_MASK 0x08
#define GAUSSIAN_3DGRT_INVALID_DISTANCE 3.402823466e+38F
#define GAUSSIAN_3DGRT_TRACE_STRATEGY_ALL_PASS 0
#define GAUSSIAN_3DGRT_TRACE_STRATEGY_STOCHASTIC_ANY_HIT 1

// Visualization surfaces use the existing payload padding as a mode tag so
// the payload size remains identical across every shader in Unity's DXR PSO.
#define GAUSSIAN_3DGRT_MESH_MODE_PHYSICAL 0u
#define GAUSSIAN_3DGRT_MESH_MODE_UNLIT_ALPHA 1u
#define GAUSSIAN_3DGRT_STOCHASTIC_PAYLOAD_TAG 0x80000000u
#define GAUSSIAN_3DGRT_STOCHASTIC_SEED_MASK 0x7FFFFFFFu

struct Gaussian3DGRTData
{
    float4 positionOpacity;
    float4 rotation;
    float4 scale;
    float4 baseColor;
};

struct Gaussian3DGRTAttributes
{
    uint colorRG;
    uint colorBA;
    uint normalOct;
};

struct Gaussian3DGRTGaussianPayload
{
    uint hitCount;
    float hitDistance[GAUSSIAN_3DGRT_HITS_PER_PASS];
    uint hitColorRG[GAUSSIAN_3DGRT_HITS_PER_PASS];
    uint hitColorBA[GAUSSIAN_3DGRT_HITS_PER_PASS];
    uint hitNormalOct[GAUSSIAN_3DGRT_HITS_PER_PASS];
};

struct Gaussian3DGRTMeshPayload
{
    uint meshHit;
    float meshDistance;
    float3 meshNormal;
    float3 meshBaseColor;
    float3 meshEmission;
    float meshTransmission;
    float meshIor;
    float meshRoughness;
    float meshMetallic;
    float meshShadowStrength;
    uint meshMaxBounces;
    uint meshFrontFace;
    // Unity 2022 builds one DXR pipeline state for all ray types and requires
    // every payload structure in that pipeline to have exactly the same size.
    // The Gaussian K-hit payload is 132 bytes after adding eight packed
    // surface normals. These fifteen dwords bring this otherwise 72-byte mesh
    // payload to the same 132-byte size.
    //
    // Some dwords double as side channels, zero meaning "feature off":
    //   [0] unlit/physical mode tag
    //   [1] light slots this material excludes
    //   [2] light slots a shadow-only occluder casts for
    //   [3] asuint(strength) with which an unlit surface receives tool shadows
    //   [4] asuint(1 - strength) with which a lit surface receives them -
    //       inverted, so a material that never writes it keeps receiving
    uint meshPadding[15];
};

// Stateless integer hashing keeps stochastic any-hit decisions independent of
// the implementation-defined order in which the acceleration structure visits
// candidates. Every input bit participates in the final avalanche.
uint Gaussian3DRTHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7FEB352Du;
    value ^= value >> 15;
    value *= 0x846CA68Bu;
    value ^= value >> 16;
    return value;
}

uint Gaussian3DRTHashCombine(uint seed, uint value)
{
    uint mixedValue = Gaussian3DRTHash(value);
    return Gaussian3DRTHash(
        seed ^ (mixedValue + 0x9E3779B9u + (seed << 6) + (seed >> 2)));
}

float Gaussian3DRTRandom01(uint seed)
{
    // Use the high 24 bits so the conversion is exact in a float mantissa and
    // the result remains in [0, 1), which makes alpha == 1 always succeed.
    return (float)(Gaussian3DRTHash(seed) >> 8) * (1.0 / 16777216.0);
}

bool Gaussian3DRTIsStochasticPayload(uint hitCount)
{
    return (hitCount & GAUSSIAN_3DGRT_STOCHASTIC_PAYLOAD_TAG) != 0;
}

uint Gaussian3DRTStochasticPayloadSeed(uint hitCount)
{
    return hitCount & GAUSSIAN_3DGRT_STOCHASTIC_SEED_MASK;
}

void Gaussian3DRTInitializeStochasticPayload(
    inout Gaussian3DGRTGaussianPayload payload,
    uint seed)
{
    payload.hitCount =
        GAUSSIAN_3DGRT_STOCHASTIC_PAYLOAD_TAG |
        (Gaussian3DRTHash(seed) & GAUSSIAN_3DGRT_STOCHASTIC_SEED_MASK);
    payload.hitDistance[0] = GAUSSIAN_3DGRT_INVALID_DISTANCE;
}

uint Gaussian3DRTPackHalf2(float first, float second)
{
    return (f32tof16(first) & 0xFFFFu) | ((f32tof16(second) & 0xFFFFu) << 16);
}

float2 Gaussian3DRTUnpackHalf2(uint packed)
{
    return float2(f16tof32(packed & 0xFFFFu), f16tof32(packed >> 16));
}

float2 Gaussian3DRTSignNotZero(float2 value)
{
    return float2(value.x >= 0 ? 1 : -1, value.y >= 0 ? 1 : -1);
}

uint Gaussian3DRTPackNormalOct(float3 normal)
{
    normal /= max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-8);
    float2 encoded = normal.xy;
    if (normal.z < 0)
        encoded = (1.0 - abs(encoded.yx)) *
            Gaussian3DRTSignNotZero(encoded);
    return Gaussian3DRTPackHalf2(encoded.x, encoded.y);
}

float3 Gaussian3DRTUnpackNormalOct(uint packed)
{
    float2 encoded = Gaussian3DRTUnpackHalf2(packed);
    float3 normal = float3(
        encoded,
        1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0)
        normal.xy = (1.0 - abs(normal.yx)) *
            Gaussian3DRTSignNotZero(normal.xy);
    return normalize(normal);
}

void Gaussian3DRTPackColorAlpha(float3 color, float alpha, out uint colorRG, out uint colorBA)
{
    colorRG = Gaussian3DRTPackHalf2(color.r, color.g);
    colorBA = Gaussian3DRTPackHalf2(color.b, alpha);
}

void Gaussian3DRTUnpackColorAlpha(uint colorRG, uint colorBA, out float3 color, out float alpha)
{
    float2 rg = Gaussian3DRTUnpackHalf2(colorRG);
    float2 ba = Gaussian3DRTUnpackHalf2(colorBA);
    color = float3(rg, ba.x);
    alpha = ba.y;
}

void Gaussian3DRTInsertHit(
    inout Gaussian3DGRTGaussianPayload payload,
    float distance,
    uint colorRG,
    uint colorBA,
    uint normalOct)
{
    uint count = min(payload.hitCount, (uint)GAUSSIAN_3DGRT_HITS_PER_PASS);
    if (count == GAUSSIAN_3DGRT_HITS_PER_PASS &&
        distance >= payload.hitDistance[GAUSSIAN_3DGRT_HITS_PER_PASS - 1])
        return;

    uint slot = count < GAUSSIAN_3DGRT_HITS_PER_PASS
        ? count
        : GAUSSIAN_3DGRT_HITS_PER_PASS - 1;
    if (count < GAUSSIAN_3DGRT_HITS_PER_PASS)
        payload.hitCount = count + 1;

    while (slot > 0 && distance < payload.hitDistance[slot - 1])
    {
        payload.hitDistance[slot] = payload.hitDistance[slot - 1];
        payload.hitColorRG[slot] = payload.hitColorRG[slot - 1];
        payload.hitColorBA[slot] = payload.hitColorBA[slot - 1];
        payload.hitNormalOct[slot] = payload.hitNormalOct[slot - 1];
        --slot;
    }

    payload.hitDistance[slot] = distance;
    payload.hitColorRG[slot] = colorRG;
    payload.hitColorBA[slot] = colorBA;
    payload.hitNormalOct[slot] = normalOct;
}

#endif
