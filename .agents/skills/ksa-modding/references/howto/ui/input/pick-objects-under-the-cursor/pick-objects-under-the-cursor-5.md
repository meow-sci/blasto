---
type: How-To
title: "Pick objects under the cursor: Ray.RaycastWatertight against parts, then a terrain march with bisection"
description: Turn the cursor or screen-centre ray into a surface anchor, a mesh-precise part hit first and a bisected terrain hit behind it, as a one-shot placement command
category: ui
group: input
topic: pick-objects-under-the-cursor
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/sticker-projected-decals]
techniques: [raycast-picking, imgui-drawlist]
hooks:
  - reflection KSA.Part.RayCastEgo
  - harmony-prefix KSA.GameViewport.OnFrame
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Part, KSA.Vehicle, KSA.Ray, KSA.Cursor, KSA.Camera, KSA.Celestial, KSA.Program, KSA.Universe]
tags: [picking, watertight, raycast, terrain, bisection, anchor, decal]
---
gatOS places stickers on the world: aim the camera (or the cursor) at a fuel tank and spray, and the sticker is anchored to the
hit part or to the hit terrain point so it follows through flight. This article is the picker behind it: a single function that
turns a ray into either a vessel anchor (part-local position and normal) or a body anchor (latitude and longitude). It is the
same goal as [approach 2](pick-objects-under-the-cursor-2.md), with two differences: it keeps the part-local hit so the
anchor survives vessel switches, and its terrain march is a fixed-step bracket followed by 24 bisections. The placement is an
on-demand one-shot command rather than a persistent click tool. Demonstrated by gatOS.

## Background: how the game does it

- `Part.RayCastEgo` loops over `SubParts` and calls `RayCastEgoSubPart`, which tests the sub-part's mesh with
  `Ray.RaycastWatertight(positionCompare, in vertexOffset, ...)` after a bounding-sphere check, then inverts `vertexOffset` to
  report the near hit position in the sub-part's assembly frame. The normal is the first vertex's mesh normal of the hit triangle,
  not an interpolation.[current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572)[^ksa-part][current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2574-L2606)[^ksa-part]
- It is the same sweep KSA's flight hover uses (`Vehicle.UpdateHighlight`).[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2924)[^ksa-vehicle]
- `Ray.RaycastWatertight` is the engine's triangle-soup ray test.[current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L207-L215)[^ksa-ray]
- Ego axes are ecliptic axes and CCE axes are ecliptic axes too, so an ego ray direction converts to body-fixed (CCF) with
  `Celestial.GetCce2Ccf` alone.

## Lifecycle and timing

- The `spray` command runs in gatOS's Frame-phase drain, in a prefix on the main-viewport `GameViewport.OnFrame`
  (`frame:viewports`), so `Camera` is the one about to render; `Cursor.GetEgoRay(Program.MainViewport)` uses the desktop cursor from
  `PrepareImGui` and the main camera. `StickerManager.Tick` later recomposes decal matrices in `StarMapBeforeGui`
  (`frame:draw-ui`). On hidden-HUD frames the tick moves to `StarMapAfterOnFrame`.
- A pick reads live game state, so it must be on the game thread. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Choose the ray. `cursor` aims down the mouse ray; otherwise down the screen centre. Reject non-finite directions. Vehicles are
   tried first and terrain behind:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs L69-L83
internal static bool TryPick(bool cursor, double range, out PickResult result)
{
    result = default;
    if (Program.GetMainCamera() is not { } camera)
        return false;

    var ray = cursor
        ? Cursor.GetEgoRay(Program.MainViewport)
        : camera.ScreenToEgoRay(new float2(camera.FramebufferSize.X * 0.5f, camera.FramebufferSize.Y * 0.5f));
    if (!double.IsFinite(ray.Direction.X) || ray.Direction.LengthSquared() <= 0)
        return false;

    return TryPickVehicle(camera, ray, range, out result)
           || TryPickTerrain(camera, ray, range, out result);
}
```

2. Sweep every live vehicle within range and keep the nearest triangle hit. Store the hit against `closestSubPart`, whose frame the
   returned position and normal are in; re-derive the same matrix from that object every frame:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs L119-L157
private static bool TryPickVehicle(KsaCamera camera, Ray ray, double range, out PickResult result)
{
    result = default;
    if (Universe.CurrentSystem is not { } system)
        return false;

    var found = false;
    var best = range;
    foreach (var astronomical in system.All.UnsafeAsList())
    {
        if (astronomical is not Vehicle vehicle)
            continue;
        var centre = camera.GetPositionEgo(vehicle);
        if (centre.Length() - vehicle.BoundingSphereRadiusBody > range)
            continue;

        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
        foreach (var part in vehicle.Parts.Parts)
        {
            if (!part.RayCastEgo(in vehicleMatrix, ray, out var distance, out _,
                    out var positionAsmb, out var normalAsmb, out _, out _,
                    out var closestSubPart, out _))
                continue;
            if (!(distance >= 0) || !(distance < best))
                continue;

            var normalLength = normalAsmb.Length();
            if (!double.IsFinite(normalLength) || normalLength <= 0)
                continue;

            best = distance;
            found = true;
            result = new PickResult(StickerAnchorKind.Vessel, vehicle, closestSubPart ?? part, null,
                positionAsmb, normalAsmb / normalLength, distance, StickerRules.DefaultRotation);
        }
    }

    return found;
}
```

3. If nothing was hit, march the ray against the nearby celestial's height field in body-fixed coordinates (CCF), the one frame
   in which the height field is defined and the answer stays valid as the planet turns. Bracket the first crossing with 64 coarse
   steps, then bisect 24 times (the final sample uses `accurate: true`):

```csharp
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs L183-L240
private static bool TryPickTerrain(KsaCamera camera, Ray ray, double range, out PickResult result)
{
    result = default;
    if (camera.NearbyCelestial is not { } body)
        return false;

    var cce2Ccf = body.GetCce2Ccf();
    // The ray is in ego; the body centre is at bodyEgo, so (rayOrigin - bodyEgo) is the origin
    // relative to the body centre, still in ecliptic axes = CCE. Rotating by cce2Ccf lands in the
    // body-fixed frame the height field is defined in.
    var originCcf = (ray.Origin - camera.GetPositionEgo(body)).Transform(cce2Ccf);
    var directionCcf = ray.Direction.Transform(cce2Ccf);
    if (!double.IsFinite(originCcf.X) || !double.IsFinite(directionCcf.X))
        return false;

    // Starting underground means the camera is inside the terrain; there is no forward crossing
    // to find and marching would report the far wall of the hole.
    if (Depth(body, originCcf, accurate: false) <= 0)
        return false;

    var above = 0.0;
    var below = double.NaN;
    for (var step = 1; step <= TerrainMarchSteps; step++)
    {
        var t = range * step / TerrainMarchSteps;
        if (Depth(body, originCcf + directionCcf * t, accurate: false) <= 0)
        {
            below = t;
            break;
        }

        above = t;
    }

    if (double.IsNaN(below))
        return false;

    for (var i = 0; i < TerrainBisections; i++)
    {
        var middle = 0.5 * (above + below);
        var accurate = i == TerrainBisections - 1;
        if (Depth(body, originCcf + directionCcf * middle, accurate) <= 0)
            below = middle;
        else
            above = middle;
    }

    var hitCcf = originCcf + directionCcf * below;
    var latitude = Celestial.GetLatitudeFromCcf(hitCcf);
    var longitude = Celestial.GetLongitudeFromCcf(hitCcf);
    if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
        return false;

    result = new PickResult(StickerAnchorKind.Body, null, null, body,
        new double3(latitude, longitude, 0), default, below,
        Heading(body, hitCcf, ray.Direction));
    return true;
}
```

4. Define "depth" as metres above the terrain surface, with NaN-safe handling for a degenerate sample:

```csharp
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs L242-L249
/// <summary>Signed metres of the point above the terrain surface (negative = underground).</summary>
private static double Depth(Celestial body, double3 pointCcf, bool accurate)
{
    var radius = pointCcf.Length();
    if (!double.IsFinite(radius) || radius <= 0)
        return double.NaN; // NaN <= 0 is false, so a degenerate sample never reports a hit
    return radius - (body.MeanRadius + body.GetTerrainHeightFromDirCcf(pointCcf / radius, accurate));
}
```

5. Turn the result into an entry. Body anchors store `(latitude, longitude, 0)`; vessel anchors store the part-local position and
   normal plus the part instance id. The command layer validates range and arguments before picking, and a miss is reported
   as "no hit within N m":

```csharp
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerManager.cs L242-L271
    if (!StickerPicker.TryPick(cursor, values[0], out var pick))
    {
        var miss = $"no hit within {Formats.Scalar(values[0])}m";
        _store.PublishLast(miss);
        return new CommandResult(CommandOutcome.NotFound, miss);
    }

    var entry = new StickerEntry
    {
        Id = SmallestFreeId(),
        Kind = pick.Kind,
        TargetId = pick.Kind == StickerAnchorKind.Body ? pick.Body!.Id : pick.Vehicle!.Id,
        PartInstanceId = pick.Part?.InstanceId ?? 0,
        Image = image,
        Position = pick.Position,
        Normal = pick.Normal,
        Vehicle = pick.Vehicle,
        Part = pick.Part,
        Body = pick.Body,
    };
    ApplyTail(entry, values, 1);
    // The picker's rotation is the "reads upright from here" default; the caller's roll= turns
    // the decal relative to that rather than replacing it.
    entry.RotationDeg = pick.RotationDeg + values[1];
    if (values[4] == StickerCommands.DepthUnset)
        entry.Depth = pick.Kind == StickerAnchorKind.Body
            ? StickerRules.DefaultDepthBody
            : StickerRules.DefaultDepthVessel;
    return Add(entry, $"hit {Formats.Scalar(pick.Distance)}m");
}
```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs) | Ray, vehicle sweep, terrain march, heading |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerManager.cs#L225-L271) | `Spray` command |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerAnchors.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerAnchors.cs) | Re-derives decal space from the anchor every frame |

## Variations and gotchas

- **Part-local, not ecliptic.** The anchor is stored in the hit sub-part's frame (or latitude and longitude), never in ecliptic
  coordinates, so it survives floating-origin shifts and bubble switches.
- **A top-level part with no sub-parts cannot be hit.** The sub-part loop never runs.
- **Start underground means no crossing.** If the eye is below the surface there is no forward crossing; marching would report the far
  wall of a hole, so the picker returns false.
- **Ground clutter cannot be aimed at.** It exists only on the GPU; the ray passes through a rock to the terrain behind it.
- **Bounds drift.** Since 2026.9.22 `Part.RayCastEgo` opens with a top-level sphere broad phase, so a transform-only sub-part write that
  moves geometry outside the part's original box can be missed.
- **Version.** gatOS verified against 2026.9.22.5482; the members used are unchanged in 2026.10.10.5554.

## See also

- [Overview of pick-objects-under-the-cursor](overview.md)
- [Approach 2: Part.RayCastEgo hull test plus terrain march](pick-objects-under-the-cursor-2.md)
- [Approach 4: clearance-stepped terrain march](pick-objects-under-the-cursor-4.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- Mod feature: [Sticker projected decals](../../../../mods/gatos/features/sticker-projected-decals.md)

[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
