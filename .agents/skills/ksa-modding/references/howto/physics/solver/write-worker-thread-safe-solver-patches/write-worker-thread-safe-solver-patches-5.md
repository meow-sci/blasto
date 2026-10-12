---
type: How-To
title: Identify the vehicle a callback runs for with a ThreadStatic set in a prefix and cleared in a finalizer
description: Bracket Vehicle.PrepareWorker with a Priority.First prefix that records the vehicle in a ThreadStatic and a Priority.Last finalizer that clears it, so a patch on a function that does not receive the vehicle can tell vehicle-driven calls from camera and UI calls
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
approach: 5
difficulty: advanced
prerequisites: [harmony]
demonstrated_by: [mods/ksastructures/features/terrain-height-roof-override]
techniques: [harmony-finalizer, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-finalizer KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.Celestial.GetTerrainHeightFromDirCce
lifecycle: [frame:dispatch-solvers, frame:viewports, frame:celestials]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.Universe, KSA.KittenEva, KSA.Camera]
tags: [physics, threadstatic, finalizer, context, terrain-height, prepareworker, harmony]
---

Outcome: a patch on a query such as `Celestial.GetTerrainHeightFromDirCce(direction)`, which gets only a
direction and no vehicle, can still answer differently for the craft being stepped and for the main camera.
KSAStructures answers "the roof" to a camera standing above a garage roof and "the pit floor" to a craft
below it. Prerequisite: Harmony. This is the smallest context-passing pattern: no queue, no dictionary, one
`[ThreadStatic]` field.

## Background: how the game does it

`Celestial.GetTerrainHeightFromDirCce` transforms its direction into the body-fixed frame and calls
`GetTerrainHeightFromDirCcf` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L800). Bepu's terrain patches sample the `Ccf`
overload directly, so patching only the `Cce` overload changes what cameras, placement, altitude readouts and
wheel code see without moving the colliders. The engine itself calls the `Cce` overload from
`Camera.OnFrame` (camera clamp) and `Program.OnFrameCelestials`; mod wheel code (KSACars) calls it from its
own `PrepareWorker` prefix.

`Universe.PrepareVehicleWorkers` calls `Vehicle.PrepareWorker(SimStep)` for each vehicle in
`CurrentSystem.All` on the main thread [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2155). That is the window in which a
mod prefix on `PrepareWorker` computes a craft's forces, so any terrain query made inside it is made on that
craft's behalf.

## Lifecycle and timing

| When | Hook | Thread | What |
|---|---|---|---|
| `frame:dispatch-solvers` | prefix (`Priority.First`) and finalizer (`Priority.Last`) on `Vehicle.PrepareWorker` | thread that runs `PrepareVehicleWorkers` (main in build 2026.10.10.5554) | Mark the vehicle, run other prefixes' terrain queries, clear |
| `frame:viewports` | postfix on `GetTerrainHeightFromDirCce` | main | Camera clamp inside `Camera.OnFrame`; the marker is `null`, so the asker is the main camera |
| `frame:celestials` | same postfix | main | `Program.OnFrameCelestials` reads the camera's terrain height; marker `null` |
| any other caller | same postfix | caller's | UI readouts, other mods; marker `null` |

Why a `[ThreadStatic]` and not a plain static: it stays correct if the prepare loop is ever parallelised, and
it can never leak into an unrelated thread. Why a finalizer and not a postfix: a Harmony finalizer runs even
when the original method or another prefix throws, so the marker cannot stay set and make every later camera
query look like a vehicle query. `Priority.First` makes your prefix run before other mods' prefixes, so their
queries see the marker; `Priority.Last` clears it after theirs. Note that this window is not the physics
worker: the code that runs inside `PrepareWorker` is main-thread code that prepares the worker (see
[the PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)).

## Steps

1. Declare the marker and install the bracket and the query postfix under one Harmony id. Both ends are
   registered with explicit priorities:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L245-L257
            MethodInfo Own(string name) => typeof(Office).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(worker, prefix: new HarmonyMethod(Own(nameof(BeforeWorker))) { priority = Priority.First },
                          finalizer: new HarmonyMethod(Own(nameof(AfterWorker))) { priority = Priority.Last });
            harmony.Patch(height, postfix: new HarmonyMethod(Own(nameof(AfterTerrainHeight))));

            // Any craft set down by latitude and longitude over the roof stands on it: a mover in
            // this mod or another, the launch menu, the console.
            if (typeof(Vehicle).GetMethod(nameof(Vehicle.TeleportToLocation), [typeof(Celestial), typeof(double), typeof(double)]) is { } teleport)
            {
                harmony.Patch(teleport, prefix: new HarmonyMethod(Own(nameof(BeforeTeleport))),
                              finalizer: new HarmonyMethod(Own(nameof(AfterTeleport))));
            }
```

2. Set the marker in the prefix and clear it in the finalizer. The same prefix is the right place for other
   per-vehicle work that must run in this window (KSAStructures carries lift riders from it):

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L287-L296
    private static void BeforeWorker(Vehicle __instance)
    {
        _stepping = __instance;

        // The engine copies the craft's state for its worker straight after this, so a rider is
        // moved here or the move is overwritten before anything reads it.
        try { Carry(__instance); } catch { }
    }

    private static void AfterWorker() => _stepping = null;
```

3. Branch on the marker in the postfix. Filter first (body, distance from the site, kittens), then answer
   according to who is asking: a craft gets a margin on the other side of the roof from a camera, because the
   engine's own camera clamp keeps 0.5 m above the ground and a camera rising through the roof must not be
   told the ground jumped:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L338-L358
            // Who is asking stands above the roof and gets the roof, or under it and gets the floor.
            // A camera only once it is clear of the roof by the half metre its own clamp keeps over
            // the ground: one rising through the roof's height, in the lift, is otherwise told the
            // ground has jumped up under it and is thrown up to meet it.
            double3 asker = _stepping is { } craft ? craft.GetPositionCce()
                          : Program.GetMainCamera() is { } camera ? camera.PositionEcl - __instance.GetPositionEcl()
                          : default;
            double clear = _stepping is null ? 0.55 : -0.3;
            double askerHeight = asker.Length() - __instance.MeanRadius;

            // Over the deep ramp the terrain is one pit under several things, and which of them is
            // the ground depends on how high whoever is asking stands.
            if (OfficeDeep.Covers(y, z))
            {
                __result = ground + OfficeDeep.SurfaceUnder(y, z, askerHeight - ground + (_stepping is null ? 0.0 : 0.3));
                return;
            }

            if (askerHeight >= roof + clear) __result = roof;
            else if (dug > 5.2 && askerHeight > ground + Floor - 1.5) __result = ground + Floor;
        }
```

4. Skip callers you must not influence. The postfix starts with `if (_tower is null || _stepping is KittenEva)
   return;`: `KittenEva.PrepareWorker` calls `base.PrepareWorker`, so the prefix runs for kittens too, and
   kittens walk on colliders, not on this answer.

5. Never patch `GetTerrainHeightFromDirCcf` for this. It feeds Bepu's terrain patches and the launch-pad
   placement, so changing it would lift the colliders off the pit.

6. Expect no log output on success. To confirm the bracket works, put a camera over the roof (it should clamp
   to the roof) and a wheeled craft in the garage (its wheels should find the floor); if the marker leaked, both
   would behave as the same asker.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L218-L362) | Patch installation, the bracket, the asker-dependent postfix |
| [src/KSAStructures/Sim/OfficeDeep.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeep.cs#L25-L75) | Pure, unit-tested surface-under-asker maths |

## Variations and gotchas

- The answer depends on the asker, so it is not a pure function of the direction: code that caches terrain
  heights across askers, or across frames while a camera moves through the roof, can see a jump.
- The marker assumes `PrepareWorker` and the query run on the same thread. If KSA ever parallelises the
  prepare loop, the `[ThreadStatic]` still holds, but the mod's cached tower ground height (plain statics)
  would race.
- The postfix runs inside the camera clamp and inside other mods' wheel code and must not throw: it is wrapped
  in an empty `catch`.
- To pass a value other than the vehicle (a flag, a step length), use the same bracket with a different
  `[ThreadStatic]` field; do not pass data through a shared static.
- A marker set in a prefix and cleared only in a postfix leaks on any exception; always use a finalizer.
- This pattern gives context to code on the same thread. It does not send data to the solver workers; for that
  see [approaches 2 to 4](overview.md) and [6](write-worker-thread-safe-solver-patches-6.md).

## See also

- [Overview and comparison](overview.md)
- [Approach 6: immutable commands](write-worker-thread-safe-solver-patches-6.md)
- Mod feature: [KSAStructures terrain height roof override](../../../../mods/ksastructures/features/terrain-height-roof-override.md)
- [Place a vessel on a surface at a latitude and longitude](../../kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md) for the roof-height lending around `TeleportToLocation`
