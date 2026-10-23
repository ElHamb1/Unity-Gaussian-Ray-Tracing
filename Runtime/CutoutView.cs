// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class CutoutView : MonoBehaviour
    {
        public enum Shape
        {
            Ellipsoid,
            Cylinder
        }

        internal const int kMaxSemanticIds = 256;

        public Shape m_Shape = Shape.Cylinder;
        [Tooltip("Gaussian renderer affected by this view when it is not a child of that renderer. If empty, Label Source is used as a legacy target, then the parent renderer, then all active renderers.")]
        public GaussianSplatRenderer m_GaussianRenderer;
        [Tooltip("Renderer whose semantic label table is used by the inspector. Kept as a legacy target for existing scenes when Gaussian Renderer is empty.")]
        public GaussianSplatRenderer m_LabelSource;
        [Min(0.0f)] public float m_Height = 2.0f;
        [Min(0.0f)] public float m_Width = 2.0f;
        [Range(3, 256)]
        [Tooltip("Cylinder view mesh segments around the circumference. More segments make smaller triangles for unified primitive sorting.")]
        public int m_CylinderRadialSegments = 64;
        [Range(1, 128)]
        [Tooltip("Cylinder view mesh subdivisions along the height. Increase this for tall cylinders so mesh triangles stay small.")]
        public int m_CylinderHeightSegments = 1;
        [Min(0.0f)] public float m_AlphaMultiplier = 0.25f;
        [Range(0.0f, 10.0f)]
        [Tooltip("Cylinder-only Gaussian radial falloff. 0 applies the alpha multiplier uniformly; higher values fade from the centerline toward the cylinder edge.")]
        public float m_GaussianFalloff = 0.0f;
        [Tooltip("When enabled, the cutout view writes alpha from Alpha Multiplier and Gaussian Falloff instead of multiplying the existing splat alpha.")]
        public bool m_ReplaceAlphaWithFalloff = false;
        [Tooltip("If empty, this view affects all semantic IDs. Otherwise only matching semantic IDs are affected.")]
        public uint[] m_SemanticIds = Array.Empty<uint>();
        [Tooltip("Optional material for the generated view mesh.")]
        public Material m_ViewMeshMaterial;
        [Tooltip("When enabled, this object keeps a renderable mesh in sync with the selected shape and height.")]
        public bool m_RenderViewMesh = true;
        public MeshFilter m_ViewMeshFilter;
        public MeshRenderer m_ViewMeshRenderer;

        Mesh m_GeneratedViewMesh;
        Shape m_LastViewMeshShape;
        float m_LastViewMeshHeight = -1.0f;
        float m_LastViewMeshWidth = -1.0f;
        int m_LastCylinderRadialSegments = -1;
        int m_LastCylinderHeightSegments = -1;
        MeshFilter m_LastViewMeshFilter;
        MeshRenderer m_LastViewMeshRenderer;
        Matrix4x4 m_LastCutoutToMeshLocal;

        public struct ShaderData
        {
            public Matrix4x4 matrix;
            public uint shape;
            public float height;
            public float width;
            public float alphaMultiplier;
            public float gaussianFalloff;
            public uint replaceAlphaWithFalloff;
            public uint semanticOffset;
            public uint semanticCount;
        }

        public static ShaderData GetShaderData(
            CutoutView self,
            Matrix4x4 rendererMatrix,
            uint semanticOffset = 0)
        {
            ShaderData sd = default;
            if (!(self && self.isActiveAndEnabled))
            {
                sd.shape = ~0u;
                return sd;
            }

            sd.matrix = self.transform.worldToLocalMatrix * rendererMatrix;
            sd.shape = (uint)self.m_Shape;
            sd.height = Mathf.Max(0.0f, self.m_Height);
            sd.width = Mathf.Max(0.0f, self.m_Width);
            sd.alphaMultiplier = Mathf.Max(0.0f, self.m_AlphaMultiplier);
            sd.gaussianFalloff = Mathf.Clamp(self.m_GaussianFalloff, 0.0f, 10.0f);
            sd.replaceAlphaWithFalloff = self.m_ReplaceAlphaWithFalloff ? 1u : 0u;
            int semanticCount = Mathf.Min(kMaxSemanticIds, self.m_SemanticIds?.Length ?? 0);
            sd.semanticOffset = semanticOffset;
            sd.semanticCount = (uint)semanticCount;

            return sd;
        }

        public GaussianSplatRenderer GetLabelSource()
        {
            if (m_LabelSource)
                return m_LabelSource;
            if (m_GaussianRenderer)
                return m_GaussianRenderer;
            return GetComponentInParent<GaussianSplatRenderer>();
        }

        public GaussianSplatRenderer GetGaussianRendererTarget()
        {
            if (m_GaussianRenderer)
                return ResolveActiveRenderer(m_GaussianRenderer);

            if (m_LabelSource)
                return ResolveActiveRenderer(m_LabelSource);

            return GetComponentInParent<GaussianSplatRenderer>();
        }

        static GaussianSplatRenderer ResolveActiveRenderer(GaussianSplatRenderer renderer)
        {
            if (!renderer)
                return null;

            if (renderer.isActiveAndEnabled)
                return renderer;

            foreach (var sibling in renderer.GetComponents<GaussianSplatRenderer>())
            {
                if (sibling && sibling.isActiveAndEnabled)
                    return sibling;
            }

            return renderer;
        }

        public void SelectAllSemanticLabels()
        {
            var entries = GetLabelSource()?.m_SemanticLabels?.entries;
            if (entries == null)
            {
                m_SemanticIds = Array.Empty<uint>();
                return;
            }

            m_SemanticIds = entries
                .Select(entry => entry.id)
                .Prepend(0u)
                .Distinct()
                .Take(kMaxSemanticIds)
                .ToArray();
        }

        public void SetSemanticIds(IEnumerable<uint> semanticIds)
        {
            m_SemanticIds = semanticIds?
                .Distinct()
                .Take(kMaxSemanticIds)
                .ToArray() ?? Array.Empty<uint>();
        }

        public void UpdateViewMesh()
        {
            UpdateViewMesh(true);
        }

        void UpdateViewMesh(bool forceRebuild)
        {
            if (!m_RenderViewMesh)
            {
                if (m_ViewMeshRenderer)
                    m_ViewMeshRenderer.enabled = false;
                return;
            }

            EnsureMeshComponents();

            if (!m_ViewMeshFilter || !m_ViewMeshRenderer)
                return;

            m_ViewMeshRenderer.enabled = true;
            if (m_ViewMeshMaterial)
                m_ViewMeshRenderer.sharedMaterial = m_ViewMeshMaterial;

            float height = Mathf.Max(0.0f, m_Height);
            float width = Mathf.Max(0.0f, m_Width);
            int radialSegments = Mathf.Clamp(m_CylinderRadialSegments, 3, 256);
            int heightSegments = Mathf.Clamp(m_CylinderHeightSegments, 1, 128);
            Matrix4x4 cutoutToMeshLocal =
                m_ViewMeshFilter.transform.worldToLocalMatrix * transform.localToWorldMatrix;

            if (!forceRebuild &&
                m_GeneratedViewMesh &&
                m_ViewMeshFilter.sharedMesh == m_GeneratedViewMesh &&
                m_LastViewMeshFilter == m_ViewMeshFilter &&
                m_LastViewMeshRenderer == m_ViewMeshRenderer &&
                m_LastViewMeshShape == m_Shape &&
                Mathf.Approximately(m_LastViewMeshHeight, height) &&
                Mathf.Approximately(m_LastViewMeshWidth, width) &&
                m_LastCylinderRadialSegments == radialSegments &&
                m_LastCylinderHeightSegments == heightSegments &&
                Approximately(m_LastCutoutToMeshLocal, cutoutToMeshLocal))
            {
                return;
            }

            Mesh mesh = m_Shape == Shape.Cylinder
                ? CreateCylinderMesh(height, width, radialSegments, heightSegments, cutoutToMeshLocal)
                : CreateEllipsoidMesh(32, 16, cutoutToMeshLocal);

            ReplaceGeneratedMesh(mesh);

            m_LastViewMeshFilter = m_ViewMeshFilter;
            m_LastViewMeshRenderer = m_ViewMeshRenderer;
            m_LastViewMeshShape = m_Shape;
            m_LastViewMeshHeight = height;
            m_LastViewMeshWidth = width;
            m_LastCylinderRadialSegments = radialSegments;
            m_LastCylinderHeightSegments = heightSegments;
            m_LastCutoutToMeshLocal = cutoutToMeshLocal;
        }

        void EnsureMeshComponents()
        {
            if (!m_ViewMeshFilter)
                m_ViewMeshFilter = GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            if (!m_ViewMeshRenderer)
                m_ViewMeshRenderer = GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
        }

        void ReplaceGeneratedMesh(Mesh mesh)
        {
            Mesh oldMesh = m_GeneratedViewMesh;
            m_GeneratedViewMesh = mesh;
            m_ViewMeshFilter.sharedMesh = mesh;
            SyncUnifiedPrimitiveMesh(mesh);

            if (oldMesh && oldMesh != mesh)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(oldMesh);
                else
#endif
                    Destroy(oldMesh);
            }
        }

        void SyncUnifiedPrimitiveMesh(Mesh mesh)
        {
#if GS_ENABLE_URP
            var unifiedMesh = m_ViewMeshFilter.GetComponent<GaussianSplatUnifiedPrimitiveMesh>();
            if (unifiedMesh)
                unifiedMesh.SetMesh(mesh);
#endif
        }

        void Update()
        {
            UpdateViewMesh(false);
        }

        static bool Approximately(Matrix4x4 lhs, Matrix4x4 rhs)
        {
            for (int i = 0; i < 16; i++)
            {
                if (!Mathf.Approximately(lhs[i], rhs[i]))
                    return false;
            }

            return true;
        }

        static Mesh CreateCylinderMesh(
            float height,
            float width,
            int radialSegments,
            int heightSegments,
            Matrix4x4 vertexTransform)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            float halfHeight = height * 0.5f;
            float radius = width * 0.5f;

            for (int y = 0; y <= heightSegments; y++)
            {
                float t = y / (float)heightSegments;
                float ringY = Mathf.Lerp(halfHeight, -halfHeight, t);
                for (int i = 0; i < radialSegments; i++)
                {
                    float angle = i / (float)radialSegments * Mathf.PI * 2.0f;
                    float x = Mathf.Cos(angle) * radius;
                    float z = Mathf.Sin(angle) * radius;
                    Vector3 vertex = new(x, ringY, z);
                    vertices.Add(vertexTransform.MultiplyPoint3x4(vertex));
                }
            }

            for (int y = 0; y < heightSegments; y++)
            {
                int topRow = y * radialSegments;
                int bottomRow = (y + 1) * radialSegments;
                for (int i = 0; i < radialSegments; i++)
                {
                    int next = (i + 1) % radialSegments;
                    int top = topRow + i;
                    int bottom = bottomRow + i;
                    int nextTop = topRow + next;
                    int nextBottom = bottomRow + next;

                    triangles.Add(top);
                    triangles.Add(nextTop);
                    triangles.Add(bottom);
                    triangles.Add(bottom);
                    triangles.Add(nextTop);
                    triangles.Add(nextBottom);
                }
            }

            var mesh = new Mesh { name = "CutoutView Cylinder" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh CreateEllipsoidMesh(int longitudeSegments, int latitudeSegments, Matrix4x4 vertexTransform)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            for (int lat = 0; lat <= latitudeSegments; lat++)
            {
                float v = lat / (float)latitudeSegments;
                float phi = v * Mathf.PI;
                float y = Mathf.Cos(phi);
                float radius = Mathf.Sin(phi);
                for (int lon = 0; lon <= longitudeSegments; lon++)
                {
                    float u = lon / (float)longitudeSegments;
                    float theta = u * Mathf.PI * 2.0f;
                    Vector3 vertex = new(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
                    vertices.Add(vertexTransform.MultiplyPoint3x4(vertex));
                }
            }

            int row = longitudeSegments + 1;
            for (int lat = 0; lat < latitudeSegments; lat++)
            {
                for (int lon = 0; lon < longitudeSegments; lon++)
                {
                    int a = lat * row + lon;
                    int b = a + row;
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(a + 1);
                    triangles.Add(a + 1);
                    triangles.Add(b);
                    triangles.Add(b + 1);
                }
            }

            var mesh = new Mesh { name = "CutoutView Ellipsoid" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void OnValidate()
        {
            m_Height = Mathf.Max(0.0f, m_Height);
            m_Width = Mathf.Max(0.0f, m_Width);
            m_CylinderRadialSegments = Mathf.Clamp(m_CylinderRadialSegments, 3, 256);
            m_CylinderHeightSegments = Mathf.Clamp(m_CylinderHeightSegments, 1, 128);
            m_AlphaMultiplier = Mathf.Max(0.0f, m_AlphaMultiplier);
            m_GaussianFalloff = Mathf.Clamp(m_GaussianFalloff, 0.0f, 10.0f);
            if (m_RenderViewMesh)
                UpdateViewMesh(false);
            else if (m_ViewMeshRenderer)
                m_ViewMeshRenderer.enabled = false;
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = Color.cyan;
            color.a = UnityEditor.Selection.Contains(gameObject) ? 0.9f : 0.35f;
            Gizmos.color = color;

            if (m_Shape == Shape.Ellipsoid)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
                return;
            }

            DrawCylinderGizmo(
                Mathf.Max(0.0f, m_Height),
                Mathf.Max(0.0f, m_Width),
                Mathf.Clamp(m_CylinderRadialSegments, 3, 256));
        }

        static void DrawCylinderGizmo(float height, float width, int segments)
        {
            float halfHeight = height * 0.5f;
            float radius = width * 0.5f;
            Vector3 prevTop = new Vector3(radius, halfHeight, 0);
            Vector3 prevBottom = new Vector3(radius, -halfHeight, 0);

            for (int i = 1; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2.0f;
                Vector3 top = new Vector3(Mathf.Cos(angle) * radius, halfHeight, Mathf.Sin(angle) * radius);
                Vector3 bottom = new Vector3(top.x, -halfHeight, top.z);
                Gizmos.DrawLine(prevTop, top);
                Gizmos.DrawLine(prevBottom, bottom);
                if (i % 12 == 0)
                    Gizmos.DrawLine(top, bottom);
                prevTop = top;
                prevBottom = bottom;
            }
        }
#endif
    }
}
