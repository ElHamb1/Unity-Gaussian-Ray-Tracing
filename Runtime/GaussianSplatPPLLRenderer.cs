// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [AddComponentMenu("Gaussian Splatting/Gaussian Splat Renderer PPLL")]
    public class GaussianSplatPPLLRenderer : GaussianSplatRenderer
    {
        void Awake()
        {
            AssignDefaultShaders();
        }

        public new void OnEnable()
        {
            AssignDefaultShaders();
            base.OnEnable();
        }

        public new void OnDisable()
        {
            base.OnDisable();
        }

        void Reset()
        {
            AssignDefaultShaders();
        }

        void OnValidate()
        {
            AssignDefaultShaders();
        }

        void AssignDefaultShaders()
        {
            CopyMissingReferencesFromSiblingRenderer();

            if (m_ShaderSplats == null || m_ShaderSplats.name == "Gaussian Splatting/Render Splats")
                m_ShaderSplats = Shader.Find("Gaussian Splatting/PPLL Render Splats");
            if (m_ShaderComposite == null || m_ShaderComposite.name == "Hidden/Gaussian Splatting/Composite")
                m_ShaderComposite = Shader.Find("Hidden/Gaussian Splatting/PPLL Composite");
            m_ShaderDebugPoints ??= Shader.Find("Gaussian Splatting/Debug/Render Points");
            m_ShaderDebugBoxes ??= Shader.Find("Gaussian Splatting/Debug/Render Boxes");
        }

        void CopyMissingReferencesFromSiblingRenderer()
        {
            foreach (var renderer in GetComponents<GaussianSplatRenderer>())
            {
                if (renderer == this)
                    continue;

                m_Asset ??= renderer.m_Asset;
                m_SemanticLabels ??= renderer.m_SemanticLabels;
                m_ShaderDebugPoints ??= renderer.m_ShaderDebugPoints;
                m_ShaderDebugBoxes ??= renderer.m_ShaderDebugBoxes;
                m_CSSplatUtilities_deviceRadixSort ??= renderer.m_CSSplatUtilities_deviceRadixSort;
                m_CSSplatUtilities_fidelityFX ??= renderer.m_CSSplatUtilities_fidelityFX;
                break;
            }
        }
    }
}
#endif
