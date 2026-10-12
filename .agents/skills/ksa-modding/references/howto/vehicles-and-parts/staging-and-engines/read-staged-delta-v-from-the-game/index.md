# Game Feature

* [Reading staged delta-v, thrust and Isp from the game or from your own stage model](overview.md) - How KSA's SequencePerformanceList simulates stage performance, when its published results are fresh, and four recipes for reading it, gating it or rebuilding a stage model yourself

# How-To

* [Estimate stage delta-v yourself from tanks and engine cores](read-staged-delta-v-from-the-game-4.md) - Walk sequences in order, drop what decouplers release, sum engine thrust and flow at a chosen ambient pressure, find reachable propellant and apply the rocket equation
* [Keep a private SequencePerformanceList with a change-gated recompute](read-staged-delta-v-from-the-game-2.md) - Run the game's stage model on your own list instance, recompute only when the answer can have moved, and wait while the part tree has pending derived work so the UI never races the vehicle solver
* [Read PartTree.PerformanceSequences paired by index with SequenceList.Sequences](read-staged-delta-v-from-the-game-1.md) - Take stage delta-v, thrust, Isp, masses and phases from the game's own staging simulator and add burn time, TWR, engine counts and reachable fuel, knowing when the list is fresh
* [Turn staging phases into constant-thrust stages for guidance](read-staged-delta-v-from-the-game-3.md) - Recompute the game's stage model from the PrepareWorker prefix, convert each phase to a constant-thrust stage, coalesce artificial splits, rescale for ambient pressure and repair double-registered engines
