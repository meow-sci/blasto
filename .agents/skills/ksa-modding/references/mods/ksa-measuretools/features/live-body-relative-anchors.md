---
type: Mod Feature
title: Live anchors in body, orbit, surface and part frames
description: Each endpoint is an immutable anchor stored in its owner's frame (body CCE offset, orbit-parent offset, body-fixed lat/lon, part-local asmb) and resolved to ECL every frame, with per-frame pruning and a save-load reset
mod: ksa-measuretools
category: flight-and-orbits
techniques: [harmony-postfix, starmap-lifecycle, frame-relative-anchors]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Universe.DeserializeSave]
lifecycle: [frame:input, frame:draw-ui, event:save-load]
game_types:
  - KSA.Astronomical
  - KSA.IParentBody
  - KSA.Celestial
  - KSA.Vehicle
  - KSA.Part
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.CelestialSystem
  - KSA.Universe
tags: [coordinates, frames, anchors, cce, ecl, lat-lon, save-load, measurements]
---

Measurements in MeasureTools are never frozen numbers. A ruler between Earth and the Moon, a point on
Mars's orbit, a pin on the lunar surface and a node on a booster all keep tracking what they are
attached to, and every value (distance, angle, arm lengths) is recomputed from the anchors whenever it
is drawn or copied.[^mt-readme] The reusable idea is the anchor model: store each point in the frame
of its owner and convert to a common frame (ECL) only at use time.

## How it works

`Anchor` is an init-only class built only through factory methods, so an anchor never mutates; a
re-homed anchor is a new instance swapped into the measurement's array.[^mt-anchor]

| `AnchorKind` | Stored data | `ResolveEcl()` |
|---|---|---|
| `BodyCenter` | `Astronomical Body` | `Body.GetPositionEcl()` |
| `OrbitPoint` | `IParentBody OrbitParent`, CCE offset | `OrbitParent.GetPositionEclFromCce(offset)` |
| `SurfaceSnap` (limb) | body, CCE offset of length `MeanRadius` | `Body.GetPositionEclFromCce(offset)`; does not rotate with the body |
| `SurfacePin` | body, latitude and longitude in degrees | `Celestial.GetPositionEclFromLatLon(lat, lon)`; body-fixed, rotates with the body |
| `FreePoint` | reference body, CCE offset | `Body.GetPositionEclFromCce(offset)` |
| `PartPoint` | owning `Vehicle`, `Part`, offset (and optional normal) in the part's local asmb frame | Mirror of `Vehicle.GetMatrixAsmb2Ego` (see the part-anchor concept) |
| `EditorPartPoint` | `Part`, part-local offset | Through `Program.Editor.EditingSpace` |
| `EditorFreePoint` | ECL-axes offset from the editing space | `EditingSpace.PositionEcl + offset` |

CCE offsets work because CCE shares axes with ECL and only moves its origin with the body:
`Astronomical.GetPositionEclFromCce` adds the offset to the body's ECL position.[^ksa-astronomical]
`Celestial.GetPositionEclFromLatLon` goes through the body-fixed frame (`GetCcf2Cce`) and the mean
radius, which is what makes surface pins rotate with the planet.[^ksa-celestial]

`Measurement` holds a mode and an `Anchor[]` and computes everything on demand: `DistanceMeters`,
`AngleRadians` (true 3D angle at the apex, NaN for a zero-length arm), the surface, circle and
face-angle values, and the axial/radial split.[^mt-measurement]

### Validity and pruning

`MeasureState.Prune()` runs first in every `[StarMapAfterGui]` call, before the window and overlay
resolve anything:[^mt-state][^mt-mod]

1. If `Universe.CurrentSystem` is a different object than last frame, clear everything (also the
   reference-body override).
2. Drop the reference override if `system.Get(id)` no longer returns the same object.
3. For each pending point and each settled measurement, `anchor.IsValid(system)`: body kinds are
   valid when `ReferenceEquals(system.Get(body.Id), body)`, an identity check so that a new object
   with the same id does not silently re-anchor. Invalid part anchors try `Rehome`; anything that
   cannot be repaired drops the pending list or the measurement.

`StateVersion` is bumped on every change that alters what the next pick returns (mode, snap toggles,
reference override, a placed or re-homed point), which invalidates the overlay's throttled preview
cache immediately.

### Save loads

`Universe.DeserializeSave` keeps the same `CelestialSystem` object (it calls
`CurrentSystem.DestroyAllVehicles()` and deserializes into the existing system), so the
system-change check in `Prune` never fires on a load.[^ksa-universe] Vehicle anchors die on their own
because their vehicles are gone, but body, orbit and free points would survive as offsets that mean
nothing in the loaded save. `Patch_LoadSave` therefore postfixes `DeserializeSave` and calls
`MeasureState.ClearAll()` and resets the reference override, inside a `try/catch` because an
exception from a postfix propagates into the game's load.[^mt-patch-loadsave]

## Lifecycle and timing

| Moment | Hook | What happens |
|---|---|---|
| Click | `Program.OnMouseButton` prefix (`frame:input`) | Anchor created from settled post-physics state |
| Every drawn frame | `[StarMapAfterGui]` (`frame:draw-ui`) | `Prune`, then the window and overlay resolve every anchor with this frame's body positions and camera |
| Save load | Harmony postfix on `Universe.DeserializeSave` (`event:save-load`) | `ClearAll` |

Resolution only reads game state. In AfterGui the next physics step is already running on worker
threads; reading main-thread body and vehicle positions there is what every stock UI pass does too.
With the HUD hidden (`Program.DrawUI == false`) AfterGui does not run, so pruning pauses with the
overlay.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs) | Anchor kinds, `ResolveEcl`, validity, factories |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs) | Live values computed from anchors |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L216-L303) | `Prune`, `RehomeAnchors` |
| [MeasureTools/Patches/Patch_LoadSave.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_LoadSave.cs) | Clear on save load |

## Snippets

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Astronomical.GetPositionEcl`, `GetPositionEclFromCce`, `GetPositionCceFromEcl`, `Id` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369-L378) | Body-relative storage and resolution |
| `Celestial.GetPositionEclFromLatLon`, `GetLatitudeFromCce`, `GetLongitudeFromCce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L677-L735) | Body-fixed surface pins |
| `CelestialSystem.Get(string)`, `All` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L295-L310) | Identity check for liveness |
| `Universe.CurrentSystem`, `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2475) | System change detection; postfix target that keeps the system object |
| `Vehicle.GetPositionEcl`, `Asmb2Ego`, `CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281) | Part-anchor resolution |
| `Camera.EgoToEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L233) | Confirms ego and ECL share axes |

## Pitfalls and version notes

- Comparing bodies by id is not enough to detect replacement; compare the object returned by
  `CelestialSystem.Get(id)` with the one you hold.
- A save load does not replace `Universe.CurrentSystem`; mods that cache per system object must hook
  the load separately.
- An anchor resolving to NaN (an editor anchor after the editor closed, before `Prune` repairs it)
  must be filtered by every consumer; the overlay's `Valid` check does this.
- Measurements are session-only and are not written to the save (README: closing the window clears
  them).
- Limb (`SurfaceSnap`) and surface-pin anchors look alike but behave differently over time: the limb
  point keeps a fixed CCE direction, the pin rotates with the body.

## See also

- [Overview](../overview.md)
- [Part-local anchors that survive staging, docking and the editor](./part-anchor-rehoming.md)
- [Orbit-line snapping](./orbit-line-snapping.md)
- [Surface great-circle tool](./surface-great-circle-tool.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Store anchors in the owner's frame and resolve them to ECL on use](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-3.md) - how-to article demonstrating this feature
- [Store points as immutable anchors in CCE, orbit-parent or body-fixed frames, prune them by identity and clear them on save load](../../../howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-2.md) - how-to article demonstrating this feature
- [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](../../../howto/persistence/saves/detect-save-load/detect-save-load-4.md) - how-to article demonstrating this feature

[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-measurement]: MeasureTools/Features/Measure/Measurement.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-patch-loadsave]: MeasureTools/Patches/Patch_LoadSave.cs
[^mt-mod]: MeasureTools/Mod.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
