# Game Feature

* [Staging automatically when engines burn out, spent stages ride along, or a delay is wanted](overview.md) - How a mod can decide when to stage around the vehicle solver apply, keep an Auto burn alive across the new engines' first tick, drop spent stages early and hold decouplers and engines back by sim time

# How-To

* [Drop a spent stage while the vehicle is still under thrust by predicting what Vehicle.Split would shed](stage-automatically-2.md) - Predict the parts the next sequence would jettison, require them to be spent, wait out a dwell, refuse when they still feed the vehicle, and only then stage
* [Hold decouplers and engines back after the staging trigger with sim-time deadlines](stage-automatically-3.md) - Reimplement SequenceList.ActivateNextSequence so engines and decouplers fire on separate deadlines, read per-part and per-sequence delays from TOML, and flush held modules on unload
* [Stage on burnout around Universe.ApplyVehicleSolvers without losing the Auto burn](stage-automatically-1.md) - Sample before the solver results are applied, evaluate after, stage through a per-vehicle state machine and take the stock out-of-propellant stop back while the new engines report their first tick
