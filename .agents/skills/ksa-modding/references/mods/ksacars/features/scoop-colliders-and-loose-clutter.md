---
type: Mod Feature
title: Switchable scoop colliders and rocks that come loose
description: Deploy a scoop blade by moving stowed collider boxes out of the hull at runtime through ColliderModule.PositionPartAsmb and NeedsColliderUpdate, and make ground clutter pushable by lowering BubbleClutterStatics.DisplaceEnergyPerKg, enabling clutter collisions and lightening rock templates
mod: ksacars
category: physics
techniques: [custom-physics, bepu-physics, xml-asset, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:after-on-frame, frame:dispatch-solvers, physics:bepu]
game_types:
  - KSA.ColliderModule
  - KSA.ColliderTemplate
  - KSA.ModuleBase
  - KSA.ConstraintSim
  - KSA.Vehicle
  - KSA.KeyframeAnimationModule
  - KSA.BubbleClutterStatics
  - KSA.PhysicsBubble
  - KSA.Universe
  - KSA.GameSettings
  - KSA.ClutterObjectTemplate
  - KSA.ClutterEcotypeReference
  - KSA.ClutterEcotypePhysicalData
  - KSA.GroundClutterRenderer
  - KSA.PlanetRenderer
  - KSA.Part
tags: [physics, colliders, bepu, ground-clutter, rocks, scoop, subpart, game-settings]
---

The Eldorado can carry a bulldozer scoop on its nose, in three sizes chosen from the car panel,
and push rocks with it. Two techniques make that work. The scoop's collider boxes are declared in
the part's XML but *stowed* inside the hull's own box, and the mod moves them out to the blade at
runtime the way KSA's animated parts move theirs. And because KSA's rocks are immovable statics
until a hit carries a lot of energy, the mod makes them easy to knock loose and lighter while a
car that can push them is about.[^ksacars-claude] The repo notes that switching clutter collisions
on at runtime has been seen to give rocks colliders on Luna, but a rock coming loose has **not
been seen** in game.[^ksacars-notes]

## How it works

### Colliders that move

| Piece | What it is |
|---|---|
| `KSACarsGameData.xml` | Every scoop box declared under the car's `<Collider>` at the hull's location (`LocationAsmb X="0.75"` or `"0.65"`, `Y="-0.18"`), inside the hull box, where it touches nothing |
| `ScoopProfile(Name, Size, SubpartSuffix, Stowed, Colliders)` | Per scoop: the blade subpart suffix, the stowed point, and per box (`ScoopCollider(Id, Deployed, DeployedTurn)`) where it goes when on |
| `ScoopProfile.Upright(yaw)` | For boxes too tall to stow upright inside the hull: declared lying down, then turned 120 degrees about (1,1,1) to swap axes and swung by `yaw` for a swept wing |
| `Buggies.SetScoop` | Once per change: for each `ColliderModule` in the part's subtree whose `TemplateId` is one of the scoop's box ids, set `PositionPartAsmb` to deployed or stowed, set `Collider2PartAsmb` to the turn or identity, and `NeedsColliderUpdate = true` |

The XXL scoop's boxes are 1.9 m tall and the Mega's 3.3 m, in two tiers, because lying down a tier
is the most that fits across the hull.[^ksacars-gamedata][^ksacars-profile][^ksacars-scoop-tests]
`ColliderModule.TemplateId` is the `<Box Id>`: `ColliderTemplate.Create` builds each module with
`base.Id`.[^ksa-collidertemplate][^ksa-modulebase] `PositionPartAsmb` and `Collider2PartAsmb` are
public fields, and `NeedsColliderUpdate` is the flag KSA itself sets when a collider is rescaled
(`ColliderModule.SetScale`) or a keyframe animation moves a part (`KeyframeAnimationModule`).[^ksa-collidermodule][^ksa-keyframeanimation]
The physics step reads it in `ConstraintSim.UpdateShape` (from `PushBodyStateToSim`), rebuilds the
vehicle's compound shape with `Vehicle.UpdateColliderCompound`, which recomputes each child's local
pose from `PositionVehicleAsmb` / `Collider2VehicleAsmb` and clears the flag.[^ksa-constraintsim][^ksa-vehicle]

The blade itself is a subpart in the same `.glb`. A subpart has no "hidden" switch, so `Buggies.Pose`
shrinks an unused blade to scale 0.001 at its stowed point inside the hull, and puts the chosen one
at the origin with scale 1 (`Part.Scale`, then `ResetCachedPosMatrixValues`).[^ksacars-buggies]

### Rocks that come loose

KSA's clutter physics, as read from the decomp:[^ksa-clutterstatics][^ksa-physicsbubble][^ksa-universe]

- With `GameSettings.Current.Simulation.GroundClutterCollisions` (TOML `groundClutterCollisions`,
  default false, labelled "[Experimental] Enable Collisions") on, each bubble's
  `SyncGroundClutterStatics` adds rocks near vehicles as Bepu statics through
  `BubbleClutterStatics.Update` -> `AddCell`, each with mass `ClutterObjectTemplate.GetMass(scale)` =
  `MassKg * scale^3`. With it off, `GroundClutterStatics.Clear` removes every static and every loose
  body.[^ksa-gamesettings][^ksa-clutterobjecttemplate]
- On contact, `ConstraintSim` computes an impact kinetic energy and calls
  `BubbleClutterStatics.ReportHit`, on Bepu's worker threads. `ApplyPendingHits` keeps the peak hit
  per static and, at `energy >= mass * DisplaceEnergyPerKg` (a public static, 75.5 J/kg), swaps the
  static for a dynamic body.[^ksa-constraintsim][^ksa-clutterstatics]

What `KsaWorld.LoosenClutter(scooping, pushers)` does each frame:[^ksacars-ksaworld]

1. `loose` = a scoop is on, or a car that can carry one is in the world and the player has clutter
   collisions on.
2. While loose: remember KSA's `DisplaceEnergyPerKg` once and set it to 0.02 J/kg; otherwise put
   KSA's value back.
3. While a scoop is on and collisions are off, switch `GroundClutterCollisions` on (the mod never
   switches it off for good).
4. Rock weight: `WeighRocks(share)` walks `Program.GetPlanetRenderer().GroundClutterRenderer.PlanetPhysicalData`
   and sets every `ClutterObjectTemplate.MassKg` to KSA's loaded value times `RockWeight` (2% by
   default, the panel's "Rock weight" slider), or back to 1x when not loose.
5. Because KSA reads a rock's mass only when it builds the rock's static, each weight change holds
   `GroundClutterCollisions` off for 400 ms: the next sync clears every rock, and the first sync
   after it is back on rebuilds them at the new mass.

## Lifecycle and timing

| Piece | Where | Thread |
|---|---|---|
| Box declarations | `KSACarsGameData.xml` (`startup:asset-load`) | main |
| `SetScoop`, `LoosenClutter`, `WeighRocks` | `[StarMapAfterGui]` (or `[StarMapAfterOnFrame]` with the UI hidden) -> `StepOnce` -> `Buggies.Sync` | main |
| Blade shrink/grow | same frame, `Buggies.Pose` | main |
| Clutter statics synced | next frame, `Universe.ExecuteNextVehicleSolvers` -> `SyncGroundClutter` -> `PhysicsBubble.SyncGroundClutterStatics`, right after `PrepareVehicleWorkers` | main |
| Collider compound rebuilt | vehicle step, `ConstraintSim.PushBodyStateToSim` -> `UpdateShape` | vehicle-solver worker |
| Hits and displacement | Bepu contact callbacks -> `ReportHit`; `ApplyPendingHits` | Bepu / vehicle-solver workers |

`Buggies.Sync` runs in the GUI-pass hook, after the frame's physics step has already been
dispatched in `Program.PrepareFrame`.[^ksacars-mod] The clutter setting and rock masses are read on
the main thread by the next `SyncGroundClutter`, so those writes are ordered.[^ksa-universe] The
collider fields and `DisplaceEnergyPerKg` are not: like every StarMap frame hook, `Buggies.Sync`
runs after `ExecuteNextVehicleSolvers` has started the step, and the worker reads them inside it.
`ConstraintSim.UpdateShape` (from `PushBodyStateToSim`, on the worker) tests `NeedsColliderUpdate`
on every collider, and `Vehicle.UpdateColliderCompoundLeaves` then reads `PositionVehicleAsmb`
(derived from `PositionPartAsmb`, a 24-byte `double3`) and `Collider2VehicleAsmb` and clears the
flag per collider.[^ksa-constraintsim][^ksa-vehicle] Nothing synchronises the two threads, so a
step that is already rebuilding the compound (because the centre of mass moved, for example) can
read a half-written position, or clear the flag the mod has just set and leave a box at its old
pose until the next scoop change. This is read from the code, not observed; the race-free place
for the write would be the mod's own `Vehicle.PrepareWorker` prefix, before the step starts.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L313-L335) | `SetScoop`: move stowed collider modules out to the blade and back |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L119-L127) | `Sync`: scoop state per car, then `LoosenClutter` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L826-L832) | Hide an unused blade by scale 0.001 |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L16-L38) | `ScoopCollider`, `ScoopProfile`, `Upright` |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L252-L280) | The Eldorado's three scoops |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L196-L293) | Stowed scoop boxes |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L30-L130) | `LoosenClutter`, `WeighRocks`, the 400 ms rebuild |
| [tests/KSACars.Tests/ScoopTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/ScoopTests.cs) | Stowed inside the hull, deployed ahead, upright and swept |

## Snippets

```csharp
// src/KSACars/Ksa/Buggies.cs L313-L335
    // Puts the scoop's colliders out on the blade or back inside the hull, once each time it changes.
    private static void SetScoop(Entry e)
    {
        ScoopProfile[] scoops = e.Drive.Profile.Scoops;
        if (scoops.Length == 0 || e.ScoopSet == e.Scoop) return;
        e.ScoopSet = e.Scoop;

        foreach (ColliderModule collider in e.Part.SubtreeModules.Get<ColliderModule>())
        {
            for (int k = 0; k < scoops.Length; k++)
            {
                foreach (ScoopCollider box in scoops[k].Colliders)
                {
                    if (collider.TemplateId != box.Id) continue;
                    bool on = k == e.Scoop;
                    collider.PositionPartAsmb = on ? box.Deployed : scoops[k].Stowed;
                    if (box.DeployedTurn is { } turn) collider.Collider2PartAsmb = on ? turn : doubleQuat.Identity;
                    collider.NeedsColliderUpdate = true;
                }
            }
        }
        Log.Info($"{KsaWorld.DisplayName(e.Craft)}: {(e.ScoopOn ? scoops[e.Scoop].Name + " on" : "scoop off")}");
    }
```

```xml
<!-- src/KSACars/KSACarsGameData.xml L196-L208 -->
      <!--
        The scoop's blade: a flat middle and two wings swept forward. Declared stowed, inside the hull
        box, where they touch nothing; the mod moves them out to the blade while the scoop is on. Stowed
        they sit high, clear of the ground under the hull. The middle one is deep enough to reach back
        to the hull's own box, so a rock cannot get in between the blade and the nose.
      -->
      <Box Id="KSACars_EldoScoopMidCollider">
        <LocationAsmb X="0.75" Y="-0.18" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.82" />
        <LengthY M="0.52" />
        <LengthZ M="1.14" />
      </Box>
```

```csharp
// src/KSACars/Ksa/KsaWorld.cs L72-L104
            bool wanted = _clutterRebuildUntilMs != 0 ? _collisionsWanted : GameSettings.Current.Simulation.GroundClutterCollisions;
            bool loose = scooping || (pushers && wanted);

            double weight = loose ? Math.Clamp(RockWeight, 0.0001, 1.0) : 1.0;
            if (weight != _rockWeightApplied && WeighRocks(weight))
            {
                _rockWeightApplied = weight;
                _collisionsWanted = scooping || wanted;
                _clutterRebuildUntilMs = Environment.TickCount64 + ClutterRebuildMs;
                Log.Info($"rocks now weigh {weight:P1} of KSA's");
            }

            if (_clutterRebuildUntilMs != 0)
            {
                bool rebuilding = Environment.TickCount64 < _clutterRebuildUntilMs;
                GameSettings.Current.Simulation.GroundClutterCollisions = !rebuilding && _collisionsWanted;
                if (!rebuilding) _clutterRebuildUntilMs = 0;
            }
            else if (scooping && !wanted)
            {
                GameSettings.Current.Simulation.GroundClutterCollisions = true;
            }

            if (loose)
            {
                _ksaDisplaceEnergyPerKg ??= BubbleClutterStatics.DisplaceEnergyPerKg;
                BubbleClutterStatics.DisplaceEnergyPerKg = ScoopDisplaceEnergyPerKg;
            }
            else if (_ksaDisplaceEnergyPerKg is { } ksa)
            {
                BubbleClutterStatics.DisplaceEnergyPerKg = ksa;
                _ksaDisplaceEnergyPerKg = null;
            }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `ColliderModule.PositionPartAsmb`, `Collider2PartAsmb`, `NeedsColliderUpdate` | [current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L30-L58) | Where a collider sits in its part; the rebuild flag |
| `ColliderTemplate.Create` -> `ColliderModule(templateId, ...)`; `ModuleBase.TemplateId` | [current/decomp/KSA/ColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderTemplate.cs#L76) | Box `Id` becomes the module's `TemplateId` |
| `ConstraintSim.PushBodyStateToSim` / `UpdateShape` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L290-L475) | Sees `NeedsColliderUpdate` and rebuilds the compound |
| `Vehicle.UpdateColliderCompoundLeaves` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2041-L2054) | Child local pose from the module, flag cleared |
| `KeyframeAnimationModule` collider update | [current/decomp/KSA/KeyframeAnimationModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyframeAnimationModule.cs#L358-L364) | KSA's own use of the same flag |
| `BubbleClutterStatics.DisplaceEnergyPerKg`, `ReportHit`, `ApplyPendingHits` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs#L210-L555) | Threshold (L210), per-worker hit report (L481), displacement (L520) |
| `BubbleClutterStatics.AddCell`, `Clear` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs#L790-L948) | Statics built with the template mass; cleared when the setting is off |
| `PhysicsBubble.SyncGroundClutterStatics` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L351-L370) | Clear or update per bubble from the setting |
| `Universe.ExecuteNextVehicleSolvers` / `SyncGroundClutter` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2201) | Main-thread clutter sync after `PrepareVehicleWorkers` |
| `GameSettings.Simulation.GroundClutterCollisions`, `GetGroundClutterCollisions()` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1050-L1051) | The experimental setting; accessor at L3265-L3268 |
| `ClutterObjectTemplate.MassKg`, `GetMass(scale)` | [current/decomp/KSA/ClutterObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterObjectTemplate.cs#L23-L54) | `[XmlIgnore]` mass from substances; scaled by `scale^3` |
| `ClutterEcotypeReference.ClutterObjects` | [current/decomp/KSA/ClutterEcotypeReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeReference.cs#L16-L40) | The templates per ecotype |
| `ClutterEcotypePhysicalData.EcotypeReference`, `GetMass` | [current/decomp/KSA/ClutterEcotypePhysicalData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypePhysicalData.cs#L208-L211) | Mass lookup used by `AddCell` |
| `GroundClutterRenderer.PlanetPhysicalData` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L308) | Per body ecotype data; reached via `Program.GetPlanetRenderer()` (Program.cs L562) |

## Pitfalls and version notes

- **Not seen working end to end.** Colliders appearing on rocks has been seen; a rock actually
  coming loose has not.[^ksacars-notes]
- **Global side effects.** `DisplaceEnergyPerKg` is a static shared by every vehicle and bubble, and
  `MassKg` is on shared templates, so while a scoop car is about every craft can knock rocks loose
  and every rock is light. The mod restores KSA's values when no car needs them.[^ksacars-ksaworld]
- **The player's setting.** The mod writes `GameSettings.Current.Simulation.GroundClutterCollisions`
  directly and leaves it on once a scoop has turned it on; whether that reaches the saved settings
  file was not traced (unverified).[^ksacars-ksaworld]
- **Mass is read only at build time.** Changing `MassKg` does nothing to rocks already in the
  physics world; the 400 ms off/on cycle forces KSA to clear and rebuild them.[^ksa-clutterstatics]
- **Heavy rocks.** KSA's rocks are dense stone; at KSA's threshold a car must hit a rock its own
  weight at about 12 m/s to free it, hence both the lower threshold and the lighter rocks.[^ksacars-notes]
- **A clutter static counts as terrain**, so a hard hit on a rock that will not move is a ground
  impact for crash detection; see [crash veto](./crash-veto.md).[^ksacars-notes]
- **Collider writes off the physics window.** The writes happen while the vehicle step is
  running (see Lifecycle and timing). KSA's own keyframe animation sets the same flag from
  `KeyframeAnimationModule.UpdateModules`, which `VehicleUpdateState` calls on the worker inside the
  step, so its write and the shape rebuild are ordered on one thread; the mod's are not.[^ksa-keyframeanimation]

## See also

- [Car part game data](./car-part-gamedata.md) (the collider block the boxes live in)
- [Pose subparts at runtime](./pose-subparts-at-runtime.md) (the blade subpart)
- [Car panel and ModMenu entry](./car-panel-and-modmenu.md) (scoop size and rock weight)
- [Crash veto](./crash-veto.md)
- [KSACars overview](../overview.md)
- [GroundClutterCollisions setting and DisplaceEnergyPerKg](../../../howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-3.md) - how-to article demonstrating this feature
- [Write PositionPartAsmb and NeedsColliderUpdate](../../../howto/physics/colliders/scale-or-move-a-vessels-colliders-at-runtime/scale-or-move-a-vessels-colliders-at-runtime-2.md) - how-to article demonstrating this feature

[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-profile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-gamedata]: src/KSACars/KSACarsGameData.xml
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-scoop-tests]: tests/KSACars.Tests/ScoopTests.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md, "Ground clutter can be knocked loose and pushed, if a setting is on"
[^ksacars-claude]: CLAUDE.md, "The cars"
[^ksa-collidermodule]: KSA.ColliderModule (decompiled)
[^ksa-collidertemplate]: KSA.ColliderTemplate (decompiled)
[^ksa-modulebase]: KSA.ModuleBase (decompiled)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-keyframeanimation]: KSA.KeyframeAnimationModule (decompiled)
[^ksa-clutterstatics]: KSA.BubbleClutterStatics (decompiled)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-gamesettings]: KSA.GameSettings (decompiled)
[^ksa-clutterobjecttemplate]: KSA.ClutterObjectTemplate (decompiled)
