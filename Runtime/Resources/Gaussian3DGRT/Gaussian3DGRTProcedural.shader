/*
 * The 3DGRT intersection algorithm in this file is based on
 * nvpro-samples/vk_gaussian_splatting.
 *
 * Copyright (c) 2019-2026, NVIDIA CORPORATION. All rights reserved.
 * Licensed under the Apache License, Version 2.0.
 * Unity/HDRP adaptation: Gaussian Ray Tracing for HDRP contributors.
 * Modified for Unity resource bindings, procedural/triangle hit integration,
 * layered/semantic data and custom lighting/shadow support. Packaged 2026-10-09.
 * SPDX-License-Identifier: Apache-2.0
 */
Shader "Hidden/Gaussian Splatting/3DGRT Procedural"
{
    SubShader
    {
        Pass
        {
            Name "Gaussian3DGRT"
            Tags { "LightMode" = "RayTracing" }

            HLSLPROGRAM
            // Unity 2022.3 can otherwise submit a DXR material while its
            // on-demand procedural variant is still compiling, which causes
            // a native failure in SetupProceduralInstanceShaders.
            #pragma editor_sync_compilation
            #pragma multi_compile_local RAY_TRACING_PROCEDURAL_GEOMETRY
            #pragma raytracing gaussian_3dgrt

            #include "Gaussian3DGRTCommon.hlsl"

            StructuredBuffer<Gaussian3DGRTData> _Gaussian3DGRTData;
            ByteAddressBuffer _Gaussian3DGRTSH;
            ByteAddressBuffer _SplatDeletedBits;
            StructuredBuffer<uint> _SplatSemantic;
            StructuredBuffer<uint> _SemanticAlphaLabelIds;
            StructuredBuffer<uint> _SemanticDimLabelIds;

            uint _SplatBitsValid;
            uint _SplatCount;
            float _SplatScale;
            float _SplatOpacityScale;
            uint _SemanticAlphaLabelId;
            uint _SemanticAlphaLabelCount;
            uint _SemanticDimLabelCount;
            float _SemanticMatchAlphaMultiplier;
            float _SemanticNonMatchAlphaMultiplier;
            float _SemanticDimAlphaMultiplier;
            uint _SHOrder;
            uint _SHOnly;
            uint _DisplaySemantic;
            float _Gaussian3DGRTKernelMinResponse;
            float _Gaussian3DGRTAlphaCullThreshold;
            float _Gaussian3DGRTAlphaClamp;

            #define SPLAT_CUTOUT_TYPE_ELLIPSOID 0
            #define SPLAT_CUTOUT_TYPE_BOX 1
            #define CUTOUT_VIEW_SHAPE_ELLIPSOID 0
            #define CUTOUT_VIEW_SHAPE_CYLINDER 1

            struct GaussianCutoutShaderData
            {
                float4x4 mat;
                uint typeAndFlags;
                int cutIndex0;
                int cutIndex1;
                int cutIndex2;
                int cutIndex3;
                int cutIndex4;
                int cutIndex5;
                int cutIndex6;
                int cutIndex7;
            };
            StructuredBuffer<GaussianCutoutShaderData> _SplatCutouts;
            uint _SplatCutoutsCount;

            struct CutoutViewShaderData
            {
                float4x4 mat;
                uint shape;
                float height;
                float width;
                float alphaMultiplier;
                float gaussianFalloff;
                uint replaceAlphaWithFalloff;
                uint semanticOffset;
                uint semanticCount;
            };
            StructuredBuffer<CutoutViewShaderData> _CutoutViews;
            StructuredBuffer<uint> _CutoutViewSemanticIds;
            uint _CutoutViewsCount;

            float3 SemanticDebugColor(uint semanticId)
            {
                if (semanticId == 0)
                    return float3(0.25, 0.25, 0.25);

                float3 seed = frac(float3(0.1031, 0.11369, 0.13787) * (semanticId + 1));
                seed += dot(seed, seed.yzx + 19.19);
                return saturate(frac((seed.xxy + seed.yzz) * seed.zyx));
            }

            float3 Gaussian3DRTRotate(float3 value, float4 rotation)
            {
                float3 t = 2.0 * cross(rotation.xyz, value);
                return value + rotation.w * t + cross(rotation.xyz, t);
            }

            float3 Gaussian3DGRTSurfaceNormal(
                Gaussian3DGRTData gaussian,
                float3 scale,
                float4 rotation,
                float4 inverseRotation)
            {
                float3 localVector =
                    ObjectRayOrigin() - gaussian.positionOpacity.xyz;
                bool3 isSmall = scale < float3(1e-6, 1e-6, 1e-6);
                uint smallCount =
                    (isSmall.x ? 1u : 0u) +
                    (isSmall.y ? 1u : 0u) +
                    (isSmall.z ? 1u : 0u);

                float3 normalOS;
                if (smallCount == 0)
                {
                    // NVIDIA's default maximum-density-plane approximation:
                    // transform the camera-to-center vector by the inverse
                    // covariance and use that gradient as the surface normal.
                    float3 canonicalVector = Gaussian3DRTRotate(
                        localVector, inverseRotation);
                    float3 scaledVector =
                        canonicalVector / max(
                            scale * scale,
                            float3(1e-16, 1e-16, 1e-16));
                    normalOS = Gaussian3DRTRotate(scaledVector, rotation);
                }
                else if (smallCount == 1)
                {
                    float3 canonicalAxis = isSmall.x
                        ? float3(1, 0, 0)
                        : (isSmall.y
                            ? float3(0, 1, 0)
                            : float3(0, 0, 1));
                    normalOS = Gaussian3DRTRotate(canonicalAxis, rotation);
                    if (dot(normalOS, localVector) < 0)
                        normalOS = -normalOS;
                }
                else
                {
                    normalOS = -ObjectRayDirection();
                }

                if (dot(normalOS, normalOS) <= 1e-12)
                    normalOS = -ObjectRayDirection();

                // Row-vector multiplication by WorldToObject applies the
                // inverse-transpose required for normals.
                float3 normalWS = normalize(mul(
                    normalOS, (float3x3)WorldToObject3x4()));
                if (dot(normalWS, -WorldRayDirection()) < 0)
                    normalWS = -normalWS;
                return normalWS;
            }

            bool Gaussian3DRTIsDeleted(uint index)
            {
                if (_SplatBitsValid == 0)
                    return false;
                uint word = _SplatDeletedBits.Load((index / 32) * 4);
                return (word & (1u << (index & 31))) != 0;
            }

            bool Gaussian3DRTIsCut(float3 position, uint index)
            {
                bool finalCut = false;
                int signedIndex = (int)index;
                for (uint i = 0; i < _SplatCutoutsCount; ++i)
                {
                    GaussianCutoutShaderData cutout = _SplatCutouts[i];
                    uint type = cutout.typeAndFlags & 0xFFu;
                    if (type == 0xFFu)
                        continue;

                    bool included = false;
                    included = included ||
                        (signedIndex >= cutout.cutIndex0 &&
                         signedIndex < cutout.cutIndex1);
                    included = included ||
                        (signedIndex >= cutout.cutIndex2 &&
                         signedIndex < cutout.cutIndex3);
                    included = included ||
                        (signedIndex >= cutout.cutIndex4 &&
                         signedIndex < cutout.cutIndex5);
                    included = included ||
                        (signedIndex >= cutout.cutIndex6 &&
                         signedIndex < cutout.cutIndex7);
                    if (!included)
                        continue;

                    bool invert = (cutout.typeAndFlags & 0xFF00u) != 0;
                    float3 cutoutPosition = mul(cutout.mat, float4(position, 1)).xyz;
                    if (type == SPLAT_CUTOUT_TYPE_ELLIPSOID &&
                        dot(cutoutPosition, cutoutPosition) <= 1)
                        return invert;
                    if (type == SPLAT_CUTOUT_TYPE_BOX &&
                        all(abs(cutoutPosition) <= 1))
                        return invert;
                    finalCut |= !invert;
                }
                return finalCut;
            }

            float Gaussian3DRTSemanticAlphaMultiplier(uint semanticId)
            {
                float alphaMultiplier = 1.0;
                if (_SemanticAlphaLabelCount > 0)
                {
                    bool semanticMatches = false;
                    for (uint i = 0; i < _SemanticAlphaLabelCount; ++i)
                    {
                        semanticMatches =
                            semanticMatches ||
                            semanticId == _SemanticAlphaLabelIds[i];
                    }
                    alphaMultiplier = semanticMatches
                        ? max(_SemanticMatchAlphaMultiplier, 0.0)
                        : max(_SemanticNonMatchAlphaMultiplier, 0.0);
                }
                else if (_SemanticAlphaLabelId != 0)
                {
                    alphaMultiplier = semanticId == _SemanticAlphaLabelId
                        ? max(_SemanticMatchAlphaMultiplier, 0.0)
                        : max(_SemanticNonMatchAlphaMultiplier, 0.0);
                }

                if (_SemanticDimLabelCount > 0)
                {
                    bool semanticDimMatches = false;
                    for (uint i = 0; i < _SemanticDimLabelCount; ++i)
                    {
                        semanticDimMatches =
                            semanticDimMatches ||
                            semanticId == _SemanticDimLabelIds[i];
                    }
                    if (semanticDimMatches)
                        alphaMultiplier *=
                            max(_SemanticDimAlphaMultiplier, 0.0);
                }
                return alphaMultiplier;
            }

            bool Gaussian3DRTCutoutViewMatchesSemantic(
                CutoutViewShaderData cutout,
                uint semanticId)
            {
                if (cutout.semanticCount == 0)
                    return true;

                for (uint i = 0;
                     i < cutout.semanticCount && i < 256;
                     ++i)
                {
                    if (semanticId ==
                        _CutoutViewSemanticIds[cutout.semanticOffset + i])
                        return true;
                }
                return false;
            }

            void Gaussian3DRTApplyCutoutViewAlpha(
                float3 position,
                uint semanticId,
                inout float alphaMultiplier,
                inout float replacementAlpha,
                inout uint replaceAlpha)
            {
                for (uint i = 0; i < _CutoutViewsCount; ++i)
                {
                    CutoutViewShaderData cutout = _CutoutViews[i];
                    if (cutout.shape == 0xFFFFFFFFu)
                        continue;
                    if (!Gaussian3DRTCutoutViewMatchesSemantic(
                            cutout, semanticId))
                        continue;

                    float3 cutoutPosition =
                        mul(cutout.mat, float4(position, 1)).xyz;
                    bool inside = false;
                    float influence = 1.0;
                    float replacementInfluence = 1.0;
                    if (cutout.shape == CUTOUT_VIEW_SHAPE_ELLIPSOID)
                    {
                        inside =
                            dot(cutoutPosition, cutoutPosition) <= 1;
                    }
                    if (cutout.shape == CUTOUT_VIEW_SHAPE_CYLINDER)
                    {
                        float radius = cutout.width * 0.5;
                        float radialDistanceSq =
                            dot(cutoutPosition.xz, cutoutPosition.xz);
                        inside =
                            radialDistanceSq <= radius * radius &&
                            abs(cutoutPosition.y) <= cutout.height * 0.5;
                        if (inside &&
                            cutout.gaussianFalloff > 0.0 &&
                            radius > 0.0)
                        {
                            float radial01 =
                                saturate(sqrt(radialDistanceSq) / radius);
                            float sigma =
                                max(cutout.gaussianFalloff, 0.0001);
                            float edgeWeight =
                                exp(-0.5 / (sigma * sigma));
                            float gaussianWeight =
                                exp(-0.5 * radial01 * radial01 /
                                    (sigma * sigma));
                            influence = saturate(
                                (gaussianWeight - edgeWeight) /
                                max(1.0 - edgeWeight, 0.0001));
                            replacementInfluence = gaussianWeight;
                        }
                    }

                    if (inside)
                    {
                        float cutoutAlpha =
                            max(cutout.alphaMultiplier, 0.0);
                        if (cutout.replaceAlphaWithFalloff != 0)
                        {
                            replacementAlpha *=
                                lerp(
                                    1.0,
                                    cutoutAlpha,
                                    replacementInfluence);
                            replaceAlpha = 1;
                        }
                        else
                        {
                            alphaMultiplier *=
                                lerp(1.0, cutoutAlpha, influence);
                        }
                    }
                }
            }

            float Gaussian3DRTLoadSHScalar(uint gaussianIndex, uint scalarIndex)
            {
                uint word = _Gaussian3DGRTSH.Load(
                    gaussianIndex * 96 + (scalarIndex / 2) * 4);
                uint packedHalf = (scalarIndex & 1u) == 0
                    ? word & 0xFFFFu
                    : word >> 16;
                return f16tof32(packedHalf);
            }

            float3 Gaussian3DRTLoadSH(uint gaussianIndex, uint coefficientIndex)
            {
                uint scalar = coefficientIndex * 3;
                return float3(
                    Gaussian3DRTLoadSHScalar(gaussianIndex, scalar),
                    Gaussian3DRTLoadSHScalar(gaussianIndex, scalar + 1),
                    Gaussian3DRTLoadSHScalar(gaussianIndex, scalar + 2));
            }

            float3 Gaussian3DRTShadeSH(uint gaussianIndex, float3 baseColor, float3 viewDirection)
            {
                static const float C1 = 0.4886025;
                static const float C2[5] =
                {
                    1.0925484, -1.0925484, 0.3153916, -1.0925484, 0.5462742
                };
                static const float C3[7] =
                {
                    -0.5900436, 2.8906114, -0.4570458, 0.3731763,
                    -0.4570458, 1.4453057, -0.5900436
                };

                // Match UnityGaussianSplatting.ShadeSH: its input points from
                // the Gaussian to the camera and is negated before evaluation.
                float3 direction = -viewDirection;
                float x = direction.x;
                float y = direction.y;
                float z = direction.z;
                float3 result = _SHOnly != 0 ? 0.5.xxx : baseColor;

                if (_SHOrder >= 1)
                {
                    result += C1 * (
                        -Gaussian3DRTLoadSH(gaussianIndex, 0) * y +
                         Gaussian3DRTLoadSH(gaussianIndex, 1) * z -
                         Gaussian3DRTLoadSH(gaussianIndex, 2) * x);
                }
                if (_SHOrder >= 2)
                {
                    float xx = x * x;
                    float yy = y * y;
                    float zz = z * z;
                    float xy = x * y;
                    float yz = y * z;
                    float xz = x * z;
                    result +=
                        C2[0] * xy * Gaussian3DRTLoadSH(gaussianIndex, 3) +
                        C2[1] * yz * Gaussian3DRTLoadSH(gaussianIndex, 4) +
                        C2[2] * (2 * zz - xx - yy) * Gaussian3DRTLoadSH(gaussianIndex, 5) +
                        C2[3] * xz * Gaussian3DRTLoadSH(gaussianIndex, 6) +
                        C2[4] * (xx - yy) * Gaussian3DRTLoadSH(gaussianIndex, 7);
                }
                if (_SHOrder >= 3)
                {
                    float xx = x * x;
                    float yy = y * y;
                    float zz = z * z;
                    result +=
                        C3[0] * y * (3 * xx - yy) * Gaussian3DRTLoadSH(gaussianIndex, 8) +
                        C3[1] * x * y * z * Gaussian3DRTLoadSH(gaussianIndex, 9) +
                        C3[2] * y * (4 * zz - xx - yy) * Gaussian3DRTLoadSH(gaussianIndex, 10) +
                        C3[3] * z * (2 * zz - 3 * xx - 3 * yy) * Gaussian3DRTLoadSH(gaussianIndex, 11) +
                        C3[4] * x * (4 * zz - xx - yy) * Gaussian3DRTLoadSH(gaussianIndex, 12) +
                        C3[5] * z * (xx - yy) * Gaussian3DRTLoadSH(gaussianIndex, 13) +
                        C3[6] * x * (xx - 3 * yy) * Gaussian3DRTLoadSH(gaussianIndex, 14);
                }
                return max(result, 0);
            }

            #if RAY_TRACING_PROCEDURAL_GEOMETRY
            [shader("intersection")]
            void Gaussian3DGRTIntersection()
            {
                uint index = PrimitiveIndex();
                if (index >= _SplatCount || Gaussian3DRTIsDeleted(index))
                    return;

                Gaussian3DGRTData gaussian = _Gaussian3DGRTData[index];
                if (Gaussian3DRTIsCut(
                        gaussian.positionOpacity.xyz, index))
                    return;

                float4 rotation = gaussian.rotation;
                float rotationLengthSquared = max(dot(rotation, rotation), 1e-12);
                rotation *= rsqrt(rotationLengthSquared);
                float4 inverseRotation =
                    rotation * float4(-1, -1, -1, 1);
                float3 scale = max(abs(gaussian.scale.xyz * _SplatScale), 1e-8);

                float3 canonicalOrigin = Gaussian3DRTRotate(
                    ObjectRayOrigin() - gaussian.positionOpacity.xyz,
                    inverseRotation) / scale;
                // Do not normalize this direction. Keeping its parameterization
                // is what makes ReportHit distances comparable after transforms.
                float3 canonicalDirection = Gaussian3DRTRotate(
                    ObjectRayDirection(), inverseRotation) / scale;
                float denominator = dot(canonicalDirection, canonicalDirection);
                if (denominator <= 1e-20)
                    return;

                float distance = -dot(canonicalOrigin, canonicalDirection) / denominator;
                // A strict lower-bound rejection lets the ray-generation
                // shader resume exactly at the last retained hit without
                // either reporting it twice or skipping nearby Gaussians.
                if (distance <= RayTMin() || distance >= RayTCurrent())
                    return;

                float3 closestPoint = canonicalOrigin + distance * canonicalDirection;
                float squaredDistance = dot(closestPoint, closestPoint);
                float response = exp(-0.5 * squaredDistance);
                if (response < _Gaussian3DGRTKernelMinResponse)
                    return;

                uint semanticId = _SplatSemantic[index];
                float semanticAlphaMultiplier =
                    Gaussian3DRTSemanticAlphaMultiplier(semanticId);
                float cutoutReplacementAlpha = 1.0;
                uint replaceAlphaWithCutout = 0;
                Gaussian3DRTApplyCutoutViewAlpha(
                    gaussian.positionOpacity.xyz,
                    semanticId,
                    semanticAlphaMultiplier,
                    cutoutReplacementAlpha,
                    replaceAlphaWithCutout);

                float opacity = replaceAlphaWithCutout != 0
                    ? cutoutReplacementAlpha
                    : gaussian.positionOpacity.w *
                        _SplatOpacityScale *
                        semanticAlphaMultiplier;
                opacity = max(opacity, 0);
                float alpha = min(_Gaussian3DGRTAlphaClamp, response * opacity);
                if (alpha <= _Gaussian3DGRTAlphaCullThreshold)
                    return;

                float3 viewDirection = normalize(
                    ObjectRayOrigin() - gaussian.positionOpacity.xyz);
                float3 radiance = Gaussian3DRTShadeSH(
                    index, gaussian.baseColor.xyz, viewDirection);
                if (_DisplaySemantic != 0)
                    radiance = SemanticDebugColor(semanticId);

                Gaussian3DGRTAttributes attributes;
                Gaussian3DRTPackColorAlpha(
                    radiance, alpha, attributes.colorRG, attributes.colorBA);
                attributes.normalOct = Gaussian3DRTPackNormalOct(
                    Gaussian3DGRTSurfaceNormal(
                        gaussian, scale, rotation, inverseRotation));
                ReportHit(distance, 0, attributes);
            }
            #endif

            [shader("anyhit")]
            void Gaussian3DGRTAnyHit(
                inout Gaussian3DGRTGaussianPayload payload : SV_RayPayload,
                Gaussian3DGRTAttributes attributes : SV_IntersectionAttributes)
            {
                if (Gaussian3DRTIsStochasticPayload(payload.hitCount))
                {
                    float candidateDistance = RayTCurrent();
                    if (candidateDistance >= payload.hitDistance[0])
                    {
                        IgnoreHit();
                        return;
                    }

                    float3 candidateRadiance;
                    float candidateAlpha;
                    Gaussian3DRTUnpackColorAlpha(
                        attributes.colorRG,
                        attributes.colorBA,
                        candidateRadiance,
                        candidateAlpha);

                    uint candidateSeed = Gaussian3DRTStochasticPayloadSeed(
                        payload.hitCount);
                    candidateSeed = Gaussian3DRTHashCombine(
                        candidateSeed, InstanceID());
                    candidateSeed = Gaussian3DRTHashCombine(
                        candidateSeed, PrimitiveIndex());
                    candidateSeed = Gaussian3DRTHashCombine(
                        candidateSeed, asuint(candidateDistance));
                    if (Gaussian3DRTRandom01(candidateSeed) >=
                        saturate(candidateAlpha))
                    {
                        IgnoreHit();
                        return;
                    }

                    // Returning from any-hit without IgnoreHit commits this
                    // candidate but does not end traversal. A subsequently
                    // visited, nearer accepted Gaussian can still replace it.
                    // Keeping alpha opaque turns alpha into an acceptance
                    // probability rather than applying it a second time.
                    payload.hitDistance[0] = candidateDistance;
                    Gaussian3DRTPackColorAlpha(
                        candidateRadiance,
                        1.0,
                        payload.hitColorRG[0],
                        payload.hitColorBA[0]);
                    payload.hitNormalOct[0] = attributes.normalOct;
                    return;
                }

                Gaussian3DRTInsertHit(
                    payload,
                    RayTCurrent(),
                    attributes.colorRG,
                    attributes.colorBA,
                    attributes.normalOct);

                // Keep traversal open until the payload is full. Once it is,
                // allow a non-retained hit to become the committed hit so DXR
                // shortens traversal while still visiting everything closer
                // than the farthest retained Gaussian.
                if (payload.hitCount < GAUSSIAN_3DGRT_HITS_PER_PASS ||
                    payload.hitDistance[GAUSSIAN_3DGRT_HITS_PER_PASS - 1] > RayTCurrent())
                    IgnoreHit();
            }

            ENDHLSL
        }
    }
}
