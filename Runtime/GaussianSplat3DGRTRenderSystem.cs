// SPDX-License-Identifier: MIT
#if GS_ENABLE_HDRP

using System;
using System.Collections.Generic;
using UnityEngine;
#if !UNITY_6000_0_OR_NEWER
using UnityEngine.Experimental.Rendering;
#endif
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting.Runtime
{
    public enum Gaussian3DGRTTraceStrategy
    {
        AllPass = 0,
        StochasticAnyHit = 1
    }

    sealed class GaussianSplat3DGRTRenderSystem : IDisposable
    {
        const string kRayTracingResource = "Gaussian3DGRT/Gaussian3DGRayTracing";
        const string kProceduralResource = "Gaussian3DGRT/Gaussian3DGRTProcedural";
        const string kCompositeResource = "Gaussian3DGRT/Gaussian3DGRTComposite";
        const string kShaderPassName = "Gaussian3DGRT";
        const string kRayGenerationName = "RayGenGaussian3DGRT";
        const uint kGaussianMask = 0x01u;
        const uint kTransmissionMeshMask = 0x02u;
        const uint kPrimaryOnlyMeshMask = 0x04u;
        const uint kShadowOnlyMeshMask = 0x08u;
        internal const int kMaxLights = 8;

        const float kLightTypeDirectional = 0.0f;
        const float kLightTypePoint = 1.0f;
        const float kLightTypeSpot = 2.0f;
        const float kDirectionalReferenceIntensity = 100000.0f;
        const float kPunctualReferenceIntensity = 50.0f;

        sealed class InstanceState
        {
            internal int handle;
            internal int generation;
            internal Matrix4x4 localToWorld;
            internal readonly MaterialPropertyBlock properties = new();
        }

        sealed class TransmissionInstanceState
        {
            internal Renderer renderer;
            internal int stateHash;
            internal Matrix4x4 localToWorld;
        }

        static class Props
        {
            internal static readonly int AccelerationStructure = Shader.PropertyToID("_GaussianRTAS");
            internal static readonly int Output = Shader.PropertyToID("_Gaussian3DGRTOutput");
            internal static readonly int CameraDepth = Shader.PropertyToID("_GaussianCameraDepth");
            internal static readonly int CameraColor = Shader.PropertyToID("_GaussianCameraColor");
            internal static readonly int CameraPosition = Shader.PropertyToID("_GaussianCameraPosition");
            internal static readonly int CameraRelativeOffset = Shader.PropertyToID("_GaussianCameraRelativeOffset");
            internal static readonly int InvViewProj = Shader.PropertyToID("_GaussianInvViewProj");
            internal static readonly int ViewProj = Shader.PropertyToID("_GaussianViewProj");
            internal static readonly int CameraIsOrthographic = Shader.PropertyToID("_GaussianCameraIsOrthographic");
            internal static readonly int LightCount = Shader.PropertyToID("_Gaussian3DGRTLightCount");
            internal static readonly int LightPositionType =
                Shader.PropertyToID("_Gaussian3DGRTLightPositionType");
            internal static readonly int LightDirectionRange =
                Shader.PropertyToID("_Gaussian3DGRTLightDirectionRange");
            internal static readonly int LightColorAngleScale =
                Shader.PropertyToID("_Gaussian3DGRTLightColorAngleScale");
            internal static readonly int LightAttenuation =
                Shader.PropertyToID("_Gaussian3DGRTLightAttenuation");
            internal static readonly int AmbientColor = Shader.PropertyToID("_Gaussian3DGRTAmbientColor");
            internal static readonly int MaxRayDistance = Shader.PropertyToID("_Gaussian3DGRTMaxRayDistance");
            internal static readonly int BackgroundProjectionDistance =
                Shader.PropertyToID("_Gaussian3DGRTBackgroundProjectionDistance");
            internal static readonly int MinTransmittance = Shader.PropertyToID("_Gaussian3DGRTMinTransmittance");
            internal static readonly int MaxTracePasses = Shader.PropertyToID("_Gaussian3DGRTMaxTracePasses");
            internal static readonly int SurfaceTransmittance =
                Shader.PropertyToID("_Gaussian3DGRTSurfaceTransmittance");
            internal static readonly int GaussianEmissiveStrength =
                Shader.PropertyToID("_Gaussian3DGRTGaussianEmissiveStrength");
            internal static readonly int GaussianRoughness =
                Shader.PropertyToID("_Gaussian3DGRTGaussianRoughness");
            internal static readonly int GaussianMetallic =
                Shader.PropertyToID("_Gaussian3DGRTGaussianMetallic");
            internal static readonly int GaussianIor =
                Shader.PropertyToID("_Gaussian3DGRTGaussianIor");
            internal static readonly int GaussianShadowBias =
                Shader.PropertyToID("_Gaussian3DGRTGaussianShadowBias");
            internal static readonly int GaussianShadowStrength =
                Shader.PropertyToID("_Gaussian3DGRTGaussianShadowStrength");
            internal static readonly int HasTransmissionMeshes =
                Shader.PropertyToID("_Gaussian3DGRTHasTransmissionMeshes");
            internal static readonly int HasShadowOnlyMeshes =
                Shader.PropertyToID("_Gaussian3DGRTHasShadowOnlyMeshes");
            internal static readonly int TraceStrategy =
                Shader.PropertyToID("_Gaussian3DGRTTraceStrategy");
            internal static readonly int StochasticShadows =
                Shader.PropertyToID("_Gaussian3DGRTStochasticShadows");
            internal static readonly int FrameSampleId =
                Shader.PropertyToID("_Gaussian3DGRTFrameSampleId");
        }

        readonly Dictionary<GaussianSplatRenderer, InstanceState> m_Instances = new();
        readonly List<GaussianSplatRenderer> m_ValidRenderers = new();
        readonly Dictionary<Gaussian3DGRTTransmission, TransmissionInstanceState>
            m_TransmissionInstances = new();
        readonly List<Gaussian3DGRTTransmission> m_ValidTransmissionMeshes = new();
        readonly Vector4[] m_LightPositionType = new Vector4[kMaxLights];
        readonly Vector4[] m_LightDirectionRange = new Vector4[kMaxLights];
        readonly Vector4[] m_LightColorAngleScale = new Vector4[kMaxLights];
        readonly Vector4[] m_LightAttenuation = new Vector4[kMaxLights];

        RayTracingShader m_RayTracingShader;
        RayTracingAccelerationStructure m_AccelerationStructure;
        Material m_ProceduralMaterial;
        Material m_CompositeMaterial;
        bool m_ReportedMissingResources;
        bool m_HasStochasticHistory;
        int m_StochasticStateHash;
        int m_NextStochasticFrameSampleId;
        int m_LastStochasticRenderFrame = -1;
        int m_ActiveLightCount;

        internal Material compositeMaterial => m_CompositeMaterial;

        static int HashValue(int hash, int value)
        {
            unchecked
            {
                return hash * 31 + value;
            }
        }

        static int HashValue(int hash, float value)
        {
            return HashValue(hash, value.GetHashCode());
        }

        static int HashValue(int hash, bool value)
        {
            return HashValue(hash, value ? 1 : 0);
        }

        static int HashValue(int hash, Vector4 value)
        {
            return HashValue(hash, value.GetHashCode());
        }

        static int HashValue(int hash, Vector3 value)
        {
            return HashValue(hash, value.GetHashCode());
        }

        static int HashValue(int hash, Matrix4x4 value)
        {
            return HashValue(hash, value.GetHashCode());
        }

        internal void InvalidateStochasticHistory()
        {
            m_HasStochasticHistory = false;
            m_StochasticStateHash = 0;
            m_NextStochasticFrameSampleId = 0;
            m_LastStochasticRenderFrame = -1;
        }

        int ComputeStochasticStateHash(
            HDCamera hdCamera,
            RTHandle output,
            Gaussian3DGRTTraceStrategy traceStrategy,
            bool stochasticShadows,
            int temporalSampleCount,
            float lightIntensity,
            float ambientIntensity,
            float surfaceTransmittance,
            float gaussianEmissiveStrength,
            float gaussianRoughness,
            float gaussianMetallic,
            float gaussianIor,
            float gaussianShadowBias,
            float gaussianShadowStrength,
            float minTransmittance,
            int maxTracePasses,
            int width,
            int height)
        {
            unchecked
            {
                int hash = 17;
                Camera camera = hdCamera.camera;
                HDCamera.ViewConstants viewConstants = hdCamera.mainViewConstants;

                hash = HashValue(hash, camera != null ? camera.GetInstanceID() : 0);
                hash = HashValue(hash, camera != null ? camera.cullingMask : 0);
                // A video/depth surface changes camera color and depth without changing
                // a registered 3DGRT mesh or the video texture's instance ID.
                hash = HashValue(hash, Shader.GetGlobalInteger("_GaussianExternalSurfaceRevision"));
                hash = HashValue(
                    hash,
                    output != null ? output.nameID.GetHashCode() : 0);
                hash = HashValue(
                    hash,
                    output != null && output.rt != null
                        ? output.rt.GetInstanceID()
                        : 0);
                hash = HashValue(hash, width);
                hash = HashValue(hash, height);
                hash = HashValue(hash, viewConstants.worldSpaceCameraPos);
                hash = HashValue(hash, viewConstants.viewMatrix);
                hash = HashValue(hash, viewConstants.viewProjMatrix);
                hash = HashValue(hash, viewConstants.invViewProjMatrix);
                hash = HashValue(hash, camera != null && camera.orthographic);
                hash = HashValue(hash, camera != null ? camera.nearClipPlane : 0);
                hash = HashValue(hash, camera != null ? camera.farClipPlane : 0);

                hash = HashValue(hash, (int)traceStrategy);
                hash = HashValue(hash, stochasticShadows);
                hash = HashValue(hash, Mathf.Max(1, temporalSampleCount));
                hash = HashValue(hash, Mathf.Max(lightIntensity, 0));
                hash = HashValue(hash, Mathf.Max(ambientIntensity, 0));
                hash = HashValue(hash, Mathf.Clamp(surfaceTransmittance, 0.01f, 0.99f));
                hash = HashValue(hash, Mathf.Max(gaussianEmissiveStrength, 0));
                hash = HashValue(hash, Mathf.Clamp(gaussianRoughness, 0.025f, 1.0f));
                hash = HashValue(hash, Mathf.Clamp01(gaussianMetallic));
                hash = HashValue(hash, Mathf.Clamp(gaussianIor, 1.0f, 2.5f));
                hash = HashValue(hash, Mathf.Max(gaussianShadowBias, 0.001f));
                hash = HashValue(hash, Mathf.Clamp01(gaussianShadowStrength));
                hash = HashValue(hash, minTransmittance);
                hash = HashValue(hash, maxTracePasses);
                hash = HashValue(hash, RenderSettings.ambientLight.linear.GetHashCode());

                hash = HashValue(hash, m_ActiveLightCount);
                for (int i = 0; i < m_ActiveLightCount; ++i)
                {
                    hash = HashValue(hash, m_LightPositionType[i]);
                    hash = HashValue(hash, m_LightDirectionRange[i]);
                    hash = HashValue(hash, m_LightColorAngleScale[i]);
                    hash = HashValue(hash, m_LightAttenuation[i]);
                }

                hash = HashValue(hash, m_ValidRenderers.Count);
                for (int i = 0; i < m_ValidRenderers.Count; ++i)
                {
                    GaussianSplatRenderer renderer = m_ValidRenderers[i];
                    hash = HashValue(hash, renderer.GetInstanceID());
                    hash = HashValue(hash, renderer.transform.localToWorldMatrix);
                    hash = HashValue(hash, renderer.Compute3DGRTVisualStateHash());
                }

                hash = HashValue(hash, m_ValidTransmissionMeshes.Count);
                for (int i = 0; i < m_ValidTransmissionMeshes.Count; ++i)
                {
                    Gaussian3DGRTTransmission transmission =
                        m_ValidTransmissionMeshes[i];
                    Renderer renderer = transmission.targetRenderer;
                    hash = HashValue(hash, transmission.GetInstanceID());
                    hash = HashValue(
                        hash,
                        transmission.ComputeVisualStateHash());
                    hash = HashValue(
                        hash,
                        renderer != null
                            ? renderer.transform.localToWorldMatrix
                            : Matrix4x4.identity);
                }

                return hash;
            }
        }

        bool EnsurePipelineResources()
        {
            if (m_RayTracingShader == null)
                m_RayTracingShader = Resources.Load<RayTracingShader>(kRayTracingResource);

            if (m_ProceduralMaterial == null)
            {
                Shader shader = Resources.Load<Shader>(kProceduralResource);
                if (shader != null)
                {
                    m_ProceduralMaterial = new Material(shader)
                    {
                        name = "Gaussian 3DGRT Procedural",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    // This material is only used for procedural AABB instances.
                    // Select the intersection-shader variant explicitly instead
                    // of relying on an on-demand variant switch at dispatch.
                    m_ProceduralMaterial.EnableKeyword(
                        "RAY_TRACING_PROCEDURAL_GEOMETRY");
                }
            }

            if (m_CompositeMaterial == null)
            {
                Shader shader = Resources.Load<Shader>(kCompositeResource);
                if (shader != null)
                    m_CompositeMaterial = new Material(shader)
                    {
                        name = "Gaussian 3DGRT Composite",
                        hideFlags = HideFlags.HideAndDontSave
                    };
            }

            bool proceduralPassReady =
                m_ProceduralMaterial != null &&
                m_ProceduralMaterial.shader.isSupported;
            int proceduralPass = proceduralPassReady
                ? m_ProceduralMaterial.FindPass(kShaderPassName)
                : -1;
            proceduralPassReady &= proceduralPass >= 0;

#if UNITY_EDITOR
            // Unity 2022.3 can reach SetupProceduralInstanceShaders before an
            // on-demand DXR hit-group variant has finished compiling. That
            // native path does not safely handle the missing variant, so force
            // just this material pass to compile before the first dispatch.
            if (proceduralPassReady &&
                !ShaderUtil.IsPassCompiled(
                    m_ProceduralMaterial,
                    proceduralPass))
            {
                ShaderUtil.CompilePass(
                    m_ProceduralMaterial,
                    proceduralPass,
                    true);
                proceduralPassReady = ShaderUtil.IsPassCompiled(
                    m_ProceduralMaterial,
                    proceduralPass);
            }
#endif

            bool valid = m_RayTracingShader != null &&
                         proceduralPassReady &&
                         m_CompositeMaterial != null;
            if (!valid && !m_ReportedMissingResources)
            {
                Debug.LogError("Gaussian 3DGRT shader resources could not be loaded from the package.");
                m_ReportedMissingResources = true;
            }
            return valid;
        }

        internal bool Render(
            IReadOnlyList<GaussianSplatRenderer> renderers,
            HDCamera hdCamera,
            CommandBuffer cmd,
            RTHandle output,
            RTHandle cameraDepth,
            RTHandle cameraColor,
            IReadOnlyList<Light> lights,
            float lightIntensity,
            float ambientIntensity,
            float surfaceTransmittance,
            float gaussianEmissiveStrength,
            float gaussianRoughness,
            float gaussianMetallic,
            float gaussianIor,
            float gaussianShadowBias,
            float gaussianShadowStrength,
            Gaussian3DGRTTraceStrategy traceStrategy,
            bool stochasticShadows,
            int temporalSampleCount,
            int width,
            int height)
        {
            if (!SystemInfo.supportsRayTracing)
            {
                InvalidateStochasticHistory();
                return false;
            }
            if (!EnsurePipelineResources())
            {
                InvalidateStochasticHistory();
                for (int i = 0; i < renderers.Count; ++i)
                    renderers[i]?.Mark3DGRTUnavailable();
                return false;
            }

            m_ValidRenderers.Clear();
            for (int i = 0; i < renderers.Count; ++i)
            {
                GaussianSplatRenderer renderer = renderers[i];
                if (renderer != null && renderer.Ensure3DGRTResources(cmd))
                    m_ValidRenderers.Add(renderer);
            }

            Gaussian3DGRTTransmission.Gather(
                hdCamera.camera, m_ValidTransmissionMeshes);

            // Registered meshes are drawn by this pass and by nothing else -
            // their shaders declare a single "LightMode" = "RayTracing" pass
            // with Fallback Off, so there is no rasterized path to fall back
            // on. Bailing out here whenever no Gaussian splats were gathered
            // therefore took the reconstructed surface and the funnel with it,
            // and the view went empty the moment the splats were disabled.
            //
            // Trace whenever there is anything at all to trace; only a scene
            // with neither splats nor meshes skips the pass.
            if (m_ValidRenderers.Count == 0 && m_ValidTransmissionMeshes.Count == 0)
            {
                ReleaseAccelerationStructure();
                return false;
            }

            bool rebuild = m_AccelerationStructure == null ||
                           m_Instances.Count != m_ValidRenderers.Count ||
                           m_TransmissionInstances.Count !=
                           m_ValidTransmissionMeshes.Count;
            if (!rebuild)
            {
                for (int i = 0; i < m_ValidRenderers.Count; ++i)
                {
                    GaussianSplatRenderer renderer = m_ValidRenderers[i];
                    if (!m_Instances.TryGetValue(renderer, out InstanceState state) ||
                        state.generation != renderer.gpu3DGRTGeneration)
                    {
                        rebuild = true;
                        break;
                    }
                }
            }
            if (!rebuild)
            {
                for (int i = 0; i < m_ValidTransmissionMeshes.Count; ++i)
                {
                    Gaussian3DGRTTransmission transmission =
                        m_ValidTransmissionMeshes[i];
                    if (!m_TransmissionInstances.TryGetValue(
                            transmission, out TransmissionInstanceState state) ||
                        state.renderer != transmission.targetRenderer ||
                        state.stateHash != transmission.ComputeStateHash())
                    {
                        rebuild = true;
                        break;
                    }
                }
            }

            bool transformChanged = false;
            if (rebuild)
            {
                RebuildAccelerationStructure(cmd);
            }
            else
            {
                for (int i = 0; i < m_ValidRenderers.Count; ++i)
                {
                    GaussianSplatRenderer renderer = m_ValidRenderers[i];
                    InstanceState state = m_Instances[renderer];
                    Matrix4x4 localToWorld = renderer.transform.localToWorldMatrix;
                    if (state.localToWorld != localToWorld)
                    {
                        m_AccelerationStructure.UpdateInstanceTransform(state.handle, localToWorld);
                        state.localToWorld = localToWorld;
                        transformChanged = true;
                    }

                    state.properties.Clear();
                    renderer.Set3DGRTDataOnMaterial(state.properties);
                    m_AccelerationStructure.UpdateInstancePropertyBlock(state.handle, state.properties);
                }

                for (int i = 0; i < m_ValidTransmissionMeshes.Count; ++i)
                {
                    Gaussian3DGRTTransmission transmission =
                        m_ValidTransmissionMeshes[i];
                    TransmissionInstanceState state =
                        m_TransmissionInstances[transmission];
                    Renderer renderer = state.renderer;
                    Matrix4x4 localToWorld = renderer.transform.localToWorldMatrix;
                    if (state.localToWorld != localToWorld)
                    {
                        m_AccelerationStructure.UpdateInstanceTransform(renderer);
                        state.localToWorld = localToWorld;
                        transformChanged = true;
                    }
                }

                if (transformChanged)
                    cmd.BuildRayTracingAccelerationStructure(m_AccelerationStructure);
            }

            SetCameraParameters(
                hdCamera,
                cmd,
                lights,
                lightIntensity,
                ambientIntensity,
                surfaceTransmittance,
                gaussianEmissiveStrength,
                gaussianRoughness,
                gaussianMetallic,
                gaussianIor,
                gaussianShadowBias,
                gaussianShadowStrength);

            // This threshold terminates mesh paths too. With no Gaussians,
            // starting at one would terminate the initial unit throughput
            // before even the first mesh is shaded.
            bool hasGaussians = m_ValidRenderers.Count > 0;
            float minTransmittance = hasGaussians ? 1 : 0.01f;
            int maxTracePasses = hasGaussians ? 1 : 0;
            for (int i = 0; i < m_ValidRenderers.Count; ++i)
            {
                minTransmittance = Mathf.Min(
                    minTransmittance, m_ValidRenderers[i].m_3DGRTMinTransmittance);
                maxTracePasses = Mathf.Max(
                    maxTracePasses, m_ValidRenderers[i].m_3DGRTMaxTracePasses);
            }

            // Only the Gaussian integration is stochastic. Mesh-only frames
            // must keep updating, including video textures and camera color.
            if (!hasGaussians)
                stochasticShadows = false;
            if (!hasGaussians ||
                traceStrategy != Gaussian3DGRTTraceStrategy.StochasticAnyHit)
                traceStrategy = Gaussian3DGRTTraceStrategy.AllPass;
            bool temporalSampling =
                traceStrategy == Gaussian3DGRTTraceStrategy.StochasticAnyHit ||
                stochasticShadows;
            int frameSampleId = 0;
            if (temporalSampling)
            {
                int currentFrame = Time.frameCount;
                bool historyInterrupted =
                    m_LastStochasticRenderFrame >= 0 &&
                    currentFrame > m_LastStochasticRenderFrame + 1;
                m_LastStochasticRenderFrame = currentFrame;
                int stochasticStateHash = ComputeStochasticStateHash(
                    hdCamera,
                    output,
                    traceStrategy,
                    stochasticShadows,
                    temporalSampleCount,
                    lightIntensity,
                    ambientIntensity,
                    surfaceTransmittance,
                    gaussianEmissiveStrength,
                    gaussianRoughness,
                    gaussianMetallic,
                    gaussianIor,
                    gaussianShadowBias,
                    gaussianShadowStrength,
                    minTransmittance,
                    maxTracePasses,
                    width,
                    height);
                if (historyInterrupted ||
                    rebuild ||
                    transformChanged ||
                    !m_HasStochasticHistory ||
                    m_StochasticStateHash != stochasticStateHash)
                {
                    m_HasStochasticHistory = true;
                    m_StochasticStateHash = stochasticStateHash;
                    m_NextStochasticFrameSampleId = 0;
                    Gaussian3DGRTAccumulation.ReportRestart();
                }

                int sampleCount = Mathf.Max(1, temporalSampleCount);
                // Published before the early-out below, so a converged frame
                // still reports its progress rather than going silent.
                Gaussian3DGRTAccumulation.ReportTemporal(
                    Mathf.Min(m_NextStochasticFrameSampleId, sampleCount),
                    sampleCount,
                    Time.frameCount);
                if (m_NextStochasticFrameSampleId >= sampleCount)
                {
                    cmd.SetGlobalTexture(Props.Output, output.nameID);
                    return true;
                }
                frameSampleId = m_NextStochasticFrameSampleId;
            }
            else
            {
                // The deterministic path overwrites the output every frame and
                // must never be mistaken for reusable stochastic history.
                InvalidateStochasticHistory();
                Gaussian3DGRTAccumulation.ReportDeterministic(Time.frameCount);
            }

            cmd.SetRayTracingShaderPass(m_RayTracingShader, kShaderPassName);
            cmd.SetRayTracingAccelerationStructure(
                m_RayTracingShader, Props.AccelerationStructure, m_AccelerationStructure);
            cmd.SetRayTracingTextureParam(
                m_RayTracingShader, Props.Output, output.nameID);
            cmd.SetRayTracingTextureParam(
                m_RayTracingShader, Props.CameraDepth, cameraDepth.nameID);
            cmd.SetRayTracingTextureParam(
                m_RayTracingShader, Props.CameraColor, cameraColor.nameID);
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader, Props.MinTransmittance, minTransmittance);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader, Props.MaxTracePasses, maxTracePasses);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader,
                Props.HasTransmissionMeshes,
                m_ValidTransmissionMeshes.Count > 0 ? 1 : 0);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader,
                Props.HasShadowOnlyMeshes,
                HasShadowOnlyMesh() ? 1 : 0);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader,
                Props.TraceStrategy,
                (int)traceStrategy);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader,
                Props.StochasticShadows,
                stochasticShadows ? 1 : 0);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader,
                Props.FrameSampleId,
                frameSampleId);
            cmd.BeginSample("GaussianSplat.3DGRT.Dispatch");
            cmd.DispatchRays(
                m_RayTracingShader,
                kRayGenerationName,
                (uint)Mathf.Max(width, 1),
                (uint)Mathf.Max(height, 1),
                1);
            cmd.EndSample("GaussianSplat.3DGRT.Dispatch");
            if (temporalSampling)
                ++m_NextStochasticFrameSampleId;
            cmd.SetGlobalTexture(Props.Output, output.nameID);
            return true;
        }

        // Lets every light loop skip its extra tool-shadow trace outright
        // when no shadow-only occluder is registered.
        bool HasShadowOnlyMesh()
        {
            for (int i = 0; i < m_ValidTransmissionMeshes.Count; ++i)
            {
                if (m_ValidTransmissionMeshes[i].IsShadowRayOnly())
                    return true;
            }
            return false;
        }

        void RebuildAccelerationStructure(CommandBuffer cmd)
        {
            ReleaseAccelerationStructure();
            m_AccelerationStructure = new RayTracingAccelerationStructure();

            for (int i = 0; i < m_ValidRenderers.Count; ++i)
            {
                GaussianSplatRenderer renderer = m_ValidRenderers[i];
                InstanceState state = new()
                {
                    generation = renderer.gpu3DGRTGeneration,
                    localToWorld = renderer.transform.localToWorldMatrix
                };
                renderer.Set3DGRTDataOnMaterial(state.properties);
#if UNITY_6000_0_OR_NEWER
                var config = new RayTracingAABBsInstanceConfig(
                    renderer.gpu3DGRTAabbs,
                    renderer.splatCount,
                    false,
                    m_ProceduralMaterial)
                {
                    aabbOffset = 0,
                    opaqueMaterial = false,
                    materialProperties = state.properties,
                    mask = kGaussianMask
                };
                state.handle = m_AccelerationStructure.AddInstance(
                    config,
                    state.localToWorld,
                    (uint)i);
#else
                state.handle = m_AccelerationStructure.AddInstance(
                    renderer.gpu3DGRTAabbs,
                    (uint)renderer.splatCount,
                    false,
                    state.localToWorld,
                    m_ProceduralMaterial,
                    false,
                    state.properties,
                    kGaussianMask,
                    (uint)i);
#endif
                m_Instances.Add(renderer, state);
            }

            for (int i = 0; i < m_ValidTransmissionMeshes.Count; ++i)
            {
                Gaussian3DGRTTransmission transmission =
                    m_ValidTransmissionMeshes[i];
                Renderer renderer = transmission.targetRenderer;
                var state = new TransmissionInstanceState
                {
                    renderer = renderer,
                    stateHash = transmission.ComputeStateHash(),
                    localToWorld = renderer.transform.localToWorldMatrix
                };
                m_AccelerationStructure.AddInstance(
                    renderer,
                    transmission.CreateSubMeshFlags(),
                    false,
                    false,
                    transmission.IsShadowRayOnly()
                        ? kShadowOnlyMeshMask
                        : transmission.IsPrimaryRayOnly()
                            ? kPrimaryOnlyMeshMask
                            : kTransmissionMeshMask,
                    (uint)(m_ValidRenderers.Count + i));
                m_TransmissionInstances.Add(transmission, state);
            }

            cmd.BuildRayTracingAccelerationStructure(m_AccelerationStructure);
        }

        internal void ReleaseAccelerationStructure()
        {
            m_AccelerationStructure?.Dispose();
            m_AccelerationStructure = null;
            m_Instances.Clear();
            m_TransmissionInstances.Clear();
            InvalidateStochasticHistory();
        }

        void SetLightParameters(
            CommandBuffer cmd,
            IReadOnlyList<Light> lights,
            float lightIntensity)
        {
            int lightCount = 0;
            float displayIntensity = Mathf.Max(lightIntensity, 0);
            if (lights != null)
            {
                for (int i = 0;
                     i < lights.Count && lightCount < kMaxLights;
                     ++i)
                {
                    Light light = lights[i];
                    if (light == null ||
                        !light.enabled ||
                        !light.gameObject.activeInHierarchy)
                        continue;

                    float lightType;
                    Vector3 position = Vector3.zero;
                    Vector3 direction = -light.transform.forward;
                    float range = 0;
                    float rangeAttenuationScale = 0;
                    float rangeAttenuationBias = 0;
                    float angleScale = 0;
                    float angleOffset = 1;
                    HDAdditionalLightData additionalLightData =
                        light.GetComponent<HDAdditionalLightData>();
                    switch (light.type)
                    {
                        case LightType.Directional:
                            lightType = kLightTypeDirectional;
                            break;
                        case LightType.Point:
                            if (light.range <= 0.0001f)
                                continue;
                            lightType = kLightTypePoint;
                            position = light.transform.position;
                            direction = Vector3.zero;
                            range = light.range;
                            break;
                        case LightType.Spot:
                            if (light.range <= 0.0001f)
                                continue;
                            lightType = kLightTypeSpot;
                            position = light.transform.position;
                            range = light.range;

                            float outerHalfAngle =
                                Mathf.Clamp(light.spotAngle, 0, 179) *
                                0.5f * Mathf.Deg2Rad;
                            float innerSpotAngle = light.innerSpotAngle;
#if !UNITY_6000_3_OR_NEWER
                            if (additionalLightData != null)
                            {
                                // HDRP stores its cone control as a percentage
                                // and explicitly does not use Light.innerSpotAngle.
                                innerSpotAngle = light.spotAngle *
                                    additionalLightData.innerSpotPercent01;
                            }
#endif
                            float innerHalfAngle =
                                Mathf.Clamp(
                                    innerSpotAngle,
                                    0,
                                    light.spotAngle) *
                                0.5f * Mathf.Deg2Rad;
                            float outerCos = Mathf.Cos(outerHalfAngle);
                            float innerCos = Mathf.Cos(innerHalfAngle);
                            angleScale = 1.0f /
                                         Mathf.Max(
                                             innerCos - outerCos,
                                             0.0001f);
                            angleOffset = -outerCos * angleScale;
                            break;
                        default:
                            continue;
                    }

                    if (light.type != LightType.Directional)
                    {
                        float inverseRangeSquared = 1.0f / (range * range);
                        bool applyRangeAttenuation =
                            additionalLightData == null ||
                            additionalLightData.applyRangeAttenuation;
                        if (applyRangeAttenuation)
                        {
                            rangeAttenuationScale = inverseRangeSquared;
                            rangeAttenuationBias = 1.0f;
                        }
                        else
                        {
                            // Match HDRP's inverse-square-only window. The
                            // explicit range check in the shader still bounds
                            // this custom light's influence volume.
                            rangeAttenuationScale =
                                4096.0f * inverseRangeSquared;
                            rangeAttenuationBias = 16777216.0f;
                        }
                    }

                    float referenceIntensity =
                        light.type == LightType.Directional
                            ? kDirectionalReferenceIntensity
                            : kPunctualReferenceIntensity;
                    float normalizedIntensity =
                        additionalLightData != null
                            ? Mathf.Max(light.intensity, 0) /
                              referenceIntensity
                            : Mathf.Max(light.intensity, 0);
                    if (normalizedIntensity <= 0)
                        continue;
                    Color color = light.color.linear *
                                  (displayIntensity * normalizedIntensity);
                    m_LightPositionType[lightCount] = new Vector4(
                        position.x, position.y, position.z, lightType);
                    m_LightDirectionRange[lightCount] = new Vector4(
                        direction.x, direction.y, direction.z, range);
                    m_LightColorAngleScale[lightCount] = new Vector4(
                        color.r, color.g, color.b, angleScale);
                    // w carries the light's *authored* slot - its index in the
                    // pass's Primary + Additional list - which is what a
                    // per-material exclusion mask is indexed by.
                    //
                    // It cannot be indexed by lightCount: this loop skips
                    // disabled, unsupported, zero-range and zero-intensity
                    // lights, so disabling one renumbers every light after it
                    // and a mask would silently start excluding the wrong ones.
                    m_LightAttenuation[lightCount] = new Vector4(
                        rangeAttenuationScale,
                        rangeAttenuationBias,
                        angleOffset,
                        i);
                    ++lightCount;
                }
            }

            m_ActiveLightCount = lightCount;

            cmd.SetRayTracingIntParam(
                m_RayTracingShader, Props.LightCount, lightCount);
            cmd.SetRayTracingVectorArrayParam(
                m_RayTracingShader,
                Props.LightPositionType,
                m_LightPositionType);
            cmd.SetRayTracingVectorArrayParam(
                m_RayTracingShader,
                Props.LightDirectionRange,
                m_LightDirectionRange);
            cmd.SetRayTracingVectorArrayParam(
                m_RayTracingShader,
                Props.LightColorAngleScale,
                m_LightColorAngleScale);
            cmd.SetRayTracingVectorArrayParam(
                m_RayTracingShader,
                Props.LightAttenuation,
                m_LightAttenuation);
        }

        void SetCameraParameters(
            HDCamera hdCamera,
            CommandBuffer cmd,
            IReadOnlyList<Light> lights,
            float lightIntensity,
            float ambientIntensity,
            float surfaceTransmittance,
            float gaussianEmissiveStrength,
            float gaussianRoughness,
            float gaussianMetallic,
            float gaussianIor,
            float gaussianShadowBias,
            float gaussianShadowStrength)
        {
            HDCamera.ViewConstants viewConstants = hdCamera.mainViewConstants;
            Vector3 cameraPosition = viewConstants.worldSpaceCameraPos;
            Vector4 viewTranslation = viewConstants.viewMatrix.GetColumn(3);
            bool cameraRelative =
                Mathf.Abs(viewTranslation.x) < 1e-6f &&
                Mathf.Abs(viewTranslation.y) < 1e-6f &&
                Mathf.Abs(viewTranslation.z) < 1e-6f;
            Vector3 cameraRelativeOffset =
                cameraRelative ? cameraPosition : Vector3.zero;
            cmd.SetRayTracingVectorParam(
                m_RayTracingShader, Props.CameraPosition, cameraPosition);
            cmd.SetRayTracingVectorParam(
                m_RayTracingShader, Props.CameraRelativeOffset, cameraRelativeOffset);
            cmd.SetRayTracingMatrixParam(
                m_RayTracingShader, Props.InvViewProj, viewConstants.invViewProjMatrix);
            cmd.SetRayTracingMatrixParam(
                m_RayTracingShader, Props.ViewProj, viewConstants.viewProjMatrix);
            cmd.SetRayTracingIntParam(
                m_RayTracingShader, Props.CameraIsOrthographic, hdCamera.camera.orthographic ? 1 : 0);

            Camera camera = hdCamera.camera;
            Color ambientColor =
                RenderSettings.ambientLight.linear *
                Mathf.Max(ambientIntensity, 0);

            SetLightParameters(cmd, lights, lightIntensity);
            cmd.SetRayTracingVectorParam(
                m_RayTracingShader, Props.AmbientColor, ambientColor);
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.SurfaceTransmittance,
                Mathf.Clamp(surfaceTransmittance, 0.01f, 0.99f));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.GaussianEmissiveStrength,
                Mathf.Max(gaussianEmissiveStrength, 0));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.GaussianRoughness,
                Mathf.Clamp(gaussianRoughness, 0.025f, 1.0f));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.GaussianMetallic,
                Mathf.Clamp01(gaussianMetallic));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.GaussianIor,
                Mathf.Clamp(gaussianIor, 1.0f, 2.5f));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.GaussianShadowBias,
                Mathf.Max(gaussianShadowBias, 0.001f));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.GaussianShadowStrength,
                Mathf.Clamp01(gaussianShadowStrength));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.MaxRayDistance,
                Mathf.Max(camera.farClipPlane * 2.0f, 1.0f));
            cmd.SetRayTracingFloatParam(
                m_RayTracingShader,
                Props.BackgroundProjectionDistance,
                Mathf.Clamp(camera.farClipPlane * 0.25f, 1.0f, 100.0f));
        }

        public void Dispose()
        {
            ReleaseAccelerationStructure();

            CoreUtils.Destroy(m_ProceduralMaterial);
            CoreUtils.Destroy(m_CompositeMaterial);
            m_ProceduralMaterial = null;
            m_CompositeMaterial = null;
            m_RayTracingShader = null;
        }
    }
}

#endif
