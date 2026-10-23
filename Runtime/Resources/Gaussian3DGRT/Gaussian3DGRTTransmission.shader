/*
 * The segmented mesh/particle transmission path used by this shader is based
 * on nvpro-samples/vk_gaussian_splatting.
 *
 * Copyright (c) 2019-2026, NVIDIA CORPORATION. All rights reserved.
 * Licensed under the Apache License, Version 2.0.
 * Unity/HDRP adaptation: Gaussian Ray Tracing for HDRP contributors.
 * Modified for Unity resource bindings, procedural/triangle hit integration,
 * layered/semantic data and custom lighting/shadow support. Packaged 2026-10-09.
 * SPDX-License-Identifier: Apache-2.0
 */
Shader "Gaussian Splatting/3DGRT Transmission"
{
    Properties
    {
        [ToggleUI] _ExcludeFrom3DGRT("Exclude From 3DGRT", Float) = 0
        [MainColor] _BaseColor("Base Color / Transmission Tint", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseColorMap("Base Color Map", 2D) = "white" {}
        // Unlit alpha composites the base color straight over what is behind it,
        // with no refraction, Fresnel, lighting or IOR. That is what an image
        // surface wants - a video frame or a photographic texture should read as
        // authored rather than as a pane of glass tinted by the scene. In this
        // mode the base color's alpha also scales opacity, so a texture can
        // carry its own cutout. Physical stays the default so existing
        // transmissive materials are unaffected.
        [ToggleUI] _UnlitAlpha("Unlit Alpha (image surface)", Float) = 0
        [Range(0, 1)] _Transmission("Transmission", Float) = 1
        [Range(0, 1)] _Metallic("Metallic", Float) = 0
        [Range(1, 2.5)] _Ior("Index of Refraction", Float) = 1.5
        [Range(0, 1)] _Roughness("Reflection Roughness", Float) = 0.05
        [HDR] _EmissionColor("Emission", Color) = (0, 0, 0, 0)
        [Range(0, 1)] _GaussianShadowStrength("Gaussian Shadow Strength", Float) = 1
        [IntRange] _MaxBounces("Maximum Interface Bounces", Range(1, 16)) = 8
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "Gaussian3DGRT"
            Tags { "LightMode" = "RayTracing" }

            HLSLPROGRAM
            #pragma raytracing gaussian_3dgrt_transmission

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "UnityRayTracingMeshUtils.cginc"
            #include "Gaussian3DGRTCommon.hlsl"

            float4 _BaseColor;
            float4 _BaseColorMap_ST;
            float4 _EmissionColor;
            float _UnlitAlpha;
            float _Transmission;
            float _Metallic;
            float _Ior;
            float _Roughness;
            float _GaussianShadowStrength;
            float _MaxBounces;

            Texture2D<float4> _BaseColorMap;
            SamplerState sampler_BaseColorMap;

            struct Gaussian3DGRTTriangleAttributes
            {
                float2 barycentrics;
            };

            float3 Gaussian3DGRTFetchNormal(uint vertexIndex)
            {
                if (UnityRayTracingHasVertexAttribute(kVertexAttributeNormal))
                    return UnityRayTracingFetchVertexAttribute3(
                        vertexIndex, kVertexAttributeNormal);
                return 0;
            }

            float2 Gaussian3DGRTFetchUv(uint vertexIndex)
            {
                if (UnityRayTracingHasVertexAttribute(kVertexAttributeTexCoord0))
                    return UnityRayTracingFetchVertexAttribute4(
                        vertexIndex, kVertexAttributeTexCoord0).xy;
                return 0;
            }

            [shader("closesthit")]
            void Gaussian3DGRTTransmissionClosestHit(
                inout Gaussian3DGRTMeshPayload payload : SV_RayPayload,
                Gaussian3DGRTTriangleAttributes attributes : SV_IntersectionAttributes)
            {
                uint3 indices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 position0 = UnityRayTracingFetchVertexAttribute3(
                    indices.x, kVertexAttributePosition);
                float3 position1 = UnityRayTracingFetchVertexAttribute3(
                    indices.y, kVertexAttributePosition);
                float3 position2 = UnityRayTracingFetchVertexAttribute3(
                    indices.z, kVertexAttributePosition);

                float3 barycentrics = float3(
                    1.0 - attributes.barycentrics.x - attributes.barycentrics.y,
                    attributes.barycentrics.x,
                    attributes.barycentrics.y);

                float3 geometricNormalOS =
                    cross(position1 - position0, position2 - position0);
                float3 geometricNormalWS = normalize(mul(
                    geometricNormalOS, (float3x3)WorldToObject3x4()));

                float3 normalOS =
                    Gaussian3DGRTFetchNormal(indices.x) * barycentrics.x +
                    Gaussian3DGRTFetchNormal(indices.y) * barycentrics.y +
                    Gaussian3DGRTFetchNormal(indices.z) * barycentrics.z;
                float3 normalWS = dot(normalOS, normalOS) > 1e-10
                    ? normalize(mul(normalOS, (float3x3)WorldToObject3x4()))
                    : geometricNormalWS;
                if (dot(normalWS, geometricNormalWS) < 0)
                    normalWS = -normalWS;

                float2 uv =
                    Gaussian3DGRTFetchUv(indices.x) * barycentrics.x +
                    Gaussian3DGRTFetchUv(indices.y) * barycentrics.y +
                    Gaussian3DGRTFetchUv(indices.z) * barycentrics.z;
                uv = uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;

                // DXR reports the true post-transform winding, including a
                // negative-determinant instance transform.
                bool frontFace = HitKind() == 0xFE;
                float3 facingNormal =
                    dot(WorldRayDirection(), normalWS) < 0
                    ? normalWS
                    : -normalWS;
                float4 baseSample =
                    _BaseColorMap.SampleLevel(sampler_BaseColorMap, uv, 0);
                float3 baseColor = _BaseColor.rgb * baseSample.rgb;

                bool unlitAlpha = _UnlitAlpha > 0.5;
                float transmission = saturate(_Transmission);
                if (unlitAlpha)
                {
                    // Let the base color's alpha carve the surface, so a
                    // textured image surface can be partly see-through without
                    // needing a second material or a separate cutout pass.
                    float coverage = saturate(_BaseColor.a * baseSample.a);
                    transmission = saturate(1.0 - (1.0 - transmission) * coverage);
                }

                payload.meshHit = 1;
                payload.meshDistance = RayTCurrent();
                payload.meshNormal = facingNormal;
                payload.meshBaseColor = max(baseColor, 0);
                payload.meshEmission = max(_EmissionColor.rgb, 0);
                payload.meshTransmission = transmission;
                payload.meshIor = clamp(_Ior, 1.0, 2.5);
                payload.meshRoughness = saturate(_Roughness);
                payload.meshMetallic = saturate(_Metallic);
                payload.meshShadowStrength = saturate(_GaussianShadowStrength);
                payload.meshMaxBounces = (uint)clamp(round(_MaxBounces), 1.0, 16.0);
                payload.meshFrontFace = frontFace ? 1 : 0;
                payload.meshPadding[0] = unlitAlpha
                    ? GAUSSIAN_3DGRT_MESH_MODE_UNLIT_ALPHA
                    : GAUSSIAN_3DGRT_MESH_MODE_PHYSICAL;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
