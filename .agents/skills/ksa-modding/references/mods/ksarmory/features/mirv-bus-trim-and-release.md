---
type: Mod Feature
title: A MIRV post-boost bus that trims with RCS, re-points between releases and lets its warheads go
description: After cutoff the bus nulls its velocity error against the propagated burnout solution one translation axis at a time in pulse mode, waits out the split, re-points so each tube lies on the release line, releases on a cost-of-holding rule, then goes quiet so KSA can put it back on rails
mod: ksarmory
category: flight-and-orbits
techniques:
  - custom-physics
  - sim-boundary
  - harmony-prefix
  - input-injection
  - closed-loop-trim
  - rails-management
  - vehicle-disposal
hooks: [starmap StarMapAfterGui (via StepOnce), harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.PhysicsBubble
  - KSA.VehicleProperties
  - KSA.Celestial
  - KSA.Universe
  - KSA.InputAction
tags: [mirv, post-boost, rcs, trim, release, multi-target, rails]
---

The ballistic computer's burn is exact at cutoff, and then the bus is pushed off it: the decoupler
that lets it off the spent stack kicks it about a metre a second, and the cutoff leaves a frame of
upper-stage thrust. At about 3,400 m of miss per m/s left radially, that is kilometres.[^bus-trim]
The MIRV bus fixes it with its own RCS (sixteen real KSA thrusters declared on nozzle subparts, fed
from the bus's own tank), then deploys six reentry vehicles one tube at a time.[^gamedata-xml][^icbm-computer]
The parts worth copying are the trim loop against an *actual* actuator whose authority is only
known by firing it, the re-pointing between releases, and getting KSA to put a coasting craft back
on rails.

## How it works

### Trim (`BusTrim`)

- **Same loop as the burn, different actuator**: take the trajectory the guidance flew to,
  propagate it to now, subtract what the bus is doing, thrust along the difference until less
  than one frame of firing is left.[^bus-trim]
- **Against the flown solution, never a fresh solve**: a re-solved transfer depends on when it is
  asked (about 20 m/s per second of arrival error on a deorbit) and goes stale while the bus
  coasts clear.[^bus-trim]
- **One direction at a time**: the stop threshold is half a frame of thrust along the direction
  actually fired, and KSA decides each nozzle's contribution from its thrust direction alone, so a
  direction that moves nothing is struck off and the next tried.[^bus-trim]
- **Actuation**: `VehicleCommand.DriveTranslation(craft, TrimAxes)` presses or releases the
  `Translate*` input actions, and `AttitudeHook.PulseMode` asks the `PrepareWorker` prefix to set
  `FlightComputer.SetManualThrustMode(Pulse)`; both are released when the trim stops.[^icbm-computer][^vehicle-command][^attitude-hook]
- **It gives up rather than holding warheads**: no thrusters or a dry tank releases on the
  trajectory it has.[^bus-trim]

### Split, clearance and stage disposal

After `LauncherSeparation` fires the decoupler, `DriveTrim` waits for the split to land, records
the vehicles that appeared (the dropped stack), and runs `SeparationClearance` before trimming,
so the bus does not thrust into what it dropped.[^icbm-computer][^separation-clearance]
`StageDisposal` then lets spent stages be removed with `KsaWorld.Remove` (`Universe.DestroyVehicle`
with `EndMission`), because each extra vehicle costs about 2 ms of frame time and the engine's
step is capped, so stages are most of what a multi-rocket world simulates.[^stage-disposal][^ksa-universe]

### Release

| Piece | Rule |
|---|---|
| When | `PostBoostAim` releases once the predicted miss is under one correction cycle's holding cost (`cycle seconds x m/s`); `HoldingCost` measures that cost off the actual trajectory (0.82 m/s on a 500 km shot to 21.79 on 12,900 km)[^post-boost-aim][^holding-cost] |
| Pointing | `ReleaseSequence` + `ReleasePointing`: tube axes are read in `Ecl` from the launcher (`TubeAxesEcl`), converted to `Cci`, and the bus turns between releases so each tube in turn lies on the one release line[^icbm-computer][^release-sequence][^release-pointing] |
| Per-tube kick | `ReleaseFocus`: a separation velocity per tube cancels the ring of tube mouths around the mean so every round lands on the mean's impact whatever the roll[^release-focus] |
| Fire | The weapon system's deployment path releases the next tube's round as a mod-simulated reentry vehicle[^weapon-system] |
| Several targets | `TargetSet` (up to six sites with warhead counts) and `ReleaseWalker` walk an itinerary; a set of one executes none of it, so single-target accuracy is unchanged. `DivertFootprint` draws the reach. The multi-target walk is built but not yet flown at this ref[^target-set][^release-walker][^divert-footprint][^mirv-targets-doc] |

### Back on rails for the coast

KSA's vehicle step takes a craft **off rails whenever any actuator is commanded**
(`FlightComputerOutput.AnyActuatorCommanded` in `PhysicsBubble`), and an integrated craft sharing a
bubble picked up about 4 m/s per probe of non-gravitational push. `CoastQuiet` decides when the bus
may stop commanding attitude (bounded at both ends; re-acquire threshold wider than the quiet one),
and the computer then queues `AttitudeHook.QuietOnRails`, which releases the attitude and asserts
rails in the prefix.[^coast-quiet][^icbm-computer][^ksa-physicsbubble]

## Lifecycle and timing

- **`frame:draw-ui`**: `IcbmComputer.Update` (inside `StepSimulation`) runs `DriveTrim`,
  `DriveDeployment` and the coast decision on the simulated step.[^icbm-computer]
- **`frame:dispatch-solvers`**: translation keys are read by the worker at the next
  `PrepareWorker`; pulse mode, attitude holds, quiet and rails are written by the `PrepareWorker`
  prefix just before the snapshot.[^attitude-hook]
- **`physics:vehicle-solver`**: whether the bus stays on rails is decided inside the worker step
  (`PhysicsBubble`), which is why the mod must stop *commanding*, not just stop thrusting.[^ksa-physicsbubble]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/BusTrim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BusTrim.cs) | Trim loop, axis selection, give-up rule |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L1721-L1800) | `DriveTrim`: split, clearance, actuation |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L3681-L3760) | `DriveDeployment`: tube axes, release sequence |
| [src/KSArmory/Sim/PostBoostAim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PostBoostAim.cs) | Release-when rule |
| [src/KSArmory/Sim/CoastQuiet.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CoastQuiet.cs) | When to stop commanding |
| [src/KSArmory/Sim/StageDisposal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StageDisposal.cs) | Which shed stages may be removed |
| [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml#L479-L582) | The bus's RCS, tank, feed wiring and control |

## Snippets

```csharp
// src/KSArmory/Ksa/IcbmComputer.cs L3715-L3729
        if (!_sequence.Begun && Config.RepointBetweenReleases
            && !(_tubeSpinSpeed > ReleaseSequence.SteadyToLatchMetresPerSecond))
        {
            int found = weapon.TubeAxesEcl(_tubeAxes);
            if (found > 0 && Parent is { } parent)
            {
                doubleQuat cce2Cci = parent.GetCce2Cci();
                for (int i = 0; i < found; i++) _tubeAxes[i] = _tubeAxes[i].Transform(cce2Cci);

                if (_sequence.Begin(_tubeAxes.AsSpan(0, found)))
                {
                    Log.Info($"aiming each of {found} tube(s) before it fires");
                }
            }
        }
```

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1439-L1456
				bool anyActuatorCommanded = vehicleUpdateState2.FlightComputerOutput.AnyActuatorCommanded;
				bool flag = vehicleUpdateState2.AnyActuatorActive();
				bool flag2 = vehicleUpdateState2.UseHighFidelityOceanPhysics && newStates.Props.Situation.IsFreeInOcean();
				bool flag3 = KeyframeAnimationModule.AnyAnimating(vehicleUpdateState2.ReadOnlyVehicle.Parts.States);
				bool flag4 = KittenWantsWake(vehicleUpdateState2);
				if (_forceOffRails)
				{
					newStates.Props.SetOnRails(isOnRails: false);
				}
				else if (anyActuatorCommanded || flag || flag2 || flag3 || flag4)
				{
					if (newStates.Props.Situation == Situation.Floating && !IsLadderParked(vehicleUpdateState2))
					{
						double num6 = UpdateFlotationRadius(newStates);
						double3 positionBub = newStates.GetPositionBub();
						newStates.Kinematic.PositionPhys = num6 * positionBub.NormalizeOrZero() - newStates.Origin.PositionBub;
					}
					newStates.Props.SetOnRails(isOnRails: false);
```

```csharp
// src/KSArmory/Sim/CoastQuiet.cs L55-L67
    public bool Update(in CoastQuietState now, double quietDeg, double reacquireDeg)
    {
        if (!now.Enabled || !now.Coasting || now.Burning || now.Trimming || now.SalvoAway
            || !now.CorrectionFinished || now.InReleaseApproach
            || !double.IsFinite(now.PointingErrorDeg))
        {
            _quiet = false;
            return false;
        }

        _quiet = now.PointingErrorDeg <= (_quiet ? reacquireDeg : quietDeg);
        return _quiet;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `PhysicsBubble` rails transition ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1439)) | Why a commanded actuator keeps a craft integrated |
| `FlightComputer.SetManualThrustMode` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs)) | Pulse mode for fine RCS taps (via the prefix) |
| `Vehicle.ProcessInput(InputAction.Translate*, ...)` | Translation thrust, as the keyboard |
| `Celestial.GetCce2Cci()` | Tube axes into the body's inertial frame |
| `Universe.DestroyVehicle(Vehicle, CrewDisposition)` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2018)) | Disposing of spent stages without debris |

## Pitfalls and version notes

- **A commanded actuator keeps a craft off rails**; stop commanding attitude to coast cheaply, and
  ask for rails explicitly where the bubble would refuse.[^ksa-physicsbubble][^icbm-computer]
- **Measure thruster authority by firing**; KSA assigns each nozzle's contribution from its thrust
  direction, so the authority along an axis is whatever the layout gives.[^bus-trim]
- **Pulse mode persists** on the double-buffered flight computer; restore it, or the player's own
  translation keys fire in millisecond taps.[^attitude-hook]
- **Small RCS nozzles were worth about 1.3 km of accuracy** in flight against oversized ones,
  for reasons the author records as not understood.[^gamedata-xml]
- The multi-target walk (phase 3) and its instruments are built but unflown at this ref.[^mirv-targets-doc]

## See also

- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [Launcher separation at decoupler](./launcher-separation-at-decoupler.md)
- [Fly another vehicle](./fly-another-vehicle.md)
- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)
- [Per-part store mass](./per-part-store-mass.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md)
- [Trim a bus with one translation direction at a time, then go quiet so KSA puts it back on rails](../../../howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-2.md) - how-to article demonstrating this feature

[^bus-trim]: src/KSArmory/Sim/BusTrim.cs
[^post-boost-aim]: src/KSArmory/Sim/PostBoostAim.cs
[^holding-cost]: src/KSArmory/Sim/HoldingCost.cs
[^release-sequence]: src/KSArmory/Sim/ReleaseSequence.cs
[^release-pointing]: src/KSArmory/Sim/ReleasePointing.cs
[^release-focus]: src/KSArmory/Sim/ReleaseFocus.cs
[^target-set]: src/KSArmory/Sim/TargetSet.cs
[^release-walker]: src/KSArmory/Sim/ReleaseWalker.cs
[^divert-footprint]: src/KSArmory/Sim/DivertFootprint.cs
[^coast-quiet]: src/KSArmory/Sim/CoastQuiet.cs
[^stage-disposal]: src/KSArmory/Sim/StageDisposal.cs
[^separation-clearance]: src/KSArmory/Sim/SeparationClearance.cs
[^icbm-computer]: src/KSArmory/Ksa/IcbmComputer.cs
[^vehicle-command]: src/KSArmory/Ksa/VehicleCommand.cs
[^attitude-hook]: src/KSArmory/Ksa/AttitudeHook.cs
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^gamedata-xml]: src/KSArmory/KSArmoryGameData.xml
[^mirv-targets-doc]: docs/MIRV-TARGETS.md
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
