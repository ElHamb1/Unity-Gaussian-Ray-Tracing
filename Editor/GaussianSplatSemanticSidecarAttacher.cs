// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatSemanticSidecarAttacher : EditorWindow
    {
        [Serializable]
        class LayerSemanticBinding
        {
            public int layer;
            public TextAsset semanticData;
            public string posAssetPath;
        }

        [Serializable]
        class SemanticLabelEntryJson
        {
            public uint id;
            public string label;
        }

        [SerializeField] GaussianSplatAsset m_Asset;
        [SerializeField] GaussianSplatSemanticLabelTable m_LabelTableAsset;
        [SerializeField] TextAsset m_LabelFile;
        [SerializeField] GameObject m_SceneSegmentationRoot;
        [SerializeField] bool m_IncludeInactiveSceneObjects;
        [SerializeField] List<LayerSemanticBinding> m_LayerBindings = new();
        [SerializeField] bool m_ClearUnassignedLayers;

        string m_StatusMessage;
        MessageType m_StatusType = MessageType.Info;
        GaussianSplatAsset m_LastSyncedAsset;

        [MenuItem("Tools/Gaussian Splats/Attach Semantic Sidecars")]
        static void Init()
        {
            Open(null);
        }

        public static void Open(GaussianSplatAsset asset)
        {
            var window = GetWindow<GaussianSplatSemanticSidecarAttacher>(false, "Attach Semantic Sidecars", true);
            window.minSize = new Vector2(460, 320);
            window.SetAsset(asset);
            window.Show();
        }

        void SetAsset(GaussianSplatAsset asset)
        {
            m_Asset = asset;
            if (m_SceneSegmentationRoot == null)
                m_SceneSegmentationRoot = GameObject.Find("FullbodySegmentations");
            SyncBindings(force: true);
        }

        void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                m_Asset = (GaussianSplatAsset)EditorGUILayout.ObjectField("Gaussian Asset", m_Asset, typeof(GaussianSplatAsset), false);
                m_LabelTableAsset = (GaussianSplatSemanticLabelTable)EditorGUILayout.ObjectField("Semantic Label Table", m_LabelTableAsset, typeof(GaussianSplatSemanticLabelTable), false);
                m_LabelFile = (TextAsset)EditorGUILayout.ObjectField("Semantic Labels File", m_LabelFile, typeof(TextAsset), false);
                m_SceneSegmentationRoot = (GameObject)EditorGUILayout.ObjectField("Scene Segmentation Root", m_SceneSegmentationRoot, typeof(GameObject), true);
                m_IncludeInactiveSceneObjects = EditorGUILayout.Toggle("Include Inactive Scene Objects", m_IncludeInactiveSceneObjects);
                m_ClearUnassignedLayers = EditorGUILayout.Toggle(new GUIContent("Clear Unassigned Layers", "If enabled, layers without a semantic file assigned will have their semantic reference cleared."), m_ClearUnassignedLayers);

                if (check.changed)
                    SyncBindings(force: false);
            }

            EditorGUILayout.HelpBox(
                "Attach already-imported semantic sidecars to an existing GaussianSplatAsset without the original PLY. " +
                "Assign one semantic TextAsset per layer. Labels are optional and can come from an existing SemanticLabelTable asset or from a JSON/CSV text asset.",
                MessageType.Info);

            if (!string.IsNullOrWhiteSpace(m_StatusMessage))
                EditorGUILayout.HelpBox(m_StatusMessage, m_StatusType);

            if (m_Asset == null)
                return;

            SyncBindings(force: false);

            EditorGUILayout.Space();
            GUILayout.Label("Layer Bindings", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(m_Asset == null))
            {
                if (GUILayout.Button("Auto-Fill From Adjacent _sem.bytes"))
                    AutoFillBindingsFromAdjacentFiles();
            }

            using (new EditorGUI.DisabledScope(m_SceneSegmentationRoot == null))
            {
                if (GUILayout.Button("Populate Label Table From Scene Names"))
                    PopulateLabelTableFromSceneNames();
            }

            if (m_LayerBindings.Count == 0)
            {
                EditorGUILayout.HelpBox("This asset has no layers to attach semantic sidecars to.", MessageType.Warning);
            }
            else
            {
                foreach (var binding in m_LayerBindings.OrderBy(b => b.layer))
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField($"Layer {binding.layer}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Position Sidecar", FilePickerControl.PathToDisplayString(binding.posAssetPath));
                    binding.semanticData = (TextAsset)EditorGUILayout.ObjectField("Semantic Sidecar", binding.semanticData, typeof(TextAsset), false);
                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!CanAttach()))
            {
                if (GUILayout.Button("Attach Semantic Sidecars"))
                    Attach();
            }
        }

        bool CanAttach()
        {
            return m_Asset != null && m_Asset.LayerData.Count > 0;
        }

        void SyncBindings(bool force)
        {
            if (!force && m_Asset == m_LastSyncedAsset)
                return;

            m_LastSyncedAsset = m_Asset;
            if (m_Asset == null)
            {
                m_LayerBindings.Clear();
                return;
            }

            var previousBindings = m_LayerBindings.ToDictionary(binding => binding.layer, binding => binding.semanticData);
            var nextBindings = new List<LayerSemanticBinding>(m_Asset.LayerData.Count);
            foreach (var layerAsset in m_Asset.LayerData.OrderBy(layer => layer.layer))
            {
                previousBindings.TryGetValue(layerAsset.layer, out TextAsset previousSemanticData);
                TextAsset semanticData = previousSemanticData != null
                    ? previousSemanticData
                    : layerAsset.m_SemanticData != null
                        ? layerAsset.m_SemanticData
                        : FindAdjacentSemanticAsset(layerAsset.m_PosData);

                nextBindings.Add(new LayerSemanticBinding
                {
                    layer = layerAsset.layer,
                    semanticData = semanticData,
                    posAssetPath = AssetDatabase.GetAssetPath(layerAsset.m_PosData)
                });
            }

            m_LayerBindings = nextBindings;
        }

        void AutoFillBindingsFromAdjacentFiles()
        {
            foreach (var binding in m_LayerBindings)
                binding.semanticData = FindAdjacentSemanticAsset(binding.posAssetPath);
        }

        void Attach()
        {
            if (!CanAttach())
                return;

            Undo.RecordObject(m_Asset, "Attach Gaussian Semantic Sidecars");

            int attachedLayers = 0;
            for (int i = 0; i < m_Asset.LayerData.Count; i++)
            {
                var layerAsset = m_Asset.LayerData[i];
                var binding = m_LayerBindings.FirstOrDefault(b => b.layer == layerAsset.layer);
                if (binding == null)
                    continue;

                if (binding.semanticData == null && !m_ClearUnassignedLayers)
                    continue;

                layerAsset.m_SemanticData = binding.semanticData;
                m_Asset.LayerData[i] = layerAsset;
                if (binding.semanticData != null)
                    attachedLayers++;
            }

            GaussianSplatSemanticLabelTable labelTable = m_LabelTableAsset;
            if (m_LabelFile != null)
            {
                if (!TryParseSemanticLabelEntries(m_LabelFile.text, out List<GaussianSplatSemanticLabelTable.Entry> entries, out string error))
                {
                    m_StatusMessage = error;
                    m_StatusType = MessageType.Error;
                    return;
                }

                labelTable = CreateOrUpdateLabelTable(entries);
            }
            else if (m_SceneSegmentationRoot != null)
            {
                if (!TryBuildSceneLabelEntries(m_SceneSegmentationRoot, m_IncludeInactiveSceneObjects, out List<GaussianSplatSemanticLabelTable.Entry> entries, out string error))
                {
                    m_StatusMessage = error;
                    m_StatusType = MessageType.Error;
                    return;
                }

                labelTable = CreateOrUpdateLabelTable(entries);
            }

            RefreshLoadedRenderers(labelTable);

            EditorUtility.SetDirty(m_Asset);
            AssetDatabase.SaveAssets();

            string labelStatus = labelTable != null ? $" Using label table '{labelTable.name}'." : string.Empty;
            m_StatusMessage = $"Attached semantic sidecars to {attachedLayers} layer(s).{labelStatus}";
            m_StatusType = MessageType.Info;
        }

        void PopulateLabelTableFromSceneNames()
        {
            if (!TryBuildSceneLabelEntries(m_SceneSegmentationRoot, m_IncludeInactiveSceneObjects, out List<GaussianSplatSemanticLabelTable.Entry> entries, out string error))
            {
                m_StatusMessage = error;
                m_StatusType = MessageType.Error;
                return;
            }

            var table = CreateOrUpdateLabelTable(entries);
            RefreshLoadedRenderers(table);
            AssetDatabase.SaveAssets();
            m_StatusMessage = $"Populated label table '{table.name}' with {entries.Count:N0} scene-derived entries.";
            m_StatusType = MessageType.Info;
        }

        GaussianSplatSemanticLabelTable CreateOrUpdateLabelTable(IEnumerable<GaussianSplatSemanticLabelTable.Entry> entries)
        {
            string assetPath = AssetDatabase.GetAssetPath(m_Asset);
            string assetFolder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(assetFolder))
                assetFolder = "Assets";

            string path = $"{assetFolder}/{m_Asset.name}_SemanticLabels.asset";
            var table = m_LabelTableAsset != null ? m_LabelTableAsset : AssetDatabase.LoadAssetAtPath<GaussianSplatSemanticLabelTable>(path);
            if (table == null)
            {
                table = CreateInstance<GaussianSplatSemanticLabelTable>();
                AssetDatabase.CreateAsset(table, path);
            }

            Undo.RecordObject(table, "Attach Gaussian Semantic Sidecars");
            table.SetEntries(entries);
            EditorUtility.SetDirty(table);
            m_LabelTableAsset = table;
            return table;
        }

        void RefreshLoadedRenderers(GaussianSplatSemanticLabelTable labelTable)
        {
            foreach (var renderer in Resources.FindObjectsOfTypeAll<GaussianSplatRenderer>())
            {
                if (renderer == null || renderer.asset != m_Asset)
                    continue;

                Undo.RecordObject(renderer, "Attach Gaussian Semantic Sidecars");
                if (labelTable != null)
                    renderer.m_SemanticLabels = labelTable;
                renderer.UpdateRessources();
                EditorUtility.SetDirty(renderer);
            }
        }

        static TextAsset FindAdjacentSemanticAsset(TextAsset posData)
        {
            return posData == null ? null : FindAdjacentSemanticAsset(AssetDatabase.GetAssetPath(posData));
        }

        static TextAsset FindAdjacentSemanticAsset(string posAssetPath)
        {
            if (string.IsNullOrWhiteSpace(posAssetPath))
                return null;

            posAssetPath = posAssetPath.Replace('\\', '/');
            string semanticPath;
            if (posAssetPath.EndsWith("_pos.bytes", StringComparison.OrdinalIgnoreCase))
                semanticPath = posAssetPath[..^"_pos.bytes".Length] + "_sem.bytes";
            else
                semanticPath = Path.ChangeExtension(posAssetPath, null) + "_sem.bytes";

            return AssetDatabase.LoadAssetAtPath<TextAsset>(semanticPath);
        }

        static bool TryParseSemanticLabelEntries(string text, out List<GaussianSplatSemanticLabelTable.Entry> entries, out string error)
        {
            entries = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                entries = new List<GaussianSplatSemanticLabelTable.Entry>();
                return true;
            }

            try
            {
                string trimmed = text.Trim();
                if (trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    var dict = trimmed.FromJson<Dictionary<string, string>>();
                    if (dict != null)
                    {
                        entries = dict
                            .Where(kv => uint.TryParse(kv.Key, out uint id) && id != 0 && !string.IsNullOrWhiteSpace(kv.Value))
                            .Select(kv => new GaussianSplatSemanticLabelTable.Entry
                            {
                                id = uint.Parse(kv.Key),
                                label = kv.Value
                            })
                            .OrderBy(entry => entry.id)
                            .ToList();
                        return true;
                    }
                }
                else if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    var jsonEntries = trimmed.FromJson<List<SemanticLabelEntryJson>>();
                    if (jsonEntries != null)
                    {
                        entries = jsonEntries
                            .Where(entry => entry != null && entry.id != 0 && !string.IsNullOrWhiteSpace(entry.label))
                            .Select(entry => new GaussianSplatSemanticLabelTable.Entry { id = entry.id, label = entry.label })
                            .OrderBy(entry => entry.id)
                            .ToList();
                        return true;
                    }
                }

                entries = ParseSemanticLabelCsv(trimmed);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to parse semantic labels: {ex.Message}";
                return false;
            }
        }

        static List<GaussianSplatSemanticLabelTable.Entry> ParseSemanticLabelCsv(string text)
        {
            var entries = new List<GaussianSplatSemanticLabelTable.Entry>();
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separatorIndex = line.IndexOf(',');
                if (separatorIndex < 0)
                    separatorIndex = line.IndexOf('\t');
                if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
                    continue;

                string idText = line[..separatorIndex].Trim();
                string label = line[(separatorIndex + 1)..].Trim();
                if (!uint.TryParse(idText, out uint id) || id == 0 || string.IsNullOrWhiteSpace(label))
                    continue;

                entries.Add(new GaussianSplatSemanticLabelTable.Entry
                {
                    id = id,
                    label = label
                });
            }

            return entries.OrderBy(entry => entry.id).ToList();
        }

        static bool TryBuildSceneLabelEntries(GameObject root, bool includeInactive, out List<GaussianSplatSemanticLabelTable.Entry> entries, out string error)
        {
            entries = null;
            error = null;

            if (root == null)
            {
                error = "Assign a Scene Segmentation Root to build labels from scene object names.";
                return false;
            }

            var sourceNames = CollectSceneSemanticSourceNames(root, includeInactive);
            if (sourceNames.Count == 0)
            {
                error = "No usable scene segmentation objects were found under the selected root.";
                return false;
            }

            entries = new List<GaussianSplatSemanticLabelTable.Entry>(sourceNames.Count);
            uint nextId = 1;
            foreach (string sourceName in sourceNames)
            {
                entries.Add(new GaussianSplatSemanticLabelTable.Entry
                {
                    id = nextId++,
                    label = SanitizeSemanticLabel(sourceName)
                });
            }

            return true;
        }

        static List<string> CollectSceneSemanticSourceNames(GameObject root, bool includeInactive)
        {
            var result = new List<string>();
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

            foreach (Transform candidate in groupRoots)
            {
                if (HasUsableSceneSemanticGeometry(candidate, includeInactive, includeChildren: true))
                    result.Add(candidate.name);
            }

            if (result.Count > 0)
                return result;

            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(includeInactive))
            {
                if (candidate == null)
                    continue;
                if (!includeInactive && !candidate.gameObject.activeInHierarchy)
                    continue;
                if (HasUsableSceneSemanticGeometry(candidate, includeInactive, includeChildren: false))
                    result.Add(candidate.name);
            }

            return result;
        }

        static bool HasUsableSceneSemanticGeometry(Transform candidate, bool includeInactive, bool includeChildren)
        {
            if (candidate == null)
                return false;

            var colliders = includeChildren
                ? candidate.GetComponentsInChildren<Collider>(includeInactive)
                : candidate.GetComponents<Collider>();
            if (colliders.Any(c => c != null && c.enabled))
                return true;

            var renderers = includeChildren
                ? candidate.GetComponentsInChildren<Renderer>(includeInactive)
                : candidate.GetComponents<Renderer>();
            if (renderers.Any(r => r != null && r.enabled && r is not ParticleSystemRenderer))
                return true;

            var meshFilters = includeChildren
                ? candidate.GetComponentsInChildren<MeshFilter>(includeInactive)
                : candidate.GetComponents<MeshFilter>();
            if (meshFilters.Any(mf =>
            {
                if (mf == null || mf.sharedMesh == null)
                    return false;
                var meshRenderer = mf.GetComponent<MeshRenderer>();
                return meshRenderer != null && meshRenderer.enabled;
            }))
                return true;

            var skinnedMeshRenderers = includeChildren
                ? candidate.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive)
                : candidate.GetComponents<SkinnedMeshRenderer>();
            return skinnedMeshRenderers.Any(smr => smr != null && smr.enabled);
        }

        static string SanitizeSemanticLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            int dotIndex = name.IndexOf('.');
            return dotIndex > 0 ? name[..dotIndex] : name;
        }
    }
}
