// SPDX-License-Identifier: MIT
#if GS_ENABLE_HDRP

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_0_OR_NEWER
using RayTracingSubMeshFlags = UnityEngine.Rendering.RayTracingSubMeshFlags;
#else
using RayTracingSubMeshFlags = UnityEngine.Experimental.Rendering.RayTracingSubMeshFlags;
#endif

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Registers triangle submeshes that provide a "Gaussian3DGRT" ray pass
    /// for the HDRP 3DGRT backend. The standard material is transmissive or
    /// metallic; specialized ray-only visualization materials are also valid.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Gaussian Splatting/3DGRT Transmission Mesh")]
    public sealed class Gaussian3DGRTTransmission : MonoBehaviour
    {
        const string kShaderPassName = "Gaussian3DGRT";
        // Only the two flags the registry itself reads are named here. Every
        // other material property is hashed generically by
        // HashAllMaterialProperties, so shaders are free to add parameters.
        static readonly int s_ExcludeFrom3DGRT =
            Shader.PropertyToID("_ExcludeFrom3DGRT");
        static readonly int s_PrimaryRayOnly =
            Shader.PropertyToID("_3DGRTPrimaryRayOnly");
        static readonly int s_ShadowRayOnly =
            Shader.PropertyToID("_3DGRTShadowRayOnly");

        static readonly HashSet<Gaussian3DGRTTransmission> s_Active = new();
        static bool s_RegistryNeedsRefresh;

        readonly List<Material> m_SharedMaterials = new();
        MeshFilter m_MeshFilter;
        MeshRenderer m_Renderer;
        int m_GeometryRevision;

        internal Renderer targetRenderer => GetTargetRenderer();

        /// <summary>
        /// Call after changing vertices or indices in-place on the current
        /// shared mesh. Bounds/counts alone cannot detect every deformation.
        /// </summary>
        public void MarkGeometryDirty()
        {
            unchecked
            {
                ++m_GeometryRevision;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRegistry()
        {
            s_Active.Clear();
            // With scene/domain reload disabled, existing enabled components do
            // not receive another OnEnable after SubsystemRegistration.
            s_RegistryNeedsRefresh = true;
        }

        void OnEnable()
        {
            CacheComponents();
            s_Active.Add(this);
        }

        void OnDisable()
        {
            s_Active.Remove(this);
        }

        void OnDestroy()
        {
            s_Active.Remove(this);
        }

        void OnValidate()
        {
            CacheComponents();
        }

        void CacheComponents()
        {
            if (m_MeshFilter == null)
                m_MeshFilter = GetComponent<MeshFilter>();
            if (m_Renderer == null)
                m_Renderer = GetComponent<MeshRenderer>();
        }

        MeshRenderer GetTargetRenderer()
        {
            CacheComponents();
            return m_Renderer;
        }

        Mesh GetSharedMesh()
        {
            CacheComponents();
            return m_MeshFilter != null ? m_MeshFilter.sharedMesh : null;
        }

        void GetSharedMaterials()
        {
            m_SharedMaterials.Clear();
            GetTargetRenderer()?.GetSharedMaterials(m_SharedMaterials);
        }

        static bool IsMaterialIncluded(Material material)
        {
            return material != null &&
                   material.FindPass(kShaderPassName) >= 0 &&
                   (!material.HasProperty(s_ExcludeFrom3DGRT) ||
                    material.GetFloat(s_ExcludeFrom3DGRT) < 0.5f);
        }

        internal bool IsUsable(Camera camera)
        {
            MeshRenderer renderer = GetTargetRenderer();
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return false;
            if (!renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded)
                return false;
            if (camera != null && (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                return false;

            Mesh mesh = GetSharedMesh();
            if (mesh == null || mesh.subMeshCount == 0)
                return false;

            GetSharedMaterials();
            int count = Mathf.Min(mesh.subMeshCount, m_SharedMaterials.Count);
            for (int i = 0; i < count; ++i)
            {
                Material material = m_SharedMaterials[i];
                if (mesh.GetTopology(i) == MeshTopology.Triangles &&
                    IsMaterialIncluded(material))
                    return true;
            }
            return false;
        }

        internal RayTracingSubMeshFlags[] CreateSubMeshFlags()
        {
            Mesh mesh = GetSharedMesh();
            if (mesh == null)
                return System.Array.Empty<RayTracingSubMeshFlags>();

            GetSharedMaterials();
            var result = new RayTracingSubMeshFlags[mesh.subMeshCount];
            for (int i = 0; i < result.Length; ++i)
            {
                Material material =
                    i < m_SharedMaterials.Count ? m_SharedMaterials[i] : null;
                result[i] =
                    mesh.GetTopology(i) == MeshTopology.Triangles &&
                    IsMaterialIncluded(material)
                    ? RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly
                    : RayTracingSubMeshFlags.Disabled;
            }
            return result;
        }

        internal bool IsPrimaryRayOnly()
        {
            Mesh mesh = GetSharedMesh();
            if (mesh == null)
                return false;
            GetSharedMaterials();
            bool foundIncludedMaterial = false;
            int count = Mathf.Min(mesh.subMeshCount, m_SharedMaterials.Count);
            for (int i = 0; i < count; ++i)
            {
                Material material = m_SharedMaterials[i];
                if (!IsMaterialIncluded(material))
                    continue;
                foundIncludedMaterial = true;
                if (!material.HasProperty(s_PrimaryRayOnly) ||
                    material.GetFloat(s_PrimaryRayOnly) < 0.5f)
                    return false;
            }
            return foundIncludedMaterial;
        }

        /// <summary>
        /// True when every included material is a shadow-only occluder: seen
        /// by light-shadow rays and nothing else. Mirrors IsPrimaryRayOnly.
        /// </summary>
        internal bool IsShadowRayOnly()
        {
            Mesh mesh = GetSharedMesh();
            if (mesh == null)
                return false;
            GetSharedMaterials();
            bool foundIncludedMaterial = false;
            int count = Mathf.Min(mesh.subMeshCount, m_SharedMaterials.Count);
            for (int i = 0; i < count; ++i)
            {
                Material material = m_SharedMaterials[i];
                if (!IsMaterialIncluded(material))
                    continue;
                foundIncludedMaterial = true;
                if (!material.HasProperty(s_ShadowRayOnly) ||
                    material.GetFloat(s_ShadowRayOnly) < 0.5f)
                    return false;
            }
            return foundIncludedMaterial;
        }

        internal int ComputeStateHash()
        {
            unchecked
            {
                int hash = 17;
                Renderer renderer = GetTargetRenderer();
                Mesh mesh = GetSharedMesh();
                hash = hash * 31 + (renderer != null ? renderer.GetInstanceID() : 0);
                hash = hash * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                hash = hash * 31 + (renderer != null && renderer.enabled ? 1 : 0);
                hash = hash * 31 + gameObject.layer;
                hash = hash * 31 + m_GeometryRevision;
                if (mesh != null)
                {
                    hash = hash * 31 + mesh.vertexCount;
                    hash = hash * 31 + mesh.subMeshCount;
                    hash = hash * 31 + mesh.bounds.GetHashCode();
                    for (int i = 0; i < mesh.subMeshCount; ++i)
                    {
                        ulong indexCount = mesh.GetIndexCount(i);
                        hash = hash * 31 + (int)indexCount;
                        hash = hash * 31 + (int)(indexCount >> 32);
                        hash = hash * 31 + (int)mesh.GetTopology(i);
                    }
                }

                if (renderer == null)
                    return hash;

                GetSharedMaterials();
                hash = hash * 31 + m_SharedMaterials.Count;
                for (int i = 0; i < m_SharedMaterials.Count; ++i)
                {
                    Material material = m_SharedMaterials[i];
                    hash = hash * 31 + (material != null ? material.GetInstanceID() : 0);
                    hash = hash * 31 + (material != null && material.shader != null
                        ? material.shader.GetInstanceID()
                        : 0);
                    hash = hash * 31 +
                           (IsMaterialIncluded(material) ? 1 : 0);
                    hash = material != null
                        ? HashMaterialFloat(
                            hash,
                            material,
                            s_PrimaryRayOnly)
                        : hash * 31;
                    hash = material != null
                        ? HashMaterialFloat(
                            hash,
                            material,
                            s_ShadowRayOnly)
                        : hash * 31;
                }
                return hash;
            }
        }

        internal int ComputeVisualStateHash()
        {
            unchecked
            {
                int hash = ComputeStateHash();
                GetSharedMaterials();
                for (int i = 0; i < m_SharedMaterials.Count; ++i)
                {
                    Material material = m_SharedMaterials[i];
                    if (material == null)
                        continue;

                    // Hash every property the shader declares rather than a
                    // fixed list. Ray-only visualization materials such as
                    // VideoDepthFunnel3DGRT can then add blending parameters
                    // without their edits being dropped from the temporal
                    // history, which would leave stochastic accumulation
                    // showing the previous appearance.
                    hash = HashAllMaterialProperties(hash, material);
                }
                return hash;
            }
        }

        static int HashAllMaterialProperties(int hash, Material material)
        {
            Shader shader = material.shader;
            if (shader == null)
                return hash * 31;

            unchecked
            {
                int count = shader.GetPropertyCount();
                hash = hash * 31 + count;
                for (int i = 0; i < count; ++i)
                {
                    int nameId = shader.GetPropertyNameId(i);
                    switch (shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Color:
                            hash = HashMaterialColor(hash, material, nameId);
                            break;
                        case ShaderPropertyType.Vector:
                            hash = HashMaterialVector(hash, material, nameId);
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            hash = HashMaterialFloat(hash, material, nameId);
                            break;
                        case ShaderPropertyType.Texture:
                            hash = HashMaterialTexture(hash, material, nameId);
                            break;
                        case ShaderPropertyType.Int:
                            hash = material.HasProperty(nameId)
                                ? hash * 31 + material.GetInteger(nameId)
                                : hash * 31;
                            break;
                        default:
                            hash *= 31;
                            break;
                    }
                }
                return hash;
            }
        }

        static int HashMaterialFloat(int hash, Material material, int property)
        {
            unchecked
            {
                return material.HasProperty(property)
                    ? hash * 31 + material.GetFloat(property).GetHashCode()
                    : hash * 31;
            }
        }

        static int HashMaterialColor(int hash, Material material, int property)
        {
            unchecked
            {
                return material.HasProperty(property)
                    ? hash * 31 + material.GetColor(property).GetHashCode()
                    : hash * 31;
            }
        }

        static int HashMaterialVector(int hash, Material material, int property)
        {
            unchecked
            {
                return material.HasProperty(property)
                    ? hash * 31 + material.GetVector(property).GetHashCode()
                    : hash * 31;
            }
        }

        static int HashMaterialTexture(int hash, Material material, int property)
        {
            unchecked
            {
                if (!material.HasProperty(property))
                    return hash * 31;
                Texture texture = material.GetTexture(property);
                hash = hash * 31 + (texture != null ? texture.GetInstanceID() : 0);
                hash = hash * 31 + material.GetTextureScale(property).GetHashCode();
                hash = hash * 31 + material.GetTextureOffset(property).GetHashCode();
                return hash;
            }
        }

        internal static void Gather(Camera camera, List<Gaussian3DGRTTransmission> result)
        {
            if (s_RegistryNeedsRefresh)
            {
                foreach (Gaussian3DGRTTransmission transmission in
                         FindObjectsByType<Gaussian3DGRTTransmission>(
                             FindObjectsSortMode.InstanceID))
                {
                    if (transmission != null &&
                        transmission.isActiveAndEnabled)
                        s_Active.Add(transmission);
                }
                s_RegistryNeedsRefresh = false;
            }

            result.Clear();
            foreach (Gaussian3DGRTTransmission transmission in s_Active)
            {
                if (transmission != null && transmission.isActiveAndEnabled &&
                    transmission.IsUsable(camera))
                    result.Add(transmission);
            }
        }
    }
}

#endif
