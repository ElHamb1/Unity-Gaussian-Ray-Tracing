// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatSemanticBaker : EditorWindow
    {
        const string kProgressTitle = "Baking Gaussian Semantics";
        const int kProgressReportInterval = 4096;

        [Serializable]
        class SemanticSource
        {
            [Serializable]
            public struct MeshDistanceSource
            {
                static readonly Vector3 s_InsideTestDirection = new Vector3(0.60213f, 0.48931f, 0.63077f);

                public Bounds bounds;
                public Vector3[] vertices;
                public int[] triangles;
                public Matrix4x4 worldToLocal;

                public bool IsUsable => vertices != null && triangles != null && triangles.Length >= 3;

                public float DistanceToSqr(Vector3 worldPos, float bestSqrDistance)
                {
                    if (!IsUsable)
                        return bestSqrDistance;

                    if (SqrDistanceToBounds(worldPos, bounds) >= bestSqrDistance)
                        return bestSqrDistance;

                    Vector3 localPos = worldToLocal.MultiplyPoint3x4(worldPos);
                    int insideHitCount = 0;
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        Vector3 a = vertices[triangles[i]];
                        Vector3 b = vertices[triangles[i + 1]];
                        Vector3 c = vertices[triangles[i + 2]];

                        if (RayIntersectsTriangle(localPos, s_InsideTestDirection, a, b, c, out float rayDistance) && rayDistance > 1e-5f)
                            insideHitCount++;

                        Vector3 closest = ClosestPointOnTriangle(localPos, a, b, c);
                        float sqrDistance = (localPos - closest).sqrMagnitude;
                        if (sqrDistance < bestSqrDistance)
                        {
                            bestSqrDistance = sqrDistance;
                            if (bestSqrDistance <= 1e-10f)
                                return 0f;
                        }
                    }

                    if ((insideHitCount & 1) != 0)
                        return 0f;

                    return bestSqrDistance;
                }
            }

            public uint id;
            public string label;
            public GameObject root;
            public Bounds bounds;
            public Collider[] colliders = Array.Empty<Collider>();
            public Bounds[] rendererBounds = Array.Empty<Bounds>();
            public MeshDistanceSource[] meshes = Array.Empty<MeshDistanceSource>();

            public bool IsUsable => root != null && (colliders.Length > 0 || meshes.Length > 0 || rendererBounds.Length > 0);

                public float DistanceToSqr(Vector3 worldPos, float bestSqrDistance)
                {
                    if (!IsUsable || SqrDistanceToBounds(worldPos, bounds) >= bestSqrDistance)
                        return bestSqrDistance;

                    bool usedPreciseSource = false;
                    foreach (var collider in colliders)
                    {
                        if (!collider || !collider.enabled)
                            continue;
                        if (collider is MeshCollider meshCollider && !meshCollider.convex && meshes.Length > 0)
                            continue;
                        usedPreciseSource = true;
                        float sqrDistance = (worldPos - collider.ClosestPoint(worldPos)).sqrMagnitude;
                        if (sqrDistance < bestSqrDistance)
                            bestSqrDistance = sqrDistance;
                    }

                foreach (var mesh in meshes)
                {
                    if (!mesh.IsUsable)
                        continue;
                    usedPreciseSource = true;
                    bestSqrDistance = Mathf.Min(bestSqrDistance, mesh.DistanceToSqr(worldPos, bestSqrDistance));
                }

                if (!usedPreciseSource)
                {
                    foreach (var rendererBound in rendererBounds)
                    {
                        float sqrDistance = SqrDistanceToBounds(worldPos, rendererBound);
                        if (sqrDistance < bestSqrDistance)
                            bestSqrDistance = sqrDistance;
                    }
                }

                return bestSqrDistance;
            }

            static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 ab = b - a;
                Vector3 ac = c - a;
                Vector3 ap = point - a;

                float d1 = Vector3.Dot(ab, ap);
                float d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f)
                    return a;

                Vector3 bp = point - b;
                float d3 = Vector3.Dot(ab, bp);
                float d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3)
                    return b;

                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                {
                    float v = d1 / (d1 - d3);
                    return a + v * ab;
                }

                Vector3 cp = point - c;
                float d5 = Vector3.Dot(ab, cp);
                float d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0f && d5 <= d6)
                    return c;

                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                {
                    float w = d2 / (d2 - d6);
                    return a + w * ac;
                }

                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                {
                    float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    return b + w * (c - b);
                }

                float denom = 1f / (va + vb + vc);
                float barycentricV = vb * denom;
                float barycentricW = vc * denom;
                return a + ab * barycentricV + ac * barycentricW;
            }

            static bool RayIntersectsTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
            {
                const float epsilon = 1e-6f;
                distance = 0f;

                Vector3 edge1 = b - a;
                Vector3 edge2 = c - a;
                Vector3 p = Vector3.Cross(direction, edge2);
                float det = Vector3.Dot(edge1, p);
                if (Mathf.Abs(det) < epsilon)
                    return false;

                float invDet = 1f / det;
                Vector3 t = origin - a;
                float u = Vector3.Dot(t, p) * invDet;
                if (u < 0f || u > 1f)
                    return false;

                Vector3 q = Vector3.Cross(t, edge1);
                float v = Vector3.Dot(direction, q) * invDet;
                if (v < 0f || u + v > 1f)
                    return false;

                distance = Vector3.Dot(edge2, q) * invDet;
                return distance >= 0f;
            }

            static float SqrDistanceToBounds(Vector3 point, Bounds targetBounds)
            {
                Vector3 closest = targetBounds.ClosestPoint(point);
                return (point - closest).sqrMagnitude;
            }
        }

        readonly struct SpatialKey : IEquatable<SpatialKey>
        {
            public readonly int x;
            public readonly int y;
            public readonly int z;

            public SpatialKey(int x, int y, int z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public bool Equals(SpatialKey other)
            {
                return x == other.x && y == other.y && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is SpatialKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = x;
                    hash = (hash * 397) ^ y;
                    hash = (hash * 397) ^ z;
                    return hash;
                }
            }
        }

        sealed class SpatialSourceIndex
        {
            readonly Dictionary<SpatialKey, List<int>> m_Cells;

            public float CellSize { get; }
            public int CellCount => m_Cells.Count;
            public int IndexedReferenceCount { get; }

            SpatialSourceIndex(float cellSize, Dictionary<SpatialKey, List<int>> cells, int indexedReferenceCount)
            {
                CellSize = cellSize;
                m_Cells = cells;
                IndexedReferenceCount = indexedReferenceCount;
            }

            public bool TryGetCandidateIndices(Vector3 worldPos, out List<int> candidates)
            {
                return m_Cells.TryGetValue(ToKey(worldPos, CellSize), out candidates);
            }

            public static SpatialSourceIndex Build(IReadOnlyList<SemanticSource> sources, float maxDistance)
            {
                bool hasSceneBounds = false;
                Bounds sceneBounds = default;
                for (int i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    if (!source.IsUsable)
                        continue;

                    Bounds expandedBounds = ExpandBounds(source.bounds, maxDistance);
                    EncapsulateBounds(ref hasSceneBounds, ref sceneBounds, expandedBounds);
                }

                float cellSize = ComputeCellSize(sceneBounds, maxDistance);
                var cells = new Dictionary<SpatialKey, List<int>>();
                int indexedReferenceCount = 0;

                for (int i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    if (!source.IsUsable)
                        continue;

                    Bounds expandedBounds = ExpandBounds(source.bounds, maxDistance);
                    SpatialKey min = ToKey(expandedBounds.min, cellSize);
                    SpatialKey max = ToKey(expandedBounds.max, cellSize);

                    for (int x = min.x; x <= max.x; x++)
                    {
                        for (int y = min.y; y <= max.y; y++)
                        {
                            for (int z = min.z; z <= max.z; z++)
                            {
                                var key = new SpatialKey(x, y, z);
                                if (!cells.TryGetValue(key, out var list))
                                {
                                    list = new List<int>(4);
                                    cells.Add(key, list);
                                }

                                list.Add(i);
                                indexedReferenceCount++;
                            }
                        }
                    }
                }

                return new SpatialSourceIndex(cellSize, cells, indexedReferenceCount);
            }

            static Bounds ExpandBounds(Bounds targetBounds, float maxDistance)
            {
                targetBounds.Expand(Mathf.Max(0f, maxDistance) * 2f);
                return targetBounds;
            }

            static float ComputeCellSize(Bounds sceneBounds, float maxDistance)
            {
                float largestAxis = Mathf.Max(sceneBounds.size.x, sceneBounds.size.y, sceneBounds.size.z);
                float sceneDerived = largestAxis > 0f ? largestAxis / 32f : 0f;
                return Mathf.Max(0.001f, Mathf.Max(maxDistance, sceneDerived));
            }

            static SpatialKey ToKey(Vector3 position, float cellSize)
            {
                return new SpatialKey(
                    Mathf.FloorToInt(position.x / cellSize),
                    Mathf.FloorToInt(position.y / cellSize),
                    Mathf.FloorToInt(position.z / cellSize));
            }
        }

        sealed class ColliderSemanticIndex
        {
            readonly Dictionary<Collider, uint> m_ColliderToSemanticId;
            Collider[] m_OverlapBuffer;

            public float InsideTestRadius { get; }
            public bool HasColliders => m_ColliderToSemanticId.Count > 0;

            ColliderSemanticIndex(Dictionary<Collider, uint> colliderToSemanticId, float insideTestRadius)
            {
                m_ColliderToSemanticId = colliderToSemanticId;
                InsideTestRadius = insideTestRadius;
                m_OverlapBuffer = new Collider[64];
            }

            public static ColliderSemanticIndex Build(IReadOnlyList<SemanticSource> sources, float insideTestRadius)
            {
                var colliderToSemanticId = new Dictionary<Collider, uint>();
                foreach (var source in sources)
                {
                    if (!source.IsUsable)
                        continue;

                    foreach (var collider in source.colliders)
                    {
                        if (!collider || !collider.enabled)
                            continue;

                        colliderToSemanticId[collider] = source.id;
                    }
                }

                return new ColliderSemanticIndex(colliderToSemanticId, Mathf.Max(1e-6f, insideTestRadius));
            }

            public bool TryFindContainingId(Vector3 worldPos, out uint semanticId)
            {
                semanticId = 0;
                if (!HasColliders)
                    return false;

                int overlapCount = Overlap(worldPos);
                if (overlapCount == 0)
                    return false;

                for (int i = 0; i < overlapCount; i++)
                {
                    var collider = m_OverlapBuffer[i];
                    if (collider && m_ColliderToSemanticId.TryGetValue(collider, out semanticId))
                    {
                        return true;
                    }
                }

                return false;
            }

            int Overlap(Vector3 worldPos)
            {
                while (true)
                {
                    int count = Physics.OverlapSphereNonAlloc(
                        worldPos,
                        InsideTestRadius,
                        m_OverlapBuffer,
                        ~0,
                        QueryTriggerInteraction.Collide);

                    if (count < m_OverlapBuffer.Length)
                        return count;

                    Array.Resize(ref m_OverlapBuffer, m_OverlapBuffer.Length * 2);
                }
            }
        }

        sealed class TemporaryColliderScope : IDisposable
        {
            readonly List<UnityEngine.Object> m_TemporaryObjects = new();

            public int CreatedColliderCount { get; private set; }

            public static TemporaryColliderScope Create(GameObject root, bool includeInactive)
            {
                var scope = new TemporaryColliderScope();
                if (root == null)
                    return scope;

                scope.CreateMeshFilterColliders(root.transform, includeInactive);
                scope.CreateSkinnedMeshColliders(root.transform, includeInactive);
                Physics.SyncTransforms();
                return scope;
            }

            public void Dispose()
            {
                for (int i = m_TemporaryObjects.Count - 1; i >= 0; i--)
                {
                    if (m_TemporaryObjects[i] != null)
                        DestroyImmediate(m_TemporaryObjects[i]);
                }

                m_TemporaryObjects.Clear();
                CreatedColliderCount = 0;
            }

            void CreateMeshFilterColliders(Transform root, bool includeInactive)
            {
                foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(includeInactive))
                {
                    if (!meshFilter || meshFilter.sharedMesh == null)
                        continue;
                    if (!includeInactive && !meshFilter.gameObject.activeInHierarchy)
                        continue;

                    var meshRenderer = meshFilter.GetComponent<MeshRenderer>();
                    if (meshRenderer == null || !meshRenderer.enabled)
                        continue;
                    if (meshFilter.GetComponent<Collider>() != null)
                        continue;

                    var meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
                    meshCollider.hideFlags = HideFlags.HideAndDontSave;
                    meshCollider.sharedMesh = meshFilter.sharedMesh;
                    meshCollider.convex = true;
                    m_TemporaryObjects.Add(meshCollider);
                    CreatedColliderCount++;
                }
            }

            void CreateSkinnedMeshColliders(Transform root, bool includeInactive)
            {
                foreach (var skinnedMeshRenderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive))
                {
                    if (!skinnedMeshRenderer)
                        continue;
                    if (!includeInactive && !skinnedMeshRenderer.gameObject.activeInHierarchy)
                        continue;
                    if (!skinnedMeshRenderer.enabled || skinnedMeshRenderer.GetComponent<Collider>() != null)
                        continue;

                    var bakedMesh = new Mesh
                    {
                        name = $"{skinnedMeshRenderer.name}_BakeColliderMesh",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    skinnedMeshRenderer.BakeMesh(bakedMesh, true);

                    var tempColliderObject = new GameObject($"{skinnedMeshRenderer.name}_BakeCollider")
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    tempColliderObject.transform.SetParent(skinnedMeshRenderer.transform, false);
                    var meshCollider = tempColliderObject.AddComponent<MeshCollider>();
                    meshCollider.hideFlags = HideFlags.HideAndDontSave;
                    meshCollider.sharedMesh = bakedMesh;
                    meshCollider.convex = true;

                    m_TemporaryObjects.Add(meshCollider);
                    m_TemporaryObjects.Add(tempColliderObject);
                    m_TemporaryObjects.Add(bakedMesh);
                    CreatedColliderCount++;
                }
            }
        }

        public readonly struct BakeProgressSnapshot
        {
            public readonly GaussianSplatRenderer renderer;
            public readonly string phase;
            public readonly int processedSplats;
            public readonly int totalSplats;
            public readonly int currentLayer;
            public readonly int layerCount;
            public readonly double elapsedSeconds;
            public readonly double etaSeconds;

            public BakeProgressSnapshot(
                GaussianSplatRenderer renderer,
                string phase,
                int processedSplats,
                int totalSplats,
                int currentLayer,
                int layerCount,
                double elapsedSeconds,
                double etaSeconds)
            {
                this.renderer = renderer;
                this.phase = phase;
                this.processedSplats = processedSplats;
                this.totalSplats = totalSplats;
                this.currentLayer = currentLayer;
                this.layerCount = layerCount;
                this.elapsedSeconds = elapsedSeconds;
                this.etaSeconds = etaSeconds;
            }

            public float Progress01 => totalSplats > 0 ? Mathf.Clamp01((float)processedSplats / totalSplats) : 0f;
            public bool HasEta => etaSeconds >= 0d;
        }

        sealed class BakeProgressState
        {
            public GaussianSplatRenderer renderer;
            public string phase;
            public int processedSplats;
            public int totalSplats;
            public int currentLayer;
            public int layerCount;
            public double startTime;
            public double lastRepaintTime;
            public bool isActive;

            public BakeProgressSnapshot ToSnapshot()
            {
                double elapsed = Math.Max(0d, EditorApplication.timeSinceStartup - startTime);
                double eta = processedSplats > 0 && totalSplats > processedSplats
                    ? elapsed / processedSplats * (totalSplats - processedSplats)
                    : -1d;
                return new BakeProgressSnapshot(renderer, phase, processedSplats, totalSplats, currentLayer, layerCount, elapsed, eta);
            }

            public void RepaintIfNeeded(bool force = false)
            {
                double now = EditorApplication.timeSinceStartup;
                if (!force && now - lastRepaintTime < 0.1d)
                    return;

                lastRepaintTime = now;
                InternalEditorUtility.RepaintAllViews();
                GaussianSplatRendererEditor.RepaintAll();
            }
        }

        static readonly BakeProgressState s_BakeProgress = new();

        GaussianSplatRenderer m_Renderer;
        GameObject m_SegmentationRoot;
        float m_MaxDistance = 0.01f;
        bool m_IncludeInactive;
        bool m_CreateTemporaryColliders = true;
        bool m_OverwriteExisting = true;

        [MenuItem("Tools/Gaussian Splats/Bake Semantic Sidecars")]
        static void Init()
        {
            Open(null);
        }

        public static void Open(GaussianSplatRenderer renderer)
        {
            var window = GetWindow<GaussianSplatSemanticBaker>(false, "Gaussian Semantic Baker", true);
            window.minSize = new Vector2(420, 240);
            window.SetRenderer(renderer);
            window.Show();
        }

        public static bool TryGetProgress(GaussianSplatRenderer renderer, out BakeProgressSnapshot snapshot)
        {
            if (renderer != null && s_BakeProgress.isActive && s_BakeProgress.renderer == renderer)
            {
                snapshot = s_BakeProgress.ToSnapshot();
                return true;
            }

            snapshot = default;
            return false;
        }

        void OnSelectionChange()
        {
            if (m_Renderer == null && Selection.activeGameObject != null)
            {
                SetRenderer(Selection.activeGameObject.GetComponent<GaussianSplatRenderer>());
                Repaint();
            }
        }

        void SetRenderer(GaussianSplatRenderer renderer)
        {
            m_Renderer = renderer;
            if (m_SegmentationRoot == null)
                m_SegmentationRoot = GameObject.Find("FullbodySegmentations");
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "This bakes semantic ids into sidecar .bytes files from the current renderer transform and scene geometry. Reusing the same asset at a different transform will not preserve those scene-relative semantics.",
                MessageType.Info);

            using (var check = new EditorGUI.ChangeCheckScope())
            {
                m_Renderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField("Renderer", m_Renderer, typeof(GaussianSplatRenderer), true);
                m_SegmentationRoot = (GameObject)EditorGUILayout.ObjectField("Segmentation Root", m_SegmentationRoot, typeof(GameObject), true);
                m_MaxDistance = EditorGUILayout.FloatField("Max Distance", m_MaxDistance);
                m_IncludeInactive = EditorGUILayout.Toggle("Include Inactive", m_IncludeInactive);
                m_CreateTemporaryColliders = EditorGUILayout.Toggle(new GUIContent("Auto Temp Colliders", "Create temporary mesh colliders for segmentation meshes that do not already have colliders."), m_CreateTemporaryColliders);
                m_OverwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", m_OverwriteExisting);

                if (check.changed && m_MaxDistance < 0f)
                    m_MaxDistance = 0f;
            }

            DrawProgressUI(m_Renderer);

            using (new EditorGUI.DisabledScope(!CanBake()))
            {
                if (GUILayout.Button("Bake Semantic Sidecars"))
                {
                    Bake();
                }
            }

            if (m_Renderer != null && m_Renderer.asset != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Asset", AssetDatabase.GetAssetPath(m_Renderer.asset));
                EditorGUILayout.LabelField("Splats", m_Renderer.asset.splatCount.ToString("N0"));
            }
        }

        bool CanBake()
        {
            return m_Renderer != null && m_Renderer.asset != null && m_Renderer.asset.LayerData.Count > 0 && m_SegmentationRoot != null;
        }

        void Bake()
        {
            if (!CanBake())
                return;

            var totalStopwatch = Stopwatch.StartNew();
            var asset = m_Renderer.asset;
            using var temporaryColliders = m_CreateTemporaryColliders
                ? TemporaryColliderScope.Create(m_SegmentationRoot, m_IncludeInactive)
                : null;

            var sources = CollectSources(m_SegmentationRoot, m_IncludeInactive, preferCollidersOnly: m_CreateTemporaryColliders);
            if (sources.Count == 0)
            {
                EditorUtility.DisplayDialog("Gaussian Semantic Baker", "No usable segmentation objects were found under the selected root.", "OK");
                return;
            }

            var colliderIndex = ColliderSemanticIndex.Build(sources, Mathf.Max(1e-6f, m_MaxDistance * 0.01f));
            int totalSplatsToBake = asset.LayerData.Sum(layerAssets =>
            {
                return asset.layerInfo.TryGetValue(layerAssets.layer, out int layerSplatCount) ? math.max(0, layerSplatCount) : 0;
            });

            string assetPath = AssetDatabase.GetAssetPath(asset);
            string assetFolder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(assetFolder))
                assetFolder = "Assets";

            try
            {
                UnityEngine.Debug.Log(
                    $"[Gaussian Semantic Baker] Baking renderer '{m_Renderer.name}' with asset '{asset.name}' at '{assetPath}'.");
                BeginProgress(m_Renderer, totalSplatsToBake, asset.LayerData.Count);
                EditorUtility.DisplayProgressBar(kProgressTitle, "Preparing semantic sources", 0f);
                Undo.RecordObject(asset, "Bake Gaussian Semantic Sidecars");
                Undo.RecordObject(m_Renderer, "Bake Gaussian Semantic Sidecars");

                var layerInfo = asset.layerInfo;
                int layerCount = asset.LayerData.Count;
                for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
                {
                    var layerAssets = asset.LayerData[layerIndex];
                    layerInfo.TryGetValue(layerAssets.layer, out int splatCount);
                    if (splatCount <= 0)
                    {
                        continue;
                    }

                    SetProgressPhase(layerAssets.layer, layerIndex + 1, layerCount);
                    UpdateEditorProgressBar();

                    var layerStopwatch = Stopwatch.StartNew();
                    uint[] semanticIds = BakeLayer(
                        asset,
                        layerAssets,
                        splatCount,
                        colliderIndex,
                        m_Renderer.transform.localToWorldMatrix,
                        processedDelta => AdvanceProgress(processedDelta));
                    int assignedCount = CountAssignedSemantics(semanticIds);
                    string semanticPath = GetSemanticPath(layerAssets.m_PosData, assetFolder, layerAssets.layer);
                    WriteSemanticFile(semanticPath, semanticIds);
                    layerStopwatch.Stop();
                    UnityEngine.Debug.Log(
                        $"[Gaussian Semantic Baker] Baked layer {layerAssets.layer} with {splatCount:N0} splats " +
                        $"({assignedCount:N0} assigned, {splatCount - assignedCount:N0} none) in {layerStopwatch.Elapsed.TotalSeconds:F2}s.");

                    AssetDatabase.ImportAsset(semanticPath, ImportAssetOptions.ForceUpdate);
                    var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(semanticPath);
                    var updatedLayer = layerAssets;
                    updatedLayer.m_SemanticData = textAsset;
                    asset.LayerData[layerIndex] = updatedLayer;
                }

                UpdateLabelTable(assetFolder, asset.name, sources);
                m_Renderer.UpdateRessources();
                EditorUtility.SetDirty(asset);
                EditorUtility.SetDirty(m_Renderer);
                AssetDatabase.SaveAssets();
                totalStopwatch.Stop();
                UnityEngine.Debug.Log(
                    $"[Gaussian Semantic Baker] Prepared {sources.Count} sources and baked {asset.splatCount:N0} splats in {totalStopwatch.Elapsed.TotalSeconds:F2}s. " +
                    $"Temporary colliders: {(temporaryColliders != null ? temporaryColliders.CreatedColliderCount : 0):N0}, semantic colliders: {colliderIndex.HasColliders}.");
            }
            finally
            {
                EndProgress();
                EditorUtility.ClearProgressBar();
            }
        }

        void UpdateLabelTable(string assetFolder, string assetName, IReadOnlyList<SemanticSource> sources)
        {
            var table = m_Renderer.m_SemanticLabels;
            if (table == null)
            {
                string path = $"{assetFolder}/{assetName}_SemanticLabels.asset";
                table = AssetDatabase.LoadAssetAtPath<GaussianSplatSemanticLabelTable>(path);
                if (table == null)
                {
                    table = CreateInstance<GaussianSplatSemanticLabelTable>();
                    AssetDatabase.CreateAsset(table, path);
                }
            }

            var entries = sources.Select(source => new GaussianSplatSemanticLabelTable.Entry
            {
                id = source.id,
                label = source.label,
            }).ToList();
            table.SetEntries(entries);
            m_Renderer.m_SemanticLabels = table;
            EditorUtility.SetDirty(table);
        }

        static List<SemanticSource> CollectSources(GameObject root, bool includeInactive, bool preferCollidersOnly)
        {
            var result = new List<SemanticSource>();
            if (root == null)
                return result;

            var groupRoots = new List<Transform>();
            if (root.transform.childCount == 0)
            {
                groupRoots.Add(root.transform);
            }
            else
            {
                foreach (Transform child in root.transform)
                {
                    if (includeInactive || child.gameObject.activeInHierarchy)
                        groupRoots.Add(child);
                }
            }

            uint nextId = 1;
            foreach (Transform candidate in groupRoots)
            {
                if (TryCreateSemanticSource(candidate, includeInactive, includeChildren: true, preferCollidersOnly, nextId, out var source))
                {
                    result.Add(source);
                    nextId++;
                }
            }

            if (result.Count > 0)
                return result;

            // Fall back to a full recursive scan so grandparent organizational nodes still work.
            foreach (var candidate in root.GetComponentsInChildren<Transform>(includeInactive))
            {
                if (candidate == null)
                    continue;
                if (!includeInactive && !candidate.gameObject.activeInHierarchy)
                    continue;

                if (TryCreateSemanticSource(candidate, includeInactive, includeChildren: false, preferCollidersOnly, nextId, out var source))
                {
                    result.Add(source);
                    nextId++;
                }
            }

            return result;
        }

        static bool TryCreateSemanticSource(Transform candidate, bool includeInactive, bool includeChildren, bool preferCollidersOnly, uint id, out SemanticSource source)
        {
            source = default;
            if (candidate == null)
                return false;

            var colliders = includeChildren
                ? candidate.GetComponentsInChildren<Collider>(includeInactive)
                : candidate.GetComponents<Collider>();
            var renderers = includeChildren
                ? candidate.GetComponentsInChildren<Renderer>(includeInactive)
                : candidate.GetComponents<Renderer>();
            renderers = renderers
                .Where(r => !(r is ParticleSystemRenderer))
                .ToArray();

            var rendererBounds = renderers
                .Where(r => r != null && r.enabled)
                .Select(r => r.bounds)
                .ToArray();
            var meshes = preferCollidersOnly && colliders.Length > 0
                ? Array.Empty<SemanticSource.MeshDistanceSource>()
                : CreateMeshSources(candidate, includeInactive, includeChildren);
            if (colliders.Length == 0 && rendererBounds.Length == 0 && meshes.Length == 0)
                return false;

            if (!TryGetSourceBounds(colliders, rendererBounds, meshes, out Bounds sourceBounds))
                return false;

            source = new SemanticSource
            {
                id = id,
                label = SanitizeSemanticLabel(candidate.name),
                root = candidate.gameObject,
                bounds = sourceBounds,
                colliders = colliders,
                rendererBounds = rendererBounds,
                meshes = meshes,
            };
            return true;
        }

        static SemanticSource.MeshDistanceSource[] CreateMeshSources(Transform candidate, bool includeInactive, bool includeChildren)
        {
            var results = new List<SemanticSource.MeshDistanceSource>();
            var meshFilters = includeChildren
                ? candidate.GetComponentsInChildren<MeshFilter>(includeInactive)
                : candidate.GetComponents<MeshFilter>();

            foreach (var meshFilter in meshFilters)
            {
                if (!meshFilter || meshFilter.sharedMesh == null)
                    continue;

                var meshRenderer = meshFilter.GetComponent<MeshRenderer>();
                if (meshRenderer == null || !meshRenderer.enabled)
                    continue;

                TryAddMesh(results, meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, meshRenderer.bounds);
            }

            var skinnedMeshRenderers = includeChildren
                ? candidate.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive)
                : candidate.GetComponents<SkinnedMeshRenderer>();
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                if (!skinnedMeshRenderer || !skinnedMeshRenderer.enabled)
                    continue;

                var bakedMesh = new Mesh();
                skinnedMeshRenderer.BakeMesh(bakedMesh, true);
                TryAddMesh(results, bakedMesh, skinnedMeshRenderer.transform.localToWorldMatrix, skinnedMeshRenderer.bounds);
                DestroyImmediate(bakedMesh);
            }

            return results.ToArray();
        }

        static void TryAddMesh(List<SemanticSource.MeshDistanceSource> results, Mesh mesh, Matrix4x4 localToWorld, Bounds bounds)
        {
            if (mesh == null)
                return;

            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
                return;

            results.Add(new SemanticSource.MeshDistanceSource
            {
                bounds = bounds,
                vertices = vertices,
                triangles = triangles,
                worldToLocal = localToWorld.inverse,
            });
        }

        static uint[] BakeLayer(GaussianSplatAsset asset, GaussianSplatAsset.LayerAssets layerAssets, int splatCount,
            ColliderSemanticIndex colliderIndex, Matrix4x4 rendererLocalToWorld,
            Action<int> reportProgress)
        {
            var semanticIds = new uint[splatCount];
            var chunkBytes = layerAssets.m_ChunkData != null ? layerAssets.m_ChunkData.bytes : null;
            var posBytes = layerAssets.m_PosData.bytes;
            int stride = GaussianSplatAsset.GetVectorSize(asset.posFormat);

            for (int i = 0; i < splatCount; ++i)
            {
                float3 localPos = DecodePosition(posBytes, stride, i, asset.posFormat);
                if (chunkBytes != null && chunkBytes.Length > 0)
                {
                    var chunk = ReadChunk(chunkBytes, i / GaussianSplatAsset.kChunkSize);
                    localPos = math.lerp(
                        new float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x),
                        new float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y),
                        localPos);
                }

                Vector3 worldPos = rendererLocalToWorld.MultiplyPoint3x4((Vector3)localPos);
                semanticIds[i] = FindColliderSemanticId(worldPos, colliderIndex);

                if ((i + 1) % kProgressReportInterval == 0)
                    reportProgress?.Invoke(kProgressReportInterval);
            }

            int remainder = splatCount % kProgressReportInterval;
            if (remainder > 0)
                reportProgress?.Invoke(remainder);

            return semanticIds;
        }

        static int CountAssignedSemantics(uint[] semanticIds)
        {
            int count = 0;
            for (int i = 0; i < semanticIds.Length; i++)
            {
                if (semanticIds[i] != 0)
                    count++;
            }

            return count;
        }

        static uint FindColliderSemanticId(Vector3 worldPos, ColliderSemanticIndex colliderIndex)
        {
            if (colliderIndex != null && colliderIndex.TryFindContainingId(worldPos, out uint containedId))
                return containedId;

            return 0;
        }

        static bool TryGetSourceBounds(Collider[] colliders, Bounds[] rendererBounds, SemanticSource.MeshDistanceSource[] meshes, out Bounds sourceBounds)
        {
            bool hasBounds = false;
            sourceBounds = default;

            foreach (var collider in colliders)
            {
                if (!collider || !collider.enabled)
                    continue;
                EncapsulateBounds(ref hasBounds, ref sourceBounds, collider.bounds);
            }

            foreach (var rendererBound in rendererBounds)
            {
                EncapsulateBounds(ref hasBounds, ref sourceBounds, rendererBound);
            }

            foreach (var mesh in meshes)
            {
                if (!mesh.IsUsable)
                    continue;
                EncapsulateBounds(ref hasBounds, ref sourceBounds, mesh.bounds);
            }

            return hasBounds;
        }

        static void EncapsulateBounds(ref bool hasBounds, ref Bounds combinedBounds, Bounds nextBounds)
        {
            if (!hasBounds)
            {
                combinedBounds = nextBounds;
                hasBounds = true;
                return;
            }

            combinedBounds.Encapsulate(nextBounds.min);
            combinedBounds.Encapsulate(nextBounds.max);
        }

        static string SanitizeSemanticLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            int dotIndex = name.IndexOf('.');
            return dotIndex > 0 ? name[..dotIndex] : name;
        }

        static void BeginProgress(GaussianSplatRenderer renderer, int totalSplats, int layerCount)
        {
            s_BakeProgress.renderer = renderer;
            s_BakeProgress.phase = "Preparing bake";
            s_BakeProgress.processedSplats = 0;
            s_BakeProgress.totalSplats = math.max(0, totalSplats);
            s_BakeProgress.currentLayer = 0;
            s_BakeProgress.layerCount = math.max(0, layerCount);
            s_BakeProgress.startTime = EditorApplication.timeSinceStartup;
            s_BakeProgress.lastRepaintTime = 0d;
            s_BakeProgress.isActive = true;
            s_BakeProgress.RepaintIfNeeded(true);
        }

        static void SetProgressPhase(int layer, int currentLayer, int layerCount)
        {
            s_BakeProgress.phase = $"Baking layer {layer}";
            s_BakeProgress.currentLayer = currentLayer;
            s_BakeProgress.layerCount = math.max(layerCount, s_BakeProgress.layerCount);
            s_BakeProgress.RepaintIfNeeded(true);
        }

        static void AdvanceProgress(int processedDelta)
        {
            s_BakeProgress.processedSplats = math.min(s_BakeProgress.totalSplats, s_BakeProgress.processedSplats + math.max(0, processedDelta));
            UpdateEditorProgressBar();
            s_BakeProgress.RepaintIfNeeded();
        }

        static void UpdateEditorProgressBar()
        {
            if (!s_BakeProgress.isActive)
                return;

            var snapshot = s_BakeProgress.ToSnapshot();
            string message = $"{snapshot.phase}  {snapshot.processedSplats:N0}/{snapshot.totalSplats:N0}";
            if (snapshot.layerCount > 0)
                message += $"  (Layer {snapshot.currentLayer}/{snapshot.layerCount})";
            if (snapshot.HasEta)
                message += $"  ETA {FormatDuration(snapshot.etaSeconds)}";

            EditorUtility.DisplayProgressBar(kProgressTitle, message, snapshot.Progress01);
        }

        static void EndProgress()
        {
            s_BakeProgress.isActive = false;
            s_BakeProgress.renderer = null;
            s_BakeProgress.phase = null;
            s_BakeProgress.processedSplats = 0;
            s_BakeProgress.totalSplats = 0;
            s_BakeProgress.currentLayer = 0;
            s_BakeProgress.layerCount = 0;
            s_BakeProgress.RepaintIfNeeded(true);
        }

        static string FormatDuration(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds < 0d)
                return "--";

            var duration = TimeSpan.FromSeconds(totalSeconds);
            if (duration.TotalHours >= 1d)
                return duration.ToString(@"h\:mm\:ss");
            if (duration.TotalMinutes >= 1d)
                return duration.ToString(@"m\:ss");
            return $"{Mathf.CeilToInt((float)duration.TotalSeconds)}s";
        }

        static void DrawProgressUI(GaussianSplatRenderer renderer)
        {
            if (!TryGetProgress(renderer, out var snapshot))
                return;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Semantic bake in progress.", MessageType.Info);
            Rect rect = GUILayoutUtility.GetRect(18f, 18f, "TextField");
            EditorGUI.ProgressBar(rect, snapshot.Progress01, $"{snapshot.processedSplats:N0} / {snapshot.totalSplats:N0} splats");
            EditorGUILayout.LabelField(
                "Status",
                $"{snapshot.phase} | Layer {snapshot.currentLayer}/{Math.Max(1, snapshot.layerCount)} | Elapsed {FormatDuration(snapshot.elapsedSeconds)} | ETA {(snapshot.HasEta ? FormatDuration(snapshot.etaSeconds) : "--")}");
        }

        static string GetSemanticPath(TextAsset posAsset, string assetFolder, int layer)
        {
            string posPath = AssetDatabase.GetAssetPath(posAsset)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(posPath))
            {
                if (posPath.EndsWith("_pos.bytes", StringComparison.OrdinalIgnoreCase))
                    return posPath[..^"_pos.bytes".Length] + "_sem.bytes";
                return Path.ChangeExtension(posPath, null) + "_sem.bytes";
            }

            return $"{assetFolder}/Layer{layer}_sem.bytes";
        }

        void WriteSemanticFile(string path, uint[] semanticIds)
        {
            if (File.Exists(path) && !m_OverwriteExisting)
                throw new InvalidOperationException($"Semantic file already exists: {path}");

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            byte[] bytes = new byte[semanticIds.Length * sizeof(uint)];
            Buffer.BlockCopy(semanticIds, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }

        static float3 DecodePosition(byte[] posBytes, int stride, int index, GaussianSplatAsset.VectorFormat format)
        {
            int offset = index * stride;
            return format switch
            {
                GaussianSplatAsset.VectorFormat.Float32 => new float3(
                    BitConverter.ToSingle(posBytes, offset),
                    BitConverter.ToSingle(posBytes, offset + 4),
                    BitConverter.ToSingle(posBytes, offset + 8)),
                GaussianSplatAsset.VectorFormat.Norm16 => DecodeNorm16(posBytes, offset),
                GaussianSplatAsset.VectorFormat.Norm11 => DecodeNorm11(posBytes, offset),
                GaussianSplatAsset.VectorFormat.Norm6 => DecodeNorm6(posBytes, offset),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };
        }

        static float3 DecodeNorm16(byte[] bytes, int offset)
        {
            uint enc0 = BitConverter.ToUInt32(bytes, offset);
            ushort enc1 = BitConverter.ToUInt16(bytes, offset + 4);
            return new float3(
                (enc0 & 65535u) / 65535f,
                ((enc0 >> 16) & 65535u) / 65535f,
                enc1 / 65535f);
        }

        static float3 DecodeNorm11(byte[] bytes, int offset)
        {
            uint enc = BitConverter.ToUInt32(bytes, offset);
            return new float3(
                (enc & 2047u) / 2047f,
                ((enc >> 11) & 1023u) / 1023f,
                ((enc >> 21) & 2047u) / 2047f);
        }

        static float3 DecodeNorm6(byte[] bytes, int offset)
        {
            ushort enc = BitConverter.ToUInt16(bytes, offset);
            return new float3(
                (enc & 63u) / 63f,
                ((enc >> 6) & 31u) / 31f,
                ((enc >> 11) & 31u) / 31f);
        }

        static unsafe GaussianSplatAsset.ChunkInfo ReadChunk(byte[] chunkBytes, int chunkIndex)
        {
            int stride = UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            fixed (byte* ptr = chunkBytes)
            {
                return UnsafeUtility.ReadArrayElementWithStride<GaussianSplatAsset.ChunkInfo>(ptr, chunkIndex, stride);
            }
        }
    }
}
