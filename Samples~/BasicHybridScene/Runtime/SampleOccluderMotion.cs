// SPDX-License-Identifier: MIT
using GaussianSplatting.Runtime;
using UnityEngine;

namespace GaussianSplatting.Samples
{
    /// <summary>A moving, deforming triangle mesh to demonstrate shadow updates.</summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(Gaussian3DGRTTransmission))]
    public sealed class SampleOccluderMotion : MonoBehaviour
    {
        public float travel = 0.5f;
        public float speed = 0.8f;
        Mesh m_Original;
        Mesh m_Dynamic;
        Vector3[] m_Vertices;
        Vector3[] m_BaseVertices;
        Vector3 m_Origin;
        MeshFilter m_Filter;
        Gaussian3DGRTTransmission m_Transmission;

        void OnEnable()
        {
            m_Filter = GetComponent<MeshFilter>();
            m_Transmission = GetComponent<Gaussian3DGRTTransmission>();
            m_Original = m_Filter.sharedMesh;
            if (m_Original == null) return;
            m_Dynamic = Instantiate(m_Original);
            m_Dynamic.name = "3DGRT sample dynamic occluder";
            m_Dynamic.MarkDynamic();
            m_BaseVertices = m_Dynamic.vertices;
            m_Vertices = (Vector3[])m_BaseVertices.Clone();
            m_Origin = transform.localPosition;
            m_Filter.sharedMesh = m_Dynamic;
            GetComponent<MeshRenderer>().rayTracingMode =
                UnityEngine.Experimental.Rendering.RayTracingMode.DynamicGeometry;
            m_Transmission.MarkGeometryDirty();
        }

        void Update()
        {
            if (m_Dynamic == null) return;
            float phase = Time.time * speed;
            transform.localPosition = m_Origin + Vector3.right * (Mathf.Sin(phase) * travel);
            for (int i = 0; i < m_Vertices.Length; ++i)
            {
                Vector3 vertex = m_BaseVertices[i];
                vertex.y += 0.12f * Mathf.Sin(phase * 1.7f + vertex.x * 2f);
                m_Vertices[i] = vertex;
            }
            m_Dynamic.vertices = m_Vertices;
            m_Dynamic.RecalculateBounds();
            m_Dynamic.RecalculateNormals();
            // MarkDynamic alone does not invalidate ray tracing geometry/history.
            m_Transmission.MarkGeometryDirty();
        }

        void OnDisable()
        {
            if (m_Dynamic == null) return;
            if (m_Filter != null && m_Filter.sharedMesh == m_Dynamic) m_Filter.sharedMesh = m_Original;
            transform.localPosition = m_Origin;
            Destroy(m_Dynamic);
            m_Dynamic = null;
            if (m_Transmission != null) m_Transmission.MarkGeometryDirty();
        }
    }
}
