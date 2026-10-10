using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Lives on a placed stained-glass window. On enable it works out the window's
    /// plane (centre / normal / right / up / half-extents) in world space and adds
    /// itself to a registry the FiresGlassProjector reads each frame, so every
    /// window that exists in the world casts its coloured light. Removed on disable.
    ///
    /// Window prefabs are built oriented so local X = width, Y = height, Z = the
    /// thin axis (the pane normal); this component reads that from the transform.
    /// For the mesh-less `va_glass here` test window, set Manual + the plane fields
    /// before enabling and it uses them as-is.
    /// </summary>
    public class FiresGlassWindow : MonoBehaviour
    {
        public static readonly List<FiresGlassWindow> Registry = new List<FiresGlassWindow>();

        public Color Tint = Color.white;
        public int ImageIndex = 0;    // slice in the projection-image array this window casts
        public float SunLit = 1f;     // 0 = the sun is blocked from this window (roofed) -> no cast; 1 = full sun
        public bool Neon = false;     // self-lit sign: beam out the front (tilted down), no sun gates, glows at night
        public bool Manual = false;   // if set, Center/Normal/Right/Up/Half are provided directly

        public Vector3 Center, Normal = Vector3.forward, Right = Vector3.right, Up = Vector3.up;
        public Vector2 Half = new Vector2(1f, 1.5f);

        private void OnEnable()
        {
            if (!Manual) Recompute();
            if (!Registry.Contains(this)) Registry.Add(this);
        }

        private void OnDisable() { Registry.Remove(this); }

        public void Recompute()
        {
            Bounds lb = RootLocalBounds();
            var t = transform;
            Center = t.TransformPoint(lb.center);
            Right = t.right;
            Up = t.up;
            Normal = t.forward;
            Vector3 ls = t.lossyScale;
            Half = new Vector2(lb.size.x * 0.5f * Mathf.Abs(ls.x), lb.size.y * 0.5f * Mathf.Abs(ls.y));
        }

        private Bounds RootLocalBounds()
        {
            var filters = GetComponentsInChildren<MeshFilter>();
            Matrix4x4 w2l = transform.worldToLocalMatrix;
            bool has = false;
            Bounds b = new Bounds();
            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                Matrix4x4 m = w2l * f.transform.localToWorldMatrix;
                Bounds mb = f.sharedMesh.bounds;
                Vector3 c = mb.center, e = mb.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 p = m.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                            if (!has) { b = new Bounds(p, Vector3.zero); has = true; }
                            else b.Encapsulate(p);
                        }
            }
            if (!has) b = new Bounds(Vector3.zero, new Vector3(2f, 3f, 0.1f));
            return b;
        }
    }
}
