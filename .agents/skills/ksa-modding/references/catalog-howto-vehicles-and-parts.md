# Vehicles and parts

Defining parts and whole craft, spawning and controlling vehicles at runtime, reading and driving part modules (engines, tanks, staging, RCS, parachutes), subpart animation, scaling, crash survival, part geometry and anchors, and loading part mods into a running game.

## Vehicles

Whole vehicles at runtime: spawning, safe creation and mutation, taking control, crash survival and scaling.

### [Changing vehicles without racing the vehicle solver](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md)

Why building, destroying or editing a vehicle from a mod hook can race KSA's physics worker, and seven recipes (lock, wait, stage, or write in the one window the worker does not overwrite)

- [Hold the shapes lock around a vehicle build](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-1.md) — intermediate
- [Wait on JobSystems.VehicleSolver before mutating vehicles](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-2.md) — intermediate
- [Destroy vehicles and fail parts safely from a frame hook](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-3.md) — advanced
- [Write state in a prefix on Universe.ExecuteNextVehicleSolvers](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-4.md) — intermediate
- [Write flight-computer and kinematic fields in a Vehicle.PrepareWorker prefix](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-5.md) — advanced
- [Write mole (tank) state where the worker will not overwrite it](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-6.md) — advanced
- [Change a part's mass at runtime in the PrepareWorker window](howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-7.md) — advanced

### [Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance](howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/overview.md)

KSA destroys vehicles through two paths, a per-part crash-tolerance comparison and a whole-craft structural check, and four recipes to veto, neutralise, filter or imitate them

- [Make one kind of craft indestructible with CrashTolerance and a destruction veto](howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-1.md) — intermediate
- [Protect chosen vessels with a tolerance getter prefix and a detector pair](howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-2.md) — advanced
- [Filter only the g-load read with a guarded transpiler and persist it by vessel identity](howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-3.md) — advanced
- [Damage parts against their crash tolerance with a weak-keyed health ledger](howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-4.md) — advanced

### [Scaling a vessel or its parts at runtime](howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md)

How Part.Scale, IRescale and the vehicle editor's scale limits work, and four ways to resize a vessel, a part, an avatar or the editor's limits

- [Scale a whole vessel with separate render, physics and collider channels](howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-1.md) — advanced
- [Scale a vessel with a recursive Part.Scale write and an avatar scalar](howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-2.md) — beginner
- [Scale parts per axis with captured baselines](howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-3.md) — intermediate
- [Lift the vehicle editor's part-scale limits with skipping prefixes and a bounds postfix](howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-4.md) — advanced

### [Spawning a vehicle at runtime](howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md)

How KSA builds a Vehicle from a part tree and an orbit, and three mod recipes for creating one from a library craft, a state vector or a single hand-built part

- [Spawn a parked copy of a library craft at a latitude and longitude](howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/spawn-a-vehicle-at-runtime-1.md) — intermediate
- [Spawn test drones into orbit from a state vector](howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/spawn-a-vehicle-at-runtime-2.md) — advanced
- [Spawn a one-part vehicle built in code](howto/vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/spawn-a-vehicle-at-runtime-3.md) — intermediate

### [Taking control of another vehicle](howto/vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md)

How KSA decides which vehicle the player flies, and two ways for a mod to fly another craft, by switching Program.ControlledVehicle or by commanding the craft's flight computer from a PrepareWorker prefix

- [Switch the controlled vehicle and restore the main view afterwards](howto/vehicles-and-parts/vehicles/take-control-of-another-vehicle/take-control-of-another-vehicle-1.md) — intermediate
- [Fly another vehicle through its flight computer and input actions](howto/vehicles-and-parts/vehicles/take-control-of-another-vehicle/take-control-of-another-vehicle-2.md) — advanced

## Parts

Defining parts and craft in XML and code: game data, ready-made craft, seats and doors, subparts, safe evolution of shipped parts and runtime part-mod loading.

### [Adding crew seats and EVA doors to a part](howto/vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)

How IVASeat and EVADoor modules are declared in part XML, paired by SeatId and used for boarding, and two mods' seat and door definitions

- [Add IVASeat entries in PartGameData and EVADoor modules on hidden subparts](howto/vehicles-and-parts/parts/add-crew-seats-and-eva-doors/add-crew-seats-and-eva-doors-1.md) — intermediate
- [Give a chair a seat and a door with the minimum XML](howto/vehicles-and-parts/parts/add-crew-seats-and-eva-doors/add-crew-seats-and-eva-doors-2.md) — beginner

### [Defining a part with its own game data and GLB model](howto/vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)

How KSA loads Assets and GameData XML bundles into PartTemplates, which XML elements a part accepts, and three mods' part definitions

- [Define a driveable part with colliders, a mass shape and its own editor category](howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-1.md) — intermediate
- [Split inert weapon parts into Assets and GameData with real RCS thrusters on subparts](howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-2.md) — advanced
- [Define furniture as a minimal static part that doubles as a craft](howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-3.md) — beginner

### [Defining articulated subparts and animating or hiding them from code](howto/vehicles-and-parts/parts/define-and-animate-subparts/overview.md)

How a subpart is a Part with settable parent-relative transforms, and four mods' ways of posing wheels, mounts and gimbals and hiding subparts from the main-thread frame

- [Pose wheels, arms and blades from StarMapAfterGui with a frame latch](howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-1.md) — intermediate
- [Drive articulated turret mounts as sibling subparts with rate-limited drives](howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-2.md) — advanced
- [Aim optical director heads on roll-nod and mast gimbals](howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-3.md) — advanced
- [Hide subparts by scale and fly them as projectiles of their launcher](howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-4.md) — advanced

### [Changing a shipped part without breaking saves](howto/vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md)

How KSA rebuilds a saved part from its current template and pairs saved subparts by position, why removing or reordering subparts crashes old saves, and the authoring rule plus a startup repair

- [Evolve part XML with append-only subpart lists and fixed part Ids](howto/vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/evolve-a-shipped-part-without-breaking-saves-1.md) — beginner
- [Repair saves and library craft at StarMapAllModsLoaded](howto/vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/evolve-a-shipped-part-without-breaking-saves-2.md) — advanced

### [Loading, validating and unloading part mods in a running game](howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md)

How KSA's boot asset pipeline can be replayed for one mod after startup, what it takes to roll back or purge it, and the validation rules that stop the game's silent failures

- [Load a part mod into a running game, one loader state per frame](howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-1.md) — advanced
- [Unload runtime-registered parts with a fail-closed gate and an ordered purge](howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-2.md) — advanced
- [Validate part XML against the game's silent failure modes before loading it](howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-3.md) — advanced

### [Shipping whole-vehicle presets and single-part craft with a part mod](howto/vehicles-and-parts/parts/ship-ready-made-craft/overview.md)

How KSA's two vehicle libraries store craft as VehicleSaveData files, and how to make a single part a complete craft that players can launch

- [Ship a preset craft as VehicleSaveData files in the vehicle library](howto/vehicles-and-parts/parts/ship-ready-made-craft/ship-ready-made-craft-1.md) — beginner
- [Make a single part a whole craft with a RootPartWhitelist tag](howto/vehicles-and-parts/parts/ship-ready-made-craft/ship-ready-made-craft-2.md) — beginner
- [Make a furniture part a one-part craft that is never built upon](howto/vehicles-and-parts/parts/ship-ready-made-craft/ship-ready-made-craft-3.md) — beginner

## Modules and resources

Part modules and resources: reading live module state, refilling and draining, commanding engines and RCS, adding parts in flight and parachutes.

### [Creating and attaching parts to a live vessel and feeding runtime engines](howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/overview.md)

How to build real parts on a flying vessel by hand-wiring the part tree, why a runtime engine finds no propellant until its declared feed connector is connected, and how to read the resource graph

- [Build an N x M grid of engines on a live vessel with a single part-tree rebuild](howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-1.md) — advanced
- [Wire FeedConnectors so runtime-created engines find propellant](howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-2.md) — advanced
- [Find the tanks an engine can draw from by walking ConsumptionOrder levels](howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-3.md) — intermediate

### [Commanding engines, throttle and RCS from code](howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md)

How KSA turns key presses into engine and thruster commands, the input-buffer and private-field routes a mod can use, and four recipes up to a custom RCS allocator

- [Switch engines on and off and set minimum throttle through KSA's input-event buffers](howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-1.md) — intermediate
- [Set manual throttle and RCS command flags by writing the boxed manual-control struct](howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-2.md) — intermediate
- [Animate a grid of engines with buffered SetIsActive calls](howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-3.md) — intermediate
- [Choose which RCS jets fire from a live wrench table](howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-4.md) — advanced

### [Customising parachutes, canopy materials and diameter limits](howto/vehicles-and-parts/modules-and-resources/customize-parachute-materials-and-limits/overview.md)

How a KSA parachute stores tuning, draws its canopy and clamps its diameter, and two patches that swap the canopy material and lift the editor's diameter slider bounds

- [Swap parachute canopy materials by editing MaterialIndices in a ChuteRenderable.Draw prefix](howto/vehicles-and-parts/modules-and-resources/customize-parachute-materials-and-limits/customize-parachute-materials-and-limits-1.md) — advanced
- [Lift the parachute diameter limits in the editor, reversibly](howto/vehicles-and-parts/modules-and-resources/customize-parachute-materials-and-limits/customize-parachute-materials-and-limits-2.md) — intermediate

### [Reading live part module state](howto/vehicles-and-parts/modules-and-resources/read-live-part-module-state/overview.md)

How KSA stores per-part module data in typed module lists and struct-of-arrays state lists on the PartTree, and two recipes for reading engines, tanks, propellant and solid motors

- [Read engines, tanks, solid motors and parts with Modules.Get and TryGetFrom, cached per vehicle](howto/vehicles-and-parts/modules-and-resources/read-live-part-module-state/read-live-part-module-state-1.md) — intermediate
- [Read a tank's shape and liquid level](howto/vehicles-and-parts/modules-and-resources/read-live-part-module-state/read-live-part-module-state-2.md) — advanced

### [Refilling, draining and topping up a vehicle's propellant and batteries](howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/overview.md)

The calls KSA uses to refill or empty tanks and batteries, the stock input buffer that runs them safely, and three mod recipes with their timing windows

- [Keep a vehicle's fuel and batteries full with a refill before the solver snapshot](howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/refill-or-drain-resources-1.md) — intermediate
- [Refuel on demand from a command queue drained in the solver window](howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/refill-or-drain-resources-2.md) — intermediate
- [Drain a tank through its mole states and recompute mass](howto/vehicles-and-parts/modules-and-resources/refill-or-drain-resources/refill-or-drain-resources-3.md) — advanced

## Staging and engines

Stages and engine performance: staged delta-v, thrust at ambient pressure, staging from code and automatic staging.

### [Computing engine thrust, Isp and mass flow at ambient pressure, and inverting for throttle](howto/vehicles-and-parts/staging-and-engines/compute-engine-thrust-at-ambient-pressure/overview.md)

How KSA evaluates an engine at a pressure and throttle, which cores follow the throttle, the manual g-load throttle cap, and two ways to reuse the model

- [Evaluate engines at an ambient pressure and throttle, mirror the g-load cap and invert for a thrust demand](howto/vehicles-and-parts/staging-and-engines/compute-engine-thrust-at-ambient-pressure/compute-engine-thrust-at-ambient-pressure-1.md) — advanced
- [Use ComputeFromCores or VacuumData inside a stage delta-v estimator](howto/vehicles-and-parts/staging-and-engines/compute-engine-thrust-at-ambient-pressure/compute-engine-thrust-at-ambient-pressure-2.md) — intermediate

### [Reading staged delta-v, thrust and Isp from the game or from your own stage model](howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)

How KSA's SequencePerformanceList simulates stage performance, when its published results are fresh, and four recipes for reading it, gating it or rebuilding a stage model yourself

- [Read PartTree.PerformanceSequences paired by index with SequenceList.Sequences](howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-1.md) — intermediate
- [Keep a private SequencePerformanceList with a change-gated recompute](howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-2.md) — advanced
- [Turn staging phases into constant-thrust stages for guidance](howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-3.md) — advanced
- [Estimate stage delta-v yourself from tanks and engine cores](howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-4.md) — intermediate

### [Staging a vehicle from code, from igniting the stage to firing one decoupler](howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/overview.md)

How the stage key activates a sequence, how decouplers split through the input buffer, and three ways a mod can stage a vehicle without racing the vehicle worker

- [Stage, ignite, fire decouplers and undock by calling the game's own entry points](howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-1.md) — intermediate
- [Stage inside the PrepareWorker prefix window so the sequence list is not mutated under the worker](howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-2.md) — advanced
- [Release a part by firing only the decoupler on its own joint](howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-3.md) — intermediate

### [Staging automatically when engines burn out, spent stages ride along, or a delay is wanted](howto/vehicles-and-parts/staging-and-engines/stage-automatically/overview.md)

How a mod can decide when to stage around the vehicle solver apply, keep an Auto burn alive across the new engines' first tick, drop spent stages early and hold decouplers and engines back by sim time

- [Stage on burnout around Universe.ApplyVehicleSolvers without losing the Auto burn](howto/vehicles-and-parts/staging-and-engines/stage-automatically/stage-automatically-1.md) — advanced
- [Drop a spent stage while the vehicle is still under thrust by predicting what Vehicle.Split would shed](howto/vehicles-and-parts/staging-and-engines/stage-automatically/stage-automatically-2.md) — advanced
- [Hold decouplers and engines back after the staging trigger with sim-time deadlines](howto/vehicles-and-parts/staging-and-engines/stage-automatically/stage-automatically-3.md) — advanced

## Part geometry

Part-level geometry: points and meshes in vehicle, world and screen space, anchors that survive vehicle changes, and engine layout diagrams.

### [Keeping an anchor on a part across staging, docking, editor changes and saves](howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)

Which game operations move Part objects between vehicles and four ways for a mod to re-find a part afterwards

- [Track a part by object identity and rehome the anchor every frame](howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-1.md) — intermediate
- [Re-resolve a part every frame by vehicle id and Part.InstanceId, dormant when gone](howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-2.md) — intermediate
- [Save a part reference as tree paths plus a topology hash and resolve it after reload](howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-3.md) — advanced
- [Read editor parts through EditingSpace and Harmony hooks without double-counting](howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-4.md) — intermediate

### [Engine layout diagrams from nozzle positions and live engine state](howto/vehicles-and-parts/part-geometry/draw-an-engine-diagram-from-nozzle-positions/overview.md)

How to enumerate a stage's engines with their states, locate their nozzles in vehicle space and lay them out as a top-down diagram

- [Draw an engine diagram from nozzle positions and live engine state](howto/vehicles-and-parts/part-geometry/draw-an-engine-diagram-from-nozzle-positions/draw-an-engine-diagram-from-nozzle-positions.md) — intermediate

### [Part meshes, attach nodes and part-local points in vehicle, world and screen space](howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/overview.md)

How KSA stores part geometry and the transform chain from a part-local point to vehicle assembly, ego, ECL and screen coordinates

- [Locate attach nodes and part centres with Connector.PositionEgo and PositionVehicleAsmb](howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-1.md) — intermediate
- [Turn a part-local point into an ECL position through the centre of mass and Asmb2Ego](howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-2.md) — advanced
- [Derive the stack axis from connectors and split vectors into axial and radial parts](howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-3.md) — intermediate
- [Analyse a part mesh: MeshView positions, weld, dihedral feature edges and Kasa circle fit](howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-4.md) — advanced
- [Clone an interleaved part mesh into a Simple mesh for your own renderer](howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-5.md) — advanced

## Craft tracking

Knowing which craft matter: part-Id-driven behaviour, rosters and loss detection, and automated weapon targeting.

### [Giving inert parts behaviour by recognising their part Id](howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/overview.md)

How a mod finds capability on a part with no module: a data-driven registry of profiles keyed by part Id, a per-craft survey and a roster keyed by Part

- [Weapon registry keyed by part Id, with profiles in a game-free layer](howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/give-inert-parts-behaviour-by-part-id-1.md) — advanced
- [Decide the dispenser kind by which part is fitted, and simulate flares and chaff as decoys](howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/give-inert-parts-behaviour-by-part-id-2.md) — advanced
- [Feed the registry from a KSArmory folder in every installed mod](howto/vehicles-and-parts/craft-tracking/give-inert-parts-behaviour-by-part-id/give-inert-parts-behaviour-by-part-id-3.md) — advanced

### [Picking and gating targets for automated weapons](howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/overview.md)

Closest-approach threat classification, sensor cone and lock dwell with terrain masking, and an explainable ladder of fire gates with a shared per-craft target tally

- [Classify threats by closed-form closest approach with median-filtered acceleration](howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/target-and-gate-automated-weapons-1.md) — advanced
- [Model a radar profile with a search cone, lock dwell and terrain-masked line of sight](howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/target-and-gate-automated-weapons-2.md) — advanced
- [Gate automated fire with an ordered ladder and a per-craft target tally](howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/target-and-gate-automated-weapons-3.md) — advanced

### [Tracking which craft carry your part, classifying them and detecting staging and loss](howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/overview.md)

Per-frame rosters keyed by Vehicle reference, reference-keyed team tables and state-diffing detection of staging, disposal and structural loss

- [Sync a Vehicle-keyed roster of craft that carry your part, once per frame from two hooks](howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-1.md) — intermediate
- [Declare teams and alliances in a reference-keyed roster rebuilt each step](howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-2.md) — advanced
- [Detect staging and callouts by diffing part count, vehicle count and attached radial decouplers](howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-3.md) — intermediate
- [Freeze on signal loss with IsDisposed, IsControllable and StructuralLoad fractions](howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-4.md) — intermediate
