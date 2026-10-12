---
type: How-To
title: Store anchors in the owner's frame and resolve them to ECL on use
description: Keep points attached to bodies, orbits, surfaces and parts as offsets in their owner's frame (CCE offset, body-fixed latitude and longitude, part-local asmb) and convert to ECL only when drawing or measuring
category: flight-and-orbits
group: flight-state
topic: understand-ksa-coordinate-frames-and-epochs
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/live-body-relative-anchors]
techniques: [frame-relative-anchors, harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Universe.DeserializeSave]
lifecycle: [frame:input, frame:draw-ui, event:save-load]
game_types: [KSA.Astronomical, KSA.Celestial, KSA.IParentBody, KSA.Vehicle, KSA.Universe, KSA.CelestialSystem]
tags: [frames, anchors, cce, body-fixed, asmb, latitude-longitude, save-load]
---

Outcome: points that keep tracking what they are attached to. A ruler from Earth to the Moon, a pin
on the lunar surface or a node on a booster is never a stored ECL vector: it is an offset in the
owner's frame, resolved to ECL every time it is drawn. KSA-MeasureTools is the demonstrating mod. [Live anchors in body, orbit, surface and part frames](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md)
Prerequisites: StarMap; Harmony for the save-load reset.

## Background: how the game does it

Body positions change every frame, so a stored ECL vector is wrong one frame later. The game offers
three owner frames that each stay valid:

| Owner frame | What stays fixed | Resolve with |
|---|---|---|
| CCE offset from a body | Position relative to the body, in ECL axes | `Astronomical.GetPositionEclFromCce(offsetCce)` = body ECL + offset |
| Body-fixed (CCF) latitude and longitude | A spot on a rotating surface | `Celestial.GetPositionEclFromLatLon(lat, lon)` |
| Part-local asmb | A spot on a part of a vehicle | the vehicle's asmb-to-ego transform about its ECL position |

CCE shares its axes with ECL and only moves its origin with the body, which is why a CCE offset
resolves with an addition.[^ksa-astronomical] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369-L372) `GetPositionEclFromLatLon` goes through the
body-fixed frame (`GetCcf2Cce`) and `MeanRadius`, so a pin rotates with the planet.[^ksa-celestial]
[current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L677-L682) For a part, the game's own render matrix is
`Vehicle.GetMatrixAsmb2Ego`: rotate the point (minus the centre of mass) by `Asmb2Ego`, translate by the
vehicle's ego position.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281) Because ego axes are ECL axes, the same rotation
applied about `GetPositionEcl()` gives the ECL position without needing a camera.

## Lifecycle and timing

| Moment | Hook | What happens |
|---|---|---|
| Click | `Program.OnMouseButton` prefix (`frame:input`) | Anchor created from the settled post-physics state |
| Every drawn frame | `[StarMapAfterGui]` (`frame:draw-ui`) | Prune, then resolve every anchor with this frame's positions |
| Save load | postfix on `Universe.DeserializeSave` (`event:save-load`) | Clear everything |

Resolution only reads game state, and the ECL caches are the ones rebuilt at `frame:apply-solvers` (see
the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
With the HUD hidden `StarMapAfterGui` does not run, so pruning pauses with the overlay.

## Steps

1. **Define an immutable anchor type** with a kind, the owner reference and the offset in the owner's
   frame. Build it only through factories so a re-homed anchor is a new instance swapped into the
   measurement. MeasureTools has `AtBody`, `OnOrbit(parent, offsetCce, id)`, `PinOnSurface(body, lat, lon)`,
   `Free(refBody, offsetCce)` and part factories in [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L205-L260).
2. **Capture a world hit as an owner offset.** From a pick in ECL, store
   `refBody.GetPositionCceFromEcl(hitEcl)` (see `Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl))` in
   [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L1037)).
3. **Resolve on use**, one `switch`:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L84-L98
public double3 ResolveEcl()
{
    return Kind switch
    {
        AnchorKind.BodyCenter => Body!.GetPositionEcl(),
        AnchorKind.OrbitPoint => OrbitParent!.GetPositionEclFromCce(OffsetCce),
        AnchorKind.SurfacePin => ((Celestial)Body!).GetPositionEclFromLatLon(Latitude, Longitude),
        AnchorKind.PartPoint => ResolvePartPointEcl(),
        AnchorKind.EditorPartPoint => ResolveEditorPartPointEcl(),
        AnchorKind.EditorFreePoint => Program.Editor is VehicleEditor editor
            ? editor.EditingSpace.PositionEcl + OffsetCce
            : InvalidPosition(),
        _ => Body!.GetPositionEclFromCce(OffsetCce),
    };
}
```

4. **Mirror the game's render transform for part points** without a camera:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L105-L111
private double3 ResolvePartPointEcl()
{
    var vehicle = (Vehicle)Body!;
    double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
    return vehicle.GetPositionEcl()
        + (posVehicleAsmb - vehicle.CenterOfMassAsmb).Transform(vehicle.Asmb2Ego);
}
```

5. **Validate by identity, not by id.** A body kind is valid only when `system.Get(body.Id)` returns the
   same object, so a new object with the same id does not silently re-anchor:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L161-L179
public bool IsValid(CelestialSystem system)
{
    if (Kind == AnchorKind.EditorPartPoint || Kind == AnchorKind.EditorFreePoint)
        return IsValidEditor();
    Astronomical? anchorBody = Kind == AnchorKind.OrbitPoint ? OrbitParent as Astronomical : Body;
    if (anchorBody == null)
        return false;
    if (!ReferenceEquals(system.Get(anchorBody.Id), anchorBody))
        return false;
    if (Kind != AnchorKind.PartPoint)
        return true;
    // A part anchor turns stale when its part leaves the stored vehicle's
    // tree (decoupled, docked away, grabbed in the editor); Rehome then finds
    // the new owner. Subparts never migrate between full parts, so the full
    // part's presence covers a stored subpart too.
    Part? fullPart = Part?.FullPart;
    return fullPart != null && Body is Vehicle vehicle
        && ReferenceEquals(vehicle.Parts?.Find(fullPart.InstanceId), fullPart);
}
```

6. **Prune first in every GUI call**: a different `Universe.CurrentSystem` object clears everything;
   invalid part anchors try to re-home (staging, docking), anything unrepairable drops the measurement.

```csharp
// MeasureTools/Features/Measure/MeasureState.cs L220-L238
    public static void Prune()
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MeasureState.Prune");
#endif
        CelestialSystem? system = Universe.CurrentSystem;
        if (!ReferenceEquals(system, _system))
        {
            // Only worth a log line when something is actually dropped (the very
            // first frame also lands here, transitioning from no system).
            if (DebugConfig.Measure && (Measurements.Count > 0 || Pending.Count > 0))
                DefaultCategory.Log.Debug(
                    $"[MeasureTools] System changed, clearing {Measurements.Count} measurement(s) and {Pending.Count} pending point(s).");
            _system = system;
            Pending.Clear();
            Measurements.Clear();
            ReferenceOverride = null;
            StateVersion++;
            return;
```

7. **Clear on save load.** `Universe.DeserializeSave` keeps the same `CelestialSystem` object (it
   deserializes into the existing system), so the system-change check never fires on a load; body,
   orbit and free points would survive as offsets that mean nothing in the loaded save:[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2476)

```csharp
// MeasureTools/Patches/Patch_LoadSave.cs L12-L30
[HarmonyPatch(typeof(Universe), nameof(Universe.DeserializeSave))]
internal static class Patch_LoadSave
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        // A postfix throws into the loader.
        try
        {
            MeasureState.ClearAll();
            MeasureState.SetReferenceOverride(null);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("loadsave-" + ex.GetType().Name,
                $"[MeasureTools] Clearing measurements on save load failed: {ex}");
        }
    }
}
```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs) | kinds, `ResolveEcl`, validity, factories |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs) | live values computed from anchors |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L216-L303) | `Prune`, `RehomeAnchors` |
| [MeasureTools/Patches/Patch_LoadSave.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_LoadSave.cs) | clear on save load |

## Variations and gotchas

- **Surface snap versus pin.** A limb snap stores a CCE offset of length `MeanRadius` and does not rotate
  with the body; a pin stores latitude and longitude and does. Choose by whether the point is
  meant to be on the ground.
- **Exceptions in a postfix propagate into the game's load**; the mod wraps the clear in `try/catch`.
- **Editor anchors** resolve through `Program.Editor.EditingSpace`, not a vehicle; keep separate kinds.
- Anchor stores must be cleared on every system or save change; a long-lived list of CCE offsets is a
  silent leak otherwise.

## See also

- [Overview of frames and epochs](overview.md)
- [Vessel state in CCI/CCF/ECL](understand-ksa-coordinate-frames-and-epochs-1.md), [draw anchor](understand-ksa-coordinate-frames-and-epochs-5.md)
- [Live anchors in body, orbit, surface and part frames](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md)
- Related topics: [Anchor points to a rotating body and pick bodies and orbit lines under the cursor](../../map-view/anchor-and-pick-points-on-bodies/overview.md)

[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
