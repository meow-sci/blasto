---
type: How-To
title: Draw seated crew on craft nobody is flying with a Harmony postfix on Vehicle.UpdateSeatedCrewRenderData
description: KSACars postfixes the crew-draw method, skips portrait viewports and calls IVASeat.UpdateSeatedRenderable for every other active car so an open-cockpit passenger stays visible when a kitten on EVA is controlled
category: kittens
group: animation
topic: animate-and-draw-seated-kitten-crew
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/seated-crew-of-unflown-craft]
techniques: [harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Vehicle.UpdateSeatedCrewRenderData]
lifecycle: [startup:all-mods-loaded, frame:render]
game_types:
  - KSA.Vehicle
  - KSA.IVASeat
  - KSA.IViewport
  - KSA.ViewportType
  - KSA.Program
  - KSA.KittenEva
  - KSA.KittenRenderable
tags: [kitten, seated, crew, harmony-postfix, viewports, render]
---
KSA draws seated kittens only for `Program.ControlledVehicle`, which suits a closed capsule. In an open car it means the passenger left sitting vanishes the moment the driver steps out on EVA, because control passes to the kitten. This article draws the crew of every craft you care about in every viewport the flown crew is drawn in, with a postfix on the engine's own method. Prerequisites: StarMap and Harmony; the seats must already exist ([approach 1](animate-and-draw-seated-kitten-crew-1.md) and [add crew seats](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)). The mod that demonstrates it is KSACars.

## Background: how the game does it

`Vehicle.UpdateSeatedCrewRenderData(IViewport, int)` is public. It returns immediately unless `this == Program.ControlledVehicle`; otherwise it builds the craft's `GetMatrixAsmb2Ego(viewport.GetCamera())` and calls `IVASeat.UpdateSeatedRenderable` for each seat in `Crew` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741)). `Program.RenderGame` calls it for the main viewport and `RenderViewport` for each secondary or portrait viewport, directly after `SuperMeshRenderSystem.ClearBuckets` and the loop that draws EVA kittens ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4481-L4496), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4687-L4703)). `UpdateSeatedRenderable` creates or reuses the seat's renderable and submits it ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L105)).

## Lifecycle and timing

`UpdateSeatedCrewRenderData` runs inside the render for each viewport (`frame:render`, main thread). It is the only point where submitting a skinned kitten for the frame is guaranteed to land in the current buckets: a StarMap GUI hook runs before the buckets are cleared, and a frame hook after the frame is submitted ([draw path](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md)). The postfix is installed once at `[StarMapAllModsLoaded]`. Because the postfix runs once per call and the engine calls the method once per rendered viewport, the unflown crews are drawn in every viewport the flown crew is. The postfix runs inside the render pass, so it must not throw: it catches everything and logs once ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)).

## Steps

1. **Find the public method with an exact signature and patch with a postfix under your own Harmony id.** Log a warning and behave as stock KSA if it moves:

```csharp
// src/KSACars/Ksa/SeatedCrewHook.cs L28-L52
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdateSeatedCrewRenderData),
                                                    [typeof(IViewport), typeof(int)]);
            if (target is null)
            {
                Log.Warn("KSA has no Vehicle.UpdateSeatedCrewRenderData to hook; a buggy's crew is drawn only while it is flown");
                return;
            }

            MethodInfo postfix = typeof(SeatedCrewHook).GetMethod(nameof(AfterSeatedCrew),
                                                                  BindingFlags.NonPublic | BindingFlags.Static)!;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Installed = true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not hook seated crew drawing; a buggy's crew is drawn only while it is flown ({e.Message})");
        }
    }
```

2. **Write the postfix.** Skip character portrait viewports (the crew-roster portraits), catch everything, log the first failure only:

```csharp
// src/KSACars/Ksa/SeatedCrewHook.cs L69-L83
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
```

3. **Draw every other craft's crew exactly as the engine draws the flown craft's:** same camera, same matrix call, same player step. Reuse the matrix from `GetMatrixAsmb2Ego(camera)` per craft:

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

4. **Pin the patched signature in your API-surface record** with a method that is never called but references it, so a signature change becomes a build error (a pattern the mod shares with its API-surface diff tool):

```csharp
// src/KSACars/Ksa/SeatedCrewHook.cs L85-L86
    // Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
    private static void PinTheSignature(Vehicle vehicle, IViewport viewport) => vehicle.UpdateSeatedCrewRenderData(viewport, 0);
```

5. **Check the result.** Step the driver out on EVA: the passenger stays seated and animated (and any pose processor from [approach 1](animate-and-draw-seated-kitten-crew-1.md) keeps solving, because the renderable is the same). If the patch cannot apply, the mod logs a warning and the crew vanishes as in stock KSA.

## Complete example

- [src/KSACars/Ksa/SeatedCrewHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SeatedCrewHook.cs#L20-L87) - install, remove, postfix, signature pin.
- [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L394-L413) - `DrawCrewOfUnflown`.
- Feature page: [Seated crew of unflown craft](../../../../mods/ksacars/features/seated-crew-of-unflown-craft.md).

## Variations and gotchas

- **No controlled vehicle, no call.** With `ControlledVehicle` null (for example after the flown craft is destroyed) the engine method is never called and no postfix draws anything.
- **Only your own craft are extended;** others keep stock behaviour. The mod's blocked-on-KSA notes list "crew drawn for every craft with visible seats, or a per-part flag" as what would make the patch unnecessary: recheck after each KSA update.
- **Overlap with `add-crew-seats-and-eva-doors`.** The seat declaration there is XML-only; this drawing hook is the code half and lives here.
- **Portrait viewports** run the same loop; skipping them avoids drawing extra kittens into the roster portraits.
- **Coexistence.** Give the patch its own Harmony id ([coexist with other mods](../../../patching/harmony/coexist-with-other-mods-patching-the-same-method/overview.md)).

## See also

- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](overview.md), [approach 1](animate-and-draw-seated-kitten-crew-1.md), [approach 3](animate-and-draw-seated-kitten-crew-3.md)
- [Add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
