// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatPPLLRenderSystem
    {
        public static GaussianSplatPPLLRenderSystem instance => ms_Instance ??= new GaussianSplatPPLLRenderSystem();
        static GaussianSplatPPLLRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatPPLLRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly List<(GaussianSplatPPLLRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        public void RegisterSplat(GaussianSplatPPLLRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatPPLLRenderer r)
        {
            m_Splats.Remove(r);
            m_ActiveSplats.RemoveAll(item => item.Item1 == r);
        }

        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((gs, kvp.Value));
            }

            return m_ActiveSplats.Count != 0;
        }

        public void RenderSplats(Camera cam, CommandBuffer cmb)
        {
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                var mpb = kvp.Item2;
                if (!gs.m_MatSplats)
                    continue;

                Matrix4x4 matrix = gs.transform.localToWorldMatrix;
                mpb.Clear();
                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplaySemantic, 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, 0);

                cmb.BeginSample(GaussianSplatRenderSystem.s_ProfCalcView);
                gs.CalcViewData(cmb, cam, matrix);
                cmb.EndSample(GaussianSplatRenderSystem.s_ProfCalcView);

                cmb.BeginSample(GaussianSplatRenderSystem.s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, gs.m_MatSplats, 0, MeshTopology.Triangles, 6, gs.splatCount, mpb);
                cmb.EndSample(GaussianSplatRenderSystem.s_ProfDraw);
            }
        }
    }

    class GaussianSplatPPLLURPFeature : ScriptableRendererFeature
    {
        [SerializeField] ComputeShader m_PPLLCompute;
        [SerializeField] Shader m_CompositeShader;
        [SerializeField] LayerMask m_PPLLMeshLayerMask = ~0;
        [SerializeField, Range(1, 64)] int m_MaxFragmentsPerPixel = 4;
        [SerializeField, Range(1, 256)] int m_MaxResolveFragmentsPerPixel = 64;
        [SerializeField] bool m_RenderPPLLMeshes = true;

        class PPLLRenderPass : ScriptableRenderPass
        {
            static readonly int s_Heads = Shader.PropertyToID("_GaussianPPLLHeads");
            static readonly int s_Nodes = Shader.PropertyToID("_GaussianPPLLNodes");
            static readonly int s_Result = Shader.PropertyToID("_GaussianPPLLResult");
            static readonly int s_Dimensions = Shader.PropertyToID("_GaussianPPLLDimensions");
            static readonly int s_MaxNodes = Shader.PropertyToID("_GaussianPPLLMaxNodes");
            static readonly int s_MaxResolveFragments = Shader.PropertyToID("_GaussianPPLLMaxResolveFragments");

            readonly ShaderTagId m_ShaderTagId = new("GaussianPPLL");
            readonly ProfilingSampler m_ProfilingSampler = new("Gaussian Splat PPLL");

            RTHandle m_Heads;
            RTHandle m_Result;
            GraphicsBuffer m_Nodes;
            Material m_CompositeMaterial;
            int m_NodeCapacity;
            int m_Width;
            int m_Height;

            internal ScriptableRenderer renderer;
            internal ComputeShader compute;
            internal Shader compositeShader;
            internal LayerMask meshLayerMask;
            internal int maxFragmentsPerPixel;
            internal int maxResolveFragmentsPerPixel;
            internal bool renderPPLLMeshes;
            internal bool hasSplats;

            public void Dispose()
            {
                m_Heads?.Release();
                m_Result?.Release();
                m_Nodes?.Dispose();
                CoreUtils.Destroy(m_CompositeMaterial);
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
                desc.msaaSamples = 1;
                desc.depthBufferBits = 0;
                desc.enableRandomWrite = true;

                var headDesc = desc;
                headDesc.graphicsFormat = GraphicsFormat.R32_UInt;
                RenderingUtils.ReAllocateIfNeeded(ref m_Heads, headDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianPPLLHeads");

                var resultDesc = desc;
                resultDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref m_Result, resultDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianPPLLResult");

                m_Width = desc.width;
                m_Height = desc.height;
                int desiredCapacity = Mathf.Max(1, m_Width * m_Height * Mathf.Max(1, maxFragmentsPerPixel));
                if (m_Nodes == null || m_NodeCapacity != desiredCapacity)
                {
                    m_Nodes?.Dispose();
                    m_NodeCapacity = desiredCapacity;
                    m_Nodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Counter, m_NodeCapacity, 24)
                    {
                        name = "GaussianPPLLNodes"
                    };
                }

                if (!m_CompositeMaterial && compositeShader)
                    m_CompositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);

                ConfigureTarget(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!compute || !m_CompositeMaterial || m_Nodes == null || m_Heads == null || m_Result == null)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get("Gaussian Splat PPLL");
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    int clearKernel = compute.FindKernel("CSClearHeads");
                    cmd.SetComputeTextureParam(compute, clearKernel, s_Heads, m_Heads);
                    cmd.SetComputeTextureParam(compute, clearKernel, s_Result, m_Result);
                    cmd.SetComputeVectorParam(compute, s_Dimensions, new Vector4(m_Width, m_Height, 0, 0));
                    cmd.DispatchCompute(compute, clearKernel, Mathf.CeilToInt(m_Width / 8.0f), Mathf.CeilToInt(m_Height / 8.0f), 1);
                    cmd.SetBufferCounterValue(m_Nodes, 0);

                    cmd.SetGlobalTexture(s_Heads, m_Heads);
                    cmd.SetGlobalBuffer(s_Nodes, m_Nodes);
                    cmd.SetGlobalVector(s_Dimensions, new Vector4(m_Width, m_Height, 0, 0));
                    cmd.SetGlobalInt(s_MaxNodes, m_NodeCapacity);
                    cmd.SetRandomWriteTarget(1, m_Heads);
                    cmd.SetRandomWriteTarget(2, m_Nodes, true);

                    GaussianSplatPPLLRenderSystem.instance.RenderSplats(renderingData.cameraData.camera, cmd);
                }

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                if (renderPPLLMeshes)
                {
                    SortingSettings sortingSettings = new(renderingData.cameraData.camera)
                    {
                        criteria = SortingCriteria.None
                    };
                    DrawingSettings drawingSettings = new(m_ShaderTagId, sortingSettings)
                    {
                        perObjectData = PerObjectData.None
                    };
                    FilteringSettings filteringSettings = new(RenderQueueRange.transparent, meshLayerMask);
                    context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filteringSettings);
                }

                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    cmd.ClearRandomWriteTargets();

                    int resolveKernel = compute.FindKernel("CSResolve");
                    cmd.SetComputeTextureParam(compute, resolveKernel, s_Heads, m_Heads);
                    cmd.SetComputeBufferParam(compute, resolveKernel, s_Nodes, m_Nodes);
                    cmd.SetComputeTextureParam(compute, resolveKernel, s_Result, m_Result);
                    cmd.SetComputeVectorParam(compute, s_Dimensions, new Vector4(m_Width, m_Height, 0, 0));
                    cmd.SetComputeIntParam(compute, s_MaxResolveFragments, Mathf.Max(1, maxResolveFragmentsPerPixel));
                    cmd.DispatchCompute(compute, resolveKernel, Mathf.CeilToInt(m_Width / 8.0f), Mathf.CeilToInt(m_Height / 8.0f), 1);

                    Blitter.BlitCameraTexture(cmd, m_Result, renderer.cameraColorTargetHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, m_CompositeMaterial, 0);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        PPLLRenderPass m_Pass;
        bool m_HasSplats;

        public override void Create()
        {
            m_CompositeShader ??= Shader.Find("Hidden/Gaussian Splatting/PPLL Composite");
            m_Pass = new PPLLRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            m_HasSplats = GaussianSplatPPLLRenderSystem.instance.GatherSplatsForCamera(renderingData.cameraData.camera);
            if (!m_HasSplats && !m_RenderPPLLMeshes)
                return;

            m_Pass.renderer = renderer;
            m_Pass.compute = m_PPLLCompute;
            m_Pass.compositeShader = m_CompositeShader;
            m_Pass.meshLayerMask = m_PPLLMeshLayerMask;
            m_Pass.maxFragmentsPerPixel = m_MaxFragmentsPerPixel;
            m_Pass.maxResolveFragmentsPerPixel = m_MaxResolveFragmentsPerPixel;
            m_Pass.renderPPLLMeshes = m_RenderPPLLMeshes;
            m_Pass.hasSplats = m_HasSplats;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            m_Pass = null;
        }
    }
}

#endif
