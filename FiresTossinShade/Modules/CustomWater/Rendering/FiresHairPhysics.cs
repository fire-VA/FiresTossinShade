using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Gives the LOCAL player's HAIR and BEARD physics. Vanilla hair/beard attach via attach_skin, so
    /// each is a SkinnedMeshRenderer skinned to the body. We re-skin a copy to a short bone chain that
    /// hangs from the attach joint (m_helmet) and runs a verlet solver on it each LateUpdate.
    ///
    /// The chain is anchored EXPLICITLY at the scalp/jaw joint and the vertices are weighted by how far
    /// they sit ALONG the hang axis (joint -> mesh centroid) - we do NOT assume the mesh's local +Y is
    /// up (attach_skin meshes are authored in body-bind space, which is why an earlier height-based
    /// version anchored at the shoulders and floated). The solver pulls each bone toward its rigid rest
    /// pose, applies gravity + wind, and keeps the strands off a head sphere AND a torso capsule so they
    /// drape over the body instead of clipping through it.
    ///
    /// Off by default; live tunables. `va_hair off` restores the vanilla hair/beard instantly.
    /// </summary>
    public static class FiresHairPhysics
    {
        public static bool Enabled = false;

        // Live tuning (config).
        public static float Stiffness   = 6f;     // pull back toward the rigid rest pose (higher = stiffer)
        public static float Damping     = 0.92f;  // velocity retention (inertia); lower = more drag
        public static float Gravity     = 2.5f;   // downward pull (m/s^2-ish, scaled)
        public static float WindInfluence = 0.4f; // how much world wind pushes the strands
        public static float MaxSway     = 0.30f;  // max deviation of any bone from its rest (m) - safety
        public static float CollisionRadius = 0.11f; // head sphere the strands can't enter
        public static float BodyRadius  = 0.15f;  // torso capsule radius (0 = body collision off)
        public static float BodyTopHeight = 1.5f; // capsule top (shoulders), metres up the player root
        public static float BodyBottomHeight = 0.6f; // capsule bottom (hips)
        // Head-local offset (metres) nudging the whole strand's root/rest so we can place it on the head
        // by eye if the auto scalp anchor is off. Applied live in the solver; default 0 = auto.
        public static Vector3 HairAnchorOffset  = Vector3.zero;
        public static Vector3 BeardAnchorOffset = Vector3.zero;
        // Distribution of motion along the strand: 0 = uniform (whole strand sways equally); higher =
        // the part near the root stays anchored and sway is pushed toward the tips. Live in the solver.
        public static float SwayFalloff = 1.5f;
        public static float ShoulderRadius = 0.12f; // radius of the two shoulder collision spheres (0 = off)
        public static float ShoulderWidth  = 0.16f; // half-distance from the spine to each shoulder sphere
        public const int Segments = 5;            // bones = Segments + 1

        private static readonly HashSet<GameObject> s_done = new HashSet<GameObject>();

        public static int ActiveCount { get { s_done.RemoveWhere(g => g == null); return s_done.Count; } }

        public static void ApplyToLocalPlayer()
        {
            if (!Enabled) return;
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();  // local OR character-select preview
            if (p == null) return;
            var ve = p.GetComponent<VisEquipment>();
            if (ve == null) return;
            Transform scalp = ve.m_helmet != null ? ve.m_helmet : p.transform;

            TryReskin(GetInstanceField(ve, "m_hairItemInstance"),  scalp, p.transform, "hair");
            TryReskin(GetInstanceField(ve, "m_beardItemInstance"), scalp, p.transform, "beard");
        }

        private static void TryReskin(GameObject inst, Transform scalp, Transform body, string label)
        {
            if (inst == null) return;            // not equipped
            if (s_done.Contains(inst)) return;

            // attach_skin -> SkinnedMeshRenderer; some items may be a rigid MeshRenderer. Handle both.
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
            var mr  = inst.GetComponentInChildren<MeshRenderer>();
            Mesh srcMesh = null; GameObject go = null; Material mat = null;
            if (smr != null && smr.sharedMesh != null) { srcMesh = smr.sharedMesh; go = smr.gameObject; mat = smr.sharedMaterial; }
            else if (mr != null) { var mf = mr.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) { srcMesh = mf.sharedMesh; go = mr.gameObject; mat = mr.sharedMaterial; } }

            if (srcMesh == null || go == null)
            {
                Debug.LogWarning($"[FiresHair] {label} '{inst.name}' has no usable mesh (smr={(smr != null)}, mr={(mr != null)}); skipping.");
                s_done.Add(inst);
                return;
            }

            try
            {
                Setup(go, srcMesh, mat, smr, scalp, body, label);
                s_done.Add(inst);
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresHair] {label} setup failed: {ex}"); s_done.Add(inst); }
        }

        // Read-only state report so we can see exactly what's attached without mutating anything.
        public static string Diagnose()
        {
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p == null) return "no local/preview player";
            var ve = p.GetComponent<VisEquipment>();
            if (ve == null) return "no VisEquipment";
            return "hair " + Describe(GetInstanceField(ve, "m_hairItemInstance")) +
                   " | beard " + Describe(GetInstanceField(ve, "m_beardItemInstance")) +
                   $" | enabled={Enabled} active={ActiveCount}";
        }

        private static string Describe(GameObject inst)
        {
            if (inst == null) return "none";
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
            var mr  = inst.GetComponentInChildren<MeshRenderer>();
            int sv = (smr != null && smr.sharedMesh != null) ? smr.sharedMesh.vertexCount : -1;
            int mv = -1;
            if (mr != null) { var mf = mr.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) mv = mf.sharedMesh.vertexCount; }
            bool solved = inst.GetComponentInChildren<HairChainSolver>() != null;
            return $"'{inst.name}' smr={(smr != null)}(v{sv}) mr={(mr != null)}(v{mv}) solver={solved} done={s_done.Contains(inst)}";
        }

        // VisEquipment.m_hairItemInstance / m_beardItemInstance are private; pull reflectively (read-only).
        private static GameObject GetInstanceField(VisEquipment ve, string field)
        {
            var f = typeof(VisEquipment).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f != null ? f.GetValue(ve) as GameObject : null;
        }

        private static void Setup(GameObject go, Mesh src, Material mat, SkinnedMeshRenderer existingSmr,
                                  Transform scalp, Transform body, string label)
        {
            // Capture each vertex's CURRENT WORLD position + normal. Unity skins via bones+bindposes and
            // IGNORES the renderer transform, so we run the skinning ourselves (manual LBS) from the
            // ORIGINAL bones+bindposes. This is scale-safe: mapping mesh verts through
            // go.transform.localToWorldMatrix double-applies the head bone's huge lossyScale and blows the
            // hair up ~100x. Rigid hair has no bones, so it's just the transform.
            Vector3[] W, Nr;
            int[] tris;
            Vector2[] uv;
            if (existingSmr != null && existingSmr.sharedMesh != null)
            {
                var sm = existingSmr.sharedMesh;
                var weights = sm.boneWeights;
                bool lbsOk = weights != null && weights.Length == sm.vertexCount
                             && existingSmr.bones != null && existingSmr.bones.Length > 0;
                // LBS needs a legacy boneWeights array (scale-safe, used for the hair). If the mesh stores
                // weights in the newer format the legacy array is empty -> fall back to BakeMesh (works
                // when the renderer isn't scaled, which is the beard's case).
                if (lbsOk) CaptureSkinned(existingSmr, out W, out Nr, out tris, out uv);
                else       CaptureBaked(existingSmr, out W, out Nr, out tris, out uv);
            }
            else
                CaptureRigid(src, go.transform, out W, out Nr, out tris, out uv);

            if (W == null || W.Length == 0)
                throw new Exception($"{label} mesh '{src.name}' is not readable (0 verts) - cannot reskin");

            // Anchor EXPLICITLY at the joint (m_helmet); the hang axis is joint -> mesh centroid; each
            // vertex is weighted by its distance ALONG that axis (0 = joint = rigid, 1 = tips = full sway).
            Vector3 anchorW = scalp.position;

            // Per-axis SIZE from FiresHairBeardScale, applied to the captured verts around the joint in
            // head-local axes (X=side, Y=up, Z=forward). A skinned mesh ignores transform.localScale, so
            // baking the scale into the verts here is the only way to resize it. Changing the slider
            // re-reskins (FiresHairPhysics.ForceRebuild) so this re-runs with the new factor.
            Vector3 sc = (label == "beard")
                ? (FiresHairBeardScale.BeardEnabled ? FiresHairBeardScale.BeardScale : Vector3.one)
                : (FiresHairBeardScale.HairEnabled  ? FiresHairBeardScale.HairScale  : Vector3.one);
            if (sc != Vector3.one)
            {
                Quaternion invRot = Quaternion.Inverse(scalp.rotation);
                for (int j = 0; j < W.Length; j++)
                    W[j] = anchorW + scalp.rotation * Vector3.Scale(invRot * (W[j] - anchorW), sc);
            }

            Vector3 centroidW = Vector3.zero;
            for (int i = 0; i < W.Length; i++) centroidW += W[i];
            centroidW /= W.Length;

            Vector3 axis = centroidW - anchorW;
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.down;
            axis.Normalize();

            float L = 1e-3f;
            for (int i = 0; i < W.Length; i++)
            {
                float proj = Vector3.Dot(W[i] - anchorW, axis);
                if (proj > L) L = proj;
            }
            int N = Segments;
            float segLenWorld = L / N;

            // Chain root rides the scalp joint; bones march from the joint along the hang axis.
            var root = new GameObject("FiresHairRoot").transform;
            root.SetParent(scalp, true);
            root.position = anchorW;
            root.rotation = scalp.rotation;

            var bones = new Transform[N + 1];
            for (int i = 0; i <= N; i++)
            {
                var b = new GameObject("hbone" + i).transform;
                b.SetParent(root, true);
                b.position = anchorW + axis * (segLenWorld * i);
                b.rotation = root.rotation;
                bones[i] = b;
            }

            // Verts are WORLD coords (LBS output), so the bind pose is just each bone's world->local.
            var bind = new Matrix4x4[N + 1];
            for (int i = 0; i <= N; i++) bind[i] = bones[i].worldToLocalMatrix;

            var bw = new BoneWeight[W.Length];
            for (int j = 0; j < W.Length; j++)
            {
                float proj = Vector3.Dot(W[j] - anchorW, axis);
                float t = Mathf.Clamp01(proj / L);          // 0 at the joint .. 1 at the tips
                float f = t * N;
                int i0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, N);
                int i1 = Mathf.Min(i0 + 1, N);
                float w = f - i0;
                bw[j].boneIndex0 = i0; bw[j].weight0 = 1f - w;
                bw[j].boneIndex1 = i1; bw[j].weight1 = w;
            }

            var mesh = new Mesh { name = src.name + "_FiresHair" };
            if (W.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = W;
            if (tris != null) mesh.triangles = tris;
            if (uv != null && uv.Length == W.Length) mesh.uv = uv;
            if (Nr != null && Nr.Length == W.Length) mesh.normals = Nr; else mesh.RecalculateNormals();
            mesh.boneWeights = bw;
            mesh.bindposes = bind;

            // Reuse the existing SkinnedMeshRenderer (attach_skin) in place; else add one + strip the
            // rigid MeshRenderer/MeshFilter so we don't double-draw.
            SkinnedMeshRenderer smr = existingSmr;
            if (smr == null)
            {
                smr = go.AddComponent<SkinnedMeshRenderer>();
                var oldMr = go.GetComponent<MeshRenderer>();
                var oldMf = go.GetComponent<MeshFilter>();
                if (oldMr != null) UnityEngine.Object.Destroy(oldMr);
                if (oldMf != null) UnityEngine.Object.Destroy(oldMf);
            }
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[0];
            if (mat != null) smr.sharedMaterial = mat;
            smr.localBounds = new Bounds(Vector3.zero, Vector3.one * (L * 2f + 0.5f));
            smr.updateWhenOffscreen = true;

            var solver = go.GetComponent<HairChainSolver>();
            if (solver == null) solver = go.AddComponent<HairChainSolver>();
            solver.Init(scalp, body, bones, root, segLenWorld, label == "beard");

            Debug.Log($"[FiresHair] {label} reskinned: {W.Length} verts, len={L:F2}m, " +
                      $"anchor=({anchorW.x:F1},{anchorW.y:F1},{anchorW.z:F1}) axis=({axis.x:F2},{axis.y:F2},{axis.z:F2}).");
        }

        // Run the original linear-blend skinning by hand to get each vertex's CURRENT world position +
        // normal (scale-safe; the renderer transform is irrelevant for skinned meshes).
        private static void CaptureSkinned(SkinnedMeshRenderer smr, out Vector3[] W, out Vector3[] Nr, out int[] tris, out Vector2[] uv)
        {
            var m = smr.sharedMesh;
            var verts = m.vertices;
            var norms = m.normals;
            var bw = m.boneWeights;
            var bp = m.bindposes;
            var bones = smr.bones;
            int n = verts.Length;
            W = new Vector3[n];
            Nr = new Vector3[n];
            bool hasN = norms != null && norms.Length == n;

            var skin = new Matrix4x4[bones.Length];                 // bone.localToWorld * bindpose
            for (int b = 0; b < bones.Length; b++)
            {
                Matrix4x4 l2w = bones[b] != null ? bones[b].localToWorldMatrix : Matrix4x4.identity;
                Matrix4x4 pose = b < bp.Length ? bp[b] : Matrix4x4.identity;
                skin[b] = l2w * pose;
            }

            for (int j = 0; j < n; j++)
            {
                BoneWeight w = bw[j];
                Vector3 v = verts[j];
                Vector3 nv = hasN ? norms[j] : Vector3.up;
                Vector3 p = Vector3.zero, nn = Vector3.zero;
                Accum(skin, w.boneIndex0, w.weight0, v, nv, hasN, ref p, ref nn);
                Accum(skin, w.boneIndex1, w.weight1, v, nv, hasN, ref p, ref nn);
                Accum(skin, w.boneIndex2, w.weight2, v, nv, hasN, ref p, ref nn);
                Accum(skin, w.boneIndex3, w.weight3, v, nv, hasN, ref p, ref nn);
                W[j] = p;
                Nr[j] = hasN && nn.sqrMagnitude > 1e-12f ? nn.normalized : Vector3.up;
            }
            tris = m.triangles;
            uv = m.uv;
        }

        private static void Accum(Matrix4x4[] skin, int idx, float wt, Vector3 v, Vector3 nv, bool hasN, ref Vector3 p, ref Vector3 nn)
        {
            if (wt <= 0f || idx < 0 || idx >= skin.Length) return;
            p += wt * skin[idx].MultiplyPoint3x4(v);
            if (hasN) nn += wt * skin[idx].MultiplyVector(nv);
        }

        private static void CaptureRigid(Mesh src, Transform t, out Vector3[] W, out Vector3[] Nr, out int[] tris, out Vector2[] uv)
        {
            var verts = src.vertices;
            var norms = src.normals;
            int n = verts.Length;
            W = new Vector3[n];
            Nr = new Vector3[n];
            Matrix4x4 l2w = t.localToWorldMatrix;
            bool hasN = norms != null && norms.Length == n;
            for (int j = 0; j < n; j++)
            {
                W[j] = l2w.MultiplyPoint3x4(verts[j]);
                Nr[j] = hasN ? l2w.MultiplyVector(norms[j]).normalized : Vector3.up;
            }
            tris = src.triangles;
            uv = src.uv;
        }

        // Fallback when the mesh lacks a legacy boneWeights array for LBS: BakeMesh snapshots the deformed
        // pose, then the renderer transform maps it to world. Correct when the renderer isn't scaled.
        private static void CaptureBaked(SkinnedMeshRenderer smr, out Vector3[] W, out Vector3[] Nr, out int[] tris, out Vector2[] uv)
        {
            var baked = new Mesh();
            smr.BakeMesh(baked);
            var verts = baked.vertices;
            var norms = baked.normals;
            int n = verts.Length;
            W = new Vector3[n];
            Nr = new Vector3[n];
            Matrix4x4 l2w = smr.transform.localToWorldMatrix;
            bool hasN = norms != null && norms.Length == n;
            for (int j = 0; j < n; j++)
            {
                W[j] = l2w.MultiplyPoint3x4(verts[j]);
                Nr[j] = hasN ? l2w.MultiplyVector(norms[j]).normalized : Vector3.up;
            }
            tris = baked.triangles;
            uv = baked.uv;
            UnityEngine.Object.Destroy(baked);
        }

        public static void Remove()
        {
            // Destroying the swapped instances makes VisEquipment rebuild the vanilla hair/beard on its
            // next refresh, so "off" cleanly restores the originals.
            foreach (var go in s_done) if (go != null) UnityEngine.Object.Destroy(go);
            s_done.Clear();
        }

        // Force VisEquipment to re-attach the vanilla hair+beard (resets its cached item hashes so the next
        // UpdateVisuals rebuilds them). The physics driver then re-reskins the fresh instances, picking up a
        // changed scale. Used when the size sliders change.
        public static void ForceRebuild()
        {
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p == null) return;
            var ve = p.GetComponent<VisEquipment>();
            if (ve == null) return;
            SetPrivateInt(ve, "m_currentHairItemHash", -1);
            SetPrivateInt(ve, "m_currentBeardItemHash", -1);
            s_done.Clear();   // rebuilt instances are new GameObjects -> they re-reskin on the next tick
        }

        private static void SetPrivateInt(object obj, string field, int value)
        {
            var f = obj.GetType().GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            f?.SetValue(obj, value);
        }

        public static Vector3 WindVector()
        {
            var env = EnvMan.instance;
            if (env == null) return Vector3.zero;
            try { return env.GetWindForce() * WindInfluence; } catch { }
            return Vector3.zero;
        }
    }

    /// <summary>Verlet chain solver that drives one re-skinned hair/beard strand chain.</summary>
    public class HairChainSolver : MonoBehaviour
    {
        private Transform _scalp, _body, _root;
        private Transform[] _bones;
        private Vector3[] _pos, _prev, _restLocal, _restW;
        private float[] _maxI;
        private float _segLen;
        private bool _init, _isBeard;

        public void Init(Transform scalp, Transform body, Transform[] bones, Transform root, float segLen, bool isBeard)
        {
            _scalp = scalp; _body = body; _bones = bones; _root = root; _segLen = segLen; _isBeard = isBeard;
            int n = bones.Length;
            _pos = new Vector3[n]; _prev = new Vector3[n]; _restLocal = new Vector3[n]; _restW = new Vector3[n]; _maxI = new float[n];
            for (int i = 0; i < n; i++) { _pos[i] = bones[i].position; _prev[i] = _pos[i]; _restLocal[i] = bones[i].localPosition; }
            _init = true;
        }

        private void LateUpdate()
        {
            if (!_init || _root == null || _scalp == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;

            int n = _bones.Length;
            // Head-local manual nudge (lets us place the strand by eye if the auto anchor is off).
            Vector3 wOff = _scalp.rotation * (_isBeard ? FiresHairPhysics.BeardAnchorOffset : FiresHairPhysics.HairAnchorOffset);
            for (int i = 0; i < n; i++) _restW[i] = _root.TransformPoint(_restLocal[i]) + wOff; // rest pose follows the head

            // anchor: top bone rigidly follows the joint
            _pos[0] = _restW[0];
            _prev[0] = _pos[0];
            _bones[0].position = _pos[0];

            Vector3 accel = Vector3.down * FiresHairPhysics.Gravity + FiresHairPhysics.WindVector();

            for (int i = 1; i < n; i++)
            {
                Vector3 cur = _pos[i];
                Vector3 vel = (cur - _prev[i]) * FiresHairPhysics.Damping;
                Vector3 next = cur + vel + accel * (dt * dt);
                next = Vector3.Lerp(next, _restW[i], Mathf.Clamp01(FiresHairPhysics.Stiffness * dt)); // spring back
                _prev[i] = cur;
                _pos[i] = next;
            }

            // Collider geometry: head sphere (centred a radius below the scalp so the root stays put) +
            // a torso capsule along the player's spine, so the strands drape over the body.
            Vector3 head = _scalp.position - Vector3.up * FiresHairPhysics.CollisionRadius;
            float hr = FiresHairPhysics.CollisionRadius;
            float br = FiresHairPhysics.BodyRadius;
            bool bodyOn = br > 0.001f && _body != null;
            Vector3 capTop = Vector3.zero, capBot = Vector3.zero;
            if (bodyOn)
            {
                capTop = _body.TransformPoint(new Vector3(0f, FiresHairPhysics.BodyTopHeight, 0f));
                capBot = _body.TransformPoint(new Vector3(0f, FiresHairPhysics.BodyBottomHeight, 0f));
            }

            // Two shoulder spheres (left/right of the neck) so strands ride OVER the shoulders/upper chest
            // instead of sinking in when the head pitches up/down - the spine capsule alone can't reach the
            // shoulders. Derived from the body transform (BodyTopHeight = shoulder height).
            float shr = FiresHairPhysics.ShoulderRadius;
            bool shouldersOn = shr > 0.001f && _body != null;
            Vector3 shL = Vector3.zero, shR = Vector3.zero;
            if (shouldersOn)
            {
                float sw = FiresHairPhysics.ShoulderWidth;
                shL = _body.TransformPoint(new Vector3(-sw, FiresHairPhysics.BodyTopHeight, 0f));
                shR = _body.TransformPoint(new Vector3( sw, FiresHairPhysics.BodyTopHeight, 0f));
            }
            // Per-bone sway budget: ~0 near the root, full at the tips, shaped by SwayFalloff. This is
            // what keeps the strand anchored close to the head while the ends swing freely.
            float falloff = FiresHairPhysics.SwayFalloff;
            for (int i = 0; i < n; i++)
            {
                float pp = (n > 1) ? (float)i / (n - 1) : 1f;
                _maxI[i] = FiresHairPhysics.MaxSway * Mathf.Pow(pp, falloff);
            }

            for (int iter = 0; iter < 6; iter++)
            {
                // segment length
                for (int i = 1; i < n; i++)
                {
                    Vector3 d = _pos[i] - _pos[i - 1];
                    float len = d.magnitude;
                    if (len > 1e-5f) _pos[i] -= d * (1f - _segLen / len);
                }
                // safety sway clamp (collision below gets the final word so it can't push back in)
                for (int i = 1; i < n; i++)
                {
                    Vector3 dev = _pos[i] - _restW[i];
                    float m = dev.magnitude;
                    if (m > _maxI[i] && m > 1e-5f) _pos[i] = _restW[i] + dev * (_maxI[i] / m);
                }
                // collisions
                for (int i = 1; i < n; i++)
                {
                    Vector3 dh = _pos[i] - head;
                    float mh = dh.magnitude;
                    if (mh < hr && mh > 1e-5f) _pos[i] = head + dh * (hr / mh);

                    if (bodyOn)
                    {
                        Vector3 c = ClosestOnSegment(_pos[i], capBot, capTop);
                        Vector3 db = _pos[i] - c;
                        float mb = db.magnitude;
                        if (mb < br && mb > 1e-5f) _pos[i] = c + db * (br / mb);
                    }

                    if (shouldersOn)
                    {
                        Vector3 dl = _pos[i] - shL; float ml = dl.magnitude;
                        if (ml < shr && ml > 1e-5f) _pos[i] = shL + dl * (shr / ml);
                        Vector3 dr = _pos[i] - shR; float mr = dr.magnitude;
                        if (mr < shr && mr > 1e-5f) _pos[i] = shR + dr * (shr / mr);
                    }
                }
            }

            for (int i = 1; i < n; i++) _bones[i].position = _pos[i];
        }

        private static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(Vector3.Dot(ab, ab), 1e-6f);
            return a + ab * Mathf.Clamp01(t);
        }
    }

    /// <summary>Ensures the local hair/beard are physics-ified; registers <c>va_hair</c>.</summary>
    public class FiresHairDriver : MonoBehaviour
    {
        private bool _cmd;
        private float _next;

        private void Update()
        {
            if (!_cmd) { try { HairCommand.Register(); _cmd = true; } catch { } }
            if (!FiresHairPhysics.Enabled) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            FiresHairPhysics.ApplyToLocalPlayer();
        }
    }

    /// <summary><c>va_hair on|off|status|diag</c>.</summary>
    public static class HairCommand
    {
        private static bool s_reg;
        public static void Register()
        {
            if (s_reg) return;
            new Terminal.ConsoleCommand("va_hair",
                "Toggle physics hair + beard on the local player. Usage: va_hair on|off|status|diag",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off") { FiresHairPhysics.Enabled = false; FiresHairPhysics.Remove(); args.Context.AddString("[FiresHair] off (hair/beard will rebuild vanilla)."); }
                    else if (a == "status") { args.Context.AddString($"[FiresHair] enabled={FiresHairPhysics.Enabled}, active={FiresHairPhysics.ActiveCount}."); }
                    else if (a == "diag") { args.Context.AddString("[FiresHair] " + FiresHairPhysics.Diagnose()); }
                    else { FiresHairPhysics.Enabled = true; FiresHairPhysics.ApplyToLocalPlayer(); args.Context.AddString($"[FiresHair] on: {FiresHairPhysics.Diagnose()}"); }
                }, isCheat: false);
            s_reg = true;
        }
    }
}
