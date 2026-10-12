# Game Feature

* [Staging a vehicle from code, from igniting the stage to firing one decoupler](overview.md) - How the stage key activates a sequence, how decouplers split through the input buffer, and three ways a mod can stage a vehicle without racing the vehicle worker

# How-To

* [Release a part by firing only the decoupler on its own joint](stage-a-vehicle-from-code-3.md) - Find the decoupler holding a launcher on, ask whether the next stage would fire it, and fire it with Decoupler.SetIsActive instead of staging the player's list
* [Stage inside the PrepareWorker prefix window so the sequence list is not mutated under the worker](stage-a-vehicle-from-code-2.md) - Queue a stage request and run ActivateNextSequence from a Harmony prefix on Vehicle.PrepareWorker, the one point where the vehicle state is settled and the snapshot is not yet taken
* [Stage, ignite, fire decouplers and undock by calling the game's own entry points](stage-a-vehicle-from-code-1.md) - Call SequenceList.ActivateNextSequence, Vehicle.SetEnum(MainIgnite), Decoupler.SetIsActive and the docking input buffer from the main thread, the way gatOS exposes them as commands
