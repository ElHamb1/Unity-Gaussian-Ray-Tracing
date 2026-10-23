// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam, bool rayTracing3DGRTAvailable = false)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                bool rasterFallback =
                    gs.m_RenderBackend == GaussianSplatRenderer.RenderBackend.RayTracing3DGRT &&
                    gs.m_3DGRTRasterFallback &&
                    (!rayTracing3DGRTAvailable || gs.is3DGRTRasterFallbackActive);
                if (gs.m_RenderBackend != GaussianSplatRenderer.RenderBackend.Raster && !rasterFallback)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by depth from camera
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                // Raster accumulation is front-to-back (Blend OneMinusDstAlpha One),
                // so higher render orders must be submitted first to appear on top.
                int order = b.Item1.m_RenderOrder.CompareTo(a.Item1.m_RenderOrder);
                if (order != 0)
                    return order;
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        internal bool Gather3DGRTSplatsForCamera(Camera cam, List<GaussianSplatRenderer> result)
        {
            result.Clear();
            if (cam.cameraType == CameraType.Preview || !SystemInfo.supportsRayTracing)
                return false;

            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled ||
                    gs.m_RenderBackend != GaussianSplatRenderer.RenderBackend.RayTracing3DGRT ||
                    gs.is3DGRTRasterFallbackActive ||
                    !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                result.Add(gs);
            }

            return result.Count != 0;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (Time.frameCount - gs.m_LastSortedFrame >= gs.m_SortNthFrame - 1         // Only sort every nth frame
                    && (!gs.m_CenterEyeOnly || gs.m_LastSortedFrame != Time.frameCount)     // dont sort multiple times a frame 
                    && gs.m_gpuSortType != GpuSorting.SortType.None)    
                {
                    gs.SortPoints(cmb, cam, matrix);
                    gs.m_LastSortedFrame = Time.frameCount;
                }

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugSemantic => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplaySemantic, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugSemantic ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam, matrix);
                cmb.EndSample(s_ProfCalcView);

                // draw
                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public enum RenderBackend
        {
            Raster,
            RayTracing3DGRT,
        }

        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugSemantic,
            DebugBoxes,
            DebugChunkBounds,
        }
        public GaussianSplatAsset m_Asset;
        [Tooltip("Raster keeps the original sorted billboard renderer. Ray Tracing 3DGRT uses procedural AABBs and DXR in HDRP.")]
        public RenderBackend m_RenderBackend = RenderBackend.Raster;
        [Tooltip("Rendering order compared to other raster splats. Within the same order, splats are sorted by distance. Higher orders render on top of lower orders.")]
        public int m_RenderOrder;
        public GaussianSplatSemanticLabelTable m_SemanticLabels;
        [Tooltip("Semantic label id to highlight with alpha multipliers. Set to 0 to disable semantic alpha filtering.")]
        public uint m_SemanticAlphaLabelId;
        [Tooltip("Semantic label ids to highlight with alpha multipliers. If empty, Semantic Alpha Label Id is used.")]
        public List<uint> m_SemanticAlphaLabelIds = new();
        [Tooltip("Semantic label ids to dim independently of highlighted semantic labels.")]
        public List<uint> m_SemanticDimLabelIds = new();
        [Tooltip("Alpha multiplier for gaussians whose semantic id matches the selected label.")]
        public float m_SemanticMatchAlphaMultiplier = 1.0f;
        [Tooltip("Alpha multiplier for gaussians whose semantic id does not match the selected label.")]
        public float m_SemanticNonMatchAlphaMultiplier = 1.0f;
        [Tooltip("Alpha multiplier for gaussians whose semantic id matches dimmed labels.")]
        public float m_SemanticDimAlphaMultiplier = 1.0f;

        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        [Tooltip("In VR settings, only sort 1 time for both eyes (middle)")]
        public bool m_CenterEyeOnly = false;

        [Header("3DGRT")]
        [Range(0.0001f, 0.25f)]
        [Tooltip("Gaussian response at the finite ray-tracing cutoff. NVIDIA's quadratic-kernel default is 0.0113 (approximately three sigma).")]
        public float m_3DGRTKernelMinResponse = 0.0113f;
        [Range(0.0f, 0.25f)]
        [Tooltip("Intersections with less final alpha are discarded.")]
        public float m_3DGRTAlphaCullThreshold = 1.0f / 255.0f;
        [Range(0.5f, 0.9999f)]
        [Tooltip("Maximum alpha contributed by one Gaussian.")]
        public float m_3DGRTAlphaClamp = 0.99f;
        [Range(0.0001f, 0.25f)]
        [Tooltip("Stop tracing once remaining transmittance falls below this value.")]
        public float m_3DGRTMinTransmittance = 0.01f;
        [Range(1, 200)]
        [Tooltip("Maximum repeated any-hit collection passes. Each pass sorts eight intersections.")]
        public int m_3DGRTMaxTracePasses = 8;
        [Tooltip("Use the raster renderer when 3DGRT is unavailable for the current pipeline, camera, device, or asset.")]
        public bool m_3DGRTRasterFallback = true;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;
        public CutoutView[] m_CutoutViews;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities_deviceRadixSort;
        public ComputeShader m_CSSplatUtilities_fidelityFX;
        private ComputeShader m_CSSplatUtilities;
        
        // layer stuff
        [SerializeField] public List<int2> m_LayerActivationState;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        private GraphicsBuffer m_GpuLayerData;
        GraphicsBuffer m_GpuSemanticData;
        GraphicsBuffer m_GpuSemanticAlphaLabelIds;
        GraphicsBuffer m_GpuSemanticDimLabelIds;
        GraphicsBuffer m_GpuCutoutViewSemanticIds;
        uint[] m_SemanticAlphaLabelIdsGpuData = Array.Empty<uint>();
        uint[] m_SemanticDimLabelIdsGpuData = Array.Empty<uint>();
        readonly List<uint> m_SemanticHighlightIdsScratch = new();
        readonly List<uint> m_SemanticDimIdsScratch = new();
        readonly List<CutoutView> m_CutoutViewsScratch = new();
        readonly Dictionary<int, int2> m_ActiveLayerIndexRanges = new();
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;
        GraphicsBuffer m_Gpu3DGRTData;
        GraphicsBuffer m_Gpu3DGRTSH;
        GraphicsBuffer m_Gpu3DGRTAabbs;
        float m_Gpu3DGRTSplatScale = -1;
        float m_Gpu3DGRTKernelMinResponse = -1;
        int m_Gpu3DGRTGeneration;
        bool m_Reported3DGRTFailure;
        bool m_3DGRTRasterFallbackActive;
        RenderBackend m_PreviousRenderBackend;
        internal Camera m_centerEyeCamera;
        internal Matrix4x4 m_centerCamMatrix;
        public bool m_OptimizeForQuest;


        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuCutoutViews;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        public GpuSorting.SortType m_gpuSortType = GpuSorting.SortType.DeviceRadixSort;
        GpuSorting m_Sorter;
        GpuSorting.SortType m_ConfiguredSortType = (GpuSorting.SortType)(-1);
        GpuSorting.SortType m_ResolvedSortType = GpuSorting.SortType.None;
        int m_UtilityKernelThreadGroupSize = 1024;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_LastSortedFrame;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        uint[] m_SemanticIdsCpu = Array.Empty<uint>();

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatLayer = Shader.PropertyToID("_SplatLayer");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSemantic = Shader.PropertyToID("_SplatSemantic");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SemanticAlphaLabelId = Shader.PropertyToID("_SemanticAlphaLabelId");
            public static readonly int SemanticAlphaLabelIds = Shader.PropertyToID("_SemanticAlphaLabelIds");
            public static readonly int SemanticAlphaLabelCount = Shader.PropertyToID("_SemanticAlphaLabelCount");
            public static readonly int SemanticDimLabelIds = Shader.PropertyToID("_SemanticDimLabelIds");
            public static readonly int SemanticDimLabelCount = Shader.PropertyToID("_SemanticDimLabelCount");
            public static readonly int SemanticMatchAlphaMultiplier = Shader.PropertyToID("_SemanticMatchAlphaMultiplier");
            public static readonly int SemanticNonMatchAlphaMultiplier = Shader.PropertyToID("_SemanticNonMatchAlphaMultiplier");
            public static readonly int SemanticDimAlphaMultiplier = Shader.PropertyToID("_SemanticDimAlphaMultiplier");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplaySemantic = Shader.PropertyToID("_DisplaySemantic");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int CutoutViewsCount = Shader.PropertyToID("_CutoutViewsCount");
            public static readonly int CutoutViews = Shader.PropertyToID("_CutoutViews");
            public static readonly int CutoutViewSemanticIds = Shader.PropertyToID("_CutoutViewSemanticIds");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
            public static readonly int OptimizeForQuest = Shader.PropertyToID("_OptimizeForQuest");
            public static readonly int Gaussian3DGRTData = Shader.PropertyToID("_Gaussian3DGRTData");
            public static readonly int Gaussian3DGRTSH = Shader.PropertyToID("_Gaussian3DGRTSH");
            public static readonly int Gaussian3DGRTAabbs = Shader.PropertyToID("_Gaussian3DGRTAabbs");
            public static readonly int Gaussian3DGRTDataOut = Shader.PropertyToID("_Gaussian3DGRTDataOut");
            public static readonly int Gaussian3DGRTSHOut = Shader.PropertyToID("_Gaussian3DGRTSHOut");
            public static readonly int Gaussian3DGRTAabbsOut = Shader.PropertyToID("_Gaussian3DGRTAabbsOut");
            public static readonly int Gaussian3DGRTKernelRadius = Shader.PropertyToID("_Gaussian3DGRTKernelRadius");
            public static readonly int Gaussian3DGRTMinAabbExtent = Shader.PropertyToID("_Gaussian3DGRTMinAabbExtent");
            public static readonly int Gaussian3DGRTKernelMinResponse = Shader.PropertyToID("_Gaussian3DGRTKernelMinResponse");
            public static readonly int Gaussian3DGRTAlphaCullThreshold = Shader.PropertyToID("_Gaussian3DGRTAlphaCullThreshold");
            public static readonly int Gaussian3DGRTAlphaClamp = Shader.PropertyToID("_Gaussian3DGRTAlphaClamp");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;
        public bool HasSemanticData => m_SemanticIdsCpu != null && m_SemanticIdsCpu.Length == m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        static readonly string[] s_KernelNames =
        {
            "CSSetIndices",
            "CSCalcDistances",
            "CSCalcViewData",
            "CSUpdateEditData",
            "CSInitEditData",
            "CSClearBuffer",
            "CSInvertSelection",
            "CSSelectAll",
            "CSOrBuffers",
            "CSSelectionUpdate",
            "CSTranslateSelection",
            "CSRotateSelection",
            "CSScaleSelection",
            "CSExportData",
            "CSCopySplats",
        };

        readonly int[] m_KernelIds = new int[s_KernelNames.Length];
        ComputeShader m_KernelCacheShader;

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posDataSize > 0 &&
            m_Asset.otherDataSize > 0 &&
            m_Asset.shDataSize > 0 &&
            m_Asset.colorDataSize > 0;
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        const int kGpuViewDataSize = 40;

        void InvalidateKernelCache()
        {
            m_KernelCacheShader = null;
            Array.Fill(m_KernelIds, -1);
        }

        void EnsureKernelCache()
        {
            if (m_CSSplatUtilities == null)
                return;
            if (m_KernelCacheShader == m_CSSplatUtilities)
                return;

            for (int i = 0; i < s_KernelNames.Length; ++i)
                m_KernelIds[i] = m_CSSplatUtilities.FindKernel(s_KernelNames[i]);

            m_KernelCacheShader = m_CSSplatUtilities;
        }

        int GetKernelIndex(KernelIndices kernel)
        {
            EnsureKernelCache();
            return m_KernelIds[(int)kernel];
        }

        int GetUtilityKernelThreadGroupSize()
        {
            return m_UtilityKernelThreadGroupSize;
        }


        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
            {
                DisposeResourcesForAsset();
                return;
            }

            UpdateRessources();
        }
        
        static int SplatIndexToTextureIndex(uint idx)                                                                                                                                                                                  
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.kTextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.kTextureWidth + x);
        }
        
        public void UpdateRessources()
        {
            DisposeResourcesForAsset();
            
            if (!HasValidAsset)
                return;

            var activeLayers = m_LayerActivationState.Where(kv => kv.y > 0).Select(kv => kv.x).ToHashSet();
            var activeLayerAssets = asset.LayerData.Where(l => activeLayers.Contains(l.layer)).ToList();
            m_SplatCount = activeLayerAssets.Sum(GetLayerGpuSplatCount);

            if (m_SplatCount == 0) 
                return;
            
            m_centerEyeCamera = Camera.main;
            
            int posSize, posMarker;
            int otherSize, otherMarker;
            int shSize, shMarker;
            int colorSize, colorMarker;
            int chunkSize, chunkMarker;
            posSize = posMarker = otherSize = otherMarker = shSize = shMarker = colorSize = colorMarker = chunkSize = chunkMarker = 0;
            var layerInfo = asset.layerInfo;
            
            foreach (var layerAssets in activeLayerAssets)
            {
                posSize += (int) layerAssets.m_PosData.dataSize;
                otherSize += (int) layerAssets.m_OtherData.dataSize;
                shSize += (int) (layerAssets.m_SHData != null ? layerAssets.m_SHData.dataSize : 0);
                colorSize += (int) layerAssets.m_ColorData.dataSize;
                chunkSize += (int) (layerAssets.m_ChunkData != null ? layerAssets.m_ChunkData.dataSize : 0);
            }

            var posDataArr = new NativeArray<byte>(NextMultipleOf(posSize, 4), Allocator.Temp);
            var otherDataArr = new NativeArray<byte>(NextMultipleOf(otherSize, 4), Allocator.Temp);
            var shDataArr = new NativeArray<byte>(NextMultipleOf(shSize, 4), Allocator.Temp);
            var colorDataArr = new NativeArray<byte>(colorSize, Allocator.TempJob);
            var chunkDataArr = new NativeArray<byte>(chunkSize, Allocator.Temp);
            var semanticDataArr = new NativeArray<uint>(m_SplatCount, Allocator.Temp);
            int semanticMarker = 0;
            m_ActiveLayerIndexRanges.Clear();
            
            foreach (var layerAssets in activeLayerAssets)
            {
                var posAssetData = layerAssets.m_PosData.GetData<byte>();
                var posSub = posDataArr.GetSubArray(posMarker, posAssetData.Length);
                posMarker += posAssetData.Length;
                posSub.CopyFrom(posAssetData);
                
                var otherAssetData = layerAssets.m_OtherData.GetData<byte>();
                var otherSub = otherDataArr.GetSubArray(otherMarker, otherAssetData.Length);
                otherMarker += otherAssetData.Length;
                otherSub.CopyFrom(otherAssetData);
                
                var colorAssetData = layerAssets.m_ColorData.GetData<byte>();
                var colorSub = colorDataArr.GetSubArray(colorMarker, colorAssetData.Length);
                colorMarker += colorAssetData.Length;
                colorSub.CopyFrom(colorAssetData);

                if (layerAssets.m_SHData != null)
                {
                    var shAssetData = layerAssets.m_SHData.GetData<byte>();
                    var shSub = shDataArr.GetSubArray(shMarker, shAssetData.Length);
                    shMarker += shAssetData.Length;
                    shSub.CopyFrom(shAssetData);
                }

                if (layerAssets.m_ChunkData != null)
                {
                    var chunkAssetData = layerAssets.m_ChunkData.GetData<byte>();
                    var chunkSub = chunkDataArr.GetSubArray(chunkMarker, chunkAssetData.Length);
                    chunkMarker += chunkAssetData.Length;
                    chunkSub.CopyFrom(chunkAssetData);
                }

                int layerGpuSplatCount = GetLayerGpuSplatCount(layerAssets);
                m_ActiveLayerIndexRanges[layerAssets.layer] = new int2(semanticMarker, semanticMarker + layerGpuSplatCount);
                int layerSplatCount = layerInfo.TryGetValue(layerAssets.layer, out int count) ? count : 0;
                if (layerAssets.m_SemanticData != null && layerSplatCount > 0 && layerGpuSplatCount > 0)
                {
                    var semanticAssetData = layerAssets.m_SemanticData.GetData<uint>();
                    int semanticCount = math.min(math.min(layerSplatCount, layerGpuSplatCount), semanticAssetData.Length);
                    var semanticSub = semanticDataArr.GetSubArray(semanticMarker, semanticCount);
                    semanticSub.CopyFrom(semanticAssetData.GetSubArray(0, semanticCount));
                }
                semanticMarker += layerGpuSplatCount;
            }
            
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, posDataArr.Length / 4, 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(posDataArr);
            
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, otherDataArr.Length / 4, 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(otherDataArr);
            
            if (asset.ClusteredSHData != null) shDataArr = asset.ClusteredSHData.GetData<byte>();
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, shDataArr.Length / 4, 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(shDataArr);

            var convertedColorData = GaussianImageCreator.CreateColorData(colorDataArr.Reinterpret<float4>(1), asset.colorFormat);
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(m_SplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var textureFlags = TextureCreationFlags.DontInitializePixels |
                               TextureCreationFlags.DontUploadUponCreate;
#if !UNITY_6000_0_OR_NEWER
            textureFlags |= TextureCreationFlags.IgnoreMipmapLimit;
#endif
            var tex = new Texture2D(texWidth, texHeight, texFormat, textureFlags) { name = "GaussianColorData" };
            tex.SetPixelData(convertedColorData, 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            convertedColorData.Dispose();
            
            if (chunkDataArr.Length > 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    chunkDataArr.Length / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>(),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(chunkDataArr);
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuSemanticData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, semanticDataArr.Length, 4) { name = "GaussianSemanticData" };
            m_GpuSemanticData.SetData(semanticDataArr);
            m_SemanticIdsCpu = semanticDataArr.ToArray();
            UpdateSemanticHighlightBuffer();
            UpdateSemanticDimBuffer();
            
            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            posDataArr.Dispose();
            otherDataArr.Dispose();
            shDataArr.Dispose();
            colorDataArr.Dispose();
            chunkDataArr.Dispose();
            semanticDataArr.Dispose();

            UpdateSortingType(m_gpuSortType);
            InitSortBuffers(splatCount);
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            // init keys buffer to splat indices
            int kernelIndex = GetKernelIndex(KernelIndices.SetIndices);
            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            int groupSize = GetUtilityKernelThreadGroupSize();
            m_CSSplatUtilities.Dispatch(kernelIndex, (m_GpuSortDistances.count + groupSize - 1) / groupSize, 1, 1);

            m_Sorter.Initialize((uint) count, m_GpuSortDistances, m_GpuSortKeys);
        }
        
        static int NextMultipleOf(int size, int multipleOf)
        {
            return (size + multipleOf - 1) / multipleOf * multipleOf;
        }

        int GetLayerGpuSplatCount(GaussianSplatAsset.LayerAssets layerAssets)
        {
            int posStride = GaussianSplatAsset.GetVectorSize(asset.posFormat);
            return posStride > 0 && layerAssets.m_PosData != null
                ? (int)(layerAssets.m_PosData.dataSize / posStride)
                : 0;
        }

        public void OnEnable()
        {
            m_PreviousRenderBackend = m_RenderBackend;
            Initialize();
        }
        
        private void Initialize()
        {
            UpdateSortingType(m_gpuSortType);

            m_LastSortedFrame = 0;
            if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_CSSplatUtilities == null)
                return;
            if (!SystemInfo.supportsComputeShaders)
                return;

            m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
            m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
            m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
            m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};

#if GS_ENABLE_URP
            if (this is GaussianSplatPPLLRenderer ppllRenderer)
                GaussianSplatPPLLRenderSystem.instance.RegisterSplat(ppllRenderer);
            else
#endif
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
            UpdateRessources();
        }

        public void UpdateSortingType(GpuSorting.SortType sortType)
        {
            m_Sorter?.DisposeResources();

            m_ConfiguredSortType = sortType;
            m_ResolvedSortType = GpuSorting.SortType.None;
            bool deviceRadixSortAllowed = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan;
            GpuSorting.SortType requestedSortType = sortType;
            if (requestedSortType == GpuSorting.SortType.DeviceRadixSort && !deviceRadixSortAllowed)
                requestedSortType = GpuSorting.SortType.FidelityFX;

            var utilityShader = deviceRadixSortAllowed && m_CSSplatUtilities_deviceRadixSort != null
                ? m_CSSplatUtilities_deviceRadixSort
                : m_CSSplatUtilities_fidelityFX;

            m_CSSplatUtilities = requestedSortType switch
            {
                GpuSorting.SortType.DeviceRadixSort => deviceRadixSortAllowed ? m_CSSplatUtilities_deviceRadixSort : null,
                GpuSorting.SortType.FidelityFX => m_CSSplatUtilities_fidelityFX,
                _ => utilityShader
            };

            m_Sorter = new GpuSorting(requestedSortType, m_CSSplatUtilities);
            if (m_Sorter.CanDispatch)
            {
                m_ResolvedSortType = requestedSortType;
            }
            else if (requestedSortType == GpuSorting.SortType.DeviceRadixSort && m_CSSplatUtilities_fidelityFX != null)
            {
                var fallbackSorter = new GpuSorting(GpuSorting.SortType.FidelityFX, m_CSSplatUtilities_fidelityFX);
                if (fallbackSorter.CanDispatch)
                {
                    m_CSSplatUtilities = m_CSSplatUtilities_fidelityFX;
                    m_Sorter = fallbackSorter;
                    m_ResolvedSortType = GpuSorting.SortType.FidelityFX;
                    Debug.LogWarning($"[{nameof(GaussianSplatRenderer)}] {name}: DeviceRadixSort is unsupported here, falling back to FidelityFX sorting.", this);
                }
            }
            else if (requestedSortType == GpuSorting.SortType.FidelityFX && deviceRadixSortAllowed && m_CSSplatUtilities_deviceRadixSort != null)
            {
                var fallbackSorter = new GpuSorting(GpuSorting.SortType.DeviceRadixSort, m_CSSplatUtilities_deviceRadixSort);
                if (fallbackSorter.CanDispatch)
                {
                    m_CSSplatUtilities = m_CSSplatUtilities_deviceRadixSort;
                    m_Sorter = fallbackSorter;
                    m_ResolvedSortType = GpuSorting.SortType.DeviceRadixSort;
                    Debug.LogWarning($"[{nameof(GaussianSplatRenderer)}] {name}: FidelityFX sorting is unsupported here, falling back to DeviceRadixSort.", this);
                }
            }

            if (!m_Sorter.CanDispatch)
            {
                m_Sorter = new GpuSorting(GpuSorting.SortType.None, null);
                m_CSSplatUtilities = utilityShader;
                m_ResolvedSortType = GpuSorting.SortType.None;
                if (sortType != GpuSorting.SortType.None)
                    Debug.LogWarning($"[{nameof(GaussianSplatRenderer)}] {name}: Requested GPU sorting backend '{sortType}' is unsupported here. Rendering will continue without GPU sorting.", this);
            }

            m_UtilityKernelThreadGroupSize = m_CSSplatUtilities == m_CSSplatUtilities_fidelityFX ? 32 : 1024;
            InvalidateKernelCache();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            SetAssetDataOnCS(cmb, GetKernelIndex(kernel));
        }

        void SetAssetDataOnCS(CommandBuffer cmb, int kernelIndex)
        {
            ComputeShader cs = m_CSSplatUtilities;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatLayer, m_GpuLayerData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSemantic, m_GpuSemanticData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SemanticAlphaLabelIds, m_GpuSemanticAlphaLabelIds);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SemanticDimLabelIds, m_GpuSemanticDimLabelIds);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmb.SetComputeIntParam(cs, Props.SemanticAlphaLabelCount, GetSemanticHighlightIds().Count);
            cmb.SetComputeIntParam(cs, Props.SemanticDimLabelCount, GetSemanticDimIds().Count);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);

            int cutoutViewCount = UpdateCutoutViewsBuffer();
            cmb.SetComputeIntParam(cs, Props.CutoutViewsCount, cutoutViewCount);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.CutoutViews, m_GpuCutoutViews);
            cmb.SetComputeBufferParam(
                cs,
                kernelIndex,
                Props.CutoutViewSemanticIds,
                m_GpuCutoutViewSemanticIds);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatLayer, m_GpuLayerData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSemantic, m_GpuSemanticData);
            mat.SetBuffer(Props.SemanticAlphaLabelIds, m_GpuSemanticAlphaLabelIds);
            mat.SetBuffer(Props.SemanticDimLabelIds, m_GpuSemanticDimLabelIds);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            mat.SetInteger(Props.SemanticAlphaLabelCount, GetSemanticHighlightIds().Count);
            mat.SetInteger(Props.SemanticDimLabelCount, GetSemanticDimIds().Count);
            mat.SetInteger(Props.OptimizeForQuest, m_OptimizeForQuest ? 1 : 0);
        }

        internal GraphicsBuffer gpu3DGRTAabbs => m_Gpu3DGRTAabbs;
        internal int gpu3DGRTGeneration => m_Gpu3DGRTGeneration;
        internal bool is3DGRTRasterFallbackActive => m_3DGRTRasterFallbackActive;

        static void Add3DGRTStateHash(ref int hash, int value)
        {
            unchecked
            {
                hash = hash * 31 + value;
            }
        }

        static void Add3DGRTStateHash(ref int hash, float value)
        {
            Add3DGRTStateHash(ref hash, value.GetHashCode());
        }

        static void Add3DGRTStateHash(ref int hash, Matrix4x4 value)
        {
            Add3DGRTStateHash(ref hash, value.GetHashCode());
        }

        internal int Compute3DGRTVisualStateHash()
        {
            unchecked
            {
                int hash = 17;
                Add3DGRTStateHash(ref hash, GetInstanceID());
                Add3DGRTStateHash(
                    ref hash,
                    m_Asset != null ? m_Asset.GetInstanceID() : 0);
                Add3DGRTStateHash(ref hash, m_SplatCount);
                Add3DGRTStateHash(ref hash, m_Gpu3DGRTGeneration);
                Add3DGRTStateHash(ref hash, (int)m_RenderBackend);
                Add3DGRTStateHash(ref hash, (int)m_RenderMode);
                Add3DGRTStateHash(ref hash, m_SplatScale);
                Add3DGRTStateHash(ref hash, m_OpacityScale);
                Add3DGRTStateHash(ref hash, m_SHOrder);
                Add3DGRTStateHash(ref hash, m_SHOnly ? 1 : 0);
                Add3DGRTStateHash(ref hash, m_3DGRTKernelMinResponse);
                Add3DGRTStateHash(ref hash, m_3DGRTAlphaCullThreshold);
                Add3DGRTStateHash(ref hash, m_3DGRTAlphaClamp);
                Add3DGRTStateHash(ref hash, m_3DGRTMinTransmittance);
                Add3DGRTStateHash(ref hash, m_3DGRTMaxTracePasses);

                Add3DGRTStateHash(ref hash, (int)m_SemanticAlphaLabelId);
                Add3DGRTStateHash(ref hash, m_SemanticMatchAlphaMultiplier);
                Add3DGRTStateHash(ref hash, m_SemanticNonMatchAlphaMultiplier);
                Add3DGRTStateHash(ref hash, m_SemanticDimAlphaMultiplier);
                Add3DGRTStateHash(
                    ref hash,
                    m_SemanticAlphaLabelIds?.Count ?? 0);
                if (m_SemanticAlphaLabelIds != null)
                {
                    for (int i = 0; i < m_SemanticAlphaLabelIds.Count; ++i)
                        Add3DGRTStateHash(
                            ref hash,
                            (int)m_SemanticAlphaLabelIds[i]);
                }
                Add3DGRTStateHash(
                    ref hash,
                    m_SemanticDimLabelIds?.Count ?? 0);
                if (m_SemanticDimLabelIds != null)
                {
                    for (int i = 0; i < m_SemanticDimLabelIds.Count; ++i)
                        Add3DGRTStateHash(
                            ref hash,
                            (int)m_SemanticDimLabelIds[i]);
                }

                Add3DGRTStateHash(ref hash, m_LayerActivationState?.Count ?? 0);
                if (m_LayerActivationState != null)
                {
                    for (int i = 0; i < m_LayerActivationState.Count; ++i)
                        Add3DGRTStateHash(
                            ref hash,
                            m_LayerActivationState[i].GetHashCode());
                }
                Add3DGRTStateHash(
                    ref hash,
                    m_GpuEditDeleted != null
                        ? m_GpuEditDeleted.GetHashCode()
                        : 0);
                Add3DGRTStateHash(ref hash, (int)editDeletedSplats);

                Matrix4x4 rendererMatrix = transform.localToWorldMatrix;
                Add3DGRTStateHash(ref hash, m_Cutouts?.Length ?? 0);
                if (m_Cutouts != null)
                {
                    for (int i = 0; i < m_Cutouts.Length; ++i)
                    {
                        GaussianCutout cutout = m_Cutouts[i];
                        Add3DGRTStateHash(
                            ref hash,
                            cutout != null ? cutout.GetInstanceID() : 0);
                        GaussianCutout.ShaderData data =
                            GaussianCutout.GetShaderData(
                                cutout,
                                rendererMatrix,
                                m_ActiveLayerIndexRanges);
                        Add3DGRTStateHash(ref hash, data.matrix);
                        Add3DGRTStateHash(ref hash, (int)data.typeAndFlags);
                        Add3DGRTStateHash(ref hash, data.cutIndex0);
                        Add3DGRTStateHash(ref hash, data.cutIndex1);
                        Add3DGRTStateHash(ref hash, data.cutIndex2);
                        Add3DGRTStateHash(ref hash, data.cutIndex3);
                        Add3DGRTStateHash(ref hash, data.cutIndex4);
                        Add3DGRTStateHash(ref hash, data.cutIndex5);
                        Add3DGRTStateHash(ref hash, data.cutIndex6);
                        Add3DGRTStateHash(ref hash, data.cutIndex7);
                    }
                }

                IReadOnlyList<CutoutView> cutoutViews = GetActiveCutoutViews();
                Add3DGRTStateHash(ref hash, cutoutViews.Count);
                uint semanticOffset = 0;
                for (int i = 0; i < cutoutViews.Count; ++i)
                {
                    CutoutView cutoutView = cutoutViews[i];
                    Add3DGRTStateHash(ref hash, cutoutView.GetInstanceID());
                    CutoutView.ShaderData data = CutoutView.GetShaderData(
                        cutoutView,
                        rendererMatrix,
                        semanticOffset);
                    Add3DGRTStateHash(ref hash, data.matrix);
                    Add3DGRTStateHash(ref hash, (int)data.shape);
                    Add3DGRTStateHash(ref hash, data.height);
                    Add3DGRTStateHash(ref hash, data.width);
                    Add3DGRTStateHash(ref hash, data.alphaMultiplier);
                    Add3DGRTStateHash(ref hash, data.gaussianFalloff);
                    Add3DGRTStateHash(
                        ref hash,
                        (int)data.replaceAlphaWithFalloff);
                    Add3DGRTStateHash(ref hash, (int)data.semanticCount);

                    int semanticCount = Mathf.Min(
                        CutoutView.kMaxSemanticIds,
                        cutoutView.m_SemanticIds?.Length ?? 0);
                    for (int semanticIndex = 0;
                         semanticIndex < semanticCount;
                         ++semanticIndex)
                    {
                        Add3DGRTStateHash(
                            ref hash,
                            (int)cutoutView.m_SemanticIds[semanticIndex]);
                    }
                    semanticOffset += (uint)semanticCount;
                }

                return hash;
            }
        }

        internal void Mark3DGRTUnavailable()
        {
            m_3DGRTRasterFallbackActive = true;
        }

        internal bool Ensure3DGRTResources(CommandBuffer cmb)
        {
            if (!HasValidAsset || !HasValidRenderSetup || m_CSSplatUtilities == null)
                return false;

            float kernelResponse = Mathf.Clamp(m_3DGRTKernelMinResponse, 0.0001f, 0.99f);
            if (m_Gpu3DGRTData != null && m_Gpu3DGRTSH != null && m_Gpu3DGRTAabbs != null &&
                Mathf.Approximately(m_Gpu3DGRTSplatScale, m_SplatScale) &&
                Mathf.Approximately(m_Gpu3DGRTKernelMinResponse, kernelResponse))
                return true;

            Dispose3DGRTResources();

            try
            {
                const string kernelName = "CSDecode3DGRT";
                if (!m_CSSplatUtilities.HasKernel(kernelName))
                    throw new InvalidOperationException(
                        $"{kernelName} is unavailable. The compute shader may not have compiled for the active graphics API.");

                int kernel = m_CSSplatUtilities.FindKernel(kernelName);
                m_CSSplatUtilities.GetKernelThreadGroupSizes(kernel, out uint groupSize, out _, out _);
                if (groupSize == 0)
                    throw new InvalidOperationException($"{kernelName} reported a zero-sized thread group.");

                m_Gpu3DGRTData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 64)
                    { name = "Gaussian3DGRTData" };
                m_Gpu3DGRTSH = new GraphicsBuffer(GraphicsBuffer.Target.Raw, checked(m_SplatCount * 24), 4)
                    { name = "Gaussian3DGRTSH" };
                m_Gpu3DGRTAabbs = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 24)
                    { name = "Gaussian3DGRTAabbs" };

                const float minAabbExtent = 1e-6f;
                float kernelRadius = Mathf.Sqrt(-2.0f * Mathf.Log(kernelResponse));
                SetAssetDataOnCS(cmb, kernel);
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, Props.Gaussian3DGRTDataOut, m_Gpu3DGRTData);
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, Props.Gaussian3DGRTSHOut, m_Gpu3DGRTSH);
                cmb.SetComputeBufferParam(m_CSSplatUtilities, kernel, Props.Gaussian3DGRTAabbsOut, m_Gpu3DGRTAabbs);
                cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.Gaussian3DGRTKernelRadius, kernelRadius);
                cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.Gaussian3DGRTMinAabbExtent, minAabbExtent);
                cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
                cmb.DispatchCompute(
                    m_CSSplatUtilities,
                    kernel,
                    (m_SplatCount + (int)groupSize - 1) / (int)groupSize,
                    1,
                    1);
            }
            catch (Exception exception)
            {
                Dispose3DGRTResources();
                m_3DGRTRasterFallbackActive = true;
                if (!m_Reported3DGRTFailure)
                {
                    Debug.LogError(
                        $"Could not initialize 3DGRT for {name} ({m_SplatCount:N0} splats): {exception.Message}",
                        this);
                    m_Reported3DGRTFailure = true;
                }
                return false;
            }

            m_Gpu3DGRTSplatScale = m_SplatScale;
            m_Gpu3DGRTKernelMinResponse = kernelResponse;
            m_Reported3DGRTFailure = false;
            m_3DGRTRasterFallbackActive = false;
            ++m_Gpu3DGRTGeneration;
            return true;
        }

        internal void Set3DGRTDataOnMaterial(MaterialPropertyBlock mat)
        {
            UpdateSemanticHighlightBuffer();
            UpdateSemanticDimBuffer();
            UpdateCutoutsBuffer();
            int cutoutViewCount = UpdateCutoutViewsBuffer();

            mat.SetBuffer(Props.Gaussian3DGRTData, m_Gpu3DGRTData);
            mat.SetBuffer(Props.Gaussian3DGRTSH, m_Gpu3DGRTSH);
            mat.SetBuffer(Props.SplatSemantic, m_GpuSemanticData);
            mat.SetBuffer(Props.SemanticAlphaLabelIds, m_GpuSemanticAlphaLabelIds);
            mat.SetBuffer(Props.SemanticDimLabelIds, m_GpuSemanticDimLabelIds);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditDeleted != null ? 1 : 0);
            mat.SetInt(Props.SplatCount, m_SplatCount);
            mat.SetFloat(Props.SplatScale, m_SplatScale);
            mat.SetFloat(Props.SplatOpacityScale, m_OpacityScale);
            mat.SetInt(Props.SHOrder, m_SHOrder);
            mat.SetInt(Props.SHOnly, m_SHOnly ? 1 : 0);
            mat.SetInt(Props.DisplaySemantic, m_RenderMode == RenderMode.DebugSemantic ? 1 : 0);

            mat.SetInt(Props.SemanticAlphaLabelId, (int)m_SemanticAlphaLabelId);
            mat.SetInt(Props.SemanticAlphaLabelCount, GetSemanticHighlightIds().Count);
            mat.SetInt(Props.SemanticDimLabelCount, GetSemanticDimIds().Count);
            mat.SetFloat(Props.SemanticMatchAlphaMultiplier, Mathf.Max(0.0f, m_SemanticMatchAlphaMultiplier));
            mat.SetFloat(Props.SemanticNonMatchAlphaMultiplier, Mathf.Max(0.0f, m_SemanticNonMatchAlphaMultiplier));
            mat.SetFloat(Props.SemanticDimAlphaMultiplier, Mathf.Max(0.0f, m_SemanticDimAlphaMultiplier));

            mat.SetInt(Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            mat.SetBuffer(Props.SplatCutouts, m_GpuEditCutouts);
            mat.SetInt(Props.CutoutViewsCount, cutoutViewCount);
            mat.SetBuffer(Props.CutoutViews, m_GpuCutoutViews);
            mat.SetBuffer(Props.CutoutViewSemanticIds, m_GpuCutoutViewSemanticIds);

            mat.SetFloat(
                Props.Gaussian3DGRTKernelMinResponse,
                Mathf.Clamp(m_3DGRTKernelMinResponse, 0.0001f, 0.99f));
            mat.SetFloat(
                Props.Gaussian3DGRTAlphaCullThreshold,
                Mathf.Clamp01(m_3DGRTAlphaCullThreshold));
            mat.SetFloat(
                Props.Gaussian3DGRTAlphaClamp,
                Mathf.Clamp(m_3DGRTAlphaClamp, 0.0f, 0.9999f));
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void Dispose3DGRTResources()
        {
            bool hadResources =
                m_Gpu3DGRTData != null ||
                m_Gpu3DGRTSH != null ||
                m_Gpu3DGRTAabbs != null;
            DisposeBuffer(ref m_Gpu3DGRTData);
            DisposeBuffer(ref m_Gpu3DGRTSH);
            DisposeBuffer(ref m_Gpu3DGRTAabbs);
            m_Gpu3DGRTSplatScale = -1;
            m_Gpu3DGRTKernelMinResponse = -1;
            if (hadResources)
                ++m_Gpu3DGRTGeneration;
        }

        void DisposeResourcesForAsset()
        {
            Dispose3DGRTResources();
            m_Reported3DGRTFailure = false;
            m_3DGRTRasterFallbackActive = false;
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuLayerData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);
            DisposeBuffer(ref m_GpuSemanticData);
            DisposeBuffer(ref m_GpuSemanticAlphaLabelIds);
            DisposeBuffer(ref m_GpuSemanticDimLabelIds);
            DisposeBuffer(ref m_GpuCutoutViewSemanticIds);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);
            DisposeBuffer(ref m_GpuCutoutViews);

            m_Sorter?.DisposeResources();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
            m_SemanticIdsCpu = Array.Empty<uint>();
            m_SemanticAlphaLabelIdsGpuData = Array.Empty<uint>();
            m_SemanticDimLabelIdsGpuData = Array.Empty<uint>();
            m_ActiveLayerIndexRanges.Clear();
            InvalidateKernelCache();
        }

        public void SetSemanticAlphaLabelIds(IEnumerable<uint> semanticIds)
        {
            m_SemanticAlphaLabelIds ??= new List<uint>();
            m_SemanticAlphaLabelIds.Clear();
            if (semanticIds != null)
            {
                foreach (uint semanticId in semanticIds)
                {
                    if (semanticId != 0 && !m_SemanticAlphaLabelIds.Contains(semanticId))
                        m_SemanticAlphaLabelIds.Add(semanticId);
                }
            }

            m_SemanticAlphaLabelId = m_SemanticAlphaLabelIds.Count > 0 ? m_SemanticAlphaLabelIds[0] : 0;
            UpdateSemanticHighlightBuffer();
        }

        public void SetSemanticDimLabelIds(IEnumerable<uint> semanticIds)
        {
            m_SemanticDimLabelIds ??= new List<uint>();
            m_SemanticDimLabelIds.Clear();
            if (semanticIds != null)
            {
                foreach (uint semanticId in semanticIds)
                {
                    if (semanticId != 0 && !m_SemanticDimLabelIds.Contains(semanticId))
                        m_SemanticDimLabelIds.Add(semanticId);
                }
            }

            UpdateSemanticDimBuffer();
        }

        IReadOnlyList<uint> GetSemanticHighlightIds()
        {
            m_SemanticHighlightIdsScratch.Clear();

            if (m_SemanticAlphaLabelIds != null)
            {
                foreach (uint semanticId in m_SemanticAlphaLabelIds)
                {
                    if (semanticId != 0 && !m_SemanticHighlightIdsScratch.Contains(semanticId))
                        m_SemanticHighlightIdsScratch.Add(semanticId);
                }
            }

            if (m_SemanticAlphaLabelId != 0 && !m_SemanticHighlightIdsScratch.Contains(m_SemanticAlphaLabelId))
                m_SemanticHighlightIdsScratch.Add(m_SemanticAlphaLabelId);

            if (m_SemanticHighlightIdsScratch.Count > 0)
                return m_SemanticHighlightIdsScratch;

            return Array.Empty<uint>();
        }

        IReadOnlyList<uint> GetSemanticDimIds()
        {
            m_SemanticDimIdsScratch.Clear();

            if (m_SemanticDimLabelIds != null)
            {
                foreach (uint semanticId in m_SemanticDimLabelIds)
                {
                    if (semanticId != 0 && !m_SemanticDimIdsScratch.Contains(semanticId))
                        m_SemanticDimIdsScratch.Add(semanticId);
                }
            }

            if (m_SemanticDimIdsScratch.Count > 0)
                return m_SemanticDimIdsScratch;

            return Array.Empty<uint>();
        }

        void UpdateSemanticHighlightBuffer()
        {
            var highlightIds = GetSemanticHighlightIds();
            int bufferCount = math.max(1, highlightIds.Count);

            if (m_GpuSemanticAlphaLabelIds == null || m_GpuSemanticAlphaLabelIds.count != bufferCount)
            {
                m_GpuSemanticAlphaLabelIds?.Dispose();
                m_GpuSemanticAlphaLabelIds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCount, 4) { name = "GaussianSemanticAlphaLabelIds" };
            }

            if (m_SemanticAlphaLabelIdsGpuData.Length != bufferCount)
                m_SemanticAlphaLabelIdsGpuData = new uint[bufferCount];
            Array.Clear(m_SemanticAlphaLabelIdsGpuData, 0, m_SemanticAlphaLabelIdsGpuData.Length);

            for (int i = 0; i < highlightIds.Count; i++)
                m_SemanticAlphaLabelIdsGpuData[i] = highlightIds[i];

            m_GpuSemanticAlphaLabelIds.SetData(m_SemanticAlphaLabelIdsGpuData);
        }

        void UpdateSemanticDimBuffer()
        {
            var dimIds = GetSemanticDimIds();
            int bufferCount = math.max(1, dimIds.Count);

            if (m_GpuSemanticDimLabelIds == null || m_GpuSemanticDimLabelIds.count != bufferCount)
            {
                m_GpuSemanticDimLabelIds?.Dispose();
                m_GpuSemanticDimLabelIds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCount, 4) { name = "GaussianSemanticDimLabelIds" };
            }

            if (m_SemanticDimLabelIdsGpuData.Length != bufferCount)
                m_SemanticDimLabelIdsGpuData = new uint[bufferCount];
            Array.Clear(m_SemanticDimLabelIdsGpuData, 0, m_SemanticDimLabelIdsGpuData.Length);

            for (int i = 0; i < dimIds.Count; i++)
                m_SemanticDimLabelIdsGpuData[i] = dimIds[i];

            m_GpuSemanticDimLabelIds.SetData(m_SemanticDimLabelIdsGpuData);
        }

        public uint GetSemanticId(int splatIndex)
        {
            if (m_SemanticIdsCpu == null || splatIndex < 0 || splatIndex >= m_SemanticIdsCpu.Length)
                return 0;
            return m_SemanticIdsCpu[splatIndex];
        }

        public string GetSemanticName(uint semanticId)
        {
            if (m_SemanticLabels != null)
                return m_SemanticLabels.GetLabel(semanticId);
            return semanticId == 0 ? "Undefined" : $"Semantic {semanticId}";
        }

        public string GetSemanticNameForSplat(int splatIndex)
        {
            return GetSemanticName(GetSemanticId(splatIndex));
        }

        public void OnDisable()
        {
            DeInitialize();
        }

        private void DeInitialize()
        {
            DisposeResourcesForAsset();
#if GS_ENABLE_URP
            if (this is GaussianSplatPPLLRenderer ppllRenderer)
                GaussianSplatPPLLRenderSystem.instance.UnregisterSplat(ppllRenderer);
            else
#endif
                GaussianSplatRenderSystem.instance.UnregisterSplat(this);

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            //Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            UpdateSemanticHighlightBuffer();
            UpdateSemanticDimBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);
            int kernelIndex = GetKernelIndex(KernelIndices.CalcViewData);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SemanticAlphaLabelId, (int)m_SemanticAlphaLabelId);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SemanticAlphaLabelCount, GetSemanticHighlightIds().Count);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SemanticMatchAlphaMultiplier, Mathf.Max(0.0f, m_SemanticMatchAlphaMultiplier));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SemanticNonMatchAlphaMultiplier, Mathf.Max(0.0f, m_SemanticNonMatchAlphaMultiplier));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SemanticDimLabelCount, GetSemanticDimIds().Count);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SemanticDimAlphaMultiplier, Mathf.Max(0.0f, m_SemanticDimAlphaMultiplier));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);

            int groupSize = GetUtilityKernelThreadGroupSize();
            cmb.DispatchCompute(m_CSSplatUtilities, kernelIndex, (m_GpuView.count + groupSize - 1) / groupSize, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            if (m_Sorter == null || !m_Sorter.CanDispatch)
                return;

            Matrix4x4 worldToCamMatrix = m_CenterEyeOnly ? m_centerCamMatrix : cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;
            int kernelIndex = GetKernelIndex(KernelIndices.CalcDistances);

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatLayer, m_GpuLayerData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            int groupSize = GetUtilityKernelThreadGroupSize();
            cmd.DispatchCompute(m_CSSplatUtilities, kernelIndex, (m_GpuSortDistances.count + groupSize - 1) / groupSize, 1, 1);

            // sort the splats
            m_Sorter.Dispatch(cmd);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            if (m_PreviousRenderBackend != m_RenderBackend)
            {
                if (m_PreviousRenderBackend == RenderBackend.RayTracing3DGRT)
                    Dispose3DGRTResources();
                m_Reported3DGRTFailure = false;
                m_3DGRTRasterFallbackActive = false;
                m_PreviousRenderBackend = m_RenderBackend;
            }

            if (m_MatSplats == null || m_MatComposite == null || m_MatDebugPoints == null || m_MatDebugBoxes == null)
            {
                DeInitialize();
                Initialize();
            }
            
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash || m_centerEyeCamera == null)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                CreateResourcesForAsset();
            }
            
            if ((m_Sorter == null || m_ConfiguredSortType != m_gpuSortType) && splatCount > 0)
            {
                UpdateSortingType(m_gpuSortType);
                InitSortBuffers(splatCount);
            }

            if (m_CenterEyeOnly)
            {
                m_centerCamMatrix = m_centerEyeCamera.worldToCameraMatrix;
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            int kernelIndex = GetKernelIndex(KernelIndices.ClearBuffer);
            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            int groupSize = GetUtilityKernelThreadGroupSize();
            m_CSSplatUtilities.Dispatch(kernelIndex, (buf.count + groupSize - 1) / groupSize, 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            int kernelIndex = GetKernelIndex(KernelIndices.OrBuffers);
            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            int groupSize = GetUtilityKernelThreadGroupSize();
            m_CSSplatUtilities.Dispatch(kernelIndex, (dst.count + groupSize - 1) / groupSize, 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            int initKernelIndex = GetKernelIndex(KernelIndices.InitEditData);
            m_CSSplatUtilities.SetBuffer(initKernelIndex, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch(initKernelIndex, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            int updateKernelIndex = GetKernelIndex(KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, updateKernelIndex, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            int groupSize = GetUtilityKernelThreadGroupSize();
            cmb.DispatchCompute(m_CSSplatUtilities, updateKernelIndex, (m_GpuEditSelected.count + groupSize - 1) / groupSize, 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix, m_ActiveLayerIndexRanges);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        int UpdateCutoutViewsBuffer()
        {
            var cutoutViews = GetActiveCutoutViews();
            int cutoutViewCount = cutoutViews.Count;
            int bufferSize = cutoutViewCount;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuCutoutViews == null || m_GpuCutoutViews.count != bufferSize)
            {
                m_GpuCutoutViews?.Dispose();
                m_GpuCutoutViews = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<CutoutView.ShaderData>()) { name = "CutoutViews" };
            }

            int semanticIdCount = 0;
            for (int i = 0; i < cutoutViewCount; ++i)
            {
                semanticIdCount += math.min(
                    CutoutView.kMaxSemanticIds,
                    cutoutViews[i].m_SemanticIds?.Length ?? 0);
            }
            int semanticBufferSize = math.max(1, semanticIdCount);
            if (m_GpuCutoutViewSemanticIds == null ||
                m_GpuCutoutViewSemanticIds.count != semanticBufferSize)
            {
                m_GpuCutoutViewSemanticIds?.Dispose();
                m_GpuCutoutViewSemanticIds = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    semanticBufferSize,
                    sizeof(uint))
                {
                    name = "CutoutViewSemanticIds"
                };
            }

            NativeArray<CutoutView.ShaderData> data = new(bufferSize, Allocator.Temp);
            NativeArray<uint> semanticIds = new(semanticBufferSize, Allocator.Temp);
            if (cutoutViewCount > 0)
            {
                var matrix = transform.localToWorldMatrix;
                int semanticOffset = 0;
                for (var i = 0; i < cutoutViewCount; ++i)
                {
                    CutoutView cutoutView = cutoutViews[i];
                    data[i] = CutoutView.GetShaderData(
                        cutoutView,
                        matrix,
                        (uint)semanticOffset);
                    int count = math.min(
                        CutoutView.kMaxSemanticIds,
                        cutoutView.m_SemanticIds?.Length ?? 0);
                    for (int semanticIndex = 0;
                         semanticIndex < count;
                         ++semanticIndex)
                    {
                        semanticIds[semanticOffset + semanticIndex] =
                            cutoutView.m_SemanticIds[semanticIndex];
                    }
                    semanticOffset += count;
                }
            }

            m_GpuCutoutViews.SetData(data);
            m_GpuCutoutViewSemanticIds.SetData(semanticIds);
            data.Dispose();
            semanticIds.Dispose();
            return cutoutViewCount;
        }

        IReadOnlyList<CutoutView> GetActiveCutoutViews()
        {
            m_CutoutViewsScratch.Clear();

            if (m_CutoutViews != null)
            {
                foreach (var cutoutView in m_CutoutViews)
                    AddCutoutViewIfRelevant(cutoutView);
            }

            foreach (var cutoutView in GetComponentsInChildren<CutoutView>(true))
                AddCutoutViewIfRelevant(cutoutView);

            foreach (var cutoutView in FindObjectsByType<CutoutView>(FindObjectsSortMode.InstanceID))
            {
                if (cutoutView && IsRelevantSceneCutoutView(cutoutView))
                    AddCutoutViewIfRelevant(cutoutView);
            }

            return m_CutoutViewsScratch;
        }

        bool IsRelevantSceneCutoutView(CutoutView cutoutView)
        {
            if (!cutoutView)
                return false;

            GaussianSplatRenderer targetRenderer = cutoutView.GetGaussianRendererTarget();
            if (targetRenderer)
                return targetRenderer == this || targetRenderer.gameObject == gameObject;

            GaussianSplatRenderer parentRenderer = cutoutView.GetComponentInParent<GaussianSplatRenderer>();
            return !parentRenderer || parentRenderer == this;
        }

        void AddCutoutViewIfRelevant(CutoutView cutoutView)
        {
            if (!cutoutView || !cutoutView.isActiveAndEnabled || m_CutoutViewsScratch.Contains(cutoutView))
                return;
            m_CutoutViewsScratch.Add(cutoutView);
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            Dispose3DGRTResources();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            int kernelIndex = GetKernelIndex(KernelIndices.RotateSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            Dispose3DGRTResources();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            int kernelIndex = GetKernelIndex(KernelIndices.ScaleSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            Dispose3DGRTResources();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, GetKernelIndex(KernelIndices.SelectAll), Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, GetKernelIndex(KernelIndices.InvertSelection), Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, GetKernelIndex(KernelIndices.ExportData), "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkDataSize > 0)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            
            // Need to fix editing. Another time
            int posStride = 0; //(int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = 0; //(int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = 0; // (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            Dispose3DGRTResources();

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.Dispose3DGRTResources();
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            int kernelIndex = GetKernelIndex(KernelIndices.CopySplats);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, kernelIndex, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            int kernelIndex = GetKernelIndex(kernel);
            int groupSize = GetUtilityKernelThreadGroupSize();
            cmb.DispatchCompute(m_CSSplatUtilities, kernelIndex, (count + groupSize - 1) / groupSize, 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
