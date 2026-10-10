using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Shader.name builds a new string on every call, and the swap scans read it for every loaded material. Cached by
    // instance id: ids are never reused in a session, and an id does not keep the shader loaded.
    internal static class ShaderNames
    {
        private static readonly Dictionary<int, string> s_names = new Dictionary<int, string>();

        internal static bool Is(Shader shader, string name)
        {
            if (shader == null) return false;
            int id = shader.GetInstanceID();
            if (!s_names.TryGetValue(id, out string cached))
            {
                cached = shader.name;
                s_names[id] = cached;
            }
            return cached == name;
        }
    }
}
