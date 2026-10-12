---
type: Mod Feature
title: Seated crew of unflown cars
description: Postfix Vehicle.UpdateSeatedCrewRenderData so the kittens sitting in every car that is not the controlled vehicle are drawn too, with the same viewport, camera matrix and player step KSA used for the flown craft
mod: ksacars
category: kittens
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.UpdateSeatedCrewRenderData]
lifecycle: [frame:render]
game_types: [KSA.Vehicle, KSA.IVASeat, KSA.Program, KSA.IViewport, KSA.ViewportType]
tags: [kittens, iva-seat, rendering, harmony, postfix, open-cockpit]
---

KSA draws seated kittens only for `Program.ControlledVehicle`, which suits a closed capsule. In an
open car it means the passenger left sitting vanishes the moment the driver steps out on EVA,
because control passes to the kitten on EVA.[^ksacars-blocked] KSACars postfixes the method that
draws the flown craft's crew and draws every other car's crew the same way.[^ksacars-seatedcrewhook]

## How it works

1. `SeatedCrewHook.Install()` (from `[StarMapAllModsLoaded]`) finds
   `Vehicle.UpdateSeatedCrewRenderData(IViewport, int)` with `AccessTools.Method` and patches it
   with a postfix under its own Harmony id.
2. The engine method returns immediately unless `this == Program.ControlledVehicle`; otherwise it
   builds the craft's `GetMatrixAsmb2Ego(viewport.GetCamera())` and calls
   `IVASeat.UpdateSeatedRenderable` for each seat in `Crew`.[^ksa-vehicle]
3. The postfix skips `ViewportType.CharacterPortrait` viewports and calls
   `Buggies.DrawCrewOfUnflown(viewport, inFrameIndex)`, which, for every active car that is not the
   controlled vehicle and is alive, computes the same matrix and calls the same
   `UpdateSeatedRenderable(viewport, inFrameIndex, Program.GetPlayerDeltaTime(), in asmb2Ego)` for
   each seat.[^ksacars-buggies]
4. `UpdateSeatedRenderable` creates or reuses the seat's `KittenRenderable` and submits it, so the
   kittens appear exactly as they would on the flown craft (and the driver's
   [hand IK](./driver-hands-steering-ik.md) keeps working).[^ksa-ivaseat]

Because the postfix runs once per call, and the engine calls the method once per rendered
viewport (the main view in `RenderGame` and each secondary view in `RenderViewport`), the unflown
crews are drawn in every viewport the flown crew is.

## Lifecycle and timing

`UpdateSeatedCrewRenderData` is called from `Program.RenderGame` and `Program.RenderViewport`,
after `SuperMeshRenderSystem.ClearBuckets` and before command recording for that viewport
(`frame:render`, main thread).[^ksa-program] That is the only point where submitting a skinned
kitten for the frame is guaranteed to land in the current buckets; a StarMap hook runs before the
buckets are cleared (GUI hook) or after the frame is submitted (frame hook). The postfix runs
inside the render pass, so it must not throw: it catches everything and logs once.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/SeatedCrewHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SeatedCrewHook.cs) | Install, remove, postfix, `PinTheSignature` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L394-L413) | `DrawCrewOfUnflown` |

## Snippets

```csharp
// src/KSACars/Ksa/SeatedCrewHook.cs L69-L86
    // Inside the engine's render pass: nothing here may throw.
    private static void AfterSeatedCrew(IViewport viewport, int inFrameIndex)
    {
        try
        {
            if (viewport.Is(ViewportType.CharacterPortrait)) return;
            Buggies.DrawCrewOfUnflown(viewport, inFrameIndex);
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("drawing a buggy's crew failed", e);
        }
    }

    // Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
    private static void PinTheSignature(Vehicle vehicle, IViewport viewport) => vehicle.UpdateSeatedCrewRenderData(viewport, 0);
```

```csharp
// src/KSACars/Ksa/Buggies.cs L398-L413
    public static void DrawCrewOfUnflown(IViewport viewport, int inFrameIndex)
    {
        if (Active.Count == 0) return;
        Vehicle? flown = KsaWorld.ControlledVehicle;
        double dt = Program.GetPlayerDeltaTime();
        foreach (Entry e in Active.Values)
        {
            if (ReferenceEquals(e.Craft, flown) || !KsaWorld.IsAlive(e.Craft)) continue;

            double4x4 asmb2Ego = e.Craft.GetMatrixAsmb2Ego(viewport.GetCamera());
            foreach (IVASeat seat in e.Craft.Crew)
            {
                seat.UpdateSeatedRenderable(viewport, inFrameIndex, dt, in asmb2Ego);
            }
        }
    }
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Vehicle.UpdateSeatedCrewRenderData(IViewport, int)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741)) | Public; draws seated crew for the controlled vehicle only; patched with a postfix |
| `Vehicle.Crew`, `Vehicle.GetMatrixAsmb2Ego(Camera)` | The craft's seats and its assembly-to-camera matrix |
| `IVASeat.UpdateSeatedRenderable` ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L104)) | Builds and submits the seated `KittenRenderable` |
| `Program.RenderGame` / `RenderViewport` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4702)) | Call sites, once per viewport |
| `Program.GetPlayerDeltaTime`, `IViewport.Is(ViewportType.CharacterPortrait)` | Same animation step as the engine; skip portrait renders |

## Pitfalls and version notes

- The engine only calls the method on `ControlledVehicle`, so the postfix piggybacks on that call.
  With **no** controlled vehicle (`ControlledVehicle` null after the flown craft is destroyed) the
  method is never called and no car's crew is drawn.
- Only cars (vehicles in the mod's `Active` set) are extended; other craft keep KSA's behaviour.
- `docs/BLOCKED-ON-KSA.md` lists "crew drawn for every craft with visible seats, or a per-part flag
  for it" as what would make this patch unnecessary; recheck after each KSA update.[^ksacars-blocked]
- Public method, pinned by `PinTheSignature`: a signature change is a build error. If the patch
  does not apply, the mod logs a warning and behaves as stock KSA.

## See also

- [Seats and EVA doors](./seats-and-eva-doors.md)
- [Driver kitten's hands on the steering wheel](./driver-hands-steering-ik.md)
- [API surface pinning and diff](./api-surface-pinning-and-diff.md)
- [Draw seated crew on craft nobody is flying with a Harmony postfix on Vehicle.UpdateSeatedCrewRenderData](../../../howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-2.md) - how-to article demonstrating this feature
- [Know where and when a kitten is animated and drawn, per viewport](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md) - how-to article demonstrating this feature

[^ksacars-seatedcrewhook]: src/KSACars/Ksa/SeatedCrewHook.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-ivaseat]: KSA.IVASeat (decompiled)
