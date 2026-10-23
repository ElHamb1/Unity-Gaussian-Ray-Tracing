// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer), true)]
    [CanEditMultipleObjects]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        const string kPrefExportBake = "nesnausk.GaussianSplatting.ExportBakeTransform";

        SerializedProperty m_PropAsset;
        SerializedProperty m_PropRenderBackend;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSemanticLabels;
        SerializedProperty m_PropSemanticMatchAlphaMultiplier;
        SerializedProperty m_PropSemanticNonMatchAlphaMultiplier;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;
        SerializedProperty m_PropSHOnly;
        SerializedProperty m_CenterEyeOnly;
        SerializedProperty m_PropSortNthFrame;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropCutouts;
        SerializedProperty m_PropCutoutViews;
        SerializedProperty m_PropShaderSplats;
        SerializedProperty m_PropShaderComposite;
        SerializedProperty m_PropShaderDebugPoints;
        SerializedProperty m_PropShaderDebugBoxes;
        SerializedProperty m_PropCSSplatUtilities_deviceRadixSort;
        SerializedProperty m_PropCSSplatUtilities_fidelityFxSort;
        SerializedProperty m_gpuSortType;
        SerializedProperty m_PropOptimizeForQuest;
        SerializedProperty m_Prop3DGRTKernelMinResponse;
        SerializedProperty m_Prop3DGRTAlphaCullThreshold;
        SerializedProperty m_Prop3DGRTAlphaClamp;
        SerializedProperty m_Prop3DGRTMinTransmittance;
        SerializedProperty m_Prop3DGRTMaxTracePasses;
        SerializedProperty m_Prop3DGRTRasterFallback;

        bool m_ResourcesExpanded = false;
        int m_CameraIndex = 0;

        bool m_ExportBakeTransform;

        static int s_EditStatsUpdateCounter = 0;

        static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

        public static void BumpGUICounter()
        {
            ++s_EditStatsUpdateCounter;
        }

        public static void RepaintAll()
        {
            foreach (var e in s_AllEditors)
                e.Repaint();
        }

        public void OnEnable()
        {
            m_ExportBakeTransform = EditorPrefs.GetBool(kPrefExportBake, false);

            m_PropAsset = serializedObject.FindProperty("m_Asset");
            m_PropRenderBackend = serializedObject.FindProperty("m_RenderBackend");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSemanticLabels = serializedObject.FindProperty("m_SemanticLabels");
            m_PropSemanticMatchAlphaMultiplier = serializedObject.FindProperty("m_SemanticMatchAlphaMultiplier");
            m_PropSemanticNonMatchAlphaMultiplier = serializedObject.FindProperty("m_SemanticNonMatchAlphaMultiplier");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
            m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_CenterEyeOnly = serializedObject.FindProperty("m_CenterEyeOnly");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropCutouts = serializedObject.FindProperty("m_Cutouts");
            m_PropCutoutViews = serializedObject.FindProperty("m_CutoutViews");
            m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
            m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
            m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
            m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
            m_PropCSSplatUtilities_deviceRadixSort = serializedObject.FindProperty("m_CSSplatUtilities_deviceRadixSort");
            m_PropCSSplatUtilities_fidelityFxSort = serializedObject.FindProperty("m_CSSplatUtilities_fidelityFX");
            m_gpuSortType = serializedObject.FindProperty("m_gpuSortType");
            m_PropOptimizeForQuest = serializedObject.FindProperty("m_OptimizeForQuest");
            m_Prop3DGRTKernelMinResponse = serializedObject.FindProperty("m_3DGRTKernelMinResponse");
            m_Prop3DGRTAlphaCullThreshold = serializedObject.FindProperty("m_3DGRTAlphaCullThreshold");
            m_Prop3DGRTAlphaClamp = serializedObject.FindProperty("m_3DGRTAlphaClamp");
            m_Prop3DGRTMinTransmittance = serializedObject.FindProperty("m_3DGRTMinTransmittance");
            m_Prop3DGRTMaxTracePasses = serializedObject.FindProperty("m_3DGRTMaxTracePasses");
            m_Prop3DGRTRasterFallback = serializedObject.FindProperty("m_3DGRTRasterFallback");
            s_AllEditors.Add(this);
        }

        public void OnDisable()
        {
            s_AllEditors.Remove(this);
        }

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            serializedObject.Update();

            GUILayout.Label("Data Asset", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropAsset);
            EditorGUILayout.PropertyField(m_PropSemanticLabels);
            if (targets.Length == 1)
            {
                DrawSemanticBakeProgress((GaussianSplatRenderer)target);

                using (new EditorGUI.DisabledScope(GaussianSplatSemanticBaker.TryGetProgress((GaussianSplatRenderer)target, out _)))
                {
                    if (GUILayout.Button("Open Semantic Baker"))
                    {
                        GaussianSplatSemanticBaker.Open((GaussianSplatRenderer)target);
                    }
                }
            }

            if (!gs.HasValidAsset)
            {
                var msg = gs.asset != null && gs.asset.formatVersion != GaussianSplatAsset.kCurrentVersion
                    ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                    : "Gaussian Splat asset is not assigned or is empty";
                EditorGUILayout.HelpBox(msg, MessageType.Error);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderBackend);
            EditorGUILayout.PropertyField(m_PropRenderOrder);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);
            EditorGUILayout.PropertyField(m_PropSHOnly);
            bool use3DGRT =
                !m_PropRenderBackend.hasMultipleDifferentValues &&
                m_PropRenderBackend.intValue ==
                (int)GaussianSplatRenderer.RenderBackend.RayTracing3DGRT;
            if (use3DGRT)
            {
                EditorGUILayout.Space();
                GUILayout.Label("3DGRT Options", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_Prop3DGRTKernelMinResponse);
                EditorGUILayout.PropertyField(m_Prop3DGRTAlphaCullThreshold);
                EditorGUILayout.PropertyField(m_Prop3DGRTAlphaClamp);
                EditorGUILayout.PropertyField(m_Prop3DGRTMinTransmittance);
                EditorGUILayout.PropertyField(m_Prop3DGRTMaxTracePasses);
                EditorGUILayout.PropertyField(m_Prop3DGRTRasterFallback);
                EditorGUILayout.HelpBox(
                    "3DGRT requires HDRP/D3D12, an After Post Process custom pass, " +
                    "and a full-resolution mono Game camera without MSAA or TAA. Other " +
                    "configurations use raster fallback when enabled.",
                    MessageType.Info);
                if (!SystemInfo.supportsRayTracing)
                {
                    EditorGUILayout.HelpBox(
                        "Hardware ray tracing is unavailable with the current graphics API/device. " +
                        "Raster fallback will be used when enabled.",
                        MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.PropertyField(m_gpuSortType);
                if (gs.m_gpuSortType != GpuSorting.SortType.None)
                {
                    EditorGUILayout.PropertyField(m_PropSortNthFrame);
                    EditorGUILayout.PropertyField(m_CenterEyeOnly);
                }
                EditorGUILayout.PropertyField(m_PropOptimizeForQuest);
            }
            DrawSemanticAlphaControls(gs);

            if (gs.HasValidAsset)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Layer Options", EditorStyles.boldLabel);
                for (int i = 0; i < gs.asset.layerInfo.Count; i++)
                {
                    if (gs.asset.layerInfo.Count > gs.m_LayerActivationState.Count)
                    {
                        var toAdd = Enumerable.Repeat(default(int2), gs.asset.layerInfo.Count - gs.m_LayerActivationState.Count);
                        gs.m_LayerActivationState.AddRange(toAdd);
                    }
                    
                    var layer = gs.m_LayerActivationState.ElementAtOrDefault(i);
                    var layerActive = layer.y > 0;
                    var check = EditorGUILayout.Toggle($"Show layer {i}", layerActive);
                    if (check != layerActive)
                    {
                        gs.m_LayerActivationState[i] = new int2(i, check ? 1 : 0);
                        gs.UpdateRessources();
                        EditorUtility.SetDirty(gs);
                    }
                }
            }

            if (!use3DGRT)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_PropRenderMode);
                if (m_PropRenderMode.intValue is (int)GaussianSplatRenderer.RenderMode.DebugPoints or (int)GaussianSplatRenderer.RenderMode.DebugPointIndices or (int)GaussianSplatRenderer.RenderMode.DebugSemantic)
                    EditorGUILayout.PropertyField(m_PropPointDisplaySize);
            }

            EditorGUILayout.Space();
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                EditorGUILayout.PropertyField(m_PropShaderSplats);
                EditorGUILayout.PropertyField(m_PropShaderComposite);
                EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
                EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities_deviceRadixSort);
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities_fidelityFxSort);
            }
            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && gs.HasValidAsset;
            if (validAndEnabled && !gs.HasValidRenderSetup)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
                validAndEnabled = false;
            }

            if (validAndEnabled && targets.Length == 1)
            {
                EditCameras(gs);
                EditGUI(gs);
            }
            if (validAndEnabled && targets.Length > 1)
            {
                MultiEditGUI();
            }

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawSemanticBakeProgress(GaussianSplatRenderer gs)
        {
            if (!GaussianSplatSemanticBaker.TryGetProgress(gs, out var snapshot))
                return;

            EditorGUILayout.Space(2f);
            EditorGUILayout.HelpBox("Semantic bake is running for this renderer.", MessageType.Info);
            Rect rect = GUILayoutUtility.GetRect(18f, 18f, "TextField");
            EditorGUI.ProgressBar(rect, snapshot.Progress01, $"{snapshot.processedSplats:N0} / {snapshot.totalSplats:N0} splats");
            EditorGUILayout.LabelField("Phase", snapshot.phase);
            EditorGUILayout.LabelField("Layer", $"{snapshot.currentLayer}/{Math.Max(1, snapshot.layerCount)}");
            EditorGUILayout.LabelField("Elapsed", FormatDuration(snapshot.elapsedSeconds));
            EditorGUILayout.LabelField("ETA", snapshot.HasEta ? FormatDuration(snapshot.etaSeconds) : "--");
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

        void DrawSemanticAlphaControls(GaussianSplatRenderer gs)
        {
            EditorGUILayout.Space();
            GUILayout.Label("Semantic Alpha", EditorStyles.boldLabel);

            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox("Select a single renderer to choose a semantic label from its label table.", MessageType.Info);
                EditorGUILayout.Slider(m_PropSemanticMatchAlphaMultiplier, 0f, 5f, new GUIContent("Matching Alpha"));
                EditorGUILayout.Slider(m_PropSemanticNonMatchAlphaMultiplier, 0f, 5f, new GUIContent("Other Alpha"));
                return;
            }

            var entries = gs.m_SemanticLabels?.entries;
            if (entries == null || entries.Count == 0)
            {
                string selectedLabel = gs.m_SemanticAlphaLabelId == 0
                    ? "None"
                    : $"ID {gs.m_SemanticAlphaLabelId}";
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField("Semantic Label", selectedLabel);
                EditorGUILayout.HelpBox("Assign a semantic label table with entries to pick a semantic label here. A selection of None disables semantic alpha filtering.", MessageType.Info);
                EditorGUILayout.Slider(m_PropSemanticMatchAlphaMultiplier, 0f, 5f, new GUIContent("Matching Alpha"));
                EditorGUILayout.Slider(m_PropSemanticNonMatchAlphaMultiplier, 0f, 5f, new GUIContent("Other Alpha"));
                return;
            }

            var ids = new List<uint> { 0u };
            var options = new List<string> { "None" };
            int selectedIndex = 0;

            foreach (var entry in entries)
            {
                if (entry.id == 0)
                    continue;

                string label = string.IsNullOrWhiteSpace(entry.label) ? $"Semantic {entry.id}" : entry.label;
                ids.Add(entry.id);
                options.Add($"{label} ({entry.id})");
                if (entry.id == gs.m_SemanticAlphaLabelId)
                    selectedIndex = ids.Count - 1;
            }

            if (gs.m_SemanticAlphaLabelId != 0 && selectedIndex == 0)
            {
                ids.Add(gs.m_SemanticAlphaLabelId);
                options.Add($"{gs.GetSemanticName(gs.m_SemanticAlphaLabelId)} ({gs.m_SemanticAlphaLabelId})");
                selectedIndex = ids.Count - 1;
            }

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup(
                new GUIContent("Semantic Label", "Gaussians with this semantic label use the matching alpha slider; all others use the other alpha slider."),
                selectedIndex,
                options.ToArray());
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(gs, "Change Semantic Alpha Label");
                gs.m_SemanticAlphaLabelId = ids[newIndex];
                EditorUtility.SetDirty(gs);
            }

            EditorGUILayout.Slider(m_PropSemanticMatchAlphaMultiplier, 0f, 5f, new GUIContent("Matching Alpha"));
            EditorGUILayout.Slider(m_PropSemanticNonMatchAlphaMultiplier, 0f, 5f, new GUIContent("Other Alpha"));

            if (gs.m_SemanticAlphaLabelId == 0)
                EditorGUILayout.HelpBox("Semantic alpha filtering is currently disabled.", MessageType.None);
        }

        void EditCameras(GaussianSplatRenderer gs)
        {
            var asset = gs.asset;
            var cameras = asset.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        void MultiEditGUI()
        {
            DrawSeparator();
            CountTargetSplats(out var totalSplats, out var totalObjects);
            EditorGUILayout.LabelField("Total Objects", $"{totalObjects}");
            EditorGUILayout.LabelField("Total Splats", $"{totalSplats:N0}");
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
            {
                EditorGUILayout.HelpBox($"Can't merge, too many splats (max. supported {GaussianSplatAsset.kMaxSplats:N0})", MessageType.Warning);
                return;
            }

            var targetGs = (GaussianSplatRenderer) target;
            if (!targetGs || !targetGs.HasValidAsset || !targetGs.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (no asset or disable)", MessageType.Warning);
                return;
            }

            if (targetGs.asset.chunkDataSize > 0)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (needs to use Very High quality preset)", MessageType.Warning);
                return;
            }
            if (GUILayout.Button($"Merge into {target.name}"))
            {
                MergeSplatObjects();
            }
        }

        void CountTargetSplats(out int totalSplats, out int totalObjects)
        {
            totalObjects = 0;
            totalSplats = 0;
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                ++totalObjects;
                totalSplats += gs.splatCount;
            }
        }

        void MergeSplatObjects()
        {
            CountTargetSplats(out var totalSplats, out _);
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
                return;
            var targetGs = (GaussianSplatRenderer) target;

            int copyDstOffset = targetGs.splatCount;
            targetGs.EditSetSplatCount(totalSplats);
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                if (gs == targetGs)
                    continue;
                gs.EditCopySplatsInto(targetGs, 0, copyDstOffset, gs.splatCount);
                copyDstOffset += gs.splatCount;
                gs.gameObject.SetActive(false);
            }
            Debug.Assert(copyDstOffset == totalSplats, $"Merge count mismatch, {copyDstOffset} vs {totalSplats}");
            Selection.activeObject = targetGs;
        }

        void EditGUI(GaussianSplatRenderer gs)
        {
            ++s_EditStatsUpdateCounter;

            DrawSeparator();
            bool wasToolActive = ToolManager.activeContextType == typeof(GaussianToolContext);
            GUILayout.BeginHorizontal();
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            using (new EditorGUI.DisabledScope(!gs.editModified))
            {
                if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog("Reset Splat Modifications?",
                            $"This will reset edits of {gs.name} to match the {gs.asset.name} asset. Continue?",
                            "Yes, reset", "Cancel"))
                    {
                        gs.enabled = false;
                        gs.enabled = true;
                    }
                }
            }

            GUILayout.EndHorizontal();
            if (!wasToolActive && isToolActive)
            {
                ToolManager.SetActiveContext<GaussianToolContext>();
                if (Tools.current == Tool.View)
                    Tools.current = Tool.Move;
            }

            if (wasToolActive && !isToolActive)
            {
                ToolManager.SetActiveContext<GameObjectToolContext>();
            }

            if (isToolActive && gs.asset.chunkDataSize > 0)
            {
                EditorGUILayout.HelpBox("Splat move/rotate/scale tools need Very High splat quality preset", MessageType.Warning);
            }

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout"))
            {
                GaussianCutout cutout = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout)).GetComponent<GaussianCutout>();
                Transform cutoutTr = cutout.transform;
                cutoutTr.SetParent(gs.transform, false);
                cutoutTr.localScale = (gs.asset.boundsMax - gs.asset.boundsMin) * 0.25f;
                gs.m_Cutouts ??= Array.Empty<GaussianCutout>();
                ArrayUtility.Add(ref gs.m_Cutouts, cutout);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutout.gameObject;
            }
            if (GUILayout.Button("Use All Cutouts"))
            {
                gs.m_Cutouts = FindObjectsByType<GaussianCutout>(FindObjectsSortMode.InstanceID);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutouts"))
            {
                gs.m_Cutouts = Array.Empty<GaussianCutout>();
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(m_PropCutouts);

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout View"))
            {
                CutoutView cutoutView = ObjectFactory.CreateGameObject("CutoutView", typeof(CutoutView)).GetComponent<CutoutView>();
                Transform cutoutViewTr = cutoutView.transform;
                cutoutViewTr.SetParent(gs.transform, false);
                cutoutViewTr.localScale = (gs.asset.boundsMax - gs.asset.boundsMin) * 0.25f;
                cutoutView.m_GaussianRenderer = gs;
                cutoutView.m_LabelSource = gs;
                cutoutView.m_Height = 2.0f;
                cutoutView.m_Width = 2.0f;
                cutoutView.UpdateViewMesh();
                gs.m_CutoutViews ??= Array.Empty<CutoutView>();
                ArrayUtility.Add(ref gs.m_CutoutViews, cutoutView);
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutoutView.gameObject;
            }
            if (GUILayout.Button("Use All Cutout Views"))
            {
                gs.m_CutoutViews = FindObjectsByType<CutoutView>(FindObjectsSortMode.InstanceID);
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutout Views"))
            {
                gs.m_CutoutViews = Array.Empty<CutoutView>();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(m_PropCutoutViews);

            bool hasCutouts = gs.m_Cutouts != null && gs.m_Cutouts.Length != 0;
            bool modifiedOrHasCutouts = gs.editModified || hasCutouts;

            var asset = gs.asset;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            m_ExportBakeTransform = EditorGUILayout.Toggle("Export in world space", m_ExportBakeTransform);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(kPrefExportBake, m_ExportBakeTransform);
            }

            if (GUILayout.Button("Export PLY"))
                ExportPlyFile(gs, m_ExportBakeTransform);
            if (asset.posFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.scaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.colorFormat > GaussianSplatAsset.ColorFormat.Float16x4 ||
                asset.shFormat > GaussianSplatAsset.SHFormat.Float16)
            {
                EditorGUILayout.HelpBox(
                    "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                    MessageType.Warning);
            }

            bool displayEditStats = isToolActive || modifiedOrHasCutouts;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
            if (displayEditStats)
            {
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                if (hasCutouts)
                {
                    if (s_EditStatsUpdateCounter > 10)
                    {
                        gs.UpdateEditCountsAndBounds();
                        s_EditStatsUpdateCounter = 0;
                    }
                }
            }
        }

        static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = default;
            bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax);
            if (gs.editSelectedSplats > 0)
            {
                bounds = gs.editSelectedBounds;
            }
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        public static Bounds TransformBounds(Transform tr, Bounds bounds )
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }

        static unsafe void ExportPlyFile(GaussianSplatRenderer gs, bool bakeTransform)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;

            int kSplatSize = UnsafeUtility.SizeOf<GaussianSplatAssetCreator.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.splatCount, kSplatSize);

            if (!gs.EditExportData(gpuData, bakeTransform))
                return;

            GaussianSplatAssetCreator.InputSplatData[] data = new GaussianSplatAssetCreator.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.GpuEditDeleted;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // note: this is a long string! but we don't use multiline literal because we want guaranteed LF line ending
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float f_rest_0\nproperty float f_rest_1\nproperty float f_rest_2\nproperty float f_rest_3\nproperty float f_rest_4\nproperty float f_rest_5\nproperty float f_rest_6\nproperty float f_rest_7\nproperty float f_rest_8\nproperty float f_rest_9\nproperty float f_rest_10\nproperty float f_rest_11\nproperty float f_rest_12\nproperty float f_rest_13\nproperty float f_rest_14\nproperty float f_rest_15\nproperty float f_rest_16\nproperty float f_rest_17\nproperty float f_rest_18\nproperty float f_rest_19\nproperty float f_rest_20\nproperty float f_rest_21\nproperty float f_rest_22\nproperty float f_rest_23\nproperty float f_rest_24\nproperty float f_rest_25\nproperty float f_rest_26\nproperty float f_rest_27\nproperty float f_rest_28\nproperty float f_rest_29\nproperty float f_rest_30\nproperty float f_rest_31\nproperty float f_rest_32\nproperty float f_rest_33\nproperty float f_rest_34\nproperty float f_rest_35\nproperty float f_rest_36\nproperty float f_rest_37\nproperty float f_rest_38\nproperty float f_rest_39\nproperty float f_rest_40\nproperty float f_rest_41\nproperty float f_rest_42\nproperty float f_rest_43\nproperty float f_rest_44\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            fs.Write(Encoding.UTF8.GetBytes(header));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    byte* ptr = (byte*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, kSplatSize));
                }
            }

            Debug.Log($"Exported PLY {path} with {aliveCount:N0} splats");
        }
    }
}
