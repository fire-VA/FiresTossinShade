# FiresGlass — Stained-Glass Light Projection (Design)

**Status:** design only, not started (2026-07-01). Part of FiresTossinShade (FTS).

## Goal
Real stained-glass behavior. When the sun is on the far side of a stained-glass
window, the window's **colored image is cast as colored light** onto whatever is
between the window and the ground — floor, walls, characters — plus **tinted
godray shafts** through the air, all aligned to the **live sun direction / time
of day**. Modeled directly on the FTS water-caustics projection.

The downloaded Amplify glass shaders (`Glass_Gen_2/Metallic/Specular`) are
GrabPass refraction glass — useful reference for the *pane* look, but the heart
of this feature is the **projection**, which is our caustics tech, not theirs.

## Reference: how caustics project (`FiresCausticsPass.cs`)
- Fullscreen command-buffer blit at `CameraEvent.AfterForwardAlpha`.
- Reconstructs each pixel's **world position from `_CameraDepthTexture`** using
  camera frustum-corner rays (`_FiresRayBL/TL/TR/BR`, pushed at `OnCamPreRender`):
  `worldPos = camPos + bilerp(rays) * linearDepth`. Purely geometric — the field
  lands on the same world floor regardless of view direction.
- Projects a field from a **plane** (live water surface) onto the floor,
  **radius-gated** around the player, **depth-faded**, **soft-edged**.
- Globals drive it; one cheap blit/frame; **torn down** (zero cost) when the
  player is far above the water (`AltitudeCutoff`).

Stained glass reuses the **depth→world reconstruction verbatim**, but:
- the "plane" is each **window**, not one global water surface;
- the projection is oriented along the **sun direction** (not straight down);
- it is **localized** to a beam in front of each window (a deferred-decal box),
  gated by whether the window actually faces the sun.

## Architecture

### 1. Window pane (`Custom/FiresGlassPane` + buildable piece)
- Buildable stained-glass window piece(s); image = the downloaded cathedral
  windows (southwark, moses, aristophanes, biology, roche, …) packed into the
  `tossinshade` bundle.
- Surface shader shows the colored image and **glows by backlit transmission**:
  emissive boost scaled by how directly the sun hits the BACK face
  (`saturate(dot(-sunDir, backNormal))`), so the glass lights up when the sun is
  behind it. Translucent per the Valheim recipe — **no Standard shader**
  (`Shader.Find("Standard")` returns null → crash).
- Each pane registers with the projector: world transform, forward normal, image
  texture, average tint (precomputed from the image).

### 2. Projection manager (`FiresGlassProjector : MonoBehaviour`)
- Registry of active `FiresGlassPane` instances near the player; cap **N nearest**
  (~6) to bound cost.
- Each frame: read **sun direction** from EnvMan (`_SkyboxSunDir` / EnvMan light)
  and the day factor; gate on `EnvMan.instance != null` (Unity6 bootstrap AV).
- Per pane, gate: `sunFacing = dot(paneBackNormal, -sunDir) > threshold` **AND**
  daytime **AND** on screen → only then it projects.
- For each active pane, issue a **screen-space projected-light draw**
  (caustics-style): a command buffer that, for pixels inside the pane's beam box,
  reconstructs world pos, **projects it back onto the pane plane along the sun
  direction** to get the pane UV, samples the pane image, and **adds** that color
  as light — distance-faded from the pane, soft-edged, intensity by sun alignment.
  - Beam = the pane rectangle extruded along `sunDir` for `Reach` metres.
  - Drawn as its **own box volume per pane** (deferred-decal style) rather than one
    global fullscreen loop — keeps it genuinely per-window and tractable.

### 3. Godrays (`Custom/FiresGlassGodray` + cone mesh)
- Per active pane: an **additive soft cone/frustum mesh** from the pane along
  `sunDir`, tinted by the pane's average color, animated noise, depth-faded,
  gated by the same sun-facing + a fog/density factor. Cheap, art-directable,
  ties the visible beam to the window. (Not screen-space raymarch — a drawn
  additive mesh in the forward/alpha queue.)

### 4. Config (ConfigManager section "20 - FiresGlass")
`Enabled, Intensity, Reach(m), Softness, ColorBoost/Saturation,
SunFacingThreshold, DayOnly, GodrayEnabled/Density/Length, MaxActiveWindows,
Radius`. Wired into `ConfigManager` + `FiresAdminTerrain` exactly like FiresPiece.

### Shaders (all in the `tossinshade` bundle)
- `Custom/FiresGlassPane` — window surface, backlit-transmission emissive.
- `Custom/FiresGlassProjection` — screen-space projected colored light,
  sun-aligned, depth-reconstructed.
- `Custom/FiresGlassGodray` — additive volumetric cone.

## Phasing
- **P1 — Projection core.** `Custom/FiresGlassProjection` + `FiresGlassProjector`
  casting ONE hardcoded test pane's image onto the ground along the sun dir
  (caustics reconstruction reused). Prove projection + sun alignment on a single
  pane before generalizing.
- **P2 — Window piece + registry.** `Custom/FiresGlassPane`, buildable pane(s)
  from the downloaded images, self-registration, N-nearest + sun-facing gating.
- **P3 — Godrays.** `Custom/FiresGlassGodray` cones per active pane.
- **P4 — Config + polish.** Full config section, time-of-day tuning, perf
  teardown (like caustics `AltitudeCutoff`), version bump, bundle rebuild+deploy.

## Constraints / gotchas (carried)
- **No Standard shader** in Valheim → pane = custom translucent recipe.
- **Deferred renderer** → projection via screen-space depth reconstruction
  (caustics pattern), not a forward Unity `Projector`; godray = additive
  transparent mesh drawn in forward/alpha.
- Bundle shaders must **compile unconditionally + be listed**; `ForceRebuild` +
  verify `GetAllAssetNames()`; embedded bundle via `AssetBundle.LoadFromMemory`.
- **Window image licensing:** real cathedral scans — local test OK, **confirm
  redistribution rights before shipping** them in the bundle.
- Sun direction from EnvMan (`_SkyboxSunDir`); gate on `EnvMan.instance != null`.
