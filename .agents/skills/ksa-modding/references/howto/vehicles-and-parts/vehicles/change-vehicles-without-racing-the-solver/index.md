# Game Feature

* [Changing vehicles without racing the vehicle solver](overview.md) - Why building, destroying or editing a vehicle from a mod hook can race KSA's physics worker, and seven recipes (lock, wait, stage, or write in the one window the worker does not overwrite)

# How-To

* [Change a part's mass at runtime in the PrepareWorker window](change-vehicles-without-racing-the-solver-7.md) - Derive the wanted mass from the roster every frame and write it through Part.SetInertMassPropertiesAsmb and an Unsafe ref to Vehicle.Props from the PrepareWorker prefix
* [Destroy vehicles and fail parts safely from a frame hook](change-vehicles-without-racing-the-solver-3.md) - Join the solver, hold the shapes lock and dispose, queue part failures on the update state instead of splitting inline, and recognise the debris the engine creates
* [Hold the shapes lock around a vehicle build](change-vehicles-without-racing-the-solver-1.md) - Wrap vehicle creation and part-tree edits in ConstraintSim.UnlockShapesBlocking so a build from a frame hook waits for the vehicle step instead of throwing
* [Wait on JobSystems.VehicleSolver before mutating vehicles](change-vehicles-without-racing-the-solver-2.md) - Join the vehicle solver scheduler before constructing kittens or teleporting vehicles from the UI pass, or stage the work like InputEvents.EvaSpawn
* [Write flight-computer and kinematic fields in a Vehicle.PrepareWorker prefix](change-vehicles-without-racing-the-solver-5.md) - Queue per-vehicle commands from frame hooks and apply them in a Harmony prefix on Vehicle.PrepareWorker, the last point before the worker snapshots the vehicle
* [Write mole (tank) state where the worker will not overwrite it](change-vehicles-without-racing-the-solver-6.md) - Owe tank mass in a queue, pay it from the PrepareWorker prefix through the live MoleState, and repeat the bookkeeping the engine does after emptying tanks
* [Write state in a prefix on Universe.ExecuteNextVehicleSolvers](change-vehicles-without-racing-the-solver-4.md) - Run a once-per-frame main-thread write after the solver results are applied and immediately before the worker snapshot, with Harmony Priority.First
