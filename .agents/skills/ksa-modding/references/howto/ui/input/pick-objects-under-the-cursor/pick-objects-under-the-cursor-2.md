---
type: How-To
title: "Pick objects under the cursor: Part.RayCastEgo hull test plus a terrain march"
description: Pick the craft whose hull the cursor ray hits using the engine's own part ray cast, decide whether ground is nearer, and use the ground point to fire at a spot
category: ui
group: input
topic: pick-objects-under-the-cursor
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/cursor-ray-world-click-tools]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:input, frame:after-on-frame]
game_types: [KSA.Part, KSA.Vehicle, KSA.Cursor, KSA.Ray, KSA.Celestial, KSA.CelestialSystem, KSA.Camera]
tags: [picking, hull, raycast, terrain, designator, click-tools]
---
KSArmory lets the operator click the world to fire at the ground or lock onto a craft. Picking a craft by where its
centre lands on screen ([approach 1](pick-objects-under-the-cursor-1.md)) is loose: a fighter filling the screen is
under the pointer wherever the click lands. KSArmory instead asks the engine's own part ray cast, the one KSA
highlights parts with, whether the cursor ray hits a hull triangle, and only then compares it against the terrain.
Demonstrated by KSArmory. The cursor ray is built as in
[cursor to world ray, approach 1](../cursor-to-world-ray/cursor-to-world-ray-1.md).

## Background: how the game does it

- `Part.RayCastEgo(in matrixVehicleAsmb2Ego, Ray, out minDistance, ...)` tests a ray in ego space against a part: a
  bounding-sphere broad phase, then each sub-part's view mesh with `Ray.RaycastWatertight`. The returned position and
  normal are in the local frame of `closestSubPart`, not of the part you called it on, and a top-level part with no
  sub-parts cannot be hit at all.[current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572)[^ksa-part]
- The engine's hover outline does the same loop per vehicle with `Cursor.GetEgoRay(viewport)` and offers each hit to the
  viewport's `PartPicker`; it runs only in orbit mode, with the cursor free and no modal open.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2924)[^ksa-vehicle]
- `Vehicle.GetMatrixAsmb2Ego(camera)` is `GetMatrixAsmb2Ego(camera.GetPositionEgo(vehicle))`; the ray's distance `t` is
  in metres along the ray from the camera.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1273)[^ksa-vehicle]
- The game offers no CPU ray-vs-terrain routine. Terrain height comes from
  `Celestial.GetTerrainHeightFromDirCce(dir, accurate)` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)[^ksa-celestial], so mods march the ray
  against the height field (the shared code is in [approach 4](pick-objects-under-the-cursor-4.md)).

## Lifecycle and timing

`KSArmoryMod.UpdateWorldClicks` runs from `OnAfterGui` (`[StarMapAfterGui]`, `frame:draw-ui`) when the HUD is drawn and from
the end of the mod's simulation step (`frame:after-on-frame` or a prefix on `Program.OnFrameCelestials`) when it is not;
`PrepareImGui` runs before the `DrawUI` gate so the mouse state is valid either way. The camera was set up in
`OnFrameViewports`, and craft and body positions are the end-of-step state applied in `PrepareFrame`. A shot made here is
consumed by the next simulated step. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[keeping click tools alive with the HUD hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. Cast against each candidate craft in two stages. A bounding-sphere prefilter (`Picking.TryHitSphere`, near root) skips
   craft the ray is nowhere near; `Part.RayCastEgo` on every part with the craft's `Asmb2Ego` matrix gives the hull
   distance. Meshless craft (kittens) fall back to the sphere:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4239-L4241,L4249-L4262,L4266-L4280,L4288-L4296
if (Program.MainViewport?.GetCamera() is not { } camera) return false;

Ray ray = new() { Origin = default, Direction = Vec.Unit(direction) };
// ...
    double radius = MeanRadius(craft);
    if (!(radius > 0.0)) continue;

    double3 centre = PositionEcl(craft);

    // Standing inside one is not aiming at it: from in there the sphere's only hit
    // ahead is its far side, which is behind the operator's own launcher.
    if (Vec.Len(eye - centre) <= radius) continue;
    if (!Picking.TryHitSphere(eye, direction, centre, radius, out double3 onSphere)) continue;

    if (craft.Parts is not { } tree) continue;

    double4x4 asmb2Ego = craft.GetMatrixAsmb2Ego(camera);
    ReadOnlySpan<Part> parts = tree.Parts;
// ...
    for (int i = 0; i < parts.Length; i++)
    {
        if (!parts[i].RayCastEgo(in asmb2Ego, ray, out double hit, out _,
                                 out _, out _, out _, out _, out _, out _))
        {
            continue;
        }

        onMesh = true;
        if (hit > 0.0 && hit < range)
        {
            range = hit;
            hitCraft = craft;
        }
    }
// ...
    if (!onMesh && !HasPickableMesh(parts))
    {
        double toSphere = Vec.Len(onSphere - eye);
        if (toSphere > 0.0 && toSphere < range)
        {
            range = toSphere;
            hitCraft = craft;
        }
    }
```

2. Decide whether the craft or the ground is nearer along the ray. The craft wins only if no ground is nearer:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4415-L4428
public static Vehicle? CraftUnderCursor(Vehicle? exclude)
{
    if (!TryCursorRayEcl(out double3 eye, out double3 direction)) return null;

    if (!TryCursorCraftHit(eye, direction, exclude, out Vehicle? craft, out double range))
    {
        return null;
    }

    bool groundFirst = TryCursorGroundPoint(out double3 ground, out _, out _, out _)
                       && Vec.Len(ground - eye) < range;

    return groundFirst ? null : craft;
}
```

3. March the ray against the terrain of every body and keep the nearest hit (the shared `TerrainRay`; see
   [approach 4](pick-objects-under-the-cursor-4.md)). Terrain only: a launch pad modelled as a thicker planet swung bearings
   wildly at its edge, and the engine has no query for a structure's surface:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L1011-L1054
public static bool TryRayGround(double3 eye, double3 direction, double maxRange,
                                out Celestial? nearest, out double nearestRange)
{
    nearest = null;
    nearestRange = double.MaxValue;

    try
    {
        if (Universe.CurrentSystem is not { } system) return false;

        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Celestial body) continue;

            double3 centre = body.GetPositionEcl();
            double top = MaxTerrainHeightMetres(body);

            // Far enough to cross the whole body. Only the part below its highest ground is
            // walked, and the walk stops at the first place it is under it.
            double reach = Math.Min(maxRange, 2.0 * (Vec.Len(eye - centre) + body.MeanRadius + top));

            // Terrain only. A launch pad is 8 m of pedestal 40 m across, and adding it here
            // models it as an 8 m thicker planet: at 5 km the resolved point moves 2.8 km, and
            // sweeping the cursor over the pad edge swings the bearing from the mount through
            // 168 degrees between one pixel and the next. Where a structure's surface is has
            // no answer in this engine -- see docs/BLOCKED-ON-KSA.md.
            if (!TerrainRay.TryFirstHit(eye, direction, reach, centre, body.MeanRadius, top,
                                        new TerrainHeights(body, accurate: true), out double range)
                || range >= nearestRange)
            {
                continue;
            }

            nearest = body;
            nearestRange = range;
        }

        return nearest is not null;
    }
    catch
    {
        return false;
    }
}
```

4. Turn the ground hit into a place: latitude, longitude and body id, and a body-fixed anchor so a locked point stays on the
   spot as the planet turns:

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

5. Fire at the spot. The designator ignores clicks ImGui owns and shift-clicks, lifts the aim point by the round's fireball
   radius, and falls back to a burst for a gun when nothing is under the cursor:

```csharp
// src/KSArmory/Ksa/Designator.cs L29-L52
public void Update(IManualFire system, SystemConfig policy)
{
    if (!policy.MouseFire) return;

    // A click on the panel is not a click on the world behind it.
    if (ImGui.GetIO().WantCaptureMouse) return;
    // Shift is the designate gesture, so a shift-click is not a click on the world.
    // Without this, locking a target while this tool is on also sends a round at it.
    if (ImGui.GetIO().KeyShift) return;

    if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)) return;

    if (!KsaWorld.TryCursorGroundPoint(out double3 groundEcl, out _, out _, out _))
    {
        // Nothing under the cursor is not a reason to hold fire. A gun shoots where it is
        // pointing rather than at a named place, and the sky is where most of its targets are:
        // requiring a ground hit would leave the CIWS unable to fire at anything above the
        // horizon, which is the one thing a CIWS is for.
        if (system.TriggerArmament == ArmamentKind.Belt) system.FireBurst();
        return;
    }

    system.FireAt(Lifted(groundEcl, system));
}
```
```csharp
// src/KSArmory/Ksa/Designator.cs L89-L94
private static double3 Lifted(double3 groundEcl, IManualFire system)
{
    double3 up = system.Platform is { } craft ? KsaWorld.LocalUp(craft) : Vec.Unit(groundEcl);

    return groundEcl + up * Math.Max(system.Munition.FireballRadius, 2.0);
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4231-L4305) | `TryCursorCraftHit` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4415-L4428) | `CraftUnderCursor` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1011-L1054) | `TryRayGround` |
| [src/KSArmory/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainRay.cs) | First terrain hit along a ray |
| [src/KSArmory/Sim/Picking.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Picking.cs) | Ray-sphere near root, nearest on screen |
| [src/KSArmory/Ksa/Designator.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Designator.cs) | Click to fire at the ground |

## Variations and gotchas

- **Pick hulls, not spheres.** A craft's mean-radius sphere stands metres off the hull; prefilter with it, then cast.
- **Exclude the controlled craft.** It usually carries the launcher and sits under the cursor for most of an orbit view;
  `TryCursorCraftRange` passes it as `exclude` so the turret does not whip round to point at its own hull.
- **Inside a sphere** the near root is behind you and the exit is the only hit ahead; the code skips craft whose centre is within
  one radius of the eye.
- **Shift is reserved** for the lock gesture; plain-click tools return on `KeyShift`.
- **No structure surfaces.** Ground hits are terrain only.
- **The game still sees the click.** KSA's selection and hover are not suppressed by this mod.
- **Version.** `Part.RayCastEgo` opens with a top-level sphere broad phase in builds after 2026.9.22; a transform-only sub-part
  write that moves geometry outside the part's original box can be missed. Checked against 2026.10.10.5554.

## See also

- [Overview of pick-objects-under-the-cursor](overview.md)
- [Approach 1: screen-space nearest](pick-objects-under-the-cursor-1.md)
- [Approach 4: clearance-stepped terrain march](pick-objects-under-the-cursor-4.md)
- [Approach 5: Ray.RaycastWatertight plus terrain bisection](pick-objects-under-the-cursor-5.md)
- Mod feature: [Cursor ray world click tools](../../../../mods/ksarmory/features/cursor-ray-world-click-tools.md)

[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
