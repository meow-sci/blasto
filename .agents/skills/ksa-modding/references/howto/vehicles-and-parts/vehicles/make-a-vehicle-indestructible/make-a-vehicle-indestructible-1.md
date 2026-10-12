---
type: How-To
title: Make one kind of craft indestructible with CrashTolerance and a destruction veto
description: Set a huge CrashTolerance on the part XML to stop per-part failure and add a Harmony prefix on Universe.DestroyVehicleFromEvent to veto whole-craft destruction for your craft
category: vehicles-and-parts
group: vehicles
topic: make-a-vehicle-indestructible
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/crash-veto]
techniques: [harmony-prefix, xml-asset]
hooks: [starmap StarMapAllModsLoaded, harmony-prefix KSA.Universe.DestroyVehicleFromEvent]
lifecycle:
  - startup:asset-load
  - startup:all-mods-loaded
  - physics:vehicle-solver
  - frame:apply-solvers
  - event:vehicle-destroyed
game_types:
  - KSA.Universe
  - KSA.Vehicle
  - KSA.VehicleDestructionEvent
  - KSA.PartTemplate
  - KSA.Part
  - KSA.PartStructuralLimits
  - KSA.PhysicsBubble
tags: [crash, veto, crash-tolerance, xml, cars]
---
Outcome: your mod's craft (KSACars' buggies) can be dropped off a cliff, driven into the sea or rammed into a
rock and stay driveable. Both of KSA's destruction paths are closed for them: the whole-craft path by a prefix
that returns false, the per-part path by an authored `CrashTolerance`.

Prerequisites: StarMap, Harmony, and a part defined in XML (see [Define a part with its own game data and GLB model](../../parts/define-a-part-with-game-data/overview.md)).
Demonstrated by [ksacars/crash-veto](../../../../mods/ksacars/features/crash-veto.md).

## Background: how the game does it

See the [overview](overview.md) for the two paths. In short:

| Path | Raised by | Applied by | Closed by |
|---|---|---|---|
| g-load, dynamic pressure, ground, sea, collision | `PhysicsBubble.DetectStructuralFailure` on the worker | `ApplyRenderEventsToVehicles` -> `VehicleDestructionEvent.Apply` -> `DestroyVehicleFromEvent` | prefix veto |
| console `explode`, explosion editor | `InputEvents.VehicleExplodeData` | `DestroyVehicleFromEvent` | prefix veto |
| contact pressure on a part | `PartFailure.Detect` | `PartFailureEvent.Apply` -> `IsolateAndDestroy` -> `DestroyVehicle` | `CrashTolerance` |

`DestroyVehicleFromEvent(Vehicle, VehicleDestructionEvent)` is public and static
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971-L2016)[^ksa-universe]. `PartTemplate.CrashTolerance` is an `[XmlAttribute] double` defaulting to
`double.NaN` [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L12-L20)[^ksa-parttemplate], and the getter uses an authored positive value as given
[current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L30-L44)[^ksa-partstructurallimits]. A car is one part, so the part-failure path would destroy the whole car
(`DestroyVehicle` on the piece that holds it) and the prefix never sees it.

## Lifecycle and timing

| Moment | Where | Thread |
|---|---|---|
| `CrashTolerance` read | `<Part>` deserialised into `PartTemplate` | main, `startup:asset-load` |
| Prefix installed | `[StarMapAllModsLoaded]` -> `CrashHook.Install` | main |
| Failures detected | `FullPhysicsEndFrame` -> `PartFailure.Detect`, `DetectStructuralFailure` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1655-L1684)[^ksa-physicsbubble] | worker (`physics:vehicle-solver`) |
| **Prefix runs** | `PrepareFrame` -> `ApplyVehicleSolvers` -> `ApplyRenderEventsToVehicles` -> `DestroyVehicleFromEvent` | main (`frame:apply-solvers`) |

The prefix runs in the settled window with fully applied vehicle state, so reading the mod's own registry is
safe. After `Apply`, KSA clears the stored event; if the car is still over its limits next step a new one is
raised and the prefix refuses it again. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) for Harmony on the frame
and [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) for the worker side.

## Steps

1. Author a huge tolerance on the part. Pascals; derived values for a light car would otherwise be reached by
   a wheel pad clipping a rock:

```xml
<!-- src/KSACars/KSACarsAssets.xml L162-167 -->
  <!--
    CrashTolerance is the contact pressure, in pascals, past which KSA breaks a part. A car is one
    part, so its breaking is the car's destruction, and a wheel pad clipping a rock at speed reaches
    what KSA derives from its mass. Set far past anything a contact gives.
  -->
  <Part Id="KSACars_Prefab_Buggy" CrashTolerance="1e12">
```

2. Patch the one public method that every whole-craft failure ends in. Resolve it with
   `AccessTools.Method` and its exact parameter list, so a missing method is a logged warning, not a crash:

```csharp
// src/KSACars/Ksa/CrashHook.cs L27-51
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = AccessTools.Method(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent),
                                                    [typeof(Vehicle), typeof(VehicleDestructionEvent)]);
            if (target is null)
            {
                Log.Warn("KSA has no Universe.DestroyVehicleFromEvent to hook; a car breaks up in a hard landing");
                return;
            }

            MethodInfo prefix = typeof(CrashHook).GetMethod(nameof(BeforeDestroy),
                                                            BindingFlags.NonPublic | BindingFlags.Static)!;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Installed = true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not hook vehicle destruction; a car breaks up in a hard landing ({e.Message})");
        }
    }
```

3. In the prefix, return `false` to skip the original for your craft and `true` for everything else. On any
   exception return `true`, so a fault in your code can never make other craft indestructible:

```csharp
// src/KSACars/Ksa/CrashHook.cs L68-84
    // False spares the craft. Inside the engine's own loop: nothing here may throw.
    private static bool BeforeDestroy(Vehicle vehicle)
    {
        try
        {
            return Buggies.Of(vehicle) is null;
        }
        catch (Exception e)
        {
            if (!_complained)
            {
                _complained = true;
                Log.Error("sparing a car from a crash failed", e);
            }
            return true;
        }
    }
```

4. Pin the signature so a game update breaks the build, not the car:

```csharp
// src/KSACars/Ksa/CrashHook.cs L85-87

    // Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
    private static void PinTheSignature(Vehicle vehicle, VehicleDestructionEvent destructionEvent) =>
```

5. Install from `StarMapAllModsLoaded` and call `Remove()` (`UnpatchAll(HarmonyId)`) on unload.
6. Test: drive off a cliff and into the sea. The log should show no `destroyed` warning and the car stays
   in the vehicle list.

## Complete example

- [src/KSACars/Ksa/CrashHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CrashHook.cs)[^ksacars-crashhook] - the whole hook
- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L162-L167)[^ksacars-ksacarsassets] and `:422` - `CrashTolerance="1e12"` on both car parts

## Variations and gotchas

- **Both halves are needed.** The prefix alone leaves per-part failure; the tolerance alone leaves the
  g-load and dynamic-pressure kills.
- **The veto is per craft, the tolerance is per template.** The tolerance affects every instance of the part;
  for per-vessel control use article 2.
- **Console `explode` is vetoed too**, because it also ends in `DestroyVehicleFromEvent`.
- **A vetoed craft keeps exceeding its limits.** The detector re-raises every step; the cost is one
  discarded event per step.
- **Not the same as the dent threshold.** A 1e12 tolerance also stops denting, since dents are
  `pressure / tolerance` against a 0.5 threshold [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L318-L370)[^ksa-fxdeformation].
- **Game updates.** Pinned public method; the XML attribute has existed as `PartTemplate.CrashTolerance`.

## See also

- [Overview](overview.md), [Article 2](make-a-vehicle-indestructible-2.md)
- [ksacars/crash-veto](../../../../mods/ksacars/features/crash-veto.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md), [Define a part with its own game data and GLB model](../../parts/define-a-part-with-game-data/overview.md)

[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-fxdeformation]: current/decomp/KSA/FxDeformation.cs (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksacars-crashhook]: KSACars: src/KSACars/Ksa/CrashHook.cs
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-partstructurallimits]: current/decomp/KSA/PartStructuralLimits.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsassets]: KSACars: src/KSACars/KSACarsAssets.xml
