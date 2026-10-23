// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(CutoutView))]
    [CanEditMultipleObjects]
    public class CutoutViewEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropShape;
        SerializedProperty m_PropGaussianRenderer;
        SerializedProperty m_PropLabelSource;
        SerializedProperty m_PropHeight;
        SerializedProperty m_PropWidth;
        SerializedProperty m_PropCylinderRadialSegments;
        SerializedProperty m_PropCylinderHeightSegments;
        SerializedProperty m_PropAlphaMultiplier;
        SerializedProperty m_PropGaussianFalloff;
        SerializedProperty m_PropReplaceAlphaWithFalloff;
        SerializedProperty m_PropSemanticIds;
        SerializedProperty m_PropViewMeshMaterial;
        SerializedProperty m_PropRenderViewMesh;
        SerializedProperty m_PropViewMeshFilter;
        SerializedProperty m_PropViewMeshRenderer;

        Vector2 m_SemanticScroll;

        void OnEnable()
        {
            m_PropShape = serializedObject.FindProperty("m_Shape");
            m_PropGaussianRenderer = serializedObject.FindProperty("m_GaussianRenderer");
            m_PropLabelSource = serializedObject.FindProperty("m_LabelSource");
            m_PropHeight = serializedObject.FindProperty("m_Height");
            m_PropWidth = serializedObject.FindProperty("m_Width");
            m_PropCylinderRadialSegments = serializedObject.FindProperty("m_CylinderRadialSegments");
            m_PropCylinderHeightSegments = serializedObject.FindProperty("m_CylinderHeightSegments");
            m_PropAlphaMultiplier = serializedObject.FindProperty("m_AlphaMultiplier");
            m_PropGaussianFalloff = serializedObject.FindProperty("m_GaussianFalloff");
            m_PropReplaceAlphaWithFalloff = serializedObject.FindProperty("m_ReplaceAlphaWithFalloff");
            m_PropSemanticIds = serializedObject.FindProperty("m_SemanticIds");
            m_PropViewMeshMaterial = serializedObject.FindProperty("m_ViewMeshMaterial");
            m_PropRenderViewMesh = serializedObject.FindProperty("m_RenderViewMesh");
            m_PropViewMeshFilter = serializedObject.FindProperty("m_ViewMeshFilter");
            m_PropViewMeshRenderer = serializedObject.FindProperty("m_ViewMeshRenderer");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(m_PropShape);
            EditorGUILayout.PropertyField(m_PropGaussianRenderer);
            EditorGUILayout.PropertyField(m_PropLabelSource);
            EditorGUILayout.PropertyField(m_PropHeight);
            EditorGUILayout.PropertyField(m_PropWidth);
            DrawCylinderMeshDensity();
            EditorGUILayout.PropertyField(m_PropAlphaMultiplier);
            EditorGUILayout.PropertyField(m_PropGaussianFalloff);
            EditorGUILayout.PropertyField(m_PropReplaceAlphaWithFalloff);

            EditorGUILayout.Space();
            DrawSemanticSelection();

            EditorGUILayout.Space();
            GUILayout.Label("View Mesh", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderViewMesh);
            EditorGUILayout.PropertyField(m_PropViewMeshMaterial);
            EditorGUILayout.PropertyField(m_PropViewMeshFilter);
            EditorGUILayout.PropertyField(m_PropViewMeshRenderer);

            serializedObject.ApplyModifiedProperties();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Update View Mesh"))
                    UpdateMeshes();
                if (GUILayout.Button("Remove Mesh Rendering"))
                    DisableMeshes();
            }
        }

        void DrawSemanticSelection()
        {
            GUILayout.Label("Semantic Filter", EditorStyles.boldLabel);

            if (targets.Length > 1)
            {
                EditorGUILayout.PropertyField(m_PropSemanticIds);
                return;
            }

            var cutoutView = (CutoutView)target;
            var source = cutoutView.GetLabelSource();
            var entries = source?.m_SemanticLabels?.entries;

            if (entries == null || entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Assign a semantic label table on the GaussianSplatRenderer to select labels here. Empty means all semantics are affected.", MessageType.Info);
                EditorGUILayout.PropertyField(m_PropSemanticIds);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All"))
                {
                    Undo.RecordObject(cutoutView, "Select All Cutout View Semantics");
                    cutoutView.SelectAllSemanticLabels();
                    EditorUtility.SetDirty(cutoutView);
                    serializedObject.Update();
                }

                if (GUILayout.Button("Clear"))
                {
                    Undo.RecordObject(cutoutView, "Clear Cutout View Semantics");
                    cutoutView.m_SemanticIds = Array.Empty<uint>();
                    EditorUtility.SetDirty(cutoutView);
                    serializedObject.Update();
                }
            }

            var selected = new HashSet<uint>(cutoutView.m_SemanticIds ?? Array.Empty<uint>());
            EditorGUILayout.HelpBox(selected.Count == 0 ? "No labels selected: this CutoutView affects all semantics." : $"{selected.Count} labels selected.", MessageType.None);

            m_SemanticScroll = EditorGUILayout.BeginScrollView(m_SemanticScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(260));
            DrawSemanticToggle(cutoutView, selected, 0u, "None");

            foreach (var entry in entries.OrderBy(entry => entry.id))
            {
                if (entry.id == 0)
                    continue;

                string label = string.IsNullOrWhiteSpace(entry.label) ? $"Semantic {entry.id}" : entry.label;
                DrawSemanticToggle(cutoutView, selected, entry.id, label);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.PropertyField(m_PropSemanticIds);
        }

        void DrawSemanticToggle(CutoutView cutoutView, HashSet<uint> selected, uint semanticId, string label)
        {
            bool isSelected = selected.Contains(semanticId);
            bool newSelected = EditorGUILayout.ToggleLeft($"{label} ({semanticId})", isSelected);
            if (newSelected == isSelected)
                return;

            Undo.RecordObject(cutoutView, "Change Cutout View Semantics");
            if (newSelected)
                selected.Add(semanticId);
            else
                selected.Remove(semanticId);
            cutoutView.SetSemanticIds(selected);
            EditorUtility.SetDirty(cutoutView);
            serializedObject.Update();
        }

        void UpdateMeshes()
        {
            foreach (var obj in targets)
            {
                var cutoutView = (CutoutView)obj;
                Undo.RecordObject(cutoutView, "Update Cutout View Mesh");
                cutoutView.m_RenderViewMesh = true;
                cutoutView.UpdateViewMesh();
                EditorUtility.SetDirty(cutoutView);
            }
        }

        void DisableMeshes()
        {
            foreach (var obj in targets)
            {
                var cutoutView = (CutoutView)obj;
                Undo.RecordObject(cutoutView, "Disable Cutout View Mesh");
                cutoutView.m_RenderViewMesh = false;
                cutoutView.UpdateViewMesh();
                EditorUtility.SetDirty(cutoutView);
            }
        }

        void DrawCylinderMeshDensity()
        {
            if ((CutoutView.Shape)m_PropShape.enumValueIndex != CutoutView.Shape.Cylinder)
                return;

            EditorGUILayout.PropertyField(m_PropCylinderRadialSegments);
            EditorGUILayout.PropertyField(m_PropCylinderHeightSegments);

            if (!m_PropCylinderRadialSegments.hasMultipleDifferentValues &&
                !m_PropCylinderHeightSegments.hasMultipleDifferentValues)
            {
                int triangleCount = m_PropCylinderRadialSegments.intValue * m_PropCylinderHeightSegments.intValue * 2;
                int vertexCount = m_PropCylinderRadialSegments.intValue * (m_PropCylinderHeightSegments.intValue + 1);
                EditorGUILayout.HelpBox($"Generated cylinder side mesh: {vertexCount} vertices, {triangleCount} triangles.", MessageType.None);
            }
        }
    }
}
