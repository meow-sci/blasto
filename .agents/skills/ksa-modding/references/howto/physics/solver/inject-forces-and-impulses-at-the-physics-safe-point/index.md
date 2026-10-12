# Game Feature

* [Inject forces, impulses and velocity changes at the physics safe point](overview.md) - How a mod pushes a craft (driven car, blast, boost, hover, lift, flip) by writing velocity, spin or pose deltas where they are integrated, and how to keep those pushes bounded at any time warp

# How-To

* [Add velocity deltas in the PrepareWorker prefix](inject-forces-and-impulses-at-the-physics-safe-point-1.md) - Push or turn a craft by adding to VelocityPhys and AngularVelocityPhys in the Vehicle.PrepareWorker prefix, and rotate or lift it in place by rewriting position, attitude and both velocities together
* [Hover and touchdown under real engine power with a PID](inject-forces-and-impulses-at-the-physics-safe-point-5.md) - Hold a hover and land at a fixed rate by commanding the craft's own engines and attitude from the PrepareWorker prefix with a quadratic sink profile and per-axis PIDs, instead of injecting velocity
* [Impulsive kick from the solver-dispatch window](inject-forces-and-impulses-at-the-physics-safe-point-3.md) - Apply an instantaneous velocity change to a vessel by rebuilding its orbit from the bumped CCI state vector in a prefix on Universe.ExecuteNextVehicleSolvers
* [Summed per-part drag-style shove with torque from a blast front](inject-forces-and-impulses-at-the-physics-safe-point-2.md) - Turn a blast front into one linear and one angular impulse per craft by summing a per-part push and its torque about the centre of mass, saturate it at the wind speed, and write it from the PrepareWorker prefix
* [Velocity and spin deltas gated by hatches in the PrepareWorker prefix](inject-forces-and-impulses-at-the-physics-safe-point-4.md) - Give a craft lift rockets, a gravity-cancelling hover, a boost and downforce as per-step velocity and spin deltas gated by 0.1 s hatches, expressed as exponential shares and per-step caps so they stay stable at any time warp
