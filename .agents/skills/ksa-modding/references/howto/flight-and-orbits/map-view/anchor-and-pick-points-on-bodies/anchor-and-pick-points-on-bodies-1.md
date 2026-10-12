---
type: How-To
title: Click a point on the terrain and keep it as a body-fixed anchor re-read into ecliptic position and velocity every frame
description: Cast the cursor ray from the camera against terrain, store the hit as an offset in the nearest body's own rotating frame, and turn it back into an ecliptic position and a ground velocity with spin every frame
category: flight-and-orbits
group: map-view
topic: anchor-and-pick-points-on-bodies
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/ground-target-designation-and-reach]
techniques: [body-fixed-anchor, cursor-ray, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.ViewportRegistry
tags: [anchor, body-fixed, ground-point, cursor-ray, picking, rotating-body, spin, designation]
---
Outcome: the player clicks the ground and your mod gets a point that stays on that spot of the planet: re-read every frame, it comes back as a fresh ecliptic position and the velocity of the ground there (orbital motion plus spin). KSArmory uses it for ground designations that guided stores and sensors chase.[Ground target designation and reach](../../../../mods/ksarmory/features/ground-target-designation-and-reach.md)

Prerequisites: StarMap for `[StarMapAfterGui]`. A terrain ray-march for the ground hit (KSArmory has its own `TerrainRay`; a mean-sphere hit is the simplest stand-in).

## Background: how the game does it

- **Cursor ray.** `Camera.ScreenToEgoRay(float2)` returns a `KSA.Ray` in the camera-relative ("ego") frame for a framebuffer pixel; `Camera.EgoToEcl(double3)` turns the ego origin (the camera) into an ecliptic point. Ego and ecliptic share axes.[^ksa-camera] [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L233) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L720)
- **Which viewport.** `ViewportRegistry.GameViews` lists the camera viewports; each `IViewport` has `Visible`, `Position`, `Width`, `Height` and `GetCamera()`, and the camera's `FramebufferSize` may differ from the viewport size under render scaling.[^ksa-viewportregistry] [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L27-L51)
- **Body frame.** `Celestial.GetBodyFixed2Ecl()` (returns `GetCcf2Cce()`) rotates body-fixed vectors into ecliptic axes; its conjugate goes the other way. `GetAngularVelocity()` is the spin rate about body-fixed +Z, the parent pole (CCI +Z).[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174-L188)
- **Bodies.** `Universe.CurrentSystem` is a `CelestialSystem` with `Count` and `GetIndex(int)`; `Celestial.GetLatitudeFromCce` and `GetLongitudeFromCce` name the spot.[^ksa-celestialsystem] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66-L80) [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L735)

## Lifecycle and timing

- **Click: `frame:draw-ui`.** KSArmory reads clicks in its `[StarMapAfterGui]` pass after its panel draws, so `ImGui.GetIO().WantCaptureMouse` already says whether the click landed on a window. Camera state is final for the frame: `OnFrameViewports` ran before the UI pass.
- **Resample: every frame**, before anything reads the point, against the celestial state `PrepareFrame` applied this frame. Reading body positions on the main thread while the next vehicle step runs on workers is what every stock UI pass does too.
- **Hidden HUD.** The GUI hooks do not fire when `Program.DrawUI` is false; resample from a hook outside that guard if the point must stay live. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the cursor ray in the right viewport.** Find the visible game viewport under the cursor, convert to that camera's framebuffer pixels, and take the ray direction plus the camera position:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4113-L4152
    public static bool TryCursorRayEcl(out double3 originEcl, out double3 directionEcl)
    {
        originEcl = default;
        directionEcl = default;
        try
        {
            float2 cursor = ImGui.GetMousePos();

            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                IGameViewport v = viewports[i];
                if (!v.Visible) continue;

                if (v.GetCamera() is not { } camera) continue;

                // Framebuffer pixels, not viewport pixels: ScreenToEgoRay divides by the camera's
                // own framebuffer, and a render or display scale makes those different sizes.
                if (!CursorAim.TryToFramebuffer(cursor, v.Position, v.Width, v.Height,
                                                camera.FramebufferSize.X, camera.FramebufferSize.Y,
                                                out float2 local))
                {
                    continue;
                }

                double3 direction = camera.ScreenToEgoRay(local).Direction;
                if (!CursorAim.IsUsableDirection(direction)) continue;

                originEcl = camera.EgoToEcl(Vec.Zero);
                directionEcl = Vec.Unit(direction);
                return Vec.IsFinite(originEcl);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
```

2. **Walk the ray to the ground.** Take the nearest body the ray hits (a moon on the horizon should mean the moon), and name the spot by latitude and longitude:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L1065-L1096
    public static bool TryCursorGroundPoint(out double3 groundEcl,
                                            out double latitudeDeg, out double longitudeDeg,
                                            out string bodyName)
    {
        groundEcl = default;
        latitudeDeg = 0.0;
        longitudeDeg = 0.0;
        bodyName = string.Empty;

        try
        {
            if (!TryCursorRayEcl(out double3 eye, out double3 direction)) return false;
            if (!TryRayGround(eye, direction, double.MaxValue, out Celestial? nearest, out double nearestRange))
            {
                return false;
            }

            if (nearest is null) return false;

            groundEcl = eye + (Vec.Unit(direction) * nearestRange);

            double3 cce = groundEcl - nearest.GetPositionEcl();
            latitudeDeg = nearest.GetLatitudeFromCce(cce);
            longitudeDeg = nearest.GetLongitudeFromCce(cce);
            bodyName = nearest.Id ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }
```

3. **Store the hit in the body's own frame.** Rotate the offset from the body centre by the conjugate of `GetBodyFixed2Ecl()`; the result does not move as the planet orbits and turns:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L932-L965
    public static bool TryAnchorToGround(double3 pointEcl, out object? body, out double3 anchor)
    {
        body = null;
        anchor = Vec.Zero;

        try
        {
            if (Universe.CurrentSystem is not { } system) return false;

            Celestial? nearest = null;
            double nearestRange = double.MaxValue;

            for (int i = 0; i < system.Count; i++)
            {
                if (system.GetIndex(i) is not Celestial candidate) continue;

                double range = Vec.Len(pointEcl - candidate.GetPositionEcl()) - candidate.MeanRadius;
                if (range >= nearestRange) continue;

                nearest = candidate;
                nearestRange = range;
            }

            if (nearest is null) return false;

            body = nearest;
            anchor = doubleQuat.Conjugate(nearest.GetBodyFixed2Ecl()) * (pointEcl - nearest.GetPositionEcl());
            return Vec.IsFinite(anchor);
        }
        catch
        {
            return false;
        }
    }
```

4. **Re-read it every frame with the ground's velocity.** Position is centre plus the rotated anchor; velocity is the body's orbital velocity plus spin about the body-fixed +Z axis crossed with the offset. The spin term is 465 m/s at Earth's equator, 4.6 km over a ten-second flight:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L978-L1005
    public static bool TryGroundAnchorEcl(object? body, double3 anchor,
                                          out double3 positionEcl, out double3 velocityEcl)
    {
        positionEcl = Vec.Zero;
        velocityEcl = Vec.Zero;

        if (body is not Celestial celestial) return false;

        try
        {
            doubleQuat spin = celestial.GetBodyFixed2Ecl();
            double3 offset = spin * anchor;

            positionEcl = celestial.GetPositionEcl() + offset;

            // Orbital motion, plus the surface sweeping under it. omega x r, with omega taken
            // about the spin axis the body's own frame defines.
            double3 axis = Vec.Unit(spin * new double3(0, 0, 1));
            velocityEcl = celestial.GetVelocityEcl()
                          + Vec.Cross(axis * celestial.GetAngularVelocity(), offset);

            return Vec.IsFinite(positionEcl) && Vec.IsFinite(velocityEcl);
        }
        catch
        {
            return false;
        }
    }
```

5. **Resample before every read.** KSArmory does it at the top of each round's target sample, so a ground aimpoint is never older than the frame (see step 3 of [Steer a falling store by its predicted impact](../../guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-3.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L932-L1096) | Anchor, resolve, cursor ground point |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4113-L4152) | Cursor ray in the hovered game viewport |
| [src/KSArmory/Sim/Aimpoint.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Aimpoint.cs) | `Ground` aimpoint kind, `Resampled` |
| [src/KSArmory/Ksa/TargetLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TargetLock.cs#L67-L80) | A click becomes a ground designation |
| [src/KSArmory/Ksa/Designator.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Designator.cs#L29-L52) | Click arbitration with `WantCaptureMouse` and Shift |

## Variations and gotchas

- **Never hold a ground point as an ecliptic coordinate.** It is left behind at ~29.8 km/s the instant it is written down.
- **CCE, not CCI, for a spin vector you cross with an ECL offset.** The two frames differ by the axial tilt (23.4 degrees on Earth); KSArmory's `GroundVelocityAt` uses `IParentBody.GetAngularVelocityCce()` for that reason.
- **Framebuffer pixels.** `ScreenToEgoRay` divides by the camera's framebuffer size; convert from viewport pixels first or picks drift under render scaling.
- **Arm a tool rather than use a button.** A button samples the cursor while it is over the button.
- **Pick on terrain, not the mean sphere**, if hills matter: a sphere hit refined by the height under it lands behind a hill seen side-on.

## See also

- [Overview](overview.md); [Immutable anchors in several frames](anchor-and-pick-points-on-bodies-2.md)
- [Ground target designation and reach (mod feature)](../../../../mods/ksarmory/features/ground-target-designation-and-reach.md)
- [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
