---
type: How-To
title: Read engines, tanks, solid motors and parts with Modules.Get and TryGetFrom, cached per vehicle
description: Enumerate modules of one type, read their live state through ModuleStateful state lists, and cache structural facts in a ConditionalWeakTable keyed by vehicle
category: vehicles-and-parts
group: modules-and-resources
topic: read-live-part-module-state
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/part-module-state-reads]
techniques: [conditional-weak-table-cache]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.ModuleList
  - KSA.ModuleStateful
  - KSA.EngineController
  - KSA.EngineControllerState
  - KSA.Tank
  - KSA.Mole
  - KSA.MoleState
  - KSA.SolidMotor
  - KSA.Battery
  - KSA.Vehicle
tags: [modules, engines, tanks, srb, battery, cache, telemetry]
---
Outcome: from a mod you can list a vehicle's engines with their activity, vacuum thrust and Isp, its tanks with
mass and capacity per substance, its solid rocket motors with grain mass per segment, and its parts, without
allocating on every sample. gatOS publishes these as `/sim` files; the readers are the general recipe.

Prerequisite: a hook on the main thread after the physics results are applied. Demonstrated by
[gatos/part-module-state-reads](../../../../mods/gatos/features/part-module-state-reads.md). Remaining propellant is the same read (sum tank `Mole` masses; solid grain is
separate, see step 4).

## Background: how the game does it

See the [overview](overview.md). The path is always: `vehicle.Parts.Modules.Get<T>()` for the modules, then
`ModuleStateful<TModule, TState, TGlobal, TFx>.TryGetFrom(vehicle.Parts.States, out list)` and
`list.GetState(module)` for the state [current/decomp/KSA/ModuleList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleList.cs#L180-L187)[^ksa-modulelist] [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L839-L843)[^ksa-modulestateful]
[current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L434-L444). A SolidMotor is a `RocketCore`, so it appears in `Parts.RocketCores`, and in
engines through its controller; its propellant is a `SolidGrainSegment` (an `ISubstanceStore`, not a `Tank`), so it
is absent from `Modules.Get<Tank>()` yet counted in `Vehicle.PropellantMass`.

## Lifecycle and timing

Read on the main thread after `Universe.ApplyVehicleSolvers` has called `Parts.UpdateFromTaskResults` for each
vehicle, so module states are the finished step's values [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2525-L2535)[^ksa-vehicle]. gatOS's sampler runs from a
Harmony prefix on the main viewport's `GameViewport.OnFrame` (`frame:viewports`), which is after
`ApplyVehicleSolvers`, and its weak-table caches are game-thread only. A `StarMapAfterGui` read is also fine; a
write from the same place is not ([Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)). See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Engines.** Enumerate `EngineController`s; the index in the span is the stable ordinal. Read state only when
   the caller wants detail. Vacuum Isp is derived from `VacuumData`:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L453-493
    private static List<EngineSnapshot> SampleEngines(Vehicle vehicle, bool detail)
    {
        var modules = vehicle.Parts.Modules.Get<EngineController>();
        var engines = new List<EngineSnapshot>(modules.Length);
        if (detail && modules.Length > 0
            && TryStates<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>(
                vehicle, out var states))
        {
            for (var i = 0; i < modules.Length; i++)
            {
                var engine = modules[i];
                ref readonly var st = ref states.GetState(engine);
                engines.Add(BuildEngine(engine, i,
                    Sanitize.Finite(st.CommandThrottle), st.IsPropellantAvailable));
            }
        }
        else
        {
            // detail off (or no state list): ThrottleCmd/PropellantAvailable keep the record
            // defaults, exactly like the pre-GP3 core pass.
            for (var i = 0; i < modules.Length; i++)
                engines.Add(BuildEngine(modules[i], i, throttleCmd: 0, propellantAvailable: false));
        }

        return engines;
    }

    private static EngineSnapshot BuildEngine(EngineController engine, int index,
        double throttleCmd, bool propellantAvailable)
    {
        double vacThrust = engine.VacuumData.ThrustMax.Length();
        double massFlow = engine.VacuumData.MassFlowRateMax;
        var isp = massFlow > 0 ? vacThrust / (massFlow * StandardGravity) : 0;
        return new EngineSnapshot(index, engine.IsActive,
            Sanitize.Finite(vacThrust), Sanitize.Finite(isp))
        {
            MinThrottle = Sanitize.Finite(engine.MinimumThrottle),
            ThrottleCmd = throttleCmd,
            PropellantAvailable = propellantAvailable,
        };
    }
```

2. **Tanks.** A tank holds one `Mole` per substance; amounts are in the shared `Moles` state list:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L516-536
    private static List<TankSnapshot> SampleTanks(Vehicle vehicle)
    {
        var tanks = new List<TankSnapshot>();
        var moleStates = vehicle.Parts.Moles;
        foreach (var tank in vehicle.Parts.Modules.Get<Tank>())
        {
            foreach (var mole in tank.Moles)
            {
                ref readonly var state = ref moleStates.GetState(mole);
                double amount = state.Mass;
                double capacity = mole.GetStoredMass(mole.ContainerVolume);
                tanks.Add(new TankSnapshot(mole.SubstancePhase.Name,
                    Sanitize.Finite(amount), Sanitize.Finite(capacity))
                {
                    Fraction = Math.Clamp(Sanitize.Finite(mole.FilledFraction(in state)), 0, 1),
                });
            }
        }

        return tanks;
    }
```

3. **The generic state accessor** that the readers share:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L969-976
    private static bool TryStates<TModule, TState, TGlobal, TFx>(
        Vehicle vehicle, out ModuleStateful<TModule, TState, TGlobal, TFx>.StateList states)
        where TModule : ModuleStateful<TModule, TState, TGlobal, TFx>, IDisposable
        where TState : unmanaged
        where TGlobal : unmanaged
        where TFx : struct
        => ModuleStateful<TModule, TState, TGlobal, TFx>.TryGetFrom(vehicle.Parts.States, out states);

```

4. **Solid motors.** Filter `Parts.RocketCores.Modules` to `SolidMotor`, read segment grain mass from
   `Moles.States[grain.StatesIdx]`, and allocate nothing until the first SRB is found (most vessels have none):

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L559-598
    private static IReadOnlyList<SrbSnapshot> SampleSrbs(Vehicle vehicle)
    {
        var cores = vehicle.Parts.RocketCores;
        var coreModules = cores.Modules;
        if (coreModules.Length == 0)
            return [];

        // GP3/GP1: most vessels carry no SRB — allocate nothing until the first one is found.
        List<SrbSnapshot>? srbs = null;
        var moleStates = vehicle.Parts.Moles.States;
        var engines = vehicle.Parts.Modules.Get<EngineController>();
        var index = 0;

        for (var i = 0; i < coreModules.Length; i++)
        {
            if (coreModules[i] is not SolidMotor motor)
                continue;

            ref readonly var st = ref cores.GetState(motor);
            var stack = motor.Stack;
            var segments = stack.Segments;

            double mass = 0, massInitial = 0;
            List<SrbSegmentSnapshot>? segSnaps = null;
            for (var s = 0; s < segments.Length; s++)
            {
                var seg = segments[s];
                var grain = seg.Grain;
                double segMass = grain is not null ? moleStates[grain.StatesIdx].Mass : 0;
                double segInitial = seg.InitialGrainMass;
                double segUnburnable = seg.UnburnableGrainMass;
                mass += segMass;
                massInitial += segInitial;

                (segSnaps ??= new List<SrbSegmentSnapshot>(segments.Length)).Add(
                    new SrbSegmentSnapshot(s, Sanitize.Finite(segMass), Sanitize.Finite(segInitial))
                    {
                        PartInstanceId = seg.FullPart.InstanceId,
                        Substance = seg.Propellant?.Name ?? "",
                        Grain = seg.Geometry.Id,
```

5. **Cache structural facts per vehicle.** Which module links to which, and the part list, only change when the
   vehicle is edited. Key a `ConditionalWeakTable<Vehicle, Entry>` (collected with the vehicle, no pruning) and
   rebuild when a module or part **count** changes, or every ten simulated seconds as a backstop for
   count-preserving edits:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs L26-50
    private const double RebuildIntervalSeconds = 10.0;

    private static readonly ConditionalWeakTable<Vehicle, CacheEntry> Cache = new();

    [KsaAnchor("Vehicle.Parts.Parts (ReadOnlySpan<Part>), .Count; Part.{InstanceId,Id,DisplayName,"
            + "Template.Id,PartParent,SubParts,PositionVehicleAsmb}",
        SourceFile = "KSA/Part.cs / KSA/PartTree.cs", Verified = "2026-09-02", GameVersion = "2026.9.7.5402",
        Risk = ChurnRisk.Low,
        Notes = "Part + subpart enumeration for the welds anchor picker (subparts are Part instances "
            + "with their own InstanceId; PositionVehicleAsmb is subpart-aware). Cached; rebuilt on "
            + "Parts.Count change (no public part-tree version exists) or every 10 s."
            + "5402 SEMANTIC DRIFT: Part..ctor now sets DisplayName = (Template.DisplayName != Template.Id ? Template.DisplayName : Id) — i.e. the AUTHORED template name ('Parachute Bay', 'Drogue Radial A'…) where one exists, the runtime Id otherwise. parts/<n>/display_name is therefore no longer unique per instance; Id and InstanceId are unchanged and remain the addressing keys. Also new at 5402: KSA's part-failure system (PartFailure.IsolateAndDestroy → Vehicle.Split per severed connection, SpawnSubPartDebris) can move a Part — keeping its InstanceId — into a NEW fragment/debris Vehicle ('<id>_N') between two samples with no command; the Parts.Count-change rebuild catches it on the source vessel.")]
    public static IReadOnlyList<PartSnapshot> Sample(Vehicle vehicle, double utSeconds)
    {
        var entry = Cache.GetOrCreateValue(vehicle);
        var liveCount = vehicle.Parts.Count;
        if (entry.Count != liveCount || utSeconds - entry.BuiltUt >= RebuildIntervalSeconds)
        {
            entry.List = Build(vehicle);
            entry.Count = liveCount;
            entry.BuiltUt = utSeconds;
        }

        return entry.List;
    }
```

6. **Return immutable snapshots** (records with sanitised finite doubles) so a consumer on another thread never sees
   a half-updated vehicle.

## Complete example

- [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L453-L536)[^gatos-vesselreader] - engines and tanks
- [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L560-L655) - solid motors
- [gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs)[^gatos-partsreader] and [gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs)[^gatos-animationlinks] - caches

## Variations and gotchas

- **Ordinals shift.** Adding or removing a part (staging, docking, failure) can change every later ordinal; gatOS
  rebuilds on count change. Content updates can add tanks to parts and shift ordinals too (gatOS recorded this in
  build 5482).
- **SRB grain is not a Tank.** `Modules.Get<Tank>()` misses it. Total propellant is `Vehicle.PropellantMass`.
- **API renames.** `Mole.GetLiquidMass` became `GetStoredMass` when solids arrived (gatOS anchors, build 5018); the
  readers guard each KSA member with a documented anchor so a rename is a build error.
- **Never write through these refs.** `GetState` returns `ref readonly`; writes belong in a window the worker will
  not overwrite ([Refill, drain or top up a vehicle's propellant and batteries](../refill-or-drain-resources/overview.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)).
- **Cost.** Allocate lazily; one `Parts.Count` read per vehicle per sample is all the hot path needs.

## See also

- [Overview](overview.md), [Article 2](read-live-part-module-state-2.md)
- [gatos/part-module-state-reads](../../../../mods/gatos/features/part-module-state-reads.md)
- [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../command-engines-throttle-and-rcs-from-code/overview.md), [Refill, drain or top up a vehicle's propellant and batteries](../refill-or-drain-resources/overview.md), `get-part-points-in-vehicle-and-world-frames`

[^gatos-animationlinks]: gatOS: gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs
[^gatos-vesselreader]: gatOS: gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs
[^ksa-modulelist]: current/decomp/KSA/ModuleList.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^gatos-partsreader]: gatOS: gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs
[^ksa-modulestateful]: current/decomp/KSA/ModuleStateful.cs (decompiled, build 2026.10.10.5554)
