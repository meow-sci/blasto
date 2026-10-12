# Game Feature

* [Test a solver or controller offline: closed loop, purity and replay](overview.md) - Build a console harness that solves, integrates dynamics with injected dispersions and re-solves, check determinism and no cross-talk before threading a solve, and replay the exact problem the live game fed the solver

# How-To

* [Dump the exact problem before the solve and replay it offline with full tracing](test-a-solver-or-controller-offline-2.md) - Serialize the plain-data problem the game fed a solver to a versioned SI JSON file at the moment it is built, then re-solve that file in a test console with iteration tracing and command-line overrides
* [Run a closed-loop harness with injected dispersions and check the solve is a pure function of its inputs](test-a-solver-or-controller-offline-1.md) - Fly the real receding-horizon loop on integrated dynamics with seeded thrust and gimbal errors, reduce each run to a few metrics, and prove determinism, input sensitivity and no cross-talk before moving a solve to a worker thread
