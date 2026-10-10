using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Adds a SECOND SunShafts post-process pass to the main camera,
    /// pointing at the aurora-light anchor transform instead of the
    /// sun/moon. Engine SunShafts is screen-space radial-blur from a
    /// point; running a second instance with a different sunTransform
    /// gives us aurora-colored god rays radiating from the aurora's
    /// screen position alongside the sun/moon's normal shafts.
    ///
    /// LIFECYCLE
    /// Vanilla SunShafts on the camera stays untouched (we don't disable
    /// it anymore — earlier rev did, that was wrong). We add a sibling
    /// SunShafts via AddComponent so both run in sequence on the same RT.
    /// The second instance's sunTransform = FiresAuroraLight.Anchor,
    /// sunColor = current aurora cycled color, sunShaftIntensity from
    /// config. Disabled when aurora isn't active so we don't pay the
    /// blur cost during day.
    ///
    /// All SunShafts type access is via reflection — assembly_sunshafts
    /// is a separate DLL we don't HintPath against.
    /// </summary>
    public static class FiresAuroraShaftsPass
    {
        public static bool Enabled = true;
        public static float Intensity = 1.0f;
        public static float BlurRadius = 2.5f;
        public static float MaxRadius = 0.75f;

        // Reflection plumbing
        private static Type s_sunShaftsType;
        private static FieldInfo s_sunTransform;
        private static FieldInfo s_sunColor;
        private static FieldInfo s_sunShaftIntensity;
        private static FieldInfo s_sunShaftBlurRadius;
        private static FieldInfo s_maxRadius;
        private static FieldInfo s_sunShaftsShader;
        private static FieldInfo s_simpleClearShader;
        private static bool s_reflectionAttempted;
        private static bool s_reflectionOk;

        private static Behaviour s_auroraShafts;
        private static Camera s_attachedCamera;

        public static void Tick(Camera cam)
        {
            if (cam == null) return;
            if (!EnsureReflection()) return;
            if (FiresAuroraLight.Anchor == null) return;

            // Don't try to attach until we're in-game. The menu camera doesn't
            // have a fully-configured vanilla SunShafts to copy shader refs
            // from — adding a fresh SunShafts there NREs on every render.
            if (ZNetScene.instance == null) return;

            // Find/create the second SunShafts on this camera. If vanilla
            // SunShafts isn't on this camera (yet), bail — we need it as a
            // template for shader refs.
            if (s_attachedCamera != cam || s_auroraShafts == null)
            {
                if (s_auroraShafts != null) UnityEngine.Object.Destroy(s_auroraShafts);
                s_auroraShafts = AttachSecondSunShafts(cam);
                s_attachedCamera = cam;
            }
            if (s_auroraShafts == null) return;

            bool wantOn = Enabled
                && Intensity > 0.001f
                && FiresAuroraLight.LightComp != null
                && FiresAuroraLight.LightComp.enabled;

            if (s_auroraShafts.enabled != wantOn) s_auroraShafts.enabled = wantOn;
            if (!wantOn) return;

            try
            {
                s_sunTransform.SetValue(s_auroraShafts, FiresAuroraLight.Anchor);
                s_sunColor.SetValue(s_auroraShafts, (Color)(FiresAuroraLight.LightComp.color * Intensity));
                s_sunShaftIntensity.SetValue(s_auroraShafts, Intensity);
                if (s_sunShaftBlurRadius != null) s_sunShaftBlurRadius.SetValue(s_auroraShafts, BlurRadius);
                if (s_maxRadius != null) s_maxRadius.SetValue(s_auroraShafts, MaxRadius);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresAuroraShaftsPass] SetValue failed: {ex.Message}");
                s_reflectionOk = false;
            }
        }

        public static void RestoreImmediate()
        {
            if (s_auroraShafts != null)
            {
                UnityEngine.Object.Destroy(s_auroraShafts);
                s_auroraShafts = null;
            }
            s_attachedCamera = null;
        }

        private static Behaviour AttachSecondSunShafts(Camera cam)
        {
            // Need vanilla SunShafts on the camera FIRST — that's our template
            // for shader references. Without valid shader refs, OnRenderImage
            // NREs on the very first render (Unity calls Awake/OnEnable
            // synchronously inside AddComponent, and OnRenderImage at end of
            // frame, BEFORE we'd otherwise mark our component enabled=false).
            var vanilla = cam.GetComponent(s_sunShaftsType) as Behaviour;
            if (vanilla == null) return null;

            // Verify vanilla has non-null shader refs before we try to clone
            // them. On the menu camera the SunShafts asset references can be
            // unloaded / missing.
            if (s_sunShaftsShader == null || s_simpleClearShader == null) return null;
            if (s_sunShaftsShader.GetValue(vanilla) == null) return null;
            if (s_simpleClearShader.GetValue(vanilla) == null) return null;

            Behaviour comp = null;
            try
            {
                comp = cam.gameObject.AddComponent(s_sunShaftsType) as Behaviour;
                if (comp == null) return null;
                // Disable IMMEDIATELY to keep Unity from invoking OnRenderImage
                // on this frame's render with un-copied (null) shader refs.
                comp.enabled = false;

                var fields = s_sunShaftsType.GetFields(BindingFlags.Instance | BindingFlags.Public);
                foreach (var f in fields)
                {
                    // sunTransform / sunColor / sunShaftIntensity are overridden
                    // per-frame in Tick; everything else (especially shader refs)
                    // we want vanilla's value for.
                    if (f.Name == "sunTransform" || f.Name == "sunColor" || f.Name == "sunShaftIntensity")
                        continue;
                    try { f.SetValue(comp, f.GetValue(vanilla)); }
                    catch { /* tolerate missing fields across Unity versions */ }
                }

                // Belt-and-suspenders: verify our shaders ended up non-null.
                if (s_sunShaftsShader.GetValue(comp) == null
                    || s_simpleClearShader.GetValue(comp) == null)
                {
                    UnityEngine.Object.Destroy(comp);
                    return null;
                }

                FiresLogger.LogVerbose("[FiresAuroraShaftsPass] Attached second SunShafts on main camera.");
                return comp;
            }
            catch (Exception ex)
            {
                if (comp != null) UnityEngine.Object.Destroy(comp);
                Debug.LogWarning($"[FiresAuroraShaftsPass] AddComponent failed: {ex.Message}");
                return null;
            }
        }

        private static bool EnsureReflection()
        {
            if (s_reflectionAttempted) return s_reflectionOk;
            s_reflectionAttempted = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    s_sunShaftsType = asm.GetType("UnityStandardAssets.ImageEffects.SunShafts", false);
                    if (s_sunShaftsType != null) break;
                }
                if (s_sunShaftsType == null)
                {
                    Debug.LogWarning("[FiresAuroraShaftsPass] SunShafts type not found — aurora shafts disabled.");
                    return false;
                }

                var flags = BindingFlags.Instance | BindingFlags.Public;
                s_sunTransform = s_sunShaftsType.GetField("sunTransform", flags);
                s_sunColor = s_sunShaftsType.GetField("sunColor", flags);
                s_sunShaftIntensity = s_sunShaftsType.GetField("sunShaftIntensity", flags);
                s_sunShaftBlurRadius = s_sunShaftsType.GetField("sunShaftBlurRadius", flags);
                s_maxRadius = s_sunShaftsType.GetField("maxRadius", flags);
                s_sunShaftsShader = s_sunShaftsType.GetField("sunShaftsShader", flags);
                s_simpleClearShader = s_sunShaftsType.GetField("simpleClearShader", flags);

                if (s_sunTransform == null || s_sunColor == null || s_sunShaftIntensity == null)
                {
                    Debug.LogWarning("[FiresAuroraShaftsPass] Required SunShafts fields not found.");
                    return false;
                }

                FiresLogger.LogVerbose("[FiresAuroraShaftsPass] Reflection ready; will add 2nd SunShafts pointing at aurora.");
                s_reflectionOk = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresAuroraShaftsPass] Reflection setup threw: {ex.Message}");
                return false;
            }
        }
    }

    public class FiresAuroraShaftsPassDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (cam != null) FiresAuroraShaftsPass.Tick(cam);
        }

        private void OnDisable() { FiresAuroraShaftsPass.RestoreImmediate(); }
    }
}
