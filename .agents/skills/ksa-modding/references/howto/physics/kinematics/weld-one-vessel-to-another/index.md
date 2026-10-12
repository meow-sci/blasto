# Game Feature

* [Weld or anchor one vessel to another](overview.md) - How a mod pins a vessel to another vessel or part by re-teleporting it onto the anchor pose every frame, stamped with the right simulation time, with optional eased animation and named presets stored in TOML

# How-To

* [BeforePhysics callback at the PrepareFrame handoff](weld-one-vessel-to-another-1.md) - Teleport the welded source onto a freshly computed orbit at the PrepareFrame handoff, stamped with SimStep.PreviousTime, composing the part anchor and offset in Cci, ordering welds topologically and suppressing contacts between welded vessels
* [Per-weld eased animation queues in player time](weld-one-vessel-to-another-3.md) - Animate a weld's offset, rotation and scale with per-weld FIFO queues advanced at the physics handoff on player time, and store reusable named weld presets in a TOML file with legacy-key migration
* [VehicleSolver.Wait then teleport stamped with the next sim step](weld-one-vessel-to-another-2.md) - Weld a vessel from a StarMap frame hook without a transpiler by waiting for the vehicle solver, re-resolving the target part by InstanceId, and teleporting onto an orbit stamped with the in-flight step's end time
