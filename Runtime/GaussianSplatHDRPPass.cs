// SPDX-License-Identifier: MIT
#if GS_ENABLE_HDRP

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Serialization;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the proper usage of CustomPass.
    // Code below "seems to work" but I'm just fumbling along, without understanding any of it.
    [System.Serializable]
    public class GaussianSplatHDRPPass : CustomPass
    {
        [SerializeField]
        [FormerlySerializedAs("m_3DGRTDirectionalLight")]
        [Tooltip("Optional primary directional, point, or spot light used by 3DGRT. If no supported light is assigned here or below, RenderSettings.sun or the first enabled scene directional light is used.")]
        Light m_3DGRTPrimaryLight;
        /// <summary>Primary light for editor setup and application-created passes.</summary>
        public Light PrimaryLight
        {
            get => m_3DGRTPrimaryLight;
            set => m_3DGRTPrimaryLight = value;
        }
        [SerializeField]
        [Tooltip("Additional directional, point, and spot lights used by 3DGRT, in priority order. The primary light and the first supported entries up to the eight-light limit are rendered.")]
        List<Light> m_3DGRTAdditionalLights = new();
        [SerializeField, Min(0)]
        [FormerlySerializedAs("m_3DGRTDirectionalLightIntensity")]
        [Tooltip("Display-space multiplier for all 3DGRT lights. Per-light HDRP intensity remains relative: 100,000 lux for directional lights or 50 candela for point/spot lights maps to a multiplier of one before this value is applied.")]
        float m_3DGRTLightIntensity = 1.0f;
        [SerializeField, Min(0)]
        [Tooltip("Display-space multiplier for RenderSettings ambient light.")]
        float m_3DGRTAmbientIntensity = 1.0f;
        [SerializeField, Range(0.01f, 0.99f)]
        [Tooltip("The first Gaussian hit where remaining transmittance falls below this value defines the shaded surface. NVIDIA's 3DGRT default is 0.7.")]
        float m_3DGRTSurfaceTransmittance = 0.7f;
        [SerializeField, Min(0)]
        [Tooltip("Unshadowed learned SH radiance retained as emission. Set to zero for a fully relit Gaussian surface.")]
        float m_3DGRTGaussianEmissiveStrength = 1.0f;
        [SerializeField, Range(0.025f, 1.0f)]
        [Tooltip("Perceptual roughness used by the Gaussian surface BRDF.")]
        float m_3DGRTGaussianRoughness = 0.5f;
        [SerializeField, Range(0.0f, 1.0f)]
        [Tooltip("Metallic weight used by the Gaussian surface BRDF.")]
        float m_3DGRTGaussianMetallic;
        [SerializeField, Range(1.0f, 2.5f)]
        [Tooltip("Index of refraction used to derive the Gaussian dielectric Fresnel reflectance.")]
        float m_3DGRTGaussianIor = 1.5f;
        [SerializeField, Min(0.001f)]
        [Tooltip("Offset toward the light before tracing Gaussian shadow transmittance. NVIDIA's 3DGRT default is 0.2 world units.")]
        float m_3DGRTGaussianShadowBias = 0.2f;
        [SerializeField, Range(0.0f, 1.0f)]
        [Tooltip("Blends Gaussian self-shadow transmittance into direct lighting. Registered 3DGRT meshes always remain shadow participants.")]
        float m_3DGRTGaussianShadowStrength = 1.0f;
        [SerializeField]
        [Tooltip("Gaussian ray traversal strategy. Stochastic Any-Hit trades deterministic per-frame integration for temporally converged samples; Gaussian shadows remain deterministic unless enabled separately below.")]
        Gaussian3DGRTTraceStrategy m_3DGRTTraceStrategy =
            Gaussian3DGRTTraceStrategy.AllPass;
        [SerializeField]
        [Tooltip("Sample Gaussian shadow transmittance stochastically with independent per-light seeds. This can be evaluated independently of the primary-ray trace strategy and requires temporal convergence.")]
        bool m_3DGRTStochasticShadows;
        [SerializeField, Min(1)]
        [Tooltip("Number of stable frames accumulated by stochastic primary rays or Gaussian shadows before ray dispatch stops. Once converged, camera depth/color and other externally driven GPU content must remain static; changing a tracked 3DGRT setting restarts accumulation.")]
        int m_3DGRTTemporalSampleCount = 256;

        RTHandle m_RenderTarget;
        RTHandle m_RayTracingTarget;
        GaussianSplat3DGRTRenderSystem m_3DGRTSystem;
        readonly List<GaussianSplatRenderer> m_3DGRTRenderers = new();

        // Only used to answer "is there anything ray-only to draw"; the render
        // system gathers its own list for the trace itself.
        readonly List<Gaussian3DGRTTransmission> m_3DGRTTransmissionProbe = new();
        readonly List<Light> m_Resolved3DGRTLights = new();
        Light m_AutoDirectionalLight;

        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in an performance manner.
        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            m_3DGRTSystem = new GaussianSplat3DGRTRenderSystem();
        }

        void EnsureRasterTarget()
        {
            if (m_RenderTarget != null)
                return;

            m_RenderTarget = RTHandles.Alloc(Vector2.one,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_GaussianSplatRT");
        }

        void EnsureRayTracingTarget()
        {
            if (m_RayTracingTarget != null)
                return;

            m_RayTracingTarget = RTHandles.Alloc(Vector2.one,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp,
                enableRandomWrite: true, name: "_Gaussian3DGRTOutput");
        }

        static bool IsUsableLight(Light light)
        {
            if (light == null ||
                !light.enabled ||
                !light.gameObject.activeInHierarchy ||
                light.intensity <= 0 ||
                light.color.maxColorComponent <= 0)
                return false;

            return light.type == LightType.Directional ||
                   ((light.type == LightType.Point ||
                     light.type == LightType.Spot) &&
                    light.range > 0.0001f);
        }

        static bool IsUsableDirectionalLight(Light light)
        {
            return IsUsableLight(light) &&
                   light.type == LightType.Directional;
        }

        void AddResolvedLight(Light light)
        {
            if (!IsUsableLight(light) ||
                m_Resolved3DGRTLights.Count >=
                    GaussianSplat3DGRTRenderSystem.kMaxLights ||
                m_Resolved3DGRTLights.Contains(light))
                return;

            m_Resolved3DGRTLights.Add(light);
        }

        IReadOnlyList<Light> ResolveLights()
        {
            m_Resolved3DGRTLights.Clear();
            AddResolvedLight(m_3DGRTPrimaryLight);
            if (m_3DGRTAdditionalLights != null)
            {
                for (int i = 0; i < m_3DGRTAdditionalLights.Count; ++i)
                    AddResolvedLight(m_3DGRTAdditionalLights[i]);
            }

            // Preserve the original automatic directional-light fallback only
            // when the pass has no usable explicitly assigned lights.
            if (m_Resolved3DGRTLights.Count > 0)
                return m_Resolved3DGRTLights;

            if (IsUsableDirectionalLight(RenderSettings.sun))
            {
                AddResolvedLight(RenderSettings.sun);
                return m_Resolved3DGRTLights;
            }
            if (IsUsableDirectionalLight(m_AutoDirectionalLight))
            {
                AddResolvedLight(m_AutoDirectionalLight);
                return m_Resolved3DGRTLights;
            }

            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.InstanceID);
            for (int i = 0; i < lights.Length; ++i)
            {
                if (!IsUsableDirectionalLight(lights[i]))
                    continue;
                m_AutoDirectionalLight = lights[i];
                AddResolvedLight(m_AutoDirectionalLight);
                break;
            }
            return m_Resolved3DGRTLights;
        }

        protected override void Execute(CustomPassContext ctx)
        {
            var cam = ctx.hdCamera.camera;

            var system = GaussianSplatRenderSystem.instance;
            var cameraData = cam.GetComponent<HDAdditionalCameraData>();
            bool temporalAA =
                cameraData != null &&
                cameraData.antialiasing ==
                HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            bool fullResolution =
                ctx.hdCamera.actualWidth == cam.pixelWidth &&
                ctx.hdCamera.actualHeight == cam.pixelHeight;
            bool cameraSupports3DGRT =
                SystemInfo.supportsRayTracing &&
                cam.cameraType == CameraType.Game &&
                !ctx.hdCamera.msaaEnabled &&
                !cam.stereoEnabled &&
                !temporalAA &&
                fullResolution &&
                injectionPoint == CustomPassInjectionPoint.AfterPostProcess;
            bool renderRaster = system.GatherSplatsForCamera(cam, cameraSupports3DGRT);
            bool render3DGRT = false;
            if (cameraSupports3DGRT)
            {
                bool hasSplats =
                    system.Gather3DGRTSplatsForCamera(cam, m_3DGRTRenderers);

                // Meshes registered through Gaussian3DGRTTransmission are drawn
                // only by this pass, so it has to run for them even when the
                // scene has no Gaussian splats at all - otherwise disabling the
                // splats also silently removes every ray-only surface.
                Gaussian3DGRTTransmission.Gather(cam, m_3DGRTTransmissionProbe);
                render3DGRT = hasSplats || m_3DGRTTransmissionProbe.Count > 0;
                Gaussian3DGRTAccumulation.ReportPass(
                    m_3DGRTRenderers.Count, m_3DGRTTransmissionProbe.Count, render3DGRT);
            }
            else
            {
                Gaussian3DGRTAccumulation.ReportPass(0, 0, false);
            }

            if (renderRaster)
            {
                EnsureRasterTarget();
                ctx.cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);
                CoreUtils.SetRenderTarget(ctx.cmd, m_RenderTarget, ctx.cameraDepthBuffer, ClearFlag.Color,
                    new Color(0, 0, 0, 0));

                // Add sorting, view calculation and billboard drawing commands.
                Material matComposite = system.SortAndRenderSplats(cam, ctx.cmd);

                ctx.cmd.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
                CoreUtils.DrawFullScreen(ctx.cmd, matComposite, ctx.propertyBlock, shaderPassId: 0);
                ctx.cmd.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
            }
            else if (cam.cameraType != CameraType.Preview && m_RenderTarget != null)
            {
                m_RenderTarget.Release();
                m_RenderTarget = null;
            }

            if (render3DGRT)
            {
                EnsureRayTracingTarget();
                bool rendered = m_3DGRTSystem.Render(
                    m_3DGRTRenderers,
                    ctx.hdCamera,
                    ctx.cmd,
                    m_RayTracingTarget,
                    ctx.cameraDepthBuffer,
                    ctx.cameraColorBuffer,
                    ResolveLights(),
                    m_3DGRTLightIntensity,
                    m_3DGRTAmbientIntensity,
                    m_3DGRTSurfaceTransmittance,
                    m_3DGRTGaussianEmissiveStrength,
                    m_3DGRTGaussianRoughness,
                    m_3DGRTGaussianMetallic,
                    m_3DGRTGaussianIor,
                    m_3DGRTGaussianShadowBias,
                    m_3DGRTGaussianShadowStrength,
                    m_3DGRTTraceStrategy,
                    m_3DGRTStochasticShadows,
                    Gaussian3DGRTAccumulation.ResolveSampleCount(
                        m_3DGRTTemporalSampleCount),
                    cam.pixelWidth,
                    cam.pixelHeight);
                if (rendered)
                {
                    ctx.cmd.BeginSample("GaussianSplat.3DGRT.Compose");
                    CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
                    CoreUtils.DrawFullScreen(
                        ctx.cmd, m_3DGRTSystem.compositeMaterial, ctx.propertyBlock, shaderPassId: 0);
                    ctx.cmd.EndSample("GaussianSplat.3DGRT.Compose");
                }
                else
                {
                    m_3DGRTSystem.ReleaseAccelerationStructure();
                }
            }
        }

        protected override void Cleanup()
        {
            m_RenderTarget?.Release();
            m_RenderTarget = null;
            m_RayTracingTarget?.Release();
            m_RayTracingTarget = null;
            m_3DGRTSystem?.Dispose();
            m_3DGRTSystem = null;
        }
    }
}

#endif
