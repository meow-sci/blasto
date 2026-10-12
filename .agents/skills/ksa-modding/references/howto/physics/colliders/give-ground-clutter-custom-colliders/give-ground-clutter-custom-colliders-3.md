---
type: How-To
title: GroundClutterCollisions setting and DisplaceEnergyPerKg
description: Turn clutter collisions on at runtime, lower the energy at which rocks come loose and make them lighter while a pusher is about, restoring the stock values afterwards, with a 400 ms off-on cycle so new masses are used
category: physics
group: colliders
topic: give-ground-clutter-custom-colliders
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/scoop-colliders-and-loose-clutter]
techniques: [bepu-physics, game-settings]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:bepu]
game_types: [KSA.GameSettings, KSA.BubbleClutterStatics, KSA.ClutterObjectTemplate, KSA.GroundClutterRenderer]
tags: [physics, clutter, rocks, displace, game-settings, ksacars, scoop]
---

Outcome: a car with a bulldozer scoop can push rocks. KSA's rocks are immovable statics until a hit carries a lot of
energy, so KSACars makes them easy to knock loose and lighter while a car that can push them is about. Prerequisites:
StarMap. The repo notes that switching clutter collisions on at runtime has been seen to give rocks colliders on
Luna, but a rock coming loose has not been seen in game; treat the loose-rock half as unconfirmed.

## Background: how the game does it

With `GameSettings.Current.Simulation.GroundClutterCollisions` on (default false, labelled experimental), each
bubble's `SyncGroundClutterStatics` adds rocks near vehicles as Bepu statics, each with mass
`ClutterObjectTemplate.GetMass(scale)` = `MassKg * scale^3`. With it off, `GroundClutterStatics.Clear` removes every
static and every loose body:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L351-L370
	public void SyncGroundClutterStatics()
	{
		ConstraintSim constraintSim = ConstraintSim;
		if (constraintSim != null)
		{
			if (BubbleTerrainBlocks.Enabled)
			{
				TerrainBlocks.EnsureShapePool(TerrainBlocks.PeakBlockDemand);
			}
			TerrainBlocks.CaptureDebugSnapshot(constraintSim, in Origin);
			GroundClutterRenderer groundClutterRenderer = Program.GetPlanetRenderer()?.GroundClutterRenderer;
			if (groundClutterRenderer == null || !GameSettings.GetGroundClutterCollisions() || !Origin.BubFrame.IsCcf() || !(Parent is Celestial celestial) || !groundClutterRenderer.PlanetPhysicalData.TryGetValue(celestial.Hash, out ClutterEcotypePhysicalData[] value))
			{
				GroundClutterStatics.Clear(constraintSim);
				return;
			}
			GroundClutterStatics.Update(constraintSim, celestial, value, _vehicleStates, in Origin);
			GroundClutterStatics.SyncDisplacedPoses(constraintSim);
		}
	}
```

On contact, `ConstraintSim` computes an impact kinetic energy and calls `BubbleClutterStatics.ReportHit` on Bepu's
worker threads. `ApplyPendingHits` keeps the peak hit per static and, at `energy >= mass * DisplaceEnergyPerKg`
(a public static, 75.5 J/kg), swaps the static for a dynamic body; at `DestroyEnergyPerKg` (1.5e13) it destroys it:

```csharp
// current/decomp/KSA/BubbleClutterStatics.cs L210-L212
	public static float DisplaceEnergyPerKg = 75.5f;

	public static float DestroyEnergyPerKg = 1.5E+13f;
```

At KSA's threshold a car must hit a rock its own weight at about 12 m/s to free it.[^ksa-bubbleclutterstatics]

## Lifecycle and timing

`LoosenClutter` runs each frame from `StarMapAfterGui`, after the frame's step has been dispatched. The setting and rock
masses are read on the main thread by the next frame's `SyncGroundClutter`, so those writes are ordered. `DisplaceEnergyPerKg` is read on
Bepu workers inside the step and the write is unsynchronised, but it is a single `float`, so a worker sees the old or the new value.
Because KSA reads a rock's mass only when it builds the rock's static, each weight change holds the setting off for
400 ms: the next sync clears every rock, and the first sync after it is back on rebuilds them at the new mass.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Decide "loose": a scoop is on, or a car that can carry one is in the world and the player has clutter collisions on.
2. While loose: remember KSA's `DisplaceEnergyPerKg` once and set it to 0.02 J/kg; otherwise put KSA's value back. While a
   scoop is on and collisions are off, switch `GroundClutterCollisions` on (the mod never switches it off for good).
3. Weigh the rocks. Walk `Program.GetPlanetRenderer().GroundClutterRenderer.PlanetPhysicalData`, remember each
   `ClutterObjectTemplate.MassKg` once in a dictionary, and set it to KSA's value times the share (the panel's "Rock weight",
   2 percent by default), or back to 1x when not loose:

```csharp
// src/KSACars/Ksa/KsaWorld.cs L114-L130
    private static bool WeighRocks(double share)
    {
        if (Program.GetPlanetRenderer()?.GroundClutterRenderer is not { } clutter) return false;

        foreach (ClutterEcotypePhysicalData[] ecotypes in clutter.PlanetPhysicalData.Values)
        {
            foreach (ClutterEcotypePhysicalData ecotype in ecotypes)
            {
                foreach (ClutterObjectTemplate rock in ecotype.EcotypeReference.ClutterObjects)
                {
                    if (!KsaRockMass.TryGetValue(rock, out double mass)) KsaRockMass[rock] = mass = rock.MassKg;
                    rock.MassKg = mass * share;
                }
            }
        }
        return true;
    }
```

4. Force a rebuild when the weight changes: set the setting off for 400 ms, then back to what the player wants:

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

5. Restore on exit: when nothing needs loose rocks, set `DisplaceEnergyPerKg` back to the stored KSA value and the
   masses back to 1x.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L67-L130) | `LoosenClutter`, `WeighRocks` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L313-L335) | `SetScoop`, the collider side (see [scale or move colliders](../scale-or-move-a-vessels-colliders-at-runtime/scale-or-move-a-vessels-colliders-at-runtime-2.md)) |

## Variations and gotchas

- `DisplaceEnergyPerKg` is a static shared by every vehicle and bubble, and `MassKg` is on shared templates, so while a scoop
  car is about every craft can knock rocks loose and every rock is light. The mod restores KSA's values when no car needs them.
- The mod writes the setting directly and leaves it on once a scoop has turned it on; whether that reaches the saved
  settings file was not traced (unverified).
- Mass is read only at build time: changing `MassKg` does nothing to rocks already in the physics world.
- A clutter static counts as terrain, so a hard hit on a rock that will not move is a ground impact for crash detection.
- A tiny `DisplaceEnergyPerKg` also makes rocks fly off from wheel contact; choose it with a test drive.

## See also

- [Overview and comparison](overview.md)
- [Register course statics as clutter](give-ground-clutter-custom-colliders-4.md)
- Mod feature: [KSACars scoop colliders and loose clutter](../../../../mods/ksacars/features/scoop-colliders-and-loose-clutter.md)
- [Scale or move a vessel's colliders at runtime](../scale-or-move-a-vessels-colliders-at-runtime/overview.md)

[^ksa-bubbleclutterstatics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
