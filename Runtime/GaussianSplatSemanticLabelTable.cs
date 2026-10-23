// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [CreateAssetMenu(fileName = "GaussianSemanticLabels", menuName = "Gaussian Splatting/Semantic Label Table")]
    public class GaussianSplatSemanticLabelTable : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public uint id;
            public string label;
        }

        [SerializeField] List<Entry> m_Entries = new();

        Dictionary<uint, string> m_Lookup;

        public IReadOnlyList<Entry> entries => m_Entries;

        void OnEnable()
        {
            RebuildLookup();
        }

        void OnValidate()
        {
            RebuildLookup();
        }

        public void SetEntries(IEnumerable<Entry> entries)
        {
            m_Entries = new List<Entry>(entries);
            RebuildLookup();
        }

        public string GetLabel(uint id)
        {
            RebuildLookup();
            if (id == 0)
                return "Undefined";
            return m_Lookup.TryGetValue(id, out string label) && !string.IsNullOrWhiteSpace(label)
                ? label
                : $"Semantic {id}";
        }

        public bool TryGetLabel(uint id, out string label)
        {
            RebuildLookup();
            if (id == 0)
            {
                label = "Undefined";
                return true;
            }

            return m_Lookup.TryGetValue(id, out label);
        }

        void RebuildLookup()
        {
            m_Lookup ??= new Dictionary<uint, string>();
            m_Lookup.Clear();
            foreach (Entry entry in m_Entries)
            {
                if (entry.id == 0 || string.IsNullOrWhiteSpace(entry.label))
                    continue;
                m_Lookup[entry.id] = entry.label;
            }
        }
    }
}