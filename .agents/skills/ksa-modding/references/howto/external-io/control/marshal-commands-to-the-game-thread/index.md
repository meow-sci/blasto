# Game Feature

* [Marshal commands from background threads to the game thread](overview.md) - Let transport threads submit immutable commands and await a result while every KSA mutation happens at one fixed point in the frame, with a second drain point for writes the vehicle solver would otherwise overwrite

# How-To

* [Solver-window drain in a Priority.First prefix on ExecuteNextVehicleSolvers](marshal-commands-to-the-game-thread-2.md) - Apply flight computer setpoints, refills and teleports between the physics apply and the next worker snapshot so the asynchronous vehicle solver cannot erase them
* [Two ConcurrentQueues with TaskCompletionSource, timeout and abandon](marshal-commands-to-the-game-thread-1.md) - Build a command queue where transport threads await a result and the game thread drains per phase at a fixed point in the frame
