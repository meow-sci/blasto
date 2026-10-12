---
type: How-To
title: Store points as immutable anchors in CCE, orbit-parent or body-fixed frames, prune them by identity and clear them on save load
description: Keep map-view points attached to what they were placed on by storing each in its owner's frame and resolving to the ecliptic only at use time, drop anchors whose body object was replaced, and clear state from a Universe.DeserializeSave postfix
category: flight-and-orbits
group: map-view
topic: anchor-and-pick-points-on-bodies
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/live-body-relative-anchors]
techniques: [frame-relative-anchors, harmony-postfix, harmony-prefix]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnMouseButton
  - harmony-postfix KSA.Universe.DeserializeSave
lifecycle: [frame:draw-ui, frame:input, event:save-load]
game_types: [KSA.Astronomical, KSA.Celestial, KSA.IParentBody, KSA.IPosition, KSA.CelestialSystem, KSA.Universe]
tags: [anchor, cce, body-fixed, lat-lon, orbit-point, identity, save-load, pruning, measurement]
---
Outcome: points the player places in the map view keep tracking what they were placed on (a body centre, a point on an orbit, a lat/lon pin that turns with the planet, a free point near a body), every value derived from them is recomputed when drawn, and none of them survives into a save or a system where it would mean nothing. KSA-MeasureTools builds its rulers and protractors on this anchor model.[Live body-relative anchors](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md)

Prerequisites: StarMap for `[StarMapAfterGui]`; Harmony for the click prefix and the save-load postfix.

## Background: how the game does it

- **CCE shares axes with the ecliptic.** `Astronomical.GetPositionEclFromCce(cce)` is just `GetPositionEcl() + cce`, and `IPosition` exposes the same call on an `IParentBody`, so a CCE offset follows the body's orbit but not its rotation.[^ksa-astronomical] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369-L378) [current/decomp/KSA/IPosition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IPosition.cs#L9)
- **Body-fixed pins.** `Celestial.GetPositionEclFromLatLon(lat, lon)` goes through the body-fixed direction, `GetCcf2Cce()` and `MeanRadius`, so it turns with the planet.[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L677-L682)
- **Identity.** `CelestialSystem.Get(string id)` returns the live object for an id; `Universe.CurrentSystem` is the system object.[^ksa-celestialsystem] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L295)
- **Save loads keep the system object.** `Universe.DeserializeSave` destroys the vehicles and deserializes into the existing `CurrentSystem`, so a mod that only watches for a system change never sees a load.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2445)

## Lifecycle and timing

| Moment | Hook | Lifecycle |
|---|---|---|
| Click creates an anchor | Harmony prefix on `Program.OnMouseButton`, inside `Glfw.PollEvents` in `PrepareFrame` | `frame:input` |
| Prune, then resolve and draw every anchor | `[StarMapAfterGui]` | `frame:draw-ui` |
| Drop everything | Harmony postfix on `Universe.DeserializeSave` | `event:save-load` |

Resolution only reads game state. With the HUD hidden `[StarMapAfterGui]` does not run, so pruning pauses with the overlay. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick a frame per anchor kind and make anchors immutable.** Build them only through factories with init-only properties; a re-homed anchor is a new instance swapped in. The orbit and limb kinds store a CCE offset; the surface pin stores latitude and longitude:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L205-L242
    public static Anchor AtBody(Astronomical body)
    {
        return new Anchor { Kind = AnchorKind.BodyCenter, Body = body, Label = body.Id };
    }

    public static Anchor OnOrbit(IParentBody parent, double3 offsetCce, string orbiterId)
    {
        return new Anchor
        {
            Kind = AnchorKind.OrbitPoint,
            OrbitParent = parent,
            OffsetCce = offsetCce,
            Label = orbiterId + " orbit",
        };
    }

    public static Anchor AtSurface(Astronomical body, double3 offsetCce)
    {
        return new Anchor
        {
            Kind = AnchorKind.SurfaceSnap,
            Body = body,
            OffsetCce = offsetCce,
            Label = body.Id + " surface",
        };
    }

    public static Anchor PinOnSurface(Celestial body, double latitudeDeg, double longitudeDeg)
    {
        return new Anchor
        {
            Kind = AnchorKind.SurfacePin,
            Body = body,
            Latitude = latitudeDeg,
            Longitude = longitudeDeg,
            Label = body.Id + " " + FormatLatLon(latitudeDeg, longitudeDeg),
        };
    }
```

2. **Resolve to the ecliptic only at use time.** Every distance, angle or overlay point is computed from `ResolveEcl()` in the frame it is drawn:

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

3. **Validate by identity, not by id.** A body is still valid only if the system returns the same object for its id, so a same-named replacement never silently re-anchors:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L161-L171
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
```

4. **Prune first thing every drawn frame.** A different `CurrentSystem` object clears everything; otherwise each pending point and measurement is validated (part anchors get a chance to re-home after staging or docking):

```csharp
// MeasureTools/Features/Measure/MeasureState.cs L220-L248
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
        }
        if (system == null)
            return;
        if (ReferenceOverride != null && !ReferenceEquals(system.Get(ReferenceOverride.Id), ReferenceOverride))
        {
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Reference override '{ReferenceOverride.Id}' no longer resolves, back to auto.");
            ReferenceOverride = null;
            StateVersion++;
        }
```

```csharp
// MeasureTools/Mod.cs L59-L71
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
#if DEBUG
            using var perfScope = new PerfTracker.Scope("Mod.Draw");
#endif
            MeasureState.Prune();
            if (!MeasureViewport.TryGetActive(out IGameViewport viewport))
                return;
            MeasureWindow.DrawActive(viewport);
            MeasureOverlay.Draw(viewport);
```

5. **Clear on save load from a postfix that cannot throw.** The system object survives a load, so the system-change check above never fires; an exception from a postfix propagates into the game's load, so catch everything:

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
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs) | Kinds, `ResolveEcl`, `IsValid`, factories |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L216-L303) | `Prune`, re-homing |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs) | Values computed from anchors on demand |
| [MeasureTools/Patches/Patch_LoadSave.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_LoadSave.cs) | Clear on save load |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L59-L71) | Prune before drawing |

## Variations and gotchas

- **Limb point versus surface pin.** A CCE offset (`SurfaceSnap`) keeps facing the direction it was placed toward; a lat/lon pin (`SurfacePin`) turns with the ground. Pick per use.
- **Orbit anchors freeze the orbit's shape at click time.** If a burn changes the orbit, the anchor does not follow the new one.
- **NaN is a valid answer.** An editor anchor after the editor closed resolves to NaN until pruned; every consumer filters non-finite points.
- **Session only.** Nothing is written to the save; clearing on load is what keeps stale offsets from reappearing against a different save's geometry.
- **Need a velocity too?** A CCE or lat/lon anchor gives position only; add the spin term as in [approach 1](anchor-and-pick-points-on-bodies-1.md).

## See also

- [Overview](overview.md); [Limb snap](anchor-and-pick-points-on-bodies-3.md); [Orbit-line snap](anchor-and-pick-points-on-bodies-4.md)
- [Live body-relative anchors (mod feature)](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md)
- [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
