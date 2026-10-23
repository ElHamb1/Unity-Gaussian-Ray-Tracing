// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatUnifiedPrimitiveRenderSystem
    {
        public static GaussianSplatUnifiedPrimitiveRenderSystem instance => ms_Instance ??= new GaussianSplatUnifiedPrimitiveRenderSystem();
        static GaussianSplatUnifiedPrimitiveRenderSystem ms_Instance;

        readonly HashSet<GaussianSplatUnifiedPrimitiveMesh> m_Meshes = new();
        readonly List<GaussianSplatUnifiedPrimitiveMesh> m_ActiveMeshes = new();
        readonly List<GaussianSplatRenderer> m_ActiveSplats = new();

        public IReadOnlyList<GaussianSplatUnifiedPrimitiveMesh> activeMeshes => m_ActiveMeshes;
        public GaussianSplatRenderer activeSplat => m_ActiveSplats.Count > 0 ? m_ActiveSplats[0] : null;
        public int activeSplatCount => m_ActiveSplats.Count;

        public void RegisterMesh(GaussianSplatUnifiedPrimitiveMesh mesh)
        {
            if (mesh)
                m_Meshes.Add(mesh);
        }

        public void UnregisterMesh(GaussianSplatUnifiedPrimitiveMesh mesh)
        {
            m_Meshes.Remove(mesh);
            m_ActiveMeshes.Remove(mesh);
        }

        public bool Gather(Camera cam, LayerMask gaussianLayerMask, LayerMask meshLayerMask)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            m_ActiveSplats.Clear();
            foreach (var splat in UnityEngine.Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None))
            {
                if (!splat || !splat.isActiveAndEnabled || !splat.gameObject.activeInHierarchy)
                    continue;
                if (!MatchesLayer(splat.gameObject, gaussianLayerMask))
                    continue;
                if (!splat.HasValidAsset || !splat.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add(splat);
            }

            if (m_ActiveSplats.Count > 1)
            {
                Transform camTr = cam.transform;
                m_ActiveSplats.Sort((a, b) =>
                {
                    float za = camTr.InverseTransformPoint(a.transform.position).z;
                    float zb = camTr.InverseTransformPoint(b.transform.position).z;
                    return za.CompareTo(zb);
                });
            }

            m_ActiveMeshes.Clear();
            foreach (var mesh in m_Meshes)
            {
                if (!mesh || !mesh.isRenderable)
                    continue;
                if (!MatchesLayer(mesh.gameObject, meshLayerMask))
                    continue;

                m_ActiveMeshes.Add(mesh);
            }

            return activeSplat != null;
        }

        static bool MatchesLayer(GameObject go, LayerMask mask)
        {
            return (mask.value & (1 << go.layer)) != 0;
        }
    }

    class GaussianSplatUnifiedPrimitiveURPFeature : ScriptableRendererFeature
    {
        enum MeshDepthMode
        {
            Centroid = 0,
            Nearest = 1,
            Farthest = 2,
        }

        [SerializeField] ComputeShader m_PrimitiveCompute;
        [SerializeField] Shader m_DrawShader;
        [SerializeField] Shader m_CompositeShader;
        [SerializeField] LayerMask m_GaussianLayerMask = ~0;
        [SerializeField] LayerMask m_MeshLayerMask = ~0;
        [SerializeField] MeshDepthMode m_MeshDepthMode = MeshDepthMode.Centroid;
        [SerializeField] bool m_RebuildMeshTopologyEveryFrame;
        [SerializeField] bool m_UseMeshFaceColors;
        [SerializeField] Color m_MeshOutsideColor = new(1, 1, 1, 0.35f);
        [SerializeField] Color m_MeshInsideColor = new(0.1f, 0.7f, 1, 0.35f);

        class UnifiedPrimitiveRenderPass : ScriptableRenderPass
        {
            [StructLayout(LayoutKind.Sequential)]
            struct MeshVertexData
            {
                public Vector3 position;
                public Vector3 normal;
                public Vector2 uv;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct MeshTriangleData
            {
                public uint index0;
                public uint index1;
                public uint index2;
                public uint meshIndex;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct MeshInstanceData
            {
                public Vector4 objectToWorld0;
                public Vector4 objectToWorld1;
                public Vector4 objectToWorld2;
                public Vector4 objectToWorld3;
                public Vector4 worldToObject0;
                public Vector4 worldToObject1;
                public Vector4 worldToObject2;
                public Vector4 worldToObject3;
                public Vector4 baseColor;
                public Vector4 baseMapST;
                public Vector4 faceOutsideColor;
                public Vector4 faceInsideColor;
                public Vector4 faceColorParams;
            }

            const int kThreadGroupSize = 256;
            const int kMeshVertexStride = 32;
            const int kMeshTriangleStride = 16;
            const int kMeshInstanceStride = 208;

            static readonly int s_GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            static readonly int s_UnifiedSortKeys = Shader.PropertyToID("_UnifiedSortKeys");
            static readonly int s_UnifiedSortValues = Shader.PropertyToID("_UnifiedSortValues");
            static readonly int s_UnifiedPrimitiveValues = Shader.PropertyToID("_UnifiedPrimitiveValues");
            static readonly int s_UnifiedPrimitiveCount = Shader.PropertyToID("_UnifiedPrimitiveCount");
            static readonly int s_UnifiedSplatCount = Shader.PropertyToID("_UnifiedSplatCount");
            static readonly int s_UnifiedMeshVertices = Shader.PropertyToID("_UnifiedMeshVertices");
            static readonly int s_UnifiedMeshTriangles = Shader.PropertyToID("_UnifiedMeshPrims");
            static readonly int s_UnifiedMeshInstances = Shader.PropertyToID("_UnifiedMeshInstances");
            static readonly int s_UnifiedMeshTriangleCount = Shader.PropertyToID("_UnifiedMeshPrimCount");
            static readonly int s_UnifiedMatrixVP = Shader.PropertyToID("_UnifiedMatrixVP");
            static readonly int s_UnifiedMeshDepthMode = Shader.PropertyToID("_UnifiedMeshDepthMode");
            static readonly int s_UnifiedMeshUseFaceColors = Shader.PropertyToID("_UnifiedMeshUseFaceColors");
            static readonly int s_UnifiedMeshOutsideColor = Shader.PropertyToID("_UnifiedMeshOutsideColor");
            static readonly int s_UnifiedMeshInsideColor = Shader.PropertyToID("_UnifiedMeshInsideColor");

            readonly ProfilingSampler m_ProfilingSampler = new("Gaussian Splat Unified Primitives");
            readonly MaterialPropertyBlock m_PropertyBlock = new();
            readonly List<MeshVertexData> m_MeshVerticesCpu = new();
            readonly List<MeshTriangleData> m_MeshTrianglesCpu = new();
            readonly List<MeshInstanceData> m_MeshInstancesCpu = new();

            RTHandle m_RenderTarget;
            GraphicsBuffer m_IndexBuffer;
            GraphicsBuffer m_SortKeys;
            GraphicsBuffer m_SortValues;
            GraphicsBuffer m_MeshVertices;
            GraphicsBuffer m_MeshTriangles;
            GraphicsBuffer m_MeshInstances;
            Material m_DrawMaterial;
            Material m_CompositeMaterial;
            GpuSorting m_Sorter;
            ComputeShader m_SorterCompute;
            int m_PrimitiveCapacity;
            int m_MeshVertexCapacity;
            int m_MeshTriangleCapacity;
            int m_MeshInstanceCapacity;
            int m_MeshTopologySignature = int.MinValue;
            int m_MeshTriangleCount;

            internal ScriptableRenderer renderer;
            internal ComputeShader primitiveCompute;
            internal Shader drawShader;
            internal Shader compositeShader;
            internal MeshDepthMode meshDepthMode;
            internal bool rebuildMeshTopologyEveryFrame;
            internal bool useMeshFaceColors;
            internal Color meshOutsideColor;
            internal Color meshInsideColor;

            public void Dispose()
            {
                m_RenderTarget?.Release();
                DisposeBuffer(ref m_IndexBuffer);
                DisposeBuffer(ref m_SortKeys);
                DisposeBuffer(ref m_SortValues);
                DisposeBuffer(ref m_MeshVertices);
                DisposeBuffer(ref m_MeshTriangles);
                DisposeBuffer(ref m_MeshInstances);
                m_Sorter?.DisposeResources();
                m_Sorter = null;
                CoreUtils.Destroy(m_DrawMaterial);
                CoreUtils.Destroy(m_CompositeMaterial);
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref m_RenderTarget, rtDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianUnifiedPrimitiveRT");
                cmd.SetGlobalTexture(s_GaussianSplatRT, m_RenderTarget);

                if (!m_DrawMaterial && drawShader)
                    m_DrawMaterial = CoreUtils.CreateEngineMaterial(drawShader);
                if (!m_CompositeMaterial && compositeShader)
                    m_CompositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
                EnsureIndexBuffer();

                ConfigureTarget(m_RenderTarget, renderer.cameraDepthTargetHandle);
                ConfigureClear(ClearFlag.Color, new Color(0, 0, 0, 0));
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var system = GaussianSplatUnifiedPrimitiveRenderSystem.instance;
                var splat = system.activeSplat;
                if (!splat || !primitiveCompute || !m_DrawMaterial || !m_CompositeMaterial || m_IndexBuffer == null)
                    return;

                BuildMeshBuffers(system.activeMeshes);

                int primitiveCount = splat.splatCount + m_MeshTriangleCount;
                if (primitiveCount <= 0)
                    return;

                EnsureSortBuffers(primitiveCount);
                if (m_Sorter == null || !m_Sorter.CanDispatch)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get("Gaussian Splat Unified Primitives");
                using (new ProfilingScope(cmd, m_ProfilingSampler))
                {
                    Matrix4x4 matrix = splat.transform.localToWorldMatrix;

                    cmd.BeginSample(GaussianSplatRenderSystem.s_ProfCalcView);
                    splat.CalcViewData(cmd, renderingData.cameraData.camera, matrix);
                    cmd.EndSample(GaussianSplatRenderSystem.s_ProfCalcView);

                    int kernel = primitiveCompute.FindKernel("CSBuildUnifiedPrimitiveSortData");
                    cmd.SetComputeBufferParam(primitiveCompute, kernel, s_UnifiedSortKeys, m_SortKeys);
                    cmd.SetComputeBufferParam(primitiveCompute, kernel, s_UnifiedSortValues, m_SortValues);
                    cmd.SetComputeBufferParam(primitiveCompute, kernel, GaussianSplatRenderer.Props.SplatViewData, splat.m_GpuView);
                    cmd.SetComputeBufferParam(primitiveCompute, kernel, s_UnifiedMeshVertices, m_MeshVertices);
                    cmd.SetComputeBufferParam(primitiveCompute, kernel, s_UnifiedMeshTriangles, m_MeshTriangles);
                    cmd.SetComputeBufferParam(primitiveCompute, kernel, s_UnifiedMeshInstances, m_MeshInstances);
                    cmd.SetComputeIntParam(primitiveCompute, s_UnifiedSplatCount, splat.splatCount);
                    cmd.SetComputeIntParam(primitiveCompute, s_UnifiedMeshTriangleCount, m_MeshTriangleCount);
                    cmd.SetComputeIntParam(primitiveCompute, s_UnifiedPrimitiveCount, primitiveCount);
                    cmd.SetComputeIntParam(primitiveCompute, s_UnifiedMeshDepthMode, (int)meshDepthMode);

                    Camera cam = renderingData.cameraData.camera;
                    Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                    cmd.SetComputeMatrixParam(primitiveCompute, s_UnifiedMatrixVP, gpuProjection * cam.worldToCameraMatrix);
                    cmd.DispatchCompute(primitiveCompute, kernel, Mathf.CeilToInt(primitiveCount / (float)kThreadGroupSize), 1, 1);

                    m_Sorter.Dispatch(cmd);

                    m_PropertyBlock.Clear();
                    splat.SetAssetDataOnMaterial(m_PropertyBlock);
                    m_PropertyBlock.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, splat.m_GpuView);
                    m_PropertyBlock.SetBuffer(s_UnifiedPrimitiveValues, m_SortValues);
                    m_PropertyBlock.SetBuffer(s_UnifiedMeshVertices, m_MeshVertices);
                    m_PropertyBlock.SetBuffer(s_UnifiedMeshTriangles, m_MeshTriangles);
                    m_PropertyBlock.SetBuffer(s_UnifiedMeshInstances, m_MeshInstances);
                    m_PropertyBlock.SetInt(s_UnifiedSplatCount, splat.splatCount);
                    m_PropertyBlock.SetInt(s_UnifiedMeshTriangleCount, m_MeshTriangleCount);
                    m_PropertyBlock.SetFloat(s_UnifiedMeshUseFaceColors, useMeshFaceColors ? 1.0f : 0.0f);
                    m_PropertyBlock.SetVector(s_UnifiedMeshOutsideColor, meshOutsideColor);
                    m_PropertyBlock.SetVector(s_UnifiedMeshInsideColor, meshInsideColor);

                    cmd.SetRenderTarget(m_RenderTarget, renderer.cameraDepthTargetHandle);
                    cmd.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);
                    cmd.BeginSample(GaussianSplatRenderSystem.s_ProfDraw);
                    cmd.DrawProcedural(m_IndexBuffer, Matrix4x4.identity, m_DrawMaterial, 0, MeshTopology.Triangles, 6, primitiveCount, m_PropertyBlock);
                    cmd.EndSample(GaussianSplatRenderSystem.s_ProfDraw);

                    cmd.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(cmd, m_RenderTarget, renderer.cameraColorTargetHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, m_CompositeMaterial, 0);
                    cmd.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            void EnsureIndexBuffer()
            {
                if (m_IndexBuffer != null)
                    return;

                m_IndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 2) { name = "GaussianUnifiedPrimitiveIndexBuffer" };
                m_IndexBuffer.SetData(new ushort[] { 0, 1, 2, 1, 3, 2 });
            }

            void EnsureSortBuffers(int primitiveCount)
            {
                if (m_SortKeys != null && m_PrimitiveCapacity == primitiveCount && m_SorterCompute == primitiveCompute)
                    return;

                DisposeBuffer(ref m_SortKeys);
                DisposeBuffer(ref m_SortValues);
                m_Sorter?.DisposeResources();

                m_PrimitiveCapacity = primitiveCount;
                m_SorterCompute = primitiveCompute;
                m_SortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, primitiveCount, 4) { name = "GaussianUnifiedPrimitiveSortKeys" };
                m_SortValues = new GraphicsBuffer(GraphicsBuffer.Target.Structured, primitiveCount, 4) { name = "GaussianUnifiedPrimitiveSortValues" };
                m_Sorter = new GpuSorting(GpuSorting.SortType.DeviceRadixSort, primitiveCompute);
                if (m_Sorter.CanDispatch)
                    m_Sorter.Initialize((uint)primitiveCount, m_SortKeys, m_SortValues);
            }

            void BuildMeshBuffers(IReadOnlyList<GaussianSplatUnifiedPrimitiveMesh> meshes)
            {
                int topologySignature = CalculateMeshTopologySignature(meshes);
                if (rebuildMeshTopologyEveryFrame || topologySignature != m_MeshTopologySignature)
                {
                    RebuildMeshTopology(meshes);
                    m_MeshTopologySignature = topologySignature;
                }

                RebuildMeshInstances(meshes);
                UploadMeshBuffers();
            }

            int CalculateMeshTopologySignature(IReadOnlyList<GaussianSplatUnifiedPrimitiveMesh> meshes)
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + meshes.Count;
                    foreach (var source in meshes)
                    {
                        Mesh mesh = source ? source.mesh : null;
                        hash = hash * 31 + (mesh ? mesh.GetInstanceID() : 0);
                        hash = hash * 31 + (mesh ? mesh.vertexCount : 0);
                        hash = hash * 31 + (mesh ? mesh.subMeshCount : 0);
                        hash = hash * 31 + (source ? source.subMeshIndex : 0);
                    }
                    return hash;
                }
            }

            void RebuildMeshTopology(IReadOnlyList<GaussianSplatUnifiedPrimitiveMesh> meshes)
            {
                m_MeshVerticesCpu.Clear();
                m_MeshTrianglesCpu.Clear();

                for (int meshIndex = 0; meshIndex < meshes.Count; ++meshIndex)
                {
                    var source = meshes[meshIndex];
                    Mesh mesh = source ? source.mesh : null;
                    if (!mesh)
                        continue;

                    int baseVertex = m_MeshVerticesCpu.Count;
                    try
                    {
                        Vector3[] vertices = mesh.vertices;
                        Vector3[] normals = mesh.normals;
                        Vector2[] uvs = mesh.uv;

                        for (int i = 0; i < vertices.Length; ++i)
                        {
                            m_MeshVerticesCpu.Add(new MeshVertexData
                            {
                                position = vertices[i],
                                normal = normals != null && normals.Length == vertices.Length ? normals[i] : Vector3.up,
                                uv = uvs != null && uvs.Length == vertices.Length ? uvs[i] : Vector2.zero,
                            });
                        }

                        int firstSubMesh = source.subMeshIndex >= 0 ? Mathf.Min(source.subMeshIndex, mesh.subMeshCount - 1) : 0;
                        int subMeshCount = source.subMeshIndex >= 0 ? 1 : mesh.subMeshCount;
                        for (int sub = firstSubMesh; sub < firstSubMesh + subMeshCount; ++sub)
                        {
                            int[] indices = mesh.GetTriangles(sub);
                            for (int i = 0; i + 2 < indices.Length; i += 3)
                            {
                                m_MeshTrianglesCpu.Add(new MeshTriangleData
                                {
                                    index0 = (uint)(baseVertex + indices[i]),
                                    index1 = (uint)(baseVertex + indices[i + 1]),
                                    index2 = (uint)(baseVertex + indices[i + 2]),
                                    meshIndex = (uint)meshIndex,
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[{nameof(GaussianSplatUnifiedPrimitiveMesh)}] Could not read mesh '{mesh.name}' for unified primitive rendering. Enable Read/Write on the mesh asset or use a readable mesh. {ex.Message}", source);
                    }
                }

                m_MeshTriangleCount = m_MeshTrianglesCpu.Count;
            }

            void RebuildMeshInstances(IReadOnlyList<GaussianSplatUnifiedPrimitiveMesh> meshes)
            {
                m_MeshInstancesCpu.Clear();
                for (int i = 0; i < meshes.Count; ++i)
                {
                    var source = meshes[i];
                    Matrix4x4 objectToWorld = source ? source.transform.localToWorldMatrix : Matrix4x4.identity;
                    Matrix4x4 worldToObject = source ? source.transform.worldToLocalMatrix : Matrix4x4.identity;
                    Color color = source ? source.ResolveBaseColor() : Color.clear;
                    Color outsideColor = source ? source.ResolveOutsideColor() : Color.clear;
                    Color insideColor = source ? source.ResolveInsideColor() : Color.clear;
                    float useFaceColors = source && source.useFaceColors ? 1.0f : 0.0f;

                    m_MeshInstancesCpu.Add(new MeshInstanceData
                    {
                        objectToWorld0 = objectToWorld.GetColumn(0),
                        objectToWorld1 = objectToWorld.GetColumn(1),
                        objectToWorld2 = objectToWorld.GetColumn(2),
                        objectToWorld3 = objectToWorld.GetColumn(3),
                        worldToObject0 = worldToObject.GetColumn(0),
                        worldToObject1 = worldToObject.GetColumn(1),
                        worldToObject2 = worldToObject.GetColumn(2),
                        worldToObject3 = worldToObject.GetColumn(3),
                        baseColor = new Vector4(color.r, color.g, color.b, color.a),
                        baseMapST = new Vector4(1, 1, 0, 0),
                        faceOutsideColor = new Vector4(outsideColor.r, outsideColor.g, outsideColor.b, outsideColor.a),
                        faceInsideColor = new Vector4(insideColor.r, insideColor.g, insideColor.b, insideColor.a),
                        faceColorParams = new Vector4(useFaceColors, 0, 0, 0),
                    });
                }
            }

            void UploadMeshBuffers()
            {
                EnsureStructuredBuffer(ref m_MeshVertices, ref m_MeshVertexCapacity, Mathf.Max(1, m_MeshVerticesCpu.Count), kMeshVertexStride, "GaussianUnifiedMeshVertices");
                EnsureStructuredBuffer(ref m_MeshTriangles, ref m_MeshTriangleCapacity, Mathf.Max(1, m_MeshTrianglesCpu.Count), kMeshTriangleStride, "GaussianUnifiedMeshTriangles");
                EnsureStructuredBuffer(ref m_MeshInstances, ref m_MeshInstanceCapacity, Mathf.Max(1, m_MeshInstancesCpu.Count), kMeshInstanceStride, "GaussianUnifiedMeshInstances");

                if (m_MeshVerticesCpu.Count > 0)
                    m_MeshVertices.SetData(m_MeshVerticesCpu);
                if (m_MeshTrianglesCpu.Count > 0)
                    m_MeshTriangles.SetData(m_MeshTrianglesCpu);
                if (m_MeshInstancesCpu.Count > 0)
                    m_MeshInstances.SetData(m_MeshInstancesCpu);
            }

            static void EnsureStructuredBuffer(ref GraphicsBuffer buffer, ref int capacity, int count, int stride, string name)
            {
                if (buffer != null && capacity == count)
                    return;

                DisposeBuffer(ref buffer);
                capacity = count;
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride) { name = name };
            }

            static void DisposeBuffer(ref GraphicsBuffer buffer)
            {
                buffer?.Dispose();
                buffer = null;
            }
        }

        UnifiedPrimitiveRenderPass m_Pass;
        LayerMask m_ActiveGaussianLayerMask;
        LayerMask m_ActiveMeshLayerMask;

        public override void Create()
        {
            m_DrawShader ??= Shader.Find("Gaussian Splatting/Unified Primitive Render");
            m_CompositeShader ??= Shader.Find("Hidden/Gaussian Splatting/Unified Primitive Composite");
            m_Pass = new UnifiedPrimitiveRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            m_ActiveGaussianLayerMask = m_GaussianLayerMask;
            m_ActiveMeshLayerMask = m_MeshLayerMask;

            var system = GaussianSplatUnifiedPrimitiveRenderSystem.instance;
            if (!system.Gather(renderingData.cameraData.camera, m_ActiveGaussianLayerMask, m_ActiveMeshLayerMask))
                return;

            m_Pass.renderer = renderer;
            m_Pass.primitiveCompute = m_PrimitiveCompute;
            m_Pass.drawShader = m_DrawShader;
            m_Pass.compositeShader = m_CompositeShader;
            m_Pass.meshDepthMode = m_MeshDepthMode;
            m_Pass.rebuildMeshTopologyEveryFrame = m_RebuildMeshTopologyEveryFrame;
            m_Pass.useMeshFaceColors = m_UseMeshFaceColors;
            m_Pass.meshOutsideColor = m_MeshOutsideColor;
            m_Pass.meshInsideColor = m_MeshInsideColor;
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
