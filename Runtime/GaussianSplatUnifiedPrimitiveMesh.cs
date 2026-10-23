// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [AddComponentMenu("Gaussian Splatting/Unified Primitive Mesh")]
    public class GaussianSplatUnifiedPrimitiveMesh : MonoBehaviour
    {
        [SerializeField] Mesh m_Mesh;
        [SerializeField] Renderer m_SourceRenderer;
        [SerializeField] Material m_MaterialOverride;
        [SerializeField] int m_SubMeshIndex = -1;
        [SerializeField] Color m_BaseColor = new(1, 1, 1, 0.35f);
        [SerializeField, Range(0, 1)] float m_Opacity = 1;
        [SerializeField] bool m_ReadBaseColorFromMaterial = true;
        [SerializeField] bool m_UseFaceColors;
        [SerializeField] Color m_OutsideColor = new(1, 1, 1, 0.35f);
        [SerializeField] Color m_InsideColor = new(0.1f, 0.7f, 1, 0.35f);

        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int s_Color = Shader.PropertyToID("_Color");

        public Mesh mesh
        {
            get
            {
                if (m_Mesh)
                    return m_Mesh;

                var filter = GetComponent<MeshFilter>();
                return filter ? filter.sharedMesh : null;
            }
        }

        public Renderer sourceRenderer
        {
            get
            {
                if (m_SourceRenderer)
                    return m_SourceRenderer;
                return GetComponent<Renderer>();
            }
        }

        public int subMeshIndex => m_SubMeshIndex;
        public bool useFaceColors => m_UseFaceColors;
        public bool isRenderable => isActiveAndEnabled && gameObject.activeInHierarchy && mesh != null;

        public Color ResolveBaseColor()
        {
            Color color = m_BaseColor;
            Material mat = m_MaterialOverride;
            if (!mat)
            {
                var srcRenderer = sourceRenderer;
                if (srcRenderer)
                    mat = srcRenderer.sharedMaterial;
            }

            if (m_ReadBaseColorFromMaterial && mat)
            {
                if (mat.HasProperty(s_BaseColor))
                    color = mat.GetColor(s_BaseColor);
                else if (mat.HasProperty(s_Color))
                    color = mat.GetColor(s_Color);
            }

            color.a *= m_Opacity;
            return color;
        }

        public Color ResolveOutsideColor()
        {
            Color color = m_OutsideColor;
            color.a *= m_Opacity;
            return color;
        }

        public Color ResolveInsideColor()
        {
            Color color = m_InsideColor;
            color.a *= m_Opacity;
            return color;
        }

        public void SetMesh(Mesh mesh)
        {
            m_Mesh = mesh;

            var filter = GetComponent<MeshFilter>();
            if (filter && filter.sharedMesh != mesh)
                filter.sharedMesh = mesh;
        }

        void OnEnable()
        {
            GaussianSplatUnifiedPrimitiveRenderSystem.instance.RegisterMesh(this);
        }

        void OnDisable()
        {
            GaussianSplatUnifiedPrimitiveRenderSystem.instance.UnregisterMesh(this);
        }

        void Reset()
        {
            m_SourceRenderer = GetComponent<Renderer>();
            var filter = GetComponent<MeshFilter>();
            if (filter)
                m_Mesh = filter.sharedMesh;
        }

        void OnValidate()
        {
            if (!m_SourceRenderer)
                m_SourceRenderer = GetComponent<Renderer>();
            if (!m_Mesh)
            {
                var filter = GetComponent<MeshFilter>();
                if (filter)
                    m_Mesh = filter.sharedMesh;
            }
        }
    }
}

#endif
