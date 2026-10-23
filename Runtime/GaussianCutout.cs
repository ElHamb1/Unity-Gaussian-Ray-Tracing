// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting.Runtime
{
    public class GaussianCutout : MonoBehaviour
    {
        public enum Type
        {
            Ellipsoid,
            Box
        }

        public Type m_Type = Type.Ellipsoid;
        public bool m_Invert = false;
        public bool[] layersToCut = Array.Empty<bool>();

        public struct ShaderData // match GaussianCutoutShaderData in CS
        {
            public Matrix4x4 matrix;
            public uint typeAndFlags;
            // Keep these as named scalar fields. Fixed-size arrays nested in a
            // StructuredBuffer element are not reflected reliably for Unity
            // 2022.3 ray-tracing material shaders.
            public int cutIndex0;
            public int cutIndex1;
            public int cutIndex2;
            public int cutIndex3;
            public int cutIndex4;
            public int cutIndex5;
            public int cutIndex6;
            public int cutIndex7;
        }
        
        public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix, IReadOnlyDictionary<int, int2> layerIndexRanges)
        {
            ShaderData sd = default;
            if (!(self && self.isActiveAndEnabled))
            {
                sd.typeAndFlags = ~0u;
                return sd;
            }

            sd.cutIndex0 = -1;
            sd.cutIndex1 = -1;
            sd.cutIndex2 = -1;
            sd.cutIndex3 = -1;
            sd.cutIndex4 = -1;
            sd.cutIndex5 = -1;
            sd.cutIndex6 = -1;
            sd.cutIndex7 = -1;
            
            var tr = self.transform;
            sd.matrix = tr.worldToLocalMatrix * rendererMatrix;
            sd.typeAndFlags = ((uint)self.m_Type) | (self.m_Invert ? 0x100u : 0u);
            
            // Doing this every frame is not very performant, should only run when properties change
            for (int layer = 0; layer < Math.Min(4, self.layersToCut.Length); layer++)
            {
                if (self.layersToCut[layer] && layerIndexRanges != null && layerIndexRanges.TryGetValue(layer, out int2 range))
                {
                    switch (layer)
                    {
                        case 0:
                            sd.cutIndex0 = range.x;
                            sd.cutIndex1 = range.y;
                            break;
                        case 1:
                            sd.cutIndex2 = range.x;
                            sd.cutIndex3 = range.y;
                            break;
                        case 2:
                            sd.cutIndex4 = range.x;
                            sd.cutIndex5 = range.y;
                            break;
                        case 3:
                            sd.cutIndex6 = range.x;
                            sd.cutIndex7 = range.y;
                            break;
                    }
                }
            }

            return sd;
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = Color.magenta;
            color.a = 0.2f;
            if (Selection.Contains(gameObject))
                color.a = 0.9f;
            else
            {
                // mid amount of alpha if a GS object that contains us as a cutout is selected
                var activeGo = Selection.activeGameObject;
                if (activeGo != null)
                {
                    var activeSplat = activeGo.GetComponent<GaussianSplatRenderer>();
                    if (activeSplat != null)
                    {
                        if (activeSplat.m_Cutouts != null && activeSplat.m_Cutouts.Contains(this))
                            color.a = 0.5f;
                    }
                }
            }

            Gizmos.color = color;
            if (m_Type == Type.Ellipsoid)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
            }
            if (m_Type == Type.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
            }
        }
#endif // #if UNITY_EDITOR
    }
}
