# Game Feature

* [Simulate mod-owned bodies in sub-steps outside the vehicle system](overview.md) - How a mod runs missiles, shells or a golf ball as plain objects integrated in fixed sub-steps on KSA's simulated time, with the right frame, epoch and terrain contact

# How-To

* [Ball integrated at up to 1/240 s in the body-fixed frame](simulate-bodies-outside-the-vehicle-system-2.md) - Integrate a free body as a few doubles in the planet-fixed Ccf frame over the applied simulation step, with flight, bounce, rolling resistance, rest and slope handling against the height field, and convert to the ecliptic only to draw
* [Deduplicate GetLastSimStep on NextTime and widen to the span since the last boundary](simulate-bodies-outside-the-vehicle-system-3.md) - Take the simulation step the engine just applied exactly once per frame, return zero when asked twice or when paused, and add back frames the mod never ran
* [Differences-only inputs and headless tests](simulate-bodies-outside-the-vehicle-system-6.md) - Keep a mod's physics maths free of game types and large coordinates by passing the mover-to-target difference, linking the engine-free folder into a test project that references no KSA assembly, and putting the engine behind narrow seams
* [Ecl state at GetElapsedTime with epoch back-dating](simulate-bodies-outside-the-vehicle-system-4.md) - Treat every engine sample as belonging to one instant, the end of the step just applied, back-date targets to the sub-step a round is at, and draw against the position KSA renders a craft at
* [IProjectile objects with fixed 5 ms sub-steps and a faithful-step clamp](simulate-bodies-outside-the-vehicle-system-1.md) - Fly guided and unguided rounds as plain objects in sub-steps across each applied simulation step, with one driver shared by the game and a headless rig, and a clamp on how much time a frame may integrate
* [Read GetStatesCcf in the PrepareWorker prefix](simulate-bodies-outside-the-vehicle-system-5.md) - Read a vehicle's physics state in the planet-fixed frame from a Vehicle.PrepareWorker prefix and sample the height field under each point, so ground height and ground speed come out in metres and metres per second over the ground
